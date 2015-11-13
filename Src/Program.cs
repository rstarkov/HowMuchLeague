using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using RT.TagSoup;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;

// Assumptions:
// - a human may have accounts with identical names in several regions (but in this case some stats will be grouped together - fixable if this is ever a concern)
// - no other people have accounts with the same names in _any_ region as any defined humans
// - the mapping is true, so no games can contain more than one of the multiple accounts belonging to the same human

namespace LeagueGenMatchHistory
{
    class Program
    {
        public static Settings Settings;
        public static Dictionary<int, string> Champions = new Dictionary<int, string>();
        public static HashSet<string> AllKnownPlayers;

        static void Main(string[] args)
        {
            SettingsUtil.LoadSettings(out Settings);
            Settings.KnownPlayers.RemoveWhere(name => Settings.Humans.Any(h => h.SummonerNames.Contains(name)));
            Settings.Save();
            Directory.CreateDirectory(Path.Combine(Settings.MatchHistoryPath, "json"));

            AllKnownPlayers = Settings.KnownPlayers.Concat(Settings.Humans.SelectMany(h => h.SummonerNames)).ToHashSet();
            foreach (var sm in Settings.Summoners)
                sm.Human = Settings.Humans.Single(h => h.SummonerNames.Contains(sm.Name));
            var generators = Settings.Summoners.ToDictionary(sm => sm, sm => new Generator(sm));

            // Load champion id to name map
            var champs = JsonDict.Parse(File.ReadAllText(Path.Combine(Settings.MatchHistoryPath, "champions.json")));
            foreach (var kvp in champs["data"].GetDict())
                Champions[kvp.Value["key"].GetIntLenient()] = kvp.Value["name"].GetString();

            // Load known game IDs by querying Riot
#if !DEBUG
            Console.WriteLine("Querying Riot...");
            foreach (var gen in generators.Values)
            {
                if (gen.Summoner.AuthorizationHeader == "")
                    continue;
                gen.DiscoverGameIds(false);
                Settings.Save();
            }
#endif

            foreach (var gen in generators.Values)
            {
                gen.LoadGames();
                gen.ProduceOutput();
                gen.ProduceOutput(200);
            }
            foreach (var human in Settings.Humans)
            {
                var gen = new Generator(human, generators.Values);
                gen.ProduceOutput();
                gen.ProduceOutput(200);
            }
        }
    }

    class Generator
    {
        public SummonerInfo Summoner;
        public HumanInfo Human;
        private List<Game> _games = new List<Game>();
        private HClient _hc;

        public Generator(SummonerInfo summoner)
        {
            Summoner = summoner;
            Human = summoner.Human;

            _hc = new HClient();
            _hc.ReqAccept = "application/json, text/javascript, */*; q=0.01";
            _hc.ReqAcceptLanguage = "en-GB,en;q=0.5";
            _hc.ReqUserAgent = "Mozilla/5.0 (Windows NT 6.1; WOW64; rv:40.0) Gecko/20100101 Firefox/40.0";
            _hc.ReqReferer = "http://matchhistory.{0}.leagueoflegends.com/en/".Fmt(Summoner.Region.ToLower());
            _hc.ReqHeaders[HttpRequestHeader.Host] = "acs.leagueoflegends.com";
            _hc.ReqHeaders["DNT"] = "1";
            _hc.ReqHeaders["Region"] = Summoner.Region.ToUpper();
            _hc.ReqHeaders["Authorization"] = Summoner.AuthorizationHeader;
            _hc.ReqHeaders["Origin"] = "http://matchhistory.{0}.leagueoflegends.com".Fmt(Summoner.Region.ToLower());
        }

        public Generator(HumanInfo human, IEnumerable<Generator> generators)
        {
            Summoner = null;
            Human = human;
            _hc = null;
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
            foreach (var kvp in Summoner.GamesAndReplays)
            {
                var json = LoadGameJson(kvp.Key);
                if (json != null)
                    _games.Add(new Game(json, Summoner, kvp.Value));
            }
            _games.RemoveAll(g => g.Type == "Custom");
        }

