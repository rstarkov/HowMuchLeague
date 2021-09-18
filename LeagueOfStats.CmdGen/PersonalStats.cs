using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LeagueOfStats.PersonalData;
using LeagueOfStats.StaticData;
using RT.TagSoup;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.CmdGen
{
    static class PersonalStats
    {
        public static void Generate(string dataPath, string outputPathTemplate, List<HumanInfo> humans)
        {
            Directory.CreateDirectory(Path.Combine(dataPath, "Static"));
            Directory.CreateDirectory(Path.Combine(dataPath, "Summoners"));

            foreach (var human in humans)
                human.Summoners = human.SummonerIds.Where(si => si.LoadData).Select(si => new SummonerInfo(Path.Combine(dataPath, "Summoners", $"{si.RegionServer.ToLower()}-{si.AccountId}.xml"))).ToList();
            foreach (var summoner in humans.SelectMany(h => h.Summoners))
            {
                Console.WriteLine($"Loading game data for {summoner}");
                if (summoner.AuthorizationHeader == "")
                    summoner.LoadGamesOffline();
                else
                    summoner.LoadGamesOnline(
                        sm => throw new Exception()/*InputBox.GetLine($"Please enter Authorization header value for {sm.Region}/{sm.Name}:", sm.AuthorizationHeader, "League of Stats")*/,
                        str => Console.WriteLine(str));
            }

            var generator = new PersonalStatsGenerator();
            generator.KnownPlayersAccountIds = humans.SelectMany(h => h.SummonerIds).Select(s => s.AccountId).ToHashSet();
            foreach (var player in humans.SelectMany(h => h.Summoners).SelectMany(s => s.Games).SelectMany(g => g.Ally.Players))
                if (generator.KnownPlayersAccountIds.Contains(player.AccountId))
                    humans.Single(h => h.SummonerIds.Any(s => s.AccountId == player.AccountId)).SummonerNames.IncSafe(player.Name);
            foreach (var human in humans.Where(h => h.Summoners.Count > 0))
            {
                generator.TimeZone = human.TimeZone;
                generator.Games = human.Summoners.SelectMany(s => s.Games).ToList();
                generator.OtherHumans = humans.Where(h => /*h.Summoners.Count > 0 &&*/ h != human).ToList();
                generator.ThisPlayerAccountIds = human.Summoners.Select(s => s.AccountId).ToList();
                generator.GamesTableFilename = outputPathTemplate.Fmt("Games-All", human.Name, "");
                generator.ProduceGamesTable();
                generator.ProduceLaneTable(outputPathTemplate.Fmt("LaneCompare", human.Name, ""));
                generator.ProduceStats(outputPathTemplate.Fmt("All", human.Name, ""));
                generator.ProduceStats(outputPathTemplate.Fmt("All", human.Name, "-200"), 200);
                generator.ProduceStats(outputPathTemplate.Fmt("All", human.Name, "-1000"), 1000);
                foreach (var summoner in human.Summoners)
                {
                    generator.Games = summoner.Games.ToList();
                    generator.ThisPlayerAccountIds = new[] { summoner.AccountId }.ToList();
                    generator.GamesTableFilename = outputPathTemplate.Fmt("Games-" + summoner.Region, summoner.Name, "");
                    generator.ProduceGamesTable();
                    generator.ProduceStats(outputPathTemplate.Fmt(summoner.Region, summoner.Name, ""));
                    generator.ProduceStats(outputPathTemplate.Fmt(summoner.Region, summoner.Name, "-200"), 200);
                    generator.ProduceStats(outputPathTemplate.Fmt(summoner.Region, summoner.Name, "-1000"), 1000);
                }
            }
        }
    }

    class PersonalStatsGenerator
    {
        public List<long> ThisPlayerAccountIds; // ordered by which account is this player if multiple are in the game
        public HashSet<long> KnownPlayersAccountIds;
        public string TimeZone;
        public string GamesTableFilename;
        private List<Game> _games;
        public IEnumerable<Game> Games
        {
            set
            {
                _games = new List<Game>();
                foreach (var game in value.Where(g => g.Duration > TimeSpan.FromMinutes(4) /* remakes */).OrderByDescending(g => g.DateUtc))
                    if (_games.Count == 0 || _games[_games.Count - 1].Id != game.Id)
                        _games.Add(game);
            }
        }
        public List<HumanInfo> OtherHumans;

        private List<IGrouping<string, Game>> getGameTypeSections(int limit)
        {
            return _games
                .Take(limit)
                .GroupBy(g => g.Queue.MapName + ", " + g.Queue.QueueName)
                .OrderByDescending(g => g.Count())
                .ToList();
        }

        private Player thisPlayer(Game game)
        {
            return ThisPlayerAccountIds.Select(accId => game.AllPlayers().FirstOrDefault(p => p.AccountId == accId)).FirstOrDefault(p => p != null);
        }

        public void ProduceGamesTable()
        {
            var outputFile = GamesTableFilename;
            Console.Write("Producing output file: " + outputFile + " ... ");
            //var gameTypeSections = getGameTypeSections(999999);
            var gameTypeSections = _games // flat list of games instead of by game type
                .GroupBy(g => "ALL")
                .ToList();

            var sections = gameTypeSections.Select(grp => Ut.NewArray<object>(
                    new H1(grp.Key) { id = new string(grp.Key.Where(c => char.IsLetterOrDigit(c)).ToArray()) },
                    new TABLE(
                        grp.OrderByDescending(g => g.DateUtc).Select(g =>
                        {
                            var allies = g.Ally.Players.Select(plr => getPlayerHtml(plr)).ToList();
                            var enemies = g.Enemy.Players.Select(plr => getPlayerHtml(plr)).ToList();
                            return Ut.NewArray<object>(
                                new TR { id = "game" + g.Id.ToString() }._(
                                    new TD { rowspan = 2, class_ = "nplr datetime" }._(new A(g.Date(TimeZone).ToString("dd/MM/yy"), new BR(), g.Date(TimeZone).ToString("HH:mm")) { href = g.DetailsUrl }),
                                    new TD { rowspan = 2, class_ = "nplr" }._(minsec(g.Duration)),
                                    new TD { rowspan = 2, class_ = "nplr " + NullTrueFalse(g.Victory, "draw", "victory", "defeat") }._(NullTrueFalse(g.Victory, "Draw", "Victory", "Defeat"), new BR(), g.Queue.MicroName),
                                    new TD { rowspan = 2, class_ = "sep" },
                                    allies.Select(p => p[0]),
                                    new TD { rowspan = 2, class_ = "sep" },
                                    enemies.Select(p => p[0])
                                ),
                                new TR(
                                    allies.Select(p => p[1]),
                                    enemies.Select(p => p[1])
                                )
                            );
                        })
                    )
                ));
            var result = Ut.NewArray<object>(
                getContents(gameTypeSections),
                sections
            );
            var css = getCss();
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
            File.WriteAllText(outputFile, new HTML(new HEAD(new META { charset = "utf-8" }, new STYLELiteral(css)), new BODY(result)).ToString());
            Console.WriteLine("done");
        }

        public void ProduceLaneTable(string outputFile)
        {
            Console.Write("Producing output file: " + outputFile + " ... ");

            (string kind, object td) wonOrLost(double thisPlr, double oppPlr, double percentageThreshold, double absoluteThreshold, bool isGood = true, double percentageThresholdHard = 999)
            {
                string kind;
                if (Math.Abs(thisPlr - oppPlr) < absoluteThreshold)
                    kind = "close";
                if (thisPlr == 0 || oppPlr == 0)
                    kind = Math.Abs(thisPlr - oppPlr) < absoluteThreshold ? "close" : thisPlr > oppPlr ? "hardwin" : "hardloss";
                if (thisPlr > oppPlr)
                    kind = thisPlr / oppPlr > 1 + percentageThresholdHard ? (isGood ? "hardwin" : "hardloss") : thisPlr / oppPlr > 1 + percentageThreshold ? (isGood ? "win" : "loss") : "close";
                else
                    kind = oppPlr / thisPlr > 1 + percentageThresholdHard ? (isGood ? "hardloss" : "hardwin") : oppPlr / thisPlr > 1 + percentageThreshold ? (isGood ? "loss" : "win") : "close";

                return (kind, new TD { class_ = $"nplr {kind}" }._($"{thisPlr} vs {oppPlr}"));
            }

            var variants = Queues.AllQueues.Where(q => q.IsSR5v5(rankedOnly: false)).Select(q => q.Variant).Distinct() // some of the queues are more or less duplicates of each other
                .OrderBy(v => v == "Ranked Solo" ? 1 : v == "Draft Pick" ? 2 : v == "Blind Pick" ? 3 : 4).ThenBy(v => v)
                .ToList();
            var stats = from variant in variants
                        let games = _games.Where(g => g.Queue.IsSR5v5(rankedOnly: false) && g.Queue.Variant == variant).Select(game =>
                        {
                            var thisPlr = thisPlayer(game);
                            var laneOpponents = game.Enemy.Players.Where(g => g.Lane == thisPlr.Lane && g.Role == thisPlr.Role);
                            var oppPlr = laneOpponents.Count() == 1 ? laneOpponents.Single() : null;
                            if (thisPlr == null || oppPlr == null)
                                return (game, thisPlr, oppPlr, null);
                            return (game: game, thisPlr: thisPlr, oppPlr: oppPlr, details: new
                            {
                                creepsAt10 = wonOrLost(thisPlr.CreepsAt10, oppPlr.CreepsAt10, 0.05, 5, percentageThresholdHard: 0.30),
                                goldAt10 = wonOrLost(thisPlr.GoldAt10, oppPlr.GoldAt10, 0.08, 100, percentageThresholdHard: 0.16),
                                goldAt20 = wonOrLost(thisPlr.GoldAt20, oppPlr.GoldAt20, 0.08, 200, percentageThresholdHard: 0.16),
                                kills = wonOrLost(thisPlr.Kills, oppPlr.Kills, 0, 1, percentageThresholdHard: 0.80),
                                deaths = wonOrLost(thisPlr.Deaths, oppPlr.Deaths, 0, 1, isGood: false, percentageThresholdHard: 1.20),
                                damage = wonOrLost(thisPlr.DamageToChampions, oppPlr.DamageToChampions, 0.08, 600, percentageThresholdHard: 0.30),
                                wards = wonOrLost(thisPlr.WardsPlaced, oppPlr.WardsPlaced, 0.12, 3, percentageThresholdHard: 2.00),
                            });
                        }).ToList()
                        select new { variant, games };

            var result = stats.Where(s => s.games.Count > 0).Select(s => Ut.NewArray<object>(
                new H1(Maps.GetName(MapId.SummonersRift) + ", 5v5: " + s.variant),
                new TABLE { class_ = "lane-compare" }._(
                    new TR(
                        new TH("Date"),
                        new TH("Result"),
                        new TH("Champion"),
                        new TH("Lane"),
                        new TH("CS@10"),
                        new TH("Gold@10"),
                        new TH("Gold@20"),
                        new TH("Kills"),
                        new TH("Deaths"),
                        new TH("Damage"),
                        new TH("Wards"),
                        new TH("Analysis")
                    ),
                    s.games.Select(g => new TR(
                        new TD(new A(g.game.Date(TimeZone).ToString("dd/MM/yy HH:mm")) { href = g.game.DetailsUrl }),
                        new TD { class_ = NullTrueFalse(g.game.Victory, "draw", "victory", "defeat") }._(NullTrueFalse(g.game.Victory, "Draw", "Victory", "Defeat")),
                        new TD($"{g.thisPlr.Champion} vs {g.oppPlr?.Champion ?? "?"}"),
                        new TD(g.thisPlr.Lane),
                        g.details?.creepsAt10.td,
                        g.details?.goldAt10.td,
                        g.details?.goldAt20.td,
                        g.details?.kills.td,
                        g.details?.deaths.td,
                        g.details?.damage.td,
                        g.details?.wards.td,
                        g.details == null ? null : new TD(
                            g.thisPlr.Role == Role.DuoSupport ? "(support)" :
                            g.details.damage.kind == "hardwin" && g.details.kills.kind == "hardwin" && g.game.Victory == false ? "Boosted teammates" :
                            g.details.damage.kind == "hardloss" && g.details.kills.kind == "hardloss" && g.game.Victory == true ? "Got carried hard" : ""
                        )
                    ))
                )
            ));

            var css = getCss();
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
            File.WriteAllText(outputFile, new HTML(new HEAD(new META { charset = "utf-8" }, new STYLELiteral(css)), new BODY(result)).ToString());
            Console.WriteLine("done");
        }

        public void ProduceStats(string outputFile, int limit = 999999)
        {
            Console.Write("Producing output file: " + outputFile + " ... ");
            var standardPvP = _games.Take(limit).Where(g => g.Queue.IsSR5v5(rankedOnly: false));
            var gameTypeSections = standardPvP.GroupBy(_ => "Summoner's Rift, ALL 5v5 PvP MODES").ToList().Concat(getGameTypeSections(limit)).ToList();
            var result = Ut.NewArray(
                new P("Generated on ", DateTime.Now.ToString("dddd', 'dd'.'MM'.'yyyy' at 'HH':'mm':'ss")),
                new H1("All game modes"),
                genStats(_games.Take(limit), allGames: true),
                new H1("Per game mode stats"),
                getContents(gameTypeSections),
                gameTypeSections.Select(grp => Ut.NewArray<object>(
                    new H1(grp.Key) { id = new string(grp.Key.Where(c => char.IsLetterOrDigit(c)).ToArray()) },
                    genStats(grp, allGames: false)
                ))
            );
            var css = getCss();
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
            File.WriteAllText(outputFile, new HTML(new HEAD(new META { charset = "utf-8" }, new STYLELiteral(css)), new BODY(result)).ToString());
            Console.WriteLine("done");
        }

        private static Tag getContents(List<IGrouping<string, Game>> gameTypeSections)
        {
            return new DIV { style = "text-align: center;" }._(new P { style = "text-align: left; display: inline-block;" }._(
                    gameTypeSections.Select(grp => Ut.NewArray<object>(
                        new A(grp.Key) { href = "#" + new string(grp.Key.Where(c => char.IsLetterOrDigit(c)).ToArray()) },
                        $" : {grp.Count():#,0} games, {grp.Sum(g => g.Duration.TotalHours):#,0} hours",
                        new BR()
                    ))
            ));
        }

        private string getCss()
        {
            var css = @"
                body { font-family: 'Open Sans', sans-serif; }
                body, td.sep { background: #eee; }
                table { border-collapse: collapse; border-bottom: 1px solid black; margin: 0 auto; }
                h1 { margin-top: 120px; }
                h1, h4 { text-align: center; }
                td.nplr { border-top: 1px solid black; }
                td:last-child { border-right: 1px solid black; }
                td { border-left: 1px solid black; }
                td, th { padding: 0 6px; background: #fff; }
                th { border: 1px solid black; background: #ccc; }
                td.plr-top { padding-top: 4px; border-top: 1px solid black; text-align: right; }
                td.plr-bot { padding-bottom: 4px; }
                table.stats tr:hover td { background: #d4eeff; }
                div.plrname { max-width: 100px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 80%; float: left; }
                div.multi { float: right; }
                div.multi5 { font-weight: bold; background: #c21; color: #fff; padding: 0 6px; }
                div.multi4 { font-weight: bold; color: #c21; }
                td.datetime { text-align: center; }
                td.victory { background: #7DF93D; }
                td.defeat { background: #FF7954; }
                table.ra td { text-align: right; }
                table.la td { text-align: left; }
                table td.ra.ra { text-align: right; }
                table td.la.la { text-align: left; }
                table.lane-compare td { border: 1px solid black; text-align: center; padding: 2px 8px; }
                table.lane-compare a { text-decoration: none; }
                .hspace { margin-right: 25px; }
                .linelist { margin-left: 10px; white-space: nowrap; }
                .win, .win:visited { color: #1A9B1B; }
                .loss, .loss:visited { color: #B02424; }
                .hardwin, .hardwin:visited { color: #1A9B1B; font-weight: bold; }
                .hardloss, .hardloss:visited { color: #B02424; font-weight: bold; }
                .close, .close:visited { color: #1A68B1; }
                .linelist:before { content: '\200B'; word-break: break-all; }";
            css += KnownPlayersAccountIds.Select(accId => $"\r\n                td.kp{accId}" + (ThisPlayerAccountIds.Contains(accId) ? " { background: #D1FECC; }" : " { background: #6EFFFF; }")).JoinString();
            css += "\r\n";
            return css;
        }

        private T NullTrueFalse<T>(bool? input, T ifNull, T ifTrue, T ifFalse)
        {
            return input == null ? ifNull : input.Value ? ifTrue : ifFalse;
        }

        private string minsec(TimeSpan time)
        {
            return ((int) time.TotalMinutes).ToString("00") + ":" + time.Seconds.ToString("00");
        }

        private object genStats(IEnumerable<Game> games, bool allGames)
        {
            var result = new List<object>();
            var histoTimeOfDay = range(5, 24, 24).Select(h => (label: h.ToString("00"), y: games.Count(g => g.Date(TimeZone).TimeOfDay.Hours == h)));
            result.Add(makeHistogram(histoTimeOfDay, "Games by time of day"));
            var gamesByDay = games.GroupBy(g => g.DateDayOnly(TimeZone)).OrderBy(g => g.Key).ToList();
            var firstDay = gamesByDay[0].Key;
            var dates = gamesByDay.Select(g => g.Key).ToList();
            var firstWeek = firstDay.AddDays(-(((int) firstDay.DayOfWeek - 1 + 7) % 7));
            Ut.Assert(firstWeek.DayOfWeek == DayOfWeek.Monday);
            var gamesByWeek = gamesByDay.GroupBy(gbd => (int) Math.Floor((gbd.Key - firstWeek).TotalDays / 7)).ToDictionary(g => g.Key, g => g.SelectMany(gg => gg).ToList());
            var gamesByMonth = games.GroupBy(g => { var d = g.DateDayOnly(TimeZone); return d.Year * 12 + d.Month - (firstDay.Year * 12 + firstDay.Month); }).ToDictionary(g => g.Key, g => g.ToList());
            var histoGamesPerDay = Enumerable.Range(1, 12).Select(c => (label: c == 12 ? "12+" : c.ToString(), y: gamesByDay.Count(grp => c == 12 ? grp.Count() >= 12 : grp.Count() == c))).ToList();
            result.Add(makeHistogram(histoGamesPerDay, "Games played per day")); // TODO: add 0 games
            var histoGamesByDayOfWeek = range(1, 7, 7).Select(dow => (label: ((DayOfWeek) dow).ToString().Substring(0, 2), y: games.Count(g => (int) g.Date(TimeZone).DayOfWeek == dow))).ToList();
            result.Add(makeHistogram(histoGamesByDayOfWeek, "Games played on ..."));
            result.Add(makeHistogram(range(1, 7, 7).Select(dow => (label: ((DayOfWeek) dow).ToString().Substring(0, 2), y: gamesByDay.Count(g => (int) g.Key.DayOfWeek == dow))).ToList(), "Days with 1+ games"));
            // TODO: days with 0 games
            if (!allGames)
                result.Add(makeHistogram2(new double[] { 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70 }, (durMin, durMax) => games.Count(g => g.Duration.TotalMinutes > durMin && g.Duration.TotalMinutes <= durMax), "Games by length, minutes"));
            result.Add(makePlotXY("Distinct champs played", dates.Select(d => (x: (d - firstDay).TotalDays, y: (double) games.Where(g => g.DateDayOnly(TimeZone) <= d).Select(g => thisPlayer(g).Champion).Distinct().Count())).ToList()));
            result.Add(makePlotXY("Distinct champs 3+ games", dates.Select(d => (x: (d - firstDay).TotalDays, y: (double) games.Where(g => g.DateDayOnly(TimeZone) <= d).GroupBy(g => thisPlayer(g).Champion).Where(grp => grp.Count() >= 3).Count())).ToList()));
            result.Add(makePlotXY("Distinct champs 10+ games", dates.Select(d => (x: (d - firstDay).TotalDays, y: (double) games.Where(g => g.DateDayOnly(TimeZone) <= d).GroupBy(g => thisPlayer(g).Champion).Where(grp => grp.Count() >= 10).Count())).ToList()));
            if (!allGames)
            {
                var plotWardProgress = games.Select(g => g.Enemy.Players.Sum(p => p.WardsPlaced / g.Duration.TotalMinutes * 30.0)).ToList();
                plotWardProgress.Reverse();
                result.Add(makePlotY("Wards over time by enemy team", plotWardProgress, average(plotWardProgress, 29).ToList()));
                var plotGameDurationProgress = games.Select(g => g.Duration.TotalMinutes).ToList();
                plotGameDurationProgress.Reverse();
                result.Add(makePlotY("Game duration over time", plotGameDurationProgress, average(plotGameDurationProgress, 49).ToList()));
            }
            var plotHoursPerWeek = Enumerable.Range(0, gamesByWeek.Keys.Max() + 1).Select(wk => gamesByWeek.ContainsKey(wk) ? gamesByWeek[wk].Sum(g => g.Duration.TotalHours) : 0).ToList();
            result.Add(makePlotY($"Hours played per week: {median(plotHoursPerWeek.Where(n => n > 0)):0.0}", plotHoursPerWeek, average(plotHoursPerWeek, 25).ToList()));
            var plotHoursPerMonth = Enumerable.Range(0, gamesByMonth.Keys.Max() + 1).Select(mo => gamesByMonth.ContainsKey(mo) ? gamesByMonth[mo].Sum(g => g.Duration.TotalHours) : 0).ToList();
            result.Add(makePlotY($"Hours played per month: {median(plotHoursPerMonth.Where(n => n > 0)):0.0}", plotHoursPerMonth, average(plotHoursPerMonth, 7).ToList()));
            if (!allGames)
            {
                result.Add(makeCsPlotOverTime("CS@20m as ADC over time", games, Role.DuoCarry, Lane.Bottom));
                result.Add(makeCsPlotOverTime("CS@20m as Mid over time", games, Role.Solo, Lane.Middle));
                result.Add(makeCsPlotOverTime("CS@20m as Top over time", games, Role.Solo, Lane.Top));
            }

            result.Add(new P(
                new B("Total games: "), games.Count().ToString("#,0"), new SPAN { class_ = "hspace" },
                new B("Total duration: "), games.Sum(g => g.Duration.TotalHours).ToString("#,0 hours"), new SPAN { class_ = "hspace" },
                new B("Avg per day: "), (games.Sum(g => g.Duration.TotalHours) / (games.Max(g => g.DateUtc) - games.Min(g => g.DateUtc)).TotalDays).ToString("0.0 hours"), new SPAN { class_ = "hspace" },
                new B("Avg per day on game days: "), (games.Sum(g => g.Duration.TotalHours) / games.Select(g => g.DateDayOnly(TimeZone)).Distinct().Count()).ToString("0.0 hours")
            ));
            result.Add(new P(new B("Longest and shortest:"),
                games.OrderByDescending(g => g.Duration).Take(7).Select(g => GetGameLink(g, minsec(g.Duration), new SUP(g.Queue.MicroName)).AddClass("linelist")), new SPAN("...") { class_ = "linelist" },
                games.OrderByDescending(g => g.Duration).TakeLast(7).Select(g => GetGameLink(g, minsec(g.Duration), new SUP(g.Queue.MicroName)).AddClass("linelist"))));

            var byLastWinLoss = games.Where(g => g.Victory != null).GroupBy(g => g.DateDayOnly(TimeZone)).Select(grp => grp.OrderBy(itm => itm.DateUtc).Last().Victory.Value);
            result.Add(new P(new B("Last game of the day: "), "victory: {0:0}%, defeat: {1:0}%".Fmt(
                byLastWinLoss.Count(v => v) / (double) byLastWinLoss.Count() * 100,
                byLastWinLoss.Count(v => !v) / (double) byLastWinLoss.Count() * 100
            )));
            result.Add(new P(new B("Longest unbroken win streaks:"), games.GroupConsecutiveBy(g => g.Victory == true).Where(grp => grp.Key).OrderByDescending(grp => grp.Count).Take(20).Select(grp => GetGameLink(grp.First(), grp.Count).AddClass("linelist"))));
            result.Add(new P(new B("Longest unbroken loss streaks:"), games.GroupConsecutiveBy(g => g.Victory == false).Where(grp => grp.Key).OrderByDescending(grp => grp.Count).Take(20).Select(grp => GetGameLink(grp.First(), grp.Count).AddClass("linelist"))));
            result.Add(new P(new B("Most wins in 20 games:"), mostWinsLosses(games, 20, wins: true).Take(12).Select(g => GetGameLink(g.firstGame, g.count).AddClass("linelist")), new SPAN { class_ = "hspace" },
                new B("in 50 games:"), mostWinsLosses(games, 50, wins: true).Take(12).Select(g => GetGameLink(g.firstGame, g.count).AddClass("linelist"))));
            result.Add(new P(new B("Most losses in 20 games:"), mostWinsLosses(games, 20, wins: false).Take(12).Select(g => GetGameLink(g.firstGame, g.count).AddClass("linelist")), new SPAN { class_ = "hspace" },
                new B("in 50 games:"), mostWinsLosses(games, 50, wins: false).Take(12).Select(g => GetGameLink(g.firstGame, g.count).AddClass("linelist"))));
            result.Add(new P(new B("AFK or leaver on our/enemy team in last N games:"), new[] { 10, 50, 100, 200, 300, 9999 }.Select(count => new SPAN(count, ": ",
                games.Take(count).Count(g => g.Ally.Players.Any(p => p.Afk == true || p.Leaver == true)), "/",
                games.Take(count).Count(g => g.Enemy.Players.Any(p => p.Afk == true || p.Leaver == true))
            )
            { class_ = "linelist" })));
            result.Add(new P(new B("Leaver on our/enemy team in last N games:"), new[] { 10, 50, 100, 200, 300, 9999 }.Select(count => new SPAN(count, ": ",
                games.Take(count).Count(g => g.Ally.Players.Any(p => p.Leaver == true)), "/",
                games.Take(count).Count(g => g.Enemy.Players.Any(p => p.Leaver == true)))
            { class_ = "linelist" })));
            result.Add(new P(new B("Longest breaks from playing:"), joinWithMore(10, games.Select(g => (g, g.DateUtc)).Concat((null, DateTime.UtcNow)).OrderBy(x => x.DateUtc).ConsecutivePairs(false).Select(p => (p.Item1.g, (p.Item2.DateUtc - p.Item1.DateUtc).TotalDays)).OrderByDescending(t => t.TotalDays)
                .Select(x => GetGameLink(x.g, $"{x.g.DateDayOnly(TimeZone):yyyy-MM-dd} ({x.TotalDays:0.00} days)").AddClass("linelist")).Take(30))));

            var champions = LeagueStaticData.Champions.Values.Select(ch => ch.Name).ToList();
            result.Add(new P(new B("Played 10+ times: "),
                (from champ in champions let c = games.Count(g => thisPlayer(g).Champion == champ) where c >= 10 orderby c descending select "{0}: {1:#,0}".Fmt(champ, c)).JoinString(", ")));
            for (int count = 9; count >= 0; count--)
            {
                var champs = champions.Where(champ => games.Count(g => thisPlayer(g).Champion == champ) == count).Order().JoinString(", ");
                if (champs != "")
                    result.Add(new P(new B($"Played {count} times: "), champs));
            }

            result.Add(new P(new B("Penta kills:"), joinWithMore(100, games.Select(g => thisPlayer(g)).Where(p => p.LargestMultiKill == 5).Select(p => GetGameLink(p.Game, p.Champion).AddClass("linelist")))));
            result.Add(new P(new B("Quadra kills:"), joinWithMore(50, games.Select(g => thisPlayer(g)).Where(p => p.LargestMultiKill == 4).Select(p => GetGameLink(p.Game, p.Champion).AddClass("linelist")))));
            result.Add(new P(new B("Triple kills:"), joinWithMore(20, games.Select(g => thisPlayer(g)).Where(p => p.LargestMultiKill == 3).Select(p => GetGameLink(p.Game, p.Champion).AddClass("linelist")))));
            result.Add(new P(new B("Perfect games:"), joinWithMore(20, games.Select(g => thisPlayer(g)).Where(p => p.Deaths == 0).Select(p => GetGameLink(p.Game, $"{p.Champion} {p.Kills}/{p.Assists}").AddClass("linelist")))));
            result.Add(new P(new B("#1 by damage:"), joinWithMore(20, games.Select(g => thisPlayer(g)).Where(p => p.RankOf(pp => pp.DamageToChampions) == 1).Select(p => GetGameLink(p.Game, p.Champion, " ", (p.DamageToChampions / 1000.0).ToString("0"), "k").AddClass("linelist")))));
            result.Add(new P(new B("#1 by kills:"), joinWithMore(20, games.Select(g => thisPlayer(g)).Where(p => p.RankOf(pp => pp.Kills) == 1).Select(p => GetGameLink(p.Game, p.Champion, " ", p.Kills).AddClass("linelist")))));
            result.Add(new P(new B("Outwarded entire enemy team:"), joinWithMore(20, games.Select(g => thisPlayer(g)).Where(p => p.WardsPlaced > p.Game.Enemy.Players.Sum(ep => ep.WardsPlaced)).Select(p => GetGameLink(p.Game, p.Champion).AddClass("linelist")))));
            if (!allGames)
                result.Add(new P(new B("Average wards per game: "), "ally = ", games.Average(g => g.Ally.Players.Sum(p => p.WardsPlaced)).ToString("0.0"), ", enemy = ", games.Average(g => g.Enemy.Players.Sum(p => p.WardsPlaced)).ToString("0.0")));

            var hlTotalKills = games.Where(g => g.Duration > TimeSpan.FromMinutes(5)).Select(g => (g, k: g.AllPlayers().Sum(p => p.Kills))).OrderByDescending(x => x.k).ToList();
            result.Add(new P(new B("Highest/lowest total kills:"), hlTotalKills.Take(10).Select(x => GetGameLink(x.g, x.k).AddClass("linelist")), new SPAN("...") { class_ = "linelist" }, hlTotalKills.TakeLast(10).Select(x => GetGameLink(x.g, x.k).AddClass("linelist"))));
            var hlKillsPM = games.Where(g => g.Duration > TimeSpan.FromMinutes(5)).Select(g => (g, kpm: g.AllPlayers().Sum(p => p.Kills) / g.Duration.TotalSeconds * 60)).OrderByDescending(x => x.kpm).ToList();
            result.Add(new P(new B("Highest/lowest kills per minute:"), hlKillsPM.Take(10).Select(x => GetGameLink(x.g, $"{x.kpm:0.0}").AddClass("linelist")), new SPAN("...") { class_ = "linelist" }, hlKillsPM.TakeLast(10).Select(x => GetGameLink(x.g, $"{x.kpm:0.0}").AddClass("linelist"))));
            result.Add(new P(new B("Highest kills difference:"), games.Select(g => (g, kdiff: Math.Abs(g.Ally.Players.Sum(p => p.Kills) - g.Enemy.Players.Sum(p => p.Kills)))).OrderByDescending(x => x.kdiff).Take(20).Select(x => GetGameLink(x.g, x.kdiff).AddClass("linelist"))));

            // Other humans
            var otherHumanGames =
                from h in OtherHumans
                let otherIds = h.SummonerIds.Select(i => i.AccountId).ToHashSet()
                let bothGames = games.Where(g => g.Ally.Players.Any(p => otherIds.Contains(p.AccountId))).ToList()
                select (h, bothGames);
            foreach (var (h, bothGames) in otherHumanGames.OrderByDescending(x => x.bothGames.Count))
            {
                var longestDays = bothGames.GroupBy(g => g.DateDayOnly(TimeZone)).OrderByDescending(grp => grp.Sum(g => g.Duration.TotalSeconds)).Take(9);
                if (longestDays.Count() == 0)
                    continue;
                result.Add(new P(new B($"Most games per day with {h.Name} ({h.SummonerNames.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key).JoinString(", ")}): "),
                    longestDays.Select(grp => GetGameLink(grp.MinElement(g => g.DateUtc), $"{grp.Key:yyyy-MM-dd}: {grp.Count()} games / {grp.Sum(g => g.Duration.TotalHours):0.0} hours").AddClass("linelist"))));
            }
            // Games with other humans on enemy team
            result.Add(new P(new B("Friends on enemy team:"), joinWithMore(20, games.Where(g => g.Enemy.Players.Any(p => KnownPlayersAccountIds.Contains(p.AccountId))).Select(g => GetGameLink(g, g.Enemy.Players.First(p => KnownPlayersAccountIds.Contains(p.AccountId)).Name).AddClass("linelist")))));

            if (!allGames)
            {
                var makeSummaryTable = Ut.Lambda((string label, IEnumerable<IGrouping<string, Player>> set) =>
                {
                    return new TABLE { class_ = "ra stats" }._(
                        new TR(new TH(label) { rowspan = 2 }, new TH("Games") { rowspan = 2 }, new TH("Wins") { rowspan = 2 }, new TH("Losses") { rowspan = 2 }, new TH("Win%") { rowspan = 2 },
                            new TH("Kills/deaths/assists/particip.") { colspan = 7 }, new TH("Dmg to champs") { colspan = 4 }, new TH("Healing") { colspan = 2 },
                            new TH("CS @ 10m") { colspan = 4 }, new TH("Gold @ 10m") { colspan = 2 }, new TH("Multikills every") { colspan = 4 }, new TH("Wards") { colspan = 4 },
                            new TH("Totals") { colspan = 6 }, new TH(label) { rowspan = 2 }),
                        new TR(
                            new TH("Avg/30m") { colspan = 3 }, new TH("Max") { colspan = 3 }, new TH("Part."), new TH("Avg/30m"), new TH("Max"), new TH("Rank"), new TH("#1 %"), new TH("Avg/30m"), new TH("Max"),
                            new TH("Avg"), new TH("Max"), new TH("Rank"), new TH("#1 %"), new TH("Avg"), new TH("Max"), new TH("5x"), new TH("4x+"), new TH("3x+"), new TH("2x+"), new TH("Avg/30m"), new TH("Max"), new TH("Rank"), new TH("#1 %"),
                            new TH("Hours"), new TH("K"), new TH("D"), new TH("A"), new TH("Ward"), new TH("Dmg")),
                        set.OrderByDescending(g => g.Count()).Select(g => new TR(
                            new TD(g.Key) { class_ = "la" },
                            new TD(g.Count()),
                            new TD(g.Count(p => p.Team.Victory)),
                            new TD(g.Count(p => !p.Team.Victory)),
                            new TD(g.Count() <= 5 ? "-" : (g.Count(p => p.Team.Victory) / (double) g.Count() * 100).ToString("0'%'")),
                            new TD("{0:0.0}".Fmt(g.Average(p => p.Kills / p.Game.Duration.TotalMinutes * 30))),
                            new TD("{0:0.0}".Fmt(g.Average(p => p.Deaths / p.Game.Duration.TotalMinutes * 30))),
                            new TD("{0:0.0}".Fmt(g.Average(p => p.Assists / p.Game.Duration.TotalMinutes * 30))),
                            new TD(GetGameLink(g.MaxElement(p => p.Kills), p => p.Kills.ToString("0"))),
                            new TD(GetGameLink(g.MaxElement(p => p.Deaths), p => p.Deaths.ToString("0"))),
                            new TD(GetGameLink(g.MaxElement(p => p.Assists), p => p.Assists.ToString("0"))),
                            new TD("{0:0}%".Fmt(g.Average(p => p.KillParticipation))),
                            new TD(g.Average(p => p.DamageToChampions / p.Game.Duration.TotalMinutes * 30).ToString("#,0")),
                            new TD(GetGameLink(g.MaxElement(p => p.DamageToChampions), p => p.DamageToChampions.ToString("#,0"))),
                            new TD("{0:0.0}".Fmt(g.Average(p => p.RankOf(pp => pp.DamageToChampions)))),
                            new TD("{0:0}%".Fmt(g.Count(p => p.RankOf(pp => pp.DamageToChampions) == 1) / (double) g.Count() * 100)),
                            new TD(g.Average(p => p.TotalHeal / p.Game.Duration.TotalMinutes * 30).ToString("#,0")),
                            new TD(GetGameLink(g.MaxElement(p => p.TotalHeal), p => p.TotalHeal.ToString("#,0"))),
                            new TD("{0:0}".Fmt(g.Average(p => p.CreepsAt10))),
                            new TD(GetGameLink(g.MaxElement(p => p.CreepsAt10), p => p.CreepsAt10.ToString("0"))),
                            new TD("{0:0.0}".Fmt(g.Average(p => p.RankOf(pp => pp.CreepsAt10)))),
                            new TD("{0:0}%".Fmt(g.Count(p => p.RankOf(pp => pp.CreepsAt10) == 1) / (double) g.Count() * 100)),
                            new TD("{0:0}".Fmt(g.Average(p => p.GoldAt10))),
                            new TD(GetGameLink(g.MaxElement(p => p.GoldAt10), p => p.GoldAt10.ToString("0"))),
                            new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 5))),
                            new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 4))),
                            new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 3))),
                            new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 2))),
                            new TD("{0:0.0}".Fmt(g.Average(p => p.WardsPlaced / p.Game.Duration.TotalMinutes * 30))),
                            new TD(GetGameLink(g.MaxElement(p => p.WardsPlaced), p => p.WardsPlaced.ToString("0"))),
                            new TD("{0:0.0}".Fmt(g.Average(p => p.RankOf(pp => pp.WardsPlaced)))),
                            new TD("{0:0}%".Fmt(g.Count(p => p.RankOf(pp => pp.WardsPlaced) == 1) / (double) g.Count() * 100)),
                            new TD($"{g.Sum(p => p.Game.Duration.TotalHours):#,0.0}"),
                            new TD($"{g.Sum(p => p.Kills):#,0}"),
                            new TD($"{g.Sum(p => p.Deaths):#,0}"),
                            new TD($"{g.Sum(p => p.Assists):#,0}"),
                            new TD($"{g.Sum(p => p.WardsPlaced):#,0}"),
                            new TD($"{g.Sum(p => p.DamageToChampions):#,0}"),
                            new TD(g.Key) { class_ = "la" }
                        ))
                    );
                });

                result.Add(new H4("By champion"));
                result.Add(makeSummaryTable("Champion", games.Select(g => thisPlayer(g)).GroupBy(p => p.Champion)));
                result.Add(new H4("By lane/role"));
                result.Add(makeSummaryTable("Lane/role", games.Select(g => thisPlayer(g)).GroupBy(p => (p.Lane == Lane.Top ? "Top" : p.Lane == Lane.Middle ? "Mid" : p.Lane == Lane.Jungle ? "JG" : "Bot") + (p.Role == Role.DuoCarry ? " adc" : p.Role == Role.DuoSupport ? " sup" : ""))));
                result.Add(new H4("Total"));
                result.Add(makeSummaryTable("Total", games.Select(g => thisPlayer(g)).GroupBy(p => "Total")));
            }

            // Strangers seen more than once
            if (allGames)
            {
                var strangers = games
                    .Where(g => g.Queue.Id != 0)
                    .SelectMany(g => g.Queue.IsPvp ? g.AllPlayers() : g.Ally.Players)
                    .Where(p => !KnownPlayersAccountIds.Contains(p.AccountId))
                    .GroupBy(p => p.AccountId)
                    .Select(g => (count: g.Count(), plr: g.First(), days: (g.Max(p => p.Game.DateUtc) - g.Min(p => p.Game.DateUtc)).TotalDays))
                    .Where(x => x.count > 1)
                    .OrderByDescending(x => x.count)
                    .ThenByDescending(x => x.days)
                    .ToList();
                result.Add(new P(new B($"Strangers seen 3+ times: "), joinWithMore(15, strangers.Where(s => s.count >= 3).Select(s => new SPAN(s.plr.Name, $" ({s.count} games over {s.days:0.0} days)") { title = s.plr.AccountId.ToString(), class_ = "linelist" }))));
                result.Add(new P(new B($"Strangers seen twice 1+ days apart: "), joinWithMore(15, strangers.Where(s => s.count == 2 && s.days >= 1).Select(s => new SPAN(s.plr.Name, $" ({s.days:0.0} days)") { title = s.plr.AccountId.ToString(), class_ = "linelist" }))));
                result.Add(new P(new B($"Strangers only seen twice within 24 hours: "), strangers.Count(s => s.count == 2 && s.days < 1)));
            }

            if (!allGames)
            {
                var allOtherChamps = games.SelectMany(g => g.AllPlayers().Where(p => !KnownPlayersAccountIds.Contains(p.AccountId))).GroupBy(p => p.ChampionId);
                result.Add(new P(new B("Champions by popularity: "), new SPAN("(excluding ours) ") { style = "color: #888;" }, new BR(),
                    allOtherChamps.OrderByDescending(grp => grp.Count()).Select(g => g.First().Champion + ": " + g.Count()).JoinString(", ")));
                int cutoff = Math.Min(30, games.Count() / 3);
                var otherChampStats =
                    (from grp in allOtherChamps
                     let total = grp.Count()
                     where total >= cutoff
                     select new
                     {
                         total,
                         champ = grp.First().Champion,
                         wins = grp.Count(p => p.Team.Victory) / (double) grp.Count() * 100,
                         avgDamage30 = grp.Average(p => p.DamageToChampions / p.Game.Duration.TotalMinutes * 30),
                         avgKills30 = grp.Average(p => p.Kills / p.Game.Duration.TotalMinutes * 30),
                         avgDeaths30 = grp.Average(p => p.Deaths / p.Game.Duration.TotalMinutes * 30),
                         supportCount = grp.Count(p => p.Role == Role.DuoSupport)
                     }).ToList();
                if (otherChampStats.Count >= 1)
                {
                    result.Add(new P(new B("Best/worst by winrate: "), new SPAN("(seen ", cutoff, "+ times, excluding ours) ") { style = "color: #888;" }, new BR(),
                        otherChampStats.Where(x => x.wins >= 55).OrderByDescending(x => x.wins).Select(x => "{0}: {1:0}%".Fmt(x.champ, x.wins)).JoinString(", "), " ... ",
                        otherChampStats.Where(x => x.wins <= 45).OrderByDescending(x => x.wins).Select(x => "{0}: {1:0}%".Fmt(x.champ, x.wins)).JoinString(", ")));
                    result.Add(new P(new B("Best/worst by average damage per 30 minutes: "), new SPAN("(seen ", cutoff, "+ times, excluding ours and supports) ") { style = "color: #888;" }, new BR(),
                        otherChampStats.OrderByDescending(x => x.avgDamage30).Take(6).Select(x => "{0}: {1:#,0}".Fmt(x.champ, x.avgDamage30)).JoinString(", "), " ... ",
                        otherChampStats.OrderByDescending(x => x.avgDamage30).Where(x => x.supportCount < x.total / 3).TakeLast(6).Select(x => "{0}: {1:#,0}".Fmt(x.champ, x.avgDamage30)).JoinString(", ")));
                    result.Add(new P(new B("Best/worst by average kills per 30 minutes: "), new SPAN("(seen ", cutoff, "+ times, excluding ours and supports) ") { style = "color: #888;" }, new BR(),
                        otherChampStats.OrderByDescending(x => x.avgKills30).Take(7).Select(x => "{0}: {1:0.0}".Fmt(x.champ, x.avgKills30)).JoinString(", "), " ... ",
                        otherChampStats.OrderByDescending(x => x.avgKills30).Where(x => x.supportCount < x.total / 3).TakeLast(7).Select(x => "{0}: {1:0.0}".Fmt(x.champ, x.avgKills30)).JoinString(", ")));
                    result.Add(new P(new B("Best/worst by average deaths per 30 minutes: "), new SPAN("(seen ", cutoff, "+ times, excluding ours) ") { style = "color: #888;" }, new BR(),
                        otherChampStats.OrderBy(x => x.avgDeaths30).Take(7).Select(x => "{0}: {1:0.0}".Fmt(x.champ, x.avgDeaths30)).JoinString(", "), " ... ",
                        otherChampStats.OrderBy(x => x.avgDeaths30).TakeLast(7).Select(x => "{0}: {1:0.0}".Fmt(x.champ, x.avgDeaths30)).JoinString(", ")));
                }
            }

            return result;
        }

        private double median(IEnumerable<double> values)
        {
            var ord = values.Order().ToList();
            if (ord.Count == 0)
                return 0;
            else if (ord.Count % 2 == 1)
                return ord[ord.Count / 2];
            else
                return (ord[ord.Count / 2 - 1] + ord[ord.Count / 2]) / 2;
        }

        private object joinWithMore(int max, IEnumerable<object> elements)
        {
            var result = new List<object>();
            List<object> moreContent = null;
            int count = 0;
            foreach (var el in elements)
            {
                count++;
                if (count == max + 1)
                    moreContent = new List<object>();
                (moreContent ?? result).Add(el);
            }
            if (moreContent != null)
            {
                var id = Rnd.GenerateString(6);
                result.Add(new A("more") { id = id + "more", href = "#", class_ = "linelist", onclick = $"document.getElementById('{id}').style.display = 'inline'; document.getElementById('{id}more').style.display = 'none'; return false;" });
                moreContent.Add(new A("less") { id = id + "less", href = "#", class_ = "linelist", onclick = $"document.getElementById('{id}').style.display = 'none'; document.getElementById('{id}more').style.display = 'inline'; return false;" });
                result.Add(new SPAN(moreContent) { id = id, style = "display: none" });
            }
            result.Add(new B($"{count} total") { class_ = "linelist" });
            return result;
        }

        private IEnumerable<(int count, Game firstGame)> mostWinsLosses(IEnumerable<Game> gamesE, int interval, bool wins)
        {
            var games = gamesE.ToArray();
            var consumed = new bool[games.Length];
            if (games.Length < interval)
                yield break;
            while (true)
            {
                int bestMax = -1;
                int bestMaxFrom = -1;
                int curMax = 0;
                int curMaxFrom = 0;
                for (int i = 0; i < games.Length; i++)
                {
                    if (consumed[i])
                    {
                        curMax = 0;
                        curMaxFrom = i + 1;
                        continue;
                    }
                    if (games[i].Victory == wins)
                        curMax++;
                    if (i - curMaxFrom >= interval)
                    {
                        if (games[curMaxFrom].Victory == wins)
                            curMax--;
                        curMaxFrom++;
                    }
                    if (i - curMaxFrom == interval - 1)
                    {
                        if (curMax > bestMax)
                        {
                            bestMax = curMax;
                            bestMaxFrom = curMaxFrom;
                        }
                    }
                }
                if (bestMax < 0)
                    yield break; // no more consecutive game regions of the desired length which weren't used for earlier results
#if DEBUG
                int check = 0;
                for (int i = bestMaxFrom; i < bestMaxFrom + interval; i++)
                {
                    Ut.Assert(!consumed[i]);
                    if (games[i].Victory == wins)
                        check++;
                }
                Ut.Assert(check == bestMax);
#endif
                yield return (count: bestMax, firstGame: games[bestMaxFrom]);
                for (int i = bestMaxFrom; i < bestMaxFrom + interval; i++)
                    consumed[i] = true;
            }
        }

        private IEnumerable<(int sinceLastAfk, Game gameWithAfk)> afkOrLeaverBetween(IEnumerable<Game> games, Func<Game, bool> check)
        {
            int sinceAfk = 0;
            foreach (var game in games)
            {
                if (check(game))
                {
                    yield return (sinceAfk, game);
                    sinceAfk = 0;
                }
                else
                    sinceAfk++;
            }
        }

        //private object makeCsPlotOverTime(string title, IEnumerable<Game> gamesUnfiltered, object playerId, Role role, Lane lane)
        //{
        //    var games = (from g in gamesUnfiltered
        //                 let thisPlayer = g.Plr(playerId)
        //                 let otherPlayer = g.OtherPlayers().FirstOrDefault(op => op.Role == role && op.Lane == lane)
        //                 where otherPlayer != null && thisPlayer.Role == role && thisPlayer.Lane == lane && g.Duration >= TimeSpan.FromMinutes(20) && otherPlayer.CreepsAt20 > 0
        //                 select new { game = g, thisPlayer, otherPlayer }).ToList();
        //    if (games.Count < 29)
        //        return null;
        //    return makePlotY(title,
        //        runningAverage(games.Select(g => (double) g.thisPlayer.CreepsAt20), 29),
        //        runningAverage(games.Select(g => g.thisPlayer.CreepsAt20 / (double) g.otherPlayer.CreepsAt20 * 100.0), 29),
        //        games.Select(_ => 100.0));
        //}

        private IEnumerable<double> average(IEnumerable<double> series, int n)
        {
            if (n > series.Count())
                n = series.Count() - 1;
            var window = new Queue<double>();
            foreach (var pt in series)
            {
                window.Enqueue(pt);
                while (window.Count > n)
                    window.Dequeue();
                //if ((window.Count & 1) == (n & 1))
                if (window.Count > n / 2)
                    yield return window.Average();
            }
            while (window.Count > 0)
            {
                window.Dequeue();
                //if ((window.Count & 1) == (n & 1))
                if (window.Count > n / 2)
                    yield return window.Average();
            }
        }

        private object makePlotXY(string title, params List<(double x, double y)>[] datas)
        {
            double width = 400;
            double height = 150;
            var sb = new StringBuilder();
            datas = datas.Where(d => d.Count > 0).ToArray();
            double maxX = datas.Max(data => data.Max(pt => pt.x));
            double maxY = datas.Max(data => data.Max(pt => pt.y));
            double chunk = Math.Ceiling(maxY / 4 / 5) * 5;
            double maxData = maxY;
            maxY = chunk * 4;
            var result = new StringBuilder();
            result.Append("<svg width='{0}' height='{1}' style='border: 1px solid #999; margin: 10px; background: #fff;' xmlns='http://www.w3.org/2000/svg'><g>".Fmt(width, height));
            result.Append("<text xml:space='preserve' text-anchor='middle' font-family='Open Sans, Arial, sans-serif' font-size='17' x='{0}' y='0' fill='#000' dominant-baseline='hanging'>{1}</text>".Fmt(width / 2, title));
            result.Append("<text xml:space='preserve' text-anchor='left' font-family='Open Sans, Arial, sans-serif' font-size='14' x='10' y='0' fill='#000' dominant-baseline='hanging'>max {0:0.#}</text>".Fmt(maxData));
            double calcX(double x) => x / maxX * (width - 20) + 10;
            double calcY(double y) => (maxY - y) / maxY * (height - 45) + 35;
            for (int i = 0; i < 5; i++)
            {
                var y = i * chunk;
                result.Append($"<polyline fill='none' stroke='#ddd' points='{calcX(0):0.000},{calcY(y):0.000} {calcX(maxX):0.000},{calcY(y):0.000}' />");
                result.Append($"<text xml:space='preserve' text-anchor='left' font-family='Open Sans, Arial, sans-serif' font-size='12' x='10' y='{calcY(y):0.000}' fill='#ccc' dominant-baseline='ideographic'>{y:0.#}</text>");
            }
            var colors = new[] { "#921", "#14f", "#1a2" }.ToQueue();
            foreach (var data in datas)
            {
                var color = colors.Dequeue();
                result.Append("<polyline fill='none' stroke='{0}' points='{1}' />".Fmt(color, data.Select(d => "{0:0.000},{1:0.000}".Fmt(calcX(d.x), calcY(d.y))).JoinString(" ")));
                colors.Enqueue(color);
            }
            result.Append("</g></svg>");
            return new RawTag(result.ToString());
        }

        private object makePlotY(string title, params IEnumerable<double>[] datas)
        {
            return makePlotXY(title, datas.Select(data => data.Select((p, i) => (x: (double) i, y: p)).ToList()).ToArray());
        }

        private object makeCsPlotOverTime(string title, IEnumerable<Game> games, Role role, Lane lane)
        {
            var first = games.Min(g => g.DateUtc);
            var plot = games
                .Select(g => (g: g, plr: thisPlayer(g)))
                .Where(x => x.plr.Role == role && x.plr.Lane == lane)
                .Select(x => (x: (x.g.DateUtc - first).TotalDays, y: (double) x.plr.CreepsAt20))
                .ToList();
            if (plot.Count == 0)
                return null;
            return makePlotXY(title, plot, average(plot.Select(p => p.y), 25).Zip(plot, (y, p) => (x: p.x, y: y)).ToList());
        }

        private IEnumerable<int> range(int first, int count, int modulus = int.MaxValue)
        {
            return Enumerable.Range(first, count).Select(i => i % modulus);
        }

        private object makeHistogram(IEnumerable<(string label, int y)> data, string title)
        {
            int x = 10;
            var sb = new StringBuilder();
            double maxY = data.Max(pt => pt.y);
            foreach (var pt in data)
            {
                double height = 100 * pt.y / maxY;
                sb.AppendFormat("<rect x='{0}' y='{1}' width='14' height='{2}' stroke-width=0 fill='#921'/>", x, 130 - height, height);
                sb.AppendFormat("<text xml:space='preserve' text-anchor='middle' font-family='Open Sans, Arial, sans-serif' font-size='15' x='{0}' y='145' fill='#000'>{1}</text>", x + 7, pt.label);
                x += 25;
            }
            return new RawTag("<svg width='{0}' height='150' style='border: 1px solid #999; margin: 10px; background: #fff;' xmlns='http://www.w3.org/2000/svg'><g>".Fmt(x)
                + "<text xml:space='preserve' text-anchor='middle' font-family='Open Sans, Arial, sans-serif' font-size='17' x='{0}' y='0' fill='#000' dominant-baseline='hanging'>{1}</text>".Fmt(x / 2, title)
                + sb.ToString()
                + "</g></svg>");
        }

        private object makeHistogram2(IEnumerable<double> boundaries, Func<double, double, int> getValue, string title)
        {
            var values = double.MinValue.Concat(boundaries).Concat(double.MaxValue).ConsecutivePairs(false).Select(p => getValue(p.Item1, p.Item2)).ToList();
            var labels = boundaries.Select(b => b.ToString("0")).ToList();

            int x = 10;
            var sb = new StringBuilder();
            double maxY = values.Max();
            for (int i = 0; i < values.Count; i++)
            {
                double height = 100 * values[i] / maxY;
                sb.AppendFormat("<rect x='{0}' y='{1}' width='14' height='{2}' stroke-width=0 fill='#921'/>", x, 130 - height, height);
                if (i < labels.Count)
                    sb.AppendFormat("<text xml:space='preserve' text-anchor='middle' font-family='Open Sans, Arial, sans-serif' font-size='15' x='{0}' y='145' fill='#000'>{1}</text>", x + 7 + 25 / 2, labels[i]);
                x += 25;
            }
            return new RawTag("<svg width='{0}' height='150' style='border: 1px solid #999; margin: 10px; background: #fff;' xmlns='http://www.w3.org/2000/svg'><g>".Fmt(x)
                + "<text xml:space='preserve' text-anchor='middle' font-family='Open Sans, Arial, sans-serif' font-size='17' x='{0}' y='0' fill='#000' dominant-baseline='hanging'>{1}</text>".Fmt(x / 2, title)
                + sb.ToString()
                + "</g></svg>");
        }

        private string fmtOrInf(double val)
        {
            if (double.IsPositiveInfinity(val))
                return "∞";
            else if (double.IsNegativeInfinity(val))
                return "–∞";
            else if (Math.Abs(val) < 10)
                return val.ToString("0.0");
            else
                return val.ToString("0");
        }

        private static Tag[] getPlayerHtml(Player plr)
        {
            return new[] {
                new TD{class_ = $"plr-top kp{plr.AccountId}" }._(new DIV(plr.Name) { class_="plrname" },
                    "{0}/{1}/{2}".Fmt(plr.Kills, plr.Deaths, plr.Assists)),
                new TD{class_ = $"plr-bot kp{plr.AccountId}" }._(
                    new DIV(plr.Leaver == true? "LVR" : plr.Afk == true ? "AFK" : plr.LargestMultiKill <= 1 ? "" : (plr.LargestMultiKill + "x")) { class_ = "multi multi" + (plr.Leaver == true || plr.Afk == true ? 5 : plr.LargestMultiKill) },
                    plr.Champion,
                    " ", plr.Lane == Lane.Top ? "(top)" : plr.Lane == Lane.Middle ? "(mid)" : plr.Lane == Lane.Jungle ? "(jg)" : plr.Role == Role.DuoCarry ? "(adc)" : plr.Role == Role.DuoSupport ? "(sup)" : "(bot)")
            };
        }

        private A GetGameLink(Game game, params object[] content)
        {
            return new A(content) { href = Path.GetFileName(GamesTableFilename) + "#game" + game.Id, class_ = (game.Victory ?? false) ? "win" : "loss" };
        }

        private A GetGameLink(Player player, Func<Player, object> getContent)
        {
            return GetGameLink(player.Game, getContent(player));
        }
    }
}
