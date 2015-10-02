using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using RT.TagSoup;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;
using RT.Util.Paths;
using Stratosphere.Imap;

namespace LeagueGenMatchHistory
{
    class Program
    {
        public static Settings Settings;
        public static Dictionary<int, string> Champions = new Dictionary<int, string>();

        static void Main(string[] args)
        {
            SettingsUtil.LoadSettings(out Settings);
            Settings.Save();
            Directory.CreateDirectory(Path.Combine(Settings.MatchHistoryPath, "json"));
            Directory.CreateDirectory(Path.Combine(Settings.MatchHistoryPath, "emails"));

            var generators = Settings.Summoners.ToDictionary(sm => sm, sm => new Generator(sm));

            // Load champion id to name map
            var champs = JsonDict.Parse(File.ReadAllText(Path.Combine(Settings.MatchHistoryPath, "champions.json")));
            foreach (var kvp in champs["data"].GetDict())
                Champions[kvp.Value["key"].GetIntLenient()] = kvp.Value["name"].GetString();

            // Locate replay URLs by scanning through all emails
            Console.WriteLine("Scanning emails...");
            foreach (var file in new PathManager(Settings.EmailPath).GetFiles())
            {
                var text = File.ReadAllText(file.FullName);
                if (!text.Contains("admin@replay.gg"))
                    continue;
                var decoded = RFC2047Decoder.ParseQuotedPrintable(Encoding.UTF8, text);
                var detailsUrl = Regex.Match(decoded, @"details\s+<a\s+href=""(?<url>.*?)"">HERE</a>").Groups["url"].Value;
                var replayUrl = Regex.Match(decoded, @"replay\s+<a\s+href=""(?<url>.*?)"">HERE</a>").Groups["url"].Value;
                var subject = Regex.Match(decoded, @"Subject: (.*?)(?=\n[^ ])", RegexOptions.Singleline | RegexOptions.Multiline).Groups[1].Value.Replace("\r\n", "").Trim();
                Console.WriteLine(subject);
                var subj = Regex.Match(subject, @"(?<name>.*?) \((?<champ>.*?)\) - (?<map>.*?) \((?<type>.*?)\) - (?<wl>\w+) - ");
                var summonerName = subj.Groups["name"].Value;
                var url = Regex.Match(detailsUrl, @"http://matchhistory.(?<regionShort>[^.]+).leagueoflegends.com/.*?/#match-details/(?<regionFull>[^/]+)/(?<id>.*?)/(?<summId>.*?)$");
                var region = url.Groups["regionShort"].Value.ToUpper();
                var regionFull = url.Groups["regionFull"].Value.ToUpper();
                var gameId = url.Groups["id"].Value;
                var summonerId = url.Groups["summId"].Value;

                var summoner = Settings.Summoners.FirstOrDefault(sm => sm.RegionFull == regionFull && sm.SummonerId == summonerId);
                if (summoner == null)
                    throw new Exception("Found a replay.gg email for an unknown summoner: {0}, {1}, {2}".Fmt(summonerName, regionFull, summonerId));
                summoner.GamesAndReplays[gameId] = replayUrl;
            }
            Settings.Save();

            // Load known game IDs by querying Riot
            Console.WriteLine("Querying Riot...");
            foreach (var gen in generators.Values)
            {
                gen.DiscoverGameIds(false);
                Settings.Save();
            }

            foreach (var gen in generators.Values)
            {
                gen.LoadGames();
                gen.ProduceOutput();
                gen.ProduceOutput(200);
            }
        }
    }

    class Generator
    {
        public SummonerInfo Summoner;
        private List<Game> _games = new List<Game>();
        private HClient _hc;