        public JsonDict LoadGameJson(string gameId)
        {
            if (Summoner == null)
                throw new Exception("Not supported for multi-account generators");
            var fullHistoryUrl = "https://acs.leagueoflegends.com/v1/stats/game/{0}/{1}?visiblePlatformId={0}&visibleAccountId={2}".Fmt(Summoner.RegionFull, gameId, Summoner.AccountId);
            var path = Path.Combine(Program.Settings.MatchHistoryPath, "json", Summoner.RegionFull.ToLower() + "-" + Summoner.AccountId, fullHistoryUrl.FilenameCharactersEscape());
            if (File.Exists(path))
                Console.WriteLine("Loading cached " + fullHistoryUrl + " ...");
            else
            {
                Console.WriteLine("Retrieving " + fullHistoryUrl + " ...");
                var resp = _hc.Get(fullHistoryUrl);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    File.WriteAllText(path, "404");
                else
                {
                    var data = resp.Expect(HttpStatusCode.OK).DataString;
                    var tryJson = JsonDict.Parse(data);
                    Ut.Assert(tryJson["participantIdentities"].GetList().Any(l => l["player"]["summonerName"].GetString() == Summoner.Name)); // a bit redundant, but makes sure we don't save this if something went wrong
                    File.WriteAllText(path, data);
                }
            }
            var json = File.ReadAllText(path);
            var rawJson = json == "404" ? null : JsonDict.Parse(json);
            if (rawJson == null)
                return null;
            Ut.Assert(rawJson["participantIdentities"].GetList().Any(l => l["player"]["summonerName"].GetString() == Summoner.Name));
            return rawJson;
        }

