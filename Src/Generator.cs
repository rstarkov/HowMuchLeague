using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RT.TagSoup;
using RT.Util;
using RT.Util.Dialogs;
using RT.Util.ExtensionMethods;

namespace LeagueGenMatchHistory
{
    class Generator
    {
        public SummonerInfo Summoner;
        public HumanInfo Human;
        private IList<Game> _games;

        public Generator(SummonerInfo summoner,  HumanInfo human)
        {
            Summoner = summoner;
            Human = human;
        }

        public Generator(HumanInfo human, IEnumerable<Generator> generators)
        {
            Summoner = null;
            Human = human;
            _games = generators
                .SelectMany(g => g._games)
                .GroupBy(g => g.Id).Select(grp => grp.First())
                .Where(g => g.Ally.Players.Any(p => human.SummonerNames.Contains(p.Name)))
                .ToList();
        }

        public void LoadGames()
        {
            if (Summoner == null)
                throw new Exception("Not supported for multi-account generators");
            Summoner.LoadGamesOnline(
                sm => InputBox.GetLine($"Please enter Authorization header value for {sm.Region}/{sm.Name}:", sm.AuthorizationHeader, "League Gen Match History"),
                str => Console.WriteLine(str)
            );
            _games = Summoner.Games;
        }

        private List<IGrouping<string, Game>> getGameTypeSections(int limit)
        {
            return _games
                .OrderByDescending(g => g.DateUtc)
                .Take(limit)
                .GroupBy(g => g.Map + ", " + g.Type)
                .OrderByDescending(g => g.Count())
                .ToList();
        }

        private string GetGamesTableFilename()
        {
            return Summoner == null
                ? Program.Settings.OutputPathTemplate.Fmt("Games-All", Human.Name, "")
                : Program.Settings.OutputPathTemplate.Fmt("Games-" + Summoner.Region, Summoner.Name, "");
        }