        public Generator(SummonerInfo summoner)
        {
            Summoner = summoner;

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

        public void LoadGames()
        {
            foreach (var kvp in Summoner.GamesAndReplays)
            {
                var json = LoadGameJson(kvp.Key);
                if (json != null)
                    _games.Add(new Game(json, Summoner, kvp.Value));
            }
        }

        public JsonDict LoadGameJson(string gameId)
        {
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
            var gameTypeSections = _games
                .OrderByDescending(g => g.DateUtc)
                .Take(limit)
                .Where(g => g.Type != "Custom")
                .GroupBy(g => g.Map + ", " + g.Type)
                .OrderByDescending(g => g.Count())
                .ToList();
            var sections = gameTypeSections.Select(grp => Ut.NewArray<object>(
                    new H1(grp.Key) { id = new string(grp.Key.Where(c => char.IsLetterOrDigit(c)).ToArray()) },
                    genOverallStats(grp),
                    (
                        from pname in (Program.Settings.KnownPlayers.Concat(Summoner.Name).Distinct())
                        let gamesWithThisPlayer = grp.Where(g => g.Ally.Players.Any(pp => pp.Name == pname))
                        let count = gamesWithThisPlayer.Count()
                        where count > 5
                        orderby count descending
                        select genStats(pname, gamesWithThisPlayer)
                    ),
                    new H4("All games"),
                    new TABLE(
                        grp.OrderByDescending(g => g.Date).Select(g =>
                        {
                            var allies = g.Ally.Players.Select(plr => getPlayerHtml(plr)).ToList();
                            var enemies = g.Enemy.Players.Select(plr => getPlayerHtml(plr)).ToList();
                            return Ut.NewArray<object>(
                                new TR { id = "game" + g.Id.ToString() }._(
                                    new TD { rowspan = 2, class_ = "nplr datetime" }._(new A(g.Date.ToString("dd/MM/yy"), new BR(), g.Date.ToString("HH:mm")) { href = g.DetailsUrl }),
                                    new TD { rowspan = 2, class_ = "nplr" }._(g.Duration.TotalMinutes.ToString("00") + ":" + g.Duration.Seconds.ToString("00")),
                                    new TD { rowspan = 2, class_ = "nplr " + g.Victory.NullTrueFalse("draw", "victory", "defeat") }._(g.Victory.NullTrueFalse("Draw", "Victory", "Defeat")),
                                    new TD { rowspan = 2, class_ = "sep" },
                                    allies.Select(p => p[0]),
                                    new TD { rowspan = 2, class_ = "sep" },
                                    enemies.Select(p => p[0]),
                                    new TD { rowspan = 2, class_ = "sep" },
                                    new TD { class_ = "nplr", rowspan = 2 }._(g.ReplayUrl.NullOr(replayUrl => new A("replay") { href = replayUrl }))
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
                genGraphs(_games),
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
                table td.la.la { text-align: left; }";
            css += "\r\n td." + Summoner.Name + " { background: #D1FECC; }\r\n";
            css += Program.Settings.KnownPlayers.Where(plr => plr != Summoner.Name).Select(plr => "td." + plr.Replace(" ", "") + " { background: #6EFFFF; }\r\n").JoinString();

            var outputFile = Program.Settings.OutputPathTemplate.Fmt(Summoner.Region, Summoner.Name, limit == 999999 ? "" : ("-" + limit));
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
            File.WriteAllText(outputFile, new HTML(new HEAD(new STYLELiteral(css)), new BODY(result)).ToString());
        }

        private object genOverallStats(IEnumerable<Game> games)
        {
            var result = new List<object>();
            var allOtherPlayers = games.SelectMany(g => g.Ally.Players.Concat(g.Enemy.Players)).Where(p => !Program.Settings.KnownPlayers.Contains(p.Name) && p.Name != Summoner.Name);
            var allOtherChamps = allOtherPlayers.GroupBy(p => p.ChampionId);
            result.Add(new H4("Champions encountered (excluding our own selections)"));
            result.Add(new P(allOtherChamps.OrderByDescending(g => g.Count()).Select(g => Program.Champions[g.Key] + ": " + g.Count()).JoinString(", ")));
            return result;
        }

        private object genStats(string playerName, IEnumerable<Game> games)
        {
            var result = new List<object>();
            var makeSummaryTable = Ut.Lambda((string label, IEnumerable<IGrouping<string, Player>> set) =>
            {
                return new TABLE { class_ = "ra stats" }._(
                    new TR(new TH(label) { rowspan = 2 }, new TH("Games") { rowspan = 2 }, new TH("Wins") { rowspan = 2 }, new TH("Losses") { rowspan = 2 }, new TH("Win%") { rowspan = 2 },
                        new TH("Kills/deaths/assists") { colspan = 6 }, new TH("Dmg to champs") { colspan = 2 }, new TH("Healing") { colspan = 2 },
                        new TH("CS @ 10m") { colspan = 2 }, new TH("Gold @ 10m") { colspan = 2 }, new TH("Multikills every") { colspan = 4 }),
                    new TR(
                        new TH("Avg/30m") { colspan = 3 }, new TH("Max") { colspan = 3 }, new TH("Avg/30m"), new TH("Max"), new TH("Avg/30m"), new TH("Max"),
                        new TH("Avg"), new TH("Max"), new TH("Avg"), new TH("Max"), new TH("5x"), new TH("4x+"), new TH("3x+"), new TH("2x+")),
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
                        new TD(g.Average(p => p.TotalHeal / p.Game.Duration.TotalMinutes * 30).ToString("#,0")),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.TotalHeal), p => p.TotalHeal.ToString("#,0"))),
                        new TD("{0:0}".Fmt(g.Average(p => p.CreepsAt10))),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.CreepsAt10), p => p.CreepsAt10.ToString("0"))),
                        new TD("{0:0}".Fmt(g.Average(p => p.GoldAt10))),
                        new TD(getGameValueAndLink(g.MaxElement(p => p.GoldAt10), p => p.GoldAt10.ToString("0"))),
                        new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 5))),
                        new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 4))),
                        new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 3))),
                        new TD(fmtOrInf(g.Count() / (double) g.Count(p => p.LargestMultiKill >= 2)))
                    ))
                );
            });

            result.Add(genGraphs(games));

            result.Add(new P(new B("Penta kills:"), games.Select(g => g.Plr(playerName)).Where(p => p.LargestMultiKill == 5).Select(p => new A(p.Champion) { href = "#game" + p.Game.Id, style = "margin-left: 8px;" })));
            result.Add(new P(new B("Quadra kills:"), games.Select(g => g.Plr(playerName)).Where(p => p.LargestMultiKill == 4).Select(p => new A(p.Champion) { href = "#game" + p.Game.Id, style = "margin-left: 8px;" })));
            result.Add(new P(new B("Triple kills:"), games.Select(g => g.Plr(playerName)).Where(p => p.LargestMultiKill == 3).Select(p => new A(p.Champion) { href = "#game" + p.Game.Id, style = "margin-left: 8px;" })));
            var byLastWinLoss = games.Where(g => g.Victory != null).GroupBy(g => g.DateDayOnly).Select(grp => grp.OrderBy(itm => itm.Date).Last().Victory.Value);
            result.Add(new P(new B("Last game of the day: "), "victory: {0:0}%, defeat: {1:0}%".Fmt(
                byLastWinLoss.Count(v => v) / (double) byLastWinLoss.Count() * 100,
                byLastWinLoss.Count(v => !v) / (double) byLastWinLoss.Count() * 100
            )));

            result.Add(new H4("{0} stats: by champion".Fmt(playerName)));
            result.Add(makeSummaryTable("Champion", games.Select(g => g.Plr(playerName)).GroupBy(p => p.Champion)));
            result.Add(new H4("{0} stats: by lane/role".Fmt(playerName)));
            result.Add(makeSummaryTable("Lane/role", games.Select(g => g.Plr(playerName)).GroupBy(p => (p.Lane == Lane.Top ? "Top" : p.Lane == Lane.Middle ? "Mid" : p.Lane == Lane.Jungle ? "JG" : "Bot") + (p.Role == Role.DuoCarry ? " adc" : p.Role == Role.DuoSupport ? " sup" : ""))));
            result.Add(new H4("{0} stats: total".Fmt(playerName)));
            result.Add(makeSummaryTable("Total", games.Select(g => g.Plr(playerName)).GroupBy(p => "Total")));
            var id = Rnd.NextBytes(8).ToHex();
            return Ut.NewArray<object>(new BUTTON("Show/hide stats for {0}".Fmt(playerName)) { onclick = "document.getElementById('{0}').style.display = (document.getElementById('{0}').style.display == 'none') ? 'block' : 'none';".Fmt(id) }, new DIV(result) { id = id, style = "display:none" });
        }

        private object genGraphs(IEnumerable<Game> games)
        {
            var result = new List<object>();
            var histoTimeOfDay = range(5, 24, 24).Select(h => Tuple.Create(h.ToString("00"), games.Count(g => g.Date.TimeOfDay.Hours == h)));
            result.Add(makeHistogram(histoTimeOfDay, "Games by time of day"));
            var firstDate = games.Min(g => g.Date.Date) + TimeSpan.FromHours(5);
            //var histoGamesPerDay = Enumerable.Range(0, 11).Select(c => Tuple.Create(c == 10 ? "10+" : c.ToString(), Enumerable.Range(0, (int) (DateTime.Today - firstDate).TotalDays).Count(day => games.Count(g => g.Date.Date >= firstDate + TimeSpan.FromDays(day) && g.Date.Date <= firstDate + TimeSpan.FromDays(day + 1)) == c))).ToList();
            var histoGamesPerDay = Enumerable.Range(1, 12).Select(c => Tuple.Create(c == 12 ? "12+" : c.ToString(), games.GroupBy(g => g.DateDayOnly).Count(grp => grp.Count() == c))).ToList();
            result.Add(makeHistogram(histoGamesPerDay, "Games played per day"));
            var histoGamesByDayOfWeek = range(1, 7, 7).Select(dow => Tuple.Create(((DayOfWeek) dow).ToString().Substring(0, 2), games.Count(g => (int) g.Date.DayOfWeek == dow))).ToList();
            result.Add(makeHistogram(histoGamesByDayOfWeek, "Games played on ..."));
            var histoGamesByDayOfWeek2 = range(1, 7, 7).Select(dow => Tuple.Create(((DayOfWeek) dow).ToString().Substring(0, 2), games.GroupBy(g => g.DateDayOnly).Count(g => (int) g.Key.DayOfWeek == dow))).ToList();
            result.Add(makeHistogram(histoGamesByDayOfWeek2, "Days with 1+ games"));
            result.Add(makeHistogram2(new double[] { 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70 }, (durMin, durMax) => games.Count(g => g.Duration.TotalMinutes > durMin && g.Duration.TotalMinutes <= durMax), "Games by length, minutes"));
            return result;
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
                new TD{class_="plr-top "+plr.Name.Replace(" ", "")}._(new DIV(plr.Name) { class_="plrname" },
                    "{0}/{1}/{2}".Fmt(plr.Kills, plr.Deaths, plr.Assists)),
                new TD{class_="plr-bot "+plr.Name.Replace(" ", "")}._(
                    new DIV(plr.LargestMultiKill <= 1 ? "" : (plr.LargestMultiKill + "x")) { class_ = "multi multi" + plr.LargestMultiKill },
                    plr.Champion,
                    " ", plr.Lane == Lane.Top ? "(top)" : plr.Lane == Lane.Middle ? "(mid)" : plr.Lane == Lane.Jungle ? "(jg)" : plr.Role == Role.DuoCarry ? "(adc)" : plr.Role == Role.DuoSupport ? "(sup)" : "(bot)")
            };
        }

        public void DiscoverGameIds(bool full)
        {
            int count = 15;
            int index = 0;
            while (true)
            {
                Console.WriteLine("{0}/{1}: retrieving games at {2} of {3}".Fmt(Summoner.Name, Summoner.Region, index, count));
                var resp = _hc.Get(@"https://acs.leagueoflegends.com/v1/stats/player_history/auth?begIndex={0}&endIndex={1}&queue=0&queue=2&queue=4&queue=6&queue=7&queue=8&queue=9&queue=14&queue=16&queue=17&queue=25&queue=31&queue=32&queue=33&queue=41&queue=42&queue=52&queue=61&queue=65&queue=70&queue=73&queue=76&queue=78&queue=83&queue=91&queue=92&queue=93&queue=96&queue=98&queue=100&queue=300&queue=313".Fmt(index, index + 15, Summoner.RegionFull, Summoner.AccountId));
                var json = resp.Expect(HttpStatusCode.OK).DataJson;

                Ut.Assert(json["accountId"].GetStringLenient() == Summoner.AccountId);
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
        public SummonerInfo Sm;
        public string Id;
        public DateTime Date, DateUtc;
        public DateTime DateDayOnly { get { return Date.TimeOfDay.TotalHours < 5 ? Date.Date.AddDays(-1) : Date.Date; } }
        public TimeSpan Duration;
        public string DetailsUrl;
        public string ReplayUrl;
        public string Champion { get { return Program.Champions[Me.ChampionId]; } }
        public int MapId;
        public int QueueId;
        public string Map;
        public string Type;
        public bool? Victory { get { return Ally.Victory ? true : Enemy.Victory ? false : (bool?) null; } }
        public Team Ally, Enemy;
        public Player Me { get { return Ally.Players.Single(p => p.SummonerId.ToString() == Sm.AccountId); } }
        public Player Plr(string name) { return Ally.Players.Single(p => p.Name == name); }

        public Game(JsonDict json, SummonerInfo summoner, string replayUrl)
        {
            Sm = summoner;
            Id = json["gameId"].GetLong().ToString();
            MapId = json["mapId"].GetInt();
            QueueId = json["queueId"].GetInt();
            setMapAndType();
            DetailsUrl = "http://matchhistory.{0}.leagueoflegends.com/en/#match-details/{1}/{2}/{3}".Fmt(Sm.Region.ToLower(), Sm.RegionFull, Id, Sm.AccountId);
            ReplayUrl = replayUrl;
            DateUtc = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc) + TimeSpan.FromSeconds(json["gameCreation"].GetLong() / 1000.0);
            Date = TimeZoneInfo.ConvertTimeFromUtc(DateUtc, TimeZoneInfo.FindSystemTimeZoneById(Sm.TimeZoneId));
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
            var ourTeamId = players.Values.Single(p => p.Name == Sm.Name).TeamId;
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
        public int Spell1Id, Spell2Id;
        public Role Role;
        public Lane Lane;
        public int Kills, Deaths, Assists;
        public int DamageToChampions, TotalHeal, TotalDamageTaken;
        public int LargestMultiKill;
        public int CreepsAt10, CreepsAt20, CreepsAt30;
        public int GoldAt10, GoldAt20, GoldAt30;
    }

    enum Lane { Top, Jungle, Middle, Bottom }
    enum Role { None, Solo, DuoSupport, Duo, DuoCarry }
}