        public void ProduceOutput(int limit = 999999)
        {
            var outputFile = Summoner == null
                ? Program.Settings.OutputPathTemplate.Fmt("All", Human.Name, limit == 999999 ? "" : ("-" + limit))
                : Program.Settings.OutputPathTemplate.Fmt(Summoner.Region, Summoner.Name, limit == 999999 ? "" : ("-" + limit));
            Console.Write("Producing output file: " + outputFile + " ... ");
            var gameTypeSections = _games
                .OrderByDescending(g => g.DateUtc)
                .Take(limit)
                .GroupBy(g => g.Map + ", " + g.Type)
                .OrderByDescending(g => g.Count())
                .ToList();
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
                    ),
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
                                    new TD { rowspan = 2, class_ = "nplr " + g.Victory.NullTrueFalse("draw", "victory", "defeat") }._(g.Victory.NullTrueFalse("Draw", "Victory", "Defeat")),
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
                genAllGameStats(_games, Human),
                new DIV { style = "text-align: center;" }._(new P { style = "text-align: left; display: inline-block;" }._(
                    gameTypeSections.Select(grp => Ut.NewArray<object>(
                        new A(grp.Key) { href = "#" + new string(grp.Key.Where(c => char.IsLetterOrDigit(c)).ToArray()) },
                        " : " + grp.Count().ToString() + " games",
                        new BR()
                    ))
                )),
                sections
            );
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
                .linelist { margin-left: 8px; }
                .linelist:before { content: '\200B'; }\r\n";
            css += Program.AllKnownPlayers.Select(plr => "td.kp" + plr.Replace(" ", "") + (Human.SummonerNames.Contains(plr) ? " { background: #D1FECC; }\r\n" : " { background: #6EFFFF; }\r\n")).JoinString();

            Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
            File.WriteAllText(outputFile, new HTML(new HEAD(new META { charset = "utf-8" }, new STYLELiteral(css)), new BODY(result)).ToString());
            Console.WriteLine("done");
        }

        private string minsec(TimeSpan time)
        {
            return ((int) time.TotalMinutes).ToString("00") + ":" + time.Seconds.ToString("00");
        }

        private object genOverallStats(IEnumerable<Game> games)
        {
            var result = new List<object>();
            var allOtherPlayers = games.SelectMany(g => g.Ally.Players.Concat(g.Enemy.Players)).Where(p => !Program.AllKnownPlayers.Contains(p.Name));
            var allOtherChamps = allOtherPlayers.GroupBy(p => p.ChampionId);
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
                     wins = grp.Count(p => p.Victory) / (double) grp.Count() * 100,
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
                        new TH("Kills/deaths/assists") { colspan = 6 }, new TH("Dmg to champs") { colspan = 4 }, new TH("Healing") { colspan = 2 },
                        new TH("CS @ 10m") { colspan = 2 }, new TH("Gold @ 10m") { colspan = 2 }, new TH("Multikills every") { colspan = 4 }, new TH("Wards") { colspan = 4 }),
                    new TR(
                        new TH("Avg/30m") { colspan = 3 }, new TH("Max") { colspan = 3 }, new TH("Avg/30m"), new TH("Max"), new TH("Rank"), new TH("#1 %"), new TH("Avg/30m"), new TH("Max"),
                        new TH("Avg"), new TH("Max"), new TH("Avg"), new TH("Max"), new TH("5x"), new TH("4x+"), new TH("3x+"), new TH("2x+"), new TH("Avg/30m"), new TH("Max"), new TH("Rank"), new TH("#1 %")),
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
                        new TD(g.Average(p => p.DamageToChampions / p.Game.Duration.TotalMinutes * 30).ToString("#,0")),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.DamageToChampions), p => p.DamageToChampions.ToString("#,0"))),
                        new TD("{0:0.0}".Fmt(g.Average(p => p.RankOf(pp => pp.DamageToChampions)))),
                        new TD("{0:0}%".Fmt(g.Count(p => p.RankOf(pp => pp.DamageToChampions) == 1) / (double) g.Count() * 100)),
                        new TD(g.Average(p => p.TotalHeal / p.Game.Duration.TotalMinutes * 30).ToString("#,0")),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.TotalHeal), p => p.TotalHeal.ToString("#,0"))),
                        new TD("{0:0}".Fmt(g.Average(p => p.CreepsAt10))),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.CreepsAt10), p => p.CreepsAt10.ToString("0"))),
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

            result.Add(new P(new B("Penta kills:"), games.Select(g => g.Plr(playerId)).Where(p => p.LargestMultiKill == 5).Select(p => new A(p.Champion) { href = "#game" + p.Game.Id, class_ = "linelist" })));
            result.Add(new P(new B("Quadra kills:"), games.Select(g => g.Plr(playerId)).Where(p => p.LargestMultiKill == 4).Select(p => new A(p.Champion) { href = "#game" + p.Game.Id, class_ = "linelist" })));
            result.Add(new P(new B("Triple kills:"), games.Select(g => g.Plr(playerId)).Where(p => p.LargestMultiKill == 3).Select(p => new A(p.Champion) { href = "#game" + p.Game.Id, class_ = "linelist" })));
            var byLastWinLoss = games.Where(g => g.Victory != null).GroupBy(g => g.DateDayOnly(Human.TimeZone)).Select(grp => grp.OrderBy(itm => itm.DateUtc).Last().Victory.Value);
            result.Add(new P(new B("Last game of the day: "), "victory: {0:0}%, defeat: {1:0}%".Fmt(
                byLastWinLoss.Count(v => v) / (double) byLastWinLoss.Count() * 100,
                byLastWinLoss.Count(v => !v) / (double) byLastWinLoss.Count() * 100
            )));

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

            result.Add(new P(new B("Total games: "), games.Count().ToString("#,0")));
            result.Add(new P(new B("Longest and shortest:"),
                games.OrderByDescending(g => g.Duration).Take(7).Select(g => new object[] { new A(minsec(g.Duration)) { href = "#game" + g.Id, class_ = "linelist" }, new SUP(g.MicroType) }), new SPAN("...") { class_ = "linelist" },
                games.OrderByDescending(g => g.Duration).TakeLast(7).Select(g => new object[] { new A(minsec(g.Duration)) { href = "#game" + g.Id, class_ = "linelist" }, new SUP(g.MicroType) })));
            result.Add(new P(new B("Played 10+ times: "),
                (from champ in Program.Champions.Values let c = games.Count(g => g.Plr(playerId).Champion == champ) where c >= 10 orderby c descending select "{0}: {1:#,0}".Fmt(champ, c)).JoinString(", ")));
            result.Add(new P(new B("Played 3-9 times: "),
                (from champ in Program.Champions.Values let c = games.Count(g => g.Plr(playerId).Champion == champ) where c >= 3 && c <= 9 orderby c descending select "{0}: {1:#,0}".Fmt(champ, c)).JoinString(", ")));
            result.Add(new P(new B("Played 1-2 times: "), Program.Champions.Values.Where(champ => { int c = games.Count(g => g.Plr(playerId).Champion == champ); return c >= 1 && c <= 2; }).Order().JoinString(", ")));
            result.Add(new P(new B("Never played: "), Program.Champions.Values.Where(champ => !games.Any(g => g.Plr(playerId).Champion == champ)).Order().JoinString(", ")));

            return result;
        }

        private object makePlotXY(string title, List<Tuple<double, double>> data)
        {
            double width = 400;
            double height = 150;
            var sb = new StringBuilder();
            double maxX = data.Max(pt => pt.Item1);
            double maxY = data.Max(pt => pt.Item2);
            return new RawTag("<svg width='{0}' height='{1}' style='border: 1px solid #999; margin: 10px; background: #fff;' xmlns='http://www.w3.org/2000/svg'><g>".Fmt(width, height)
                + "<text xml:space='preserve' text-anchor='middle' font-family='Open Sans, Arial, sans-serif' font-size='17' x='{0}' y='0' fill='#000' dominant-baseline='hanging'>{1}</text>".Fmt(width / 2, title)
                + "<text xml:space='preserve' text-anchor='left' font-family='Open Sans, Arial, sans-serif' font-size='17' x='10' y='20' fill='#000' dominant-baseline='hanging'>{0}</text>".Fmt(maxY.ToString())
                + "<polyline fill='none' stroke='#921' points='{0}' />".Fmt(data.Select(d => "{0:0.000},{1:0.000}".Fmt(d.Item1 / maxX * (width - 20) + 10, (maxY - d.Item2) / maxY * (height - 40) + 30)).JoinString(" "))
                + "</g></svg>");
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
            return new A(text(linkTo)) { href = "#game" + linkTo.Game.Id };
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

        public void DiscoverGameIds(bool full)
        {
            if (Summoner == null)
                throw new Exception("Not supported for multi-account generators");
            int count = 15;
            int index = 0;
            while (true)
            {
                Console.WriteLine("{0}/{1}: retrieving games at {2} of {3}".Fmt(Summoner.Name, Summoner.Region, index, count));
                var resp = _hc.Get(@"https://acs.leagueoflegends.com/v1/stats/player_history/auth?begIndex={0}&endIndex={1}&queue=0&queue=2&queue=4&queue=6&queue=7&queue=8&queue=9&queue=14&queue=16&queue=17&queue=25&queue=31&queue=32&queue=33&queue=41&queue=42&queue=52&queue=61&queue=65&queue=70&queue=73&queue=76&queue=78&queue=83&queue=91&queue=92&queue=93&queue=96&queue=98&queue=100&queue=300&queue=313".Fmt(index, index + 15, Summoner.RegionFull, Summoner.AccountId));
                var json = resp.Expect(HttpStatusCode.OK).DataJson;

                Ut.Assert(json["accountId"].GetLongLenient() == Summoner.AccountId);
                Ut.Assert(json["platformId"].GetString().EqualsNoCase(Summoner.Region) || json["platformId"].GetString().EqualsNoCase(Summoner.RegionFull));

                index += 15;
                count = json["games"]["gameCount"].GetInt();

                foreach (var gameId in json["games"]["games"].GetList().Select(js => js["gameId"].GetLong().ToString()))
                    if (!Summoner.GamesAndReplays.ContainsKey(gameId))
                        Summoner.GamesAndReplays[gameId] = null;

                if (!full)
                    break;
                if (index >= count)
                    break;
            }
            Console.WriteLine("{0}/{1}: done.".Fmt(Summoner.Name, Summoner.Region));
        }
    }

    class Game
    {
        public string Id;
        public DateTime DateUtc;
        public TimeSpan Duration;
        public DateTime Date(string timeZoneId) { return TimeZoneInfo.ConvertTimeFromUtc(DateUtc, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)); }
        public DateTime DateDayOnly(string timeZoneId) { var d = Date(timeZoneId); return d.TimeOfDay.TotalHours < 5 ? d.Date.AddDays(-1) : d.Date; }
        public string DetailsUrl;
        public string ReplayUrl;
        public int MapId;
        public int QueueId;
        public string Map;
        public string Type;
        public bool? Victory { get { return Ally.Victory ? true : Enemy.Victory ? false : (bool?) null; } }
        public Team Ally, Enemy;
        public Player Plr(string summonerName) { return Ally.Players.Single(p => p.Name == summonerName); }
        public Player Plr(HumanInfo human) { return Ally.Players.Single(p => human.SummonerNames.Contains(p.Name)); }
        public Player Plr(object playerId) { return playerId is string ? Plr(playerId as string) : playerId is HumanInfo ? Plr(playerId as HumanInfo) : Ut.Throw<Player>(new Exception()); }
        public string MicroType { get { return Regex.Matches((Map == "Summoner's Rift" ? "" : " " + Map) + " " + Type, @"\s\(?(.)").Cast<Match>().Select(m => m.Groups[1].Value).JoinString(); } }

        public Game(JsonDict json, SummonerInfo summoner, string replayUrl)
        {
            Id = json["gameId"].GetLong().ToString();
            MapId = json["mapId"].GetInt();
            QueueId = json["queueId"].GetInt();
            setMapAndType();
            DetailsUrl = "http://matchhistory.{0}.leagueoflegends.com/en/#match-details/{1}/{2}/{3}".Fmt(summoner.Region.ToLower(), summoner.RegionFull, Id, summoner.AccountId);
            ReplayUrl = replayUrl;
            DateUtc = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc) + TimeSpan.FromSeconds(json["gameCreation"].GetLong() / 1000.0);
            Duration = TimeSpan.FromSeconds(json["gameDuration"].GetInt());
            var players = json["participantIdentities"].GetList().Select(p =>
            {
                var result = new Player();
                result.ParticipantId = p["participantId"].GetInt();
                result.AccountId = p["player"]["accountId"].GetLong();
                result.SummonerId = p["player"].ContainsKey("summonerId") ? p["player"]["summonerId"].GetLong() : -1; // -1 when it's a bot
                result.Name = p["player"]["summonerName"].GetString();
                return result;
            }).ToDictionary(plr => plr.ParticipantId);
            foreach (var p in json["participants"].GetList())
            {
                var pp = players[p["participantId"].GetInt()];
                pp.TeamId = p["teamId"].GetInt();
                pp.ChampionId = p["championId"].GetInt();
                pp.Spell1Id = p["spell1Id"].GetInt();
                pp.Spell2Id = p["spell2Id"].GetInt();
                var role = p["timeline"]["role"].GetString();
                pp.Role = role == "DUO" ? Role.Duo : role == "DUO_CARRY" ? Role.DuoCarry : role == "DUO_SUPPORT" ? Role.DuoSupport : role == "SOLO" ? Role.Solo : role == "NONE" ? Role.None : Ut.Throw<Role>(new Exception());
                var lane = p["timeline"]["lane"].GetString();
                pp.Lane = lane == "TOP" ? Lane.Top : lane == "JUNGLE" ? Lane.Jungle : lane == "MIDDLE" ? Lane.Middle : lane == "BOTTOM" ? Lane.Bottom : Ut.Throw<Lane>(new Exception());
                var stats = p["stats"].GetDict();
                pp.Kills = stats["kills"].GetInt();
                pp.Deaths = stats["deaths"].GetInt();
                pp.Assists = stats["assists"].GetInt();
                pp.DamageToChampions = stats["totalDamageDealtToChampions"].GetInt();
                pp.TotalHeal = stats["totalHeal"].GetInt();
                pp.TotalDamageTaken = stats["totalDamageTaken"].GetInt();
                pp.LargestMultiKill = stats["largestMultiKill"].GetInt();
                pp.WardsPlaced = stats.ContainsKey("wardsPlaced") ? stats["wardsPlaced"].GetInt() : 0;
                var timeline = p["timeline"].GetDict();
                if (Duration > TimeSpan.FromMinutes(10))
                {
                    pp.CreepsAt10 = (int) (timeline["creepsPerMinDeltas"]["0-10"].GetDouble() * 10);
                    pp.GoldAt10 = (int) (timeline["goldPerMinDeltas"]["0-10"].GetDouble() * 10);
                }
                if (Duration > TimeSpan.FromMinutes(20))
                {
                    pp.CreepsAt20 = pp.CreepsAt10 + (int) (timeline["creepsPerMinDeltas"]["10-20"].GetDouble() * 10);
                    pp.GoldAt20 = pp.GoldAt10 + (int) (timeline["goldPerMinDeltas"]["10-20"].GetDouble() * 10);
                }
                if (Duration > TimeSpan.FromMinutes(30))
                {
                    pp.CreepsAt30 = pp.CreepsAt20 + (int) (timeline["creepsPerMinDeltas"]["20-30"].GetDouble() * 10);
                    pp.GoldAt30 = pp.GoldAt20 + (int) (timeline["goldPerMinDeltas"]["20-30"].GetDouble() * 10);
                }
            }
            var teams = players.Values.GroupBy(p => p.TeamId).ToDictionary(g => g.Key, g => new
            {
                Team = new Team { Players = g.OrderBy(p => p.Lane).ThenBy(p => p.Role).ToList() },
                Json = json["teams"].GetList().Single(t => t["teamId"].GetInt() == g.First().TeamId)
            });
            Ut.Assert(teams.Count == 2);
            var ourTeamId = players.Values.Single(p => p.SummonerId == summoner.SummonerId).TeamId;
            Ally = teams[ourTeamId].Team;
            Enemy = teams[teams.Keys.Where(k => k != ourTeamId).Single()].Team;
            foreach (var t in teams.Values)
            {
                t.Team.Victory = t.Json["win"].GetString() == "Win" ? true : t.Json["win"].GetString() == "Fail" ? false : Ut.Throw<bool>(new Exception());
                foreach (var p in t.Team.Players)
                {
                    p.Team = t.Team;
                    p.Game = this;
                }
            }
        }

        private void setMapAndType()
        {
            switch (MapId)
            {
                case 1: Map = "Summoner's Rift (v1 summer)"; break;
                case 2: Map = "Summoner's Rift (v1 autumn)"; break;
                case 3: Map = "Proving Grounds"; break;
                case 4: Map = "Twisted Treeline (v1)"; break;
                case 8: Map = "Crystal Scar"; break;
                case 10: Map = "Twisted Treeline"; break;
                case 11: Map = "Summoner's Rift"; break;
                case 12: Map = "Howling Abyss"; break;
                case 14: Map = "Butcher's Bridge"; break;
            }
            string queueMap = null;
            switch (QueueId)
            {
                case 0: Type = "Custom"; break;
                case 8: Type = "3v3"; queueMap = "Twisted Treeline"; break;
                case 2: Type = "5v5 Blind Pick"; queueMap = "Summoner's Rift"; break;
                case 14: Type = "5v5 Draft Pick"; queueMap = "Summoner's Rift"; break;
                case 4: Type = "5v5 Ranked Solo"; queueMap = "Summoner's Rift"; break;
                case 6: Type = "5v5 Ranked Premade"; queueMap = "Summoner's Rift"; break;
                case 9: Type = "3v3 Ranked Premade"; queueMap = "Summoner's Rift"; break;
                case 41: Type = "3v3 Ranked Team"; break;
                case 42: Type = "5v5 Ranked Team"; queueMap = "Summoner's Rift"; break;
                case 16: Type = "5v5 Blind Pick"; queueMap = "Crystal Scar"; break;
                case 17: Type = "5v5 Draft Pick"; queueMap = "Crystal Scar"; break;
                case 7: Type = "Coop vs AI (old)"; queueMap = "Summoner's Rift"; break;
                case 25: Type = "Dominion Coop vs AI"; queueMap = "Crystal Scar"; break;
                case 31: Type = "Coop vs AI (Intro)"; queueMap = "Summoner's Rift"; break;
                case 32: Type = "Coop vs AI (Beginner)"; queueMap = "Summoner's Rift"; break;
                case 33: Type = "Coop vs AI (Intermediate)"; queueMap = "Summoner's Rift"; break;
                case 52: Type = "Coop vs AI"; queueMap = "Twisted Treeline"; break;
                case 61: Type = "Team Builder"; break;
                case 65: Type = "ARAM"; queueMap = "Howling Abyss"; break;
                case 70: Type = "One for All"; break;
                case 72: Type = "1v1 Snowdown Showdown"; break;
                case 73: Type = "2v2 Snowdown Showdown"; break;
                case 75: Type = "6v6 Hexakill"; queueMap = "Summoner's Rift"; break;
                case 76: Type = "Ultra Rapid Fire"; break;
                case 83: Type = "Ultra Rapid Fire vs AI"; break;
                case 91: Type = "Doom Bots Rank 1"; break;
                case 92: Type = "Doom Bots Rank 2"; break;
                case 93: Type = "Doom Bots Rank 5"; break;
                case 96: Type = "Ascension"; break;
                case 98: Type = "6v6 Hexakill"; queueMap = "Twisted Treeline"; break;
                case 100: Type = "ARAM"; queueMap = "Butcher's Bridge"; break;
                case 300: Type = "King Poro"; break;
                case 310: Type = "Nemesis"; break;
                case 313: Type = "Black Market Brawlers"; break;
            }
            if (queueMap != null)
                Ut.Assert(Map == queueMap);
        }
    }

    class Team
    {
        public bool Victory;
        public List<Player> Players;
    }

    class Player
    {
        public Team Team;
        public Game Game;
        public int ParticipantId, TeamId;
        public long AccountId, SummonerId;
        public string Name;
        public int ChampionId;
        public string Champion { get { return Program.Champions[ChampionId]; } }
        public bool Victory { get { return Team.Victory; } }
        public int Spell1Id, Spell2Id;
        public Role Role;
        public Lane Lane;
        public int Kills, Deaths, Assists;
        public int DamageToChampions, TotalHeal, TotalDamageTaken;
        public int LargestMultiKill;
        public int WardsPlaced;
        public int CreepsAt10, CreepsAt20, CreepsAt30;
        public int GoldAt10, GoldAt20, GoldAt30;

        public int RankOf(Func<Player, double> prop)
        {
            var groups = Game.Ally.Players.Concat(Game.Enemy.Players).GroupBy(p => prop(p)).OrderByDescending(g => g.Key).Select(g => new { count = g.Count(), containsThis = g.Contains(this) }).ToList();
            int rank = 1;
            foreach (var g in groups)
            {
                if (g.containsThis)
                    return rank;
                rank += g.count;
            }
            throw new Exception();
        }

        public override string ToString()
        {
            return Name + " - " + Champion;
        }
    }

    enum Lane { Top, Jungle, Middle, Bottom }
    enum Role { None, Solo, DuoSupport, Duo, DuoCarry }
}