        public void ProduceGamesTable()
        {
            var outputFile = GetGamesTableFilename();
            Console.Write("Producing output file: " + outputFile + " ... ");
            var gameTypeSections = getGameTypeSections(999999);
            var sections = gameTypeSections.Select(grp => Ut.NewArray<object>(
                    new H1(grp.Key) { id = new string(grp.Key.Where(c => char.IsLetterOrDigit(c)).ToArray()) },
                    new H4("All games"),
                    new TABLE(
                        grp.OrderByDescending(g => g.DateUtc).Select(g =>
                        {
                            var allies = g.Ally.Players.Select(plr => getPlayerHtml(plr)).ToList();
                            var enemies = g.Enemy.Players.Select(plr => getPlayerHtml(plr)).ToList();
                            return Ut.NewArray<object>(
                                new TR { id = "game" + g.Id.ToString() }._(
                                    new TD { rowspan = 2, class_ = "nplr datetime" }._(new A(g.Date(Human.TimeZone).ToString("dd/MM/yy"), new BR(), g.Date(Human.TimeZone).ToString("HH:mm")) { href = g.DetailsUrl }),
                                    new TD { rowspan = 2, class_ = "nplr" }._(minsec(g.Duration)),
                                    new TD { rowspan = 2, class_ = "nplr " + NullTrueFalse(g.Victory, "draw", "victory", "defeat") }._(NullTrueFalse(g.Victory, "Draw", "Victory", "Defeat")),
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

        public void ProduceStats(int limit = 999999)
        {
            var outputFile = Summoner == null
                ? Program.Settings.OutputPathTemplate.Fmt("All", Human.Name, limit == 999999 ? "" : ("-" + limit))
                : Program.Settings.OutputPathTemplate.Fmt(Summoner.Region, Summoner.Name, limit == 999999 ? "" : ("-" + limit));
            Console.Write("Producing output file: " + outputFile + " ... ");
            var gameTypeSections = getGameTypeSections(limit);
            var sections = gameTypeSections.Select(grp => Ut.NewArray<object>(
                    new H1(grp.Key) { id = new string(grp.Key.Where(c => char.IsLetterOrDigit(c)).ToArray()) },
                    genOverallStats(grp),
                    (
                        from human in Program.Settings.Humans
                        let gamesWithThisHuman = grp.Where(g => g.Ally.Players.Any(pp => human.SummonerNames.Contains(pp.Name)))
                        let count = gamesWithThisHuman.Count()
                        where count > 10
                        orderby count descending
                        select genStats(human, gamesWithThisHuman)
                    ),
                    new BR(),
                    (
                        from pname in (Program.AllKnownPlayers)
                        let gamesWithThisPlayer = grp.Where(g => g.Ally.Players.Any(pp => pp.Name == pname))
                        let count = gamesWithThisPlayer.Count()
                        where count > 10
                        orderby count descending
                        select genStats(pname, gamesWithThisPlayer)
                    )
                ));
            var result = Ut.NewArray(
                genAllGameStats(_games, Human),
                getContents(gameTypeSections),
                sections
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
                .hspace { margin-right: 25px; }
                .linelist { margin-left: 8px; }
                .linelist:before { content: '\200B'; }\r\n";
            css += Program.AllKnownPlayers.Select(plr => "td.kp" + plr.Replace(" ", "") + (Human.SummonerNames.Contains(plr) ? " { background: #D1FECC; }\r\n" : " { background: #6EFFFF; }\r\n")).JoinString();
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

        private object genOverallStats(IEnumerable<Game> games)
        {
            var result = new List<object>();
            var allOtherChamps = games.SelectMany(g => g.OtherPlayers()).GroupBy(p => p.ChampionId);
            result.Add(new P(new B("Champions by popularity: "), "(excluding ours) ", allOtherChamps.OrderByDescending(grp => grp.Count()).Select(g => g.First().Champion + ": " + g.Count()).JoinString(", ")));
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
            return result;
        }

        private object genStats(object playerId, IEnumerable<Game> games)
        {
            var result = new List<object>();
            var makeSummaryTable = Ut.Lambda((string label, IEnumerable<IGrouping<string, Player>> set) =>
            {
                return new TABLE { class_ = "ra stats" }._(
                    new TR(new TH(label) { rowspan = 2 }, new TH("Games") { rowspan = 2 }, new TH("Wins") { rowspan = 2 }, new TH("Losses") { rowspan = 2 }, new TH("Win%") { rowspan = 2 },
                        new TH("Kills/deaths/assists/particip.") { colspan = 7 }, new TH("Dmg to champs") { colspan = 4 }, new TH("Healing") { colspan = 2 },
                        new TH("CS @ 10m") { colspan = 4 }, new TH("Gold @ 10m") { colspan = 2 }, new TH("Multikills every") { colspan = 4 }, new TH("Wards") { colspan = 4 }),
                    new TR(
                        new TH("Avg/30m") { colspan = 3 }, new TH("Max") { colspan = 3 }, new TH("Part."), new TH("Avg/30m"), new TH("Max"), new TH("Rank"), new TH("#1 %"), new TH("Avg/30m"), new TH("Max"),
                        new TH("Avg"), new TH("Max"), new TH("Rank"), new TH("#1 %"), new TH("Avg"), new TH("Max"), new TH("5x"), new TH("4x+"), new TH("3x+"), new TH("2x+"), new TH("Avg/30m"), new TH("Max"), new TH("Rank"), new TH("#1 %")),
                    set.OrderByDescending(g => g.Count()).Select(g => new TR(
                        new TD(g.Key) { class_ = "la" },
                        new TD(g.Count()),
                        new TD(g.Count(p => p.Team.Victory)),
                        new TD(g.Count(p => !p.Team.Victory)),
                        new TD(g.Count() <= 5 ? "-" : (g.Count(p => p.Team.Victory) / (double) g.Count() * 100).ToString("0'%'")),
                        new TD("{0:0.0}".Fmt(g.Average(p => p.Kills / p.Game.Duration.TotalMinutes * 30))),
                        new TD("{0:0.0}".Fmt(g.Average(p => p.Deaths / p.Game.Duration.TotalMinutes * 30))),
                        new TD("{0:0.0}".Fmt(g.Average(p => p.Assists / p.Game.Duration.TotalMinutes * 30))),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.Kills), p => p.Kills.ToString("0"))),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.Deaths), p => p.Deaths.ToString("0"))),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.Assists), p => p.Assists.ToString("0"))),
                        new TD("{0:0}%".Fmt(g.Average(p => p.KillParticipation))),
                        new TD(g.Average(p => p.DamageToChampions / p.Game.Duration.TotalMinutes * 30).ToString("#,0")),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.DamageToChampions), p => p.DamageToChampions.ToString("#,0"))),
                        new TD("{0:0.0}".Fmt(g.Average(p => p.RankOf(pp => pp.DamageToChampions)))),
                        new TD("{0:0}%".Fmt(g.Count(p => p.RankOf(pp => pp.DamageToChampions) == 1) / (double) g.Count() * 100)),
                        new TD(g.Average(p => p.TotalHeal / p.Game.Duration.TotalMinutes * 30).ToString("#,0")),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.TotalHeal), p => p.TotalHeal.ToString("#,0"))),
                        new TD("{0:0}".Fmt(g.Average(p => p.CreepsAt10))),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.CreepsAt10), p => p.CreepsAt10.ToString("0"))),
                        new TD("{0:0.0}".Fmt(g.Average(p => p.RankOf(pp => pp.CreepsAt10)))),
                        new TD("{0:0}%".Fmt(g.Count(p => p.RankOf(pp => pp.CreepsAt10) == 1) / (double) g.Count() * 100)),
                        new TD("{0:0}".Fmt(g.Average(p => p.GoldAt10))),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.GoldAt10), p => p.GoldAt10.ToString("0"))),
                        new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 5))),
                        new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 4))),
                        new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 3))),
                        new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 2))),
                        new TD("{0:0.0}".Fmt(g.Average(p => p.WardsPlaced / p.Game.Duration.TotalMinutes * 30))),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.WardsPlaced), p => p.WardsPlaced.ToString("0"))),
                        new TD("{0:0.0}".Fmt(g.Average(p => p.RankOf(pp => pp.WardsPlaced)))),
                        new TD("{0:0}%".Fmt(g.Count(p => p.RankOf(pp => pp.WardsPlaced) == 1) / (double) g.Count() * 100))
                    ))
                );
            });

            result.Add(genAllGameStats(games, playerId));

            result.Add(new P(new B("Average wards per game: "), "ally = ", games.Average(g => g.Ally.Players.Sum(p => p.WardsPlaced)).ToString("0.0"), ", enemy = ", games.Average(g => g.Enemy.Players.Sum(p => p.WardsPlaced)).ToString("0.0")));

            result.Add(new P(new B("Penta kills:"), games.Select(g => g.Plr(playerId)).Where(p => p.LargestMultiKill == 5).Select(p => new A(p.Champion) { href = GetGameLink(p.Game), class_ = "linelist" })));
            result.Add(new P(new B("Quadra kills:"), games.Select(g => g.Plr(playerId)).Where(p => p.LargestMultiKill == 4).Select(p => new A(p.Champion) { href = GetGameLink(p.Game), class_ = "linelist" })));
            result.Add(new P(new B("Triple kills:"), games.Select(g => g.Plr(playerId)).Where(p => p.LargestMultiKill == 3).Select(p => new A(p.Champion) { href = GetGameLink(p.Game), class_ = "linelist" })));
            result.Add(new P(new B("Outwarded entire enemy team:"), games.Select(g => g.Plr(playerId)).Where(p => p.WardsPlaced >= p.Game.Enemy.Players.Sum(ep => ep.WardsPlaced)).Select(p => new A(p.Champion) { href = GetGameLink(p.Game), class_ = "linelist" })));
            result.Add(new P(new B("#1 by damage:"), games.Select(g => g.Plr(playerId)).Where(p => p.RankOf(pp => pp.DamageToChampions) == 1).Select(p => new A(p.Champion, " ", (p.DamageToChampions / 1000.0).ToString("0"), "k") { href = GetGameLink(p.Game), class_ = "linelist" })));
            result.Add(new P(new B("#1 by kills:"), games.Select(g => g.Plr(playerId)).Where(p => p.RankOf(pp => pp.Kills) == 1).Select(p => new A(p.Champion, " ", p.Kills) { href = GetGameLink(p.Game), class_ = "linelist" })));
            var byLastWinLoss = games.Where(g => g.Victory != null).GroupBy(g => g.DateDayOnly(Human.TimeZone)).Select(grp => grp.OrderBy(itm => itm.DateUtc).Last().Victory.Value);
            result.Add(new P(new B("Last game of the day: "), "victory: {0:0}%, defeat: {1:0}%".Fmt(
                byLastWinLoss.Count(v => v) / (double) byLastWinLoss.Count() * 100,
                byLastWinLoss.Count(v => !v) / (double) byLastWinLoss.Count() * 100
            )));
            result.Add(new P(new B("Longest win streaks: "), games.GroupConsecutiveBy(g => g.Victory == true).Where(grp => grp.Key).Select(grp => grp.Count).OrderByDescending(c => c).Take(3).JoinString(", ")));
            result.Add(new P(new B("Longest loss streaks: "), games.GroupConsecutiveBy(g => g.Victory == false).Where(grp => grp.Key).Select(grp => grp.Count).OrderByDescending(c => c).Take(3).JoinString(", ")));

            var name = playerId is HumanInfo ? (playerId as HumanInfo).Name : (string) playerId;
            result.Add(new H4("{0} stats: by champion".Fmt(name)));
            result.Add(makeSummaryTable("Champion", games.Select(g => g.Plr(playerId)).GroupBy(p => p.Champion)));
            result.Add(new H4("{0} stats: by lane/role".Fmt(name)));
            result.Add(makeSummaryTable("Lane/role", games.Select(g => g.Plr(playerId)).GroupBy(p => (p.Lane == Lane.Top ? "Top" : p.Lane == Lane.Middle ? "Mid" : p.Lane == Lane.Jungle ? "JG" : "Bot") + (p.Role == Role.DuoCarry ? " adc" : p.Role == Role.DuoSupport ? " sup" : ""))));
            result.Add(new H4("{0} stats: total".Fmt(name)));
            result.Add(makeSummaryTable("Total", games.Select(g => g.Plr(playerId)).GroupBy(p => "Total")));
            var id = Rnd.NextBytes(8).ToHex();
            return Ut.NewArray<object>(new BUTTON("Show/hide stats for {0}".Fmt(name)) { onclick = "document.getElementById('{0}').style.display = (document.getElementById('{0}').style.display == 'none') ? 'block' : 'none';".Fmt(id) }, new DIV(result) { id = id, style = "display:none" });
        }

        private object genAllGameStats(IEnumerable<Game> games, object playerId)
        {
            var result = new List<object>();
            var histoTimeOfDay = range(5, 24, 24).Select(h => Tuple.Create(h.ToString("00"), games.Count(g => g.Date(Human.TimeZone).TimeOfDay.Hours == h)));
            result.Add(makeHistogram(histoTimeOfDay, "Games by time of day"));
            var gamesByDay = games.GroupBy(g => g.DateDayOnly(Human.TimeZone)).OrderBy(g => g.Key).ToList();
            var firstDay = gamesByDay[0].Key;
            var dates = gamesByDay.Select(g => g.Key).ToList();
            var histoGamesPerDay = Enumerable.Range(1, 12).Select(c => Tuple.Create(c == 12 ? "12+" : c.ToString(), gamesByDay.Count(grp => grp.Count() == c))).ToList();
            result.Add(makeHistogram(histoGamesPerDay, "Games played per day"));
            var histoGamesByDayOfWeek = range(1, 7, 7).Select(dow => Tuple.Create(((DayOfWeek) dow).ToString().Substring(0, 2), games.Count(g => (int) g.Date(Human.TimeZone).DayOfWeek == dow))).ToList();
            result.Add(makeHistogram(histoGamesByDayOfWeek, "Games played on ..."));
            var histoGamesByDayOfWeek2 = range(1, 7, 7).Select(dow => Tuple.Create(((DayOfWeek) dow).ToString().Substring(0, 2), gamesByDay.Count(g => (int) g.Key.DayOfWeek == dow))).ToList();
            result.Add(makeHistogram(histoGamesByDayOfWeek2, "Days with 1+ games"));
            result.Add(makeHistogram2(new double[] { 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70 }, (durMin, durMax) => games.Count(g => g.Duration.TotalMinutes > durMin && g.Duration.TotalMinutes <= durMax), "Games by length, minutes"));
            result.Add(makePlotXY("Distinct champs played", dates.Select(d => Tuple.Create((d - firstDay).TotalDays, (double) games.Where(g => g.DateDayOnly(Human.TimeZone) <= d).Select(g => g.Plr(playerId).Champion).Distinct().Count())).ToList()));
            var plotWardProgress = games.Select(g => g.Enemy.Players.Sum(p => p.WardsPlaced / g.Duration.TotalMinutes * 30.0)).ToList();
            plotWardProgress.Reverse();
            result.Add(makePlotY("Wards over time by enemy team", plotWardProgress, runningAverage(plotWardProgress, 29).ToList()));
            var plotGameDurationProgress = games.Select(g => g.Duration.TotalMinutes).ToList();
            plotGameDurationProgress.Reverse();
            result.Add(makePlotY("Game duration over time", plotGameDurationProgress, runningAverage(plotGameDurationProgress, 49).ToList()));
            //result.Add(makeCsPlotOverTime("CS@20m as ADC over time", games, playerId, Role.DuoCarry, Lane.Bottom));
            //result.Add(makeCsPlotOverTime("CS@20m as Mid over time", games, playerId, Role.Solo, Lane.Middle));
            //result.Add(makeCsPlotOverTime("CS@20m as Top over time", games, playerId, Role.Solo, Lane.Top));

            result.Add(new P(
                new B("Total games: "), games.Count().ToString("#,0"), new SPAN { class_ = "hspace" },
                new B("Total duration: "), games.Sum(g => g.Duration.TotalHours).ToString("#,0 hours"), new SPAN { class_ = "hspace" },
                new B("Avg per day: "), (games.Sum(g => g.Duration.TotalHours) / (games.Max(g => g.DateUtc) - games.Min(g => g.DateUtc)).TotalDays).ToString("0.0 hours")
            ));
            result.Add(new P(new B("Longest and shortest:"),
                games.OrderByDescending(g => g.Duration).Take(7).Select(g => new object[] { new A(minsec(g.Duration)) { href = GetGameLink(g), class_ = "linelist" }, new SUP(g.MicroType) }), new SPAN("...") { class_ = "linelist" },
                games.OrderByDescending(g => g.Duration).TakeLast(7).Select(g => new object[] { new A(minsec(g.Duration)) { href = GetGameLink(g), class_ = "linelist" }, new SUP(g.MicroType) })));
            result.Add(new P(new B("Played 10+ times: "),
                (from champ in Program.Champions.Values let c = games.Count(g => g.Plr(playerId).Champion == champ) where c >= 10 orderby c descending select "{0}: {1:#,0}".Fmt(champ, c)).JoinString(", ")));
            result.Add(new P(new B("Played 3-9 times: "),
                (from champ in Program.Champions.Values let c = games.Count(g => g.Plr(playerId).Champion == champ) where c >= 3 && c <= 9 orderby c descending select "{0}: {1:#,0}".Fmt(champ, c)).JoinString(", ")));
            result.Add(new P(new B("Played 1-2 times: "), Program.Champions.Values.Where(champ => { int c = games.Count(g => g.Plr(playerId).Champion == champ); return c >= 1 && c <= 2; }).Order().JoinString(", ")));
            result.Add(new P(new B("Never played: "), Program.Champions.Values.Where(champ => !games.Any(g => g.Plr(playerId).Champion == champ)).Order().JoinString(", ")));

            return result;
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

        private IEnumerable<double> runningAverage(IEnumerable<double> series, int n)
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

        private object makePlotXY(string title, params List<Tuple<double, double>>[] datas)
        {
            double width = 400;
            double height = 150;
            var sb = new StringBuilder();
            double maxX = datas.Max(data => data.Max(pt => pt.Item1));
            double maxY = datas.Max(data => data.Max(pt => pt.Item2));
            var result = new StringBuilder();
            result.Append("<svg width='{0}' height='{1}' style='border: 1px solid #999; margin: 10px; background: #fff;' xmlns='http://www.w3.org/2000/svg'><g>".Fmt(width, height));
            result.Append("<text xml:space='preserve' text-anchor='middle' font-family='Open Sans, Arial, sans-serif' font-size='17' x='{0}' y='0' fill='#000' dominant-baseline='hanging'>{1}</text>".Fmt(width / 2, title));
            result.Append("<text xml:space='preserve' text-anchor='left' font-family='Open Sans, Arial, sans-serif' font-size='17' x='10' y='20' fill='#000' dominant-baseline='hanging'>{0:0.#}</text>".Fmt(maxY));
            var colors = new[] { "#921", "#14f", "#1a2" }.ToQueue();
            foreach (var data in datas)
            {
                var color = colors.Dequeue();
                result.Append("<polyline fill='none' stroke='{0}' points='{1}' />".Fmt(color, data.Select(d => "{0:0.000},{1:0.000}".Fmt(d.Item1 / maxX * (width - 20) + 10, (maxY - d.Item2) / maxY * (height - 40) + 30)).JoinString(" ")));
                colors.Enqueue(color);
            }
            result.Append("</g></svg>");
            return new RawTag(result.ToString());
        }

        private object makePlotY(string title, params IEnumerable<double>[] datas)
        {
            return makePlotXY(title, datas.Select(data => data.Select((p, i) => Tuple.Create((double) i, p)).ToList()).ToArray());
        }

        private IEnumerable<int> range(int first, int count, int modulus = int.MaxValue)
        {
            return Enumerable.Range(first, count).Select(i => i % modulus);
        }

        private object makeHistogram(IEnumerable<Tuple<string, int>> data, string title)
        {
            int x = 10;
            var sb = new StringBuilder();
            double maxY = data.Max(pt => pt.Item2);
            foreach (var pt in data)
            {
                double height = 100 * pt.Item2 / maxY;
                sb.AppendFormat("<rect x='{0}' y='{1}' width='14' height='{2}' stroke-width=0 fill='#921'/>", x, 130 - height, height);
                sb.AppendFormat("<text xml:space='preserve' text-anchor='middle' font-family='Open Sans, Arial, sans-serif' font-size='15' x='{0}' y='145' fill='#000'>{1}</text>", x + 7, pt.Item1);
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

        private object getGameValueAndLink(Player linkTo, Func<Player, string> text)
        {
            return new A(text(linkTo)) { href = GetGameLink(linkTo.Game) };
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
                new TD{class_="plr-top kp"+plr.Name.Replace(" ", "")}._(new DIV(plr.Name) { class_="plrname" },
                    "{0}/{1}/{2}".Fmt(plr.Kills, plr.Deaths, plr.Assists)),
                new TD{class_="plr-bot kp"+plr.Name.Replace(" ", "")}._(
                    new DIV(plr.LargestMultiKill <= 1 ? "" : (plr.LargestMultiKill + "x")) { class_ = "multi multi" + plr.LargestMultiKill },
                    plr.Champion,
                    " ", plr.Lane == Lane.Top ? "(top)" : plr.Lane == Lane.Middle ? "(mid)" : plr.Lane == Lane.Jungle ? "(jg)" : plr.Role == Role.DuoCarry ? "(adc)" : plr.Role == Role.DuoSupport ? "(sup)" : "(bot)")
            };
        }

        public string GetGameLink(Game game)
        {
            return Path.GetFileName(GetGamesTableFilename()) + "#game" + game.Id;
        }
    }
}
