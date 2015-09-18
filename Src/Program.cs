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
using RT.Util.Paths;
using Stratosphere.Imap;

// list of best games by damage, by total kills, by max kills, by total healed
// total stats for each champ: games, wins, avg kills/damage (per minute?)
// correlations: enemy team most winny and most lossy champs

namespace LeagueGenMatchHistory
{
    class Program
    {
        static string EmailPath = @"C:\League\Emails";
        static string MatchHistoryPath = @"C:\League\MatchHistory";

        static List<string> KnownPlayers = new List<string> { "redacted84", "redacted49", "redacted60", "redacted51" };
        static Dictionary<int, string> Champions = new Dictionary<int, string>();

        static void Main(string[] args)
        {
            Directory.CreateDirectory(Path.Combine(MatchHistoryPath, "json"));
            Directory.CreateDirectory(Path.Combine(MatchHistoryPath, "emails"));

            var champs = JsonDict.Parse(File.ReadAllText(Path.Combine(MatchHistoryPath, "champions.json")));
            foreach (var kvp in champs["data"].GetDict())
                Champions[kvp.Value["key"].GetIntLenient()] = kvp.Value["name"].GetString();

            foreach (var file in new PathManager(EmailPath).GetFiles())
            {
                var text = File.ReadAllText(file.FullName);
                if (text.Contains("admin@replay.gg"))
                {
                    Console.WriteLine(file.FullName);
                    File.WriteAllText(Path.Combine(MatchHistoryPath, "emails", file.FullName.Replace(EmailPath, "").FilenameCharactersEscape()), text);
                }
            }

            var games = new List<Game>();
            var hc = new HClient();
            hc.ReqAccept = "application/json, text/javascript, */*; q=0.01";
            hc.ReqAcceptLanguage = "en-GB,en;q=0.5";
            hc.ReqUserAgent = "Mozilla/5.0 (Windows NT 6.1; WOW64; rv:40.0) Gecko/20100101 Firefox/40.0";
            hc.ReqReferer = "http://matchhistory.na.leagueoflegends.com/en/";
            hc.ReqHeaders[HttpRequestHeader.Host] = "acs.leagueoflegends.com";
            hc.ReqHeaders["DNT"] = "1";
            hc.ReqHeaders["Region"] = "NA";
            hc.ReqHeaders["Authorization"] = "Vapor redacted";
            hc.ReqHeaders["Origin"] = "http://matchhistory.na.leagueoflegends.com";
            foreach (var file in new PathManager(Path.Combine(MatchHistoryPath, "emails")).GetFiles())
            {
                var text = File.ReadAllText(file.FullName);
                var decoded = RFC2047Decoder.ParseQuotedPrintable(Encoding.UTF8, text);
                var game = new Game();
                games.Add(game);
                game.DetailsUrl = Regex.Match(decoded, @"details\s+<a\s+href=""(?<url>.*?)"">HERE</a>").Groups["url"].Value;
                game.ReplayUrl = Regex.Match(decoded, @"replay\s+<a\s+href=""(?<url>.*?)"">HERE</a>").Groups["url"].Value;
                game.Date = DateTime.Parse(Regex.Match(decoded, @"Date: ([^\n]*)").Groups[1].Value.Trim());
                game.Subj = Regex.Match(decoded, @"Subject: (.*?)(?=\n[^ ])", RegexOptions.Singleline | RegexOptions.Multiline).Groups[1].Value.Replace("\r\n", "").Trim();
                var subj = Regex.Match(game.Subj, @"(?<name>.*?) \((?<champ>.*?)\) - (?<map>.*?) \((?<type>.*?)\) - (?<wl>\w+) - ");
                game.Summoner = subj.Groups["name"].Value;
                game.Champion = subj.Groups["champ"].Value;
                game.Map = subj.Groups["map"].Value;
                game.Type = subj.Groups["type"].Value;
                game.Victory = subj.Groups["wl"].Value == "WIN" ? true : subj.Groups["wl"].Value == "LOSS" ? false : subj.Groups["wl"].Value == "DRAW" ? (bool?) null : Ut.Throw<bool>(new Exception());
                Console.WriteLine(game.Subj);

                var url = Regex.Match(game.DetailsUrl, @"#match-details/(?<region>.*?)/(?<id>.*?)/(?<acctId>.*?)$");
                game.Region = url.Groups["region"].Value;
                game.Id = url.Groups["id"].Value;
                game.AccountId = url.Groups["acctId"].Value;
                var fullHistoryUrl = "https://acs.leagueoflegends.com/v1/stats/game/{0}/{1}?visiblePlatformId={0}&visibleAccountId={2}".Fmt(game.Region, game.Id, game.AccountId);
                var path = Path.Combine(MatchHistoryPath, "json", fullHistoryUrl.FilenameCharactersEscape());
                if (!File.Exists(path))
                {
                    Console.WriteLine("Retrieving " + fullHistoryUrl + " ...");
                    var resp = hc.Get(fullHistoryUrl);
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        File.WriteAllText(path, "404");
                    else
                    {
                        var data = resp.Expect(HttpStatusCode.OK).DataString;
                        var tryJson = JsonDict.Parse(data);
                        Ut.Assert(tryJson["participantIdentities"].GetList().Any(l => l["player"]["summonerName"].GetString() == game.Summoner)); // a bit redundant, but makes sure we don't save this if something went wrong
                        File.WriteAllText(path, data);
                    }
                }
                var json = File.ReadAllText(path);
                game.RawJson = json == "404" ? null : JsonDict.Parse(json);
                if (game.RawJson != null)
                {
                    Ut.Assert(game.RawJson["participantIdentities"].GetList().Any(l => l["player"]["summonerName"].GetString() == game.Summoner));
                    game.ReadJson();
                }
            }

            var output = new StringBuilder();
            var result =
                games.Where(g => g.Type != "Custom").GroupBy(g => g.Map + ", " + g.Type).OrderByDescending(g => g.First().Map).Select(grp => Ut.NewArray<object>(
                    new H1(grp.Key),
                    genOverallStats(grp),
                    KnownPlayers.Where(pname => grp.Count(g => g.Ally.Players.Any(pp => pp.Name == pname)) > 5).Select(
                        pname => genStats(pname, grp.Where(g => g.Ally.Players.Any(pp => pp.Name == pname)))
                    ),
                    new H4("All games"),
                    new TABLE(
                        grp.OrderByDescending(g => g.Date).Select(g =>
                            {
                                var allies = g.Ally.Players.Select(plr => getPlayerHtml(plr)).ToList();
                                var enemies = g.Enemy.Players.Select(plr => getPlayerHtml(plr)).ToList();
                                return Ut.NewArray<object>(
                                    new TR(
                                        new TD { rowspan = 2, class_ = "nplr datetime" }._(new A(g.Date.ToString("dd/MM/yy"), new BR(), g.Date.ToString("HH:mm")) { href = g.DetailsUrl }),
                                        new TD { rowspan = 2, class_ = "nplr" }._(g.Detail(() => g.Duration.TotalMinutes.ToString("00") + ":" + g.Duration.Seconds.ToString("00"))),
                                        new TD { rowspan = 2, class_ = "nplr " + g.Victory.NullTrueFalse("draw", "victory", "defeat") }._(g.Victory.NullTrueFalse("Draw", "Victory", "Defeat")),
                                        new TD { rowspan = 2, class_ = "sep" },
                                        allies.Select(p => p[0]),
                                        new TD { rowspan = 2, class_ = "sep" },
                                        enemies.Select(p => p[0]),
                                        new TD { rowspan = 2, class_ = "sep" },
                                        new TD { class_ = "nplr", rowspan = 2 }._(new A("replay") { href = g.ReplayUrl })
                                    ),
                                    new TR(
                                        allies.Select(p => p[1]),
                                        enemies.Select(p => p[1])
                                    )
                                );
                            }
                        )
                    )
                ));
            File.WriteAllText(@"C:\Downloads\HttpServer\download\league.html", new HTML(new HEAD(new STYLELiteral(@"
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

td.redacted84 { background: #D1FECC; }
td.redacted49 { background: #6EFFFF; }
td.redacted60 { background: #6EFFFF; }
td.GasTheHues { background: #6EFFFF; }
            ")), new BODY(result)).ToString());
        }

        private static object genOverallStats(IEnumerable<Game> games)
        {
            var result = new List<object>();
            var allOtherPlayers = games.SelectMany(g => g.Ally.Players.Concat(g.Enemy.Players)).Where(p => !KnownPlayers.Contains(p.Name));
            var allOtherChamps = allOtherPlayers.GroupBy(p => p.ChampionId);
            result.Add(new H4("Champions encountered (excluding our own selections)"));
            result.Add(new P(allOtherChamps.OrderByDescending(g => g.Count()).Select(g => Champions[g.Key] + ": " + g.Count()).JoinString(", ")));
            return result;
        }

        private static object genStats(string playerName, IEnumerable<Game> games)
        {
            var result = new List<object>();
            var makeSummaryTable = Ut.Lambda((string label, IEnumerable<IGrouping<string, Player>> set) =>
                {
                    return new TABLE { class_ = "ra" }._(
                        new TR(new TH(label) { rowspan = 2 }, new TH("Games") { rowspan = 2 }, new TH("Wins") { rowspan = 2 }, new TH("Losses") { rowspan = 2 }, new TH("Win%") { rowspan = 2 },
                            new TH("Kills/deaths/assists") { colspan = 6 }, new TH("Max multi") { rowspan = 2 }, new TH("Dmg champ") { colspan = 2 },
                            new TH("CS @ 10m") { colspan = 2 }, new TH("Gold @ 10m") { colspan = 2 }),
                        new TR(
                            new TH("Avg/30m") { colspan = 3 }, new TH("Max") { colspan = 3 }, new TH("Avg/30m"), new TH("Max"),
                            new TH("Avg"), new TH("Max"), new TH("Avg"), new TH("Max")),
                        set.OrderByDescending(g => g.Count()).Select(g => new TR(
                            new TD(g.Key) { class_ = "la" },
                            new TD(g.Count()),
                            new TD(g.Count(p => p.Team.Victory)),
                            new TD(g.Count(p => !p.Team.Victory)),
                            new TD(g.Count() <= 5 ? "-" : (g.Count(p => p.Team.Victory) / (double) g.Count() * 100).ToString("0'%'")),
                            //new TD("{0:0.0} / {1:0.0} / {2:0.0}".Fmt(g.Average(p => p.Kills), g.Average(p => p.Deaths), g.Average(p => p.Assists))),
                            new TD("{0:0.0}".Fmt(g.Average(p => p.Kills / p.Game.Duration.TotalMinutes * 30))),
                            new TD("{0:0.0}".Fmt(g.Average(p => p.Deaths / p.Game.Duration.TotalMinutes * 30))),
                            new TD("{0:0.0}".Fmt(g.Average(p => p.Assists / p.Game.Duration.TotalMinutes * 30))),
                            new TD("{0:0}".Fmt(g.Max(p => p.Kills))),
                            new TD("{0:0}".Fmt(g.Max(p => p.Deaths))),
                            new TD("{0:0}".Fmt(g.Max(p => p.Assists))),
                            new TD(g.Max(p => p.LargestMultiKill)),
                            new TD(g.Average(p => p.DamageToChampions / p.Game.Duration.TotalMinutes * 30).ToString("#,0")),
                            new TD(g.Max(p => p.DamageToChampions).ToString("#,0")),
                            new TD("{0:0}".Fmt(g.Average(p => p.CreepsAt10))),
                            new TD("{0:0}".Fmt(g.Max(p => p.CreepsAt10))),
                            new TD("{0:0}".Fmt(g.Average(p => p.GoldAt10))),
                            new TD("{0:0}".Fmt(g.Max(p => p.GoldAt10)))
                        ))
                    );
                });

            result.Add(new H4("{0} stats: by champion".Fmt(playerName)));
            result.Add(makeSummaryTable("Champion", games.Select(g => g.Plr(playerName)).GroupBy(p => Champions[p.ChampionId])));
            result.Add(new H4("{0} stats: by lane/role".Fmt(playerName)));
            result.Add(makeSummaryTable("Lane/role", games.Select(g => g.Plr(playerName)).GroupBy(p => (p.Lane == Lane.Top ? "Top" : p.Lane == Lane.Middle ? "Mid" : p.Lane == Lane.Jungle ? "JG" : "Bot") + (p.Role == "DUO_CARRY" ? " adc" : p.Role == "DUO_SUPPORT" ? " sup" : ""))));
            result.Add(new H4("{0} stats: total".Fmt(playerName)));
            result.Add(makeSummaryTable("Total", games.Select(g => g.Plr(playerName)).GroupBy(p => "Total")));
            var id = Rnd.NextBytes(8).ToHex();
            return Ut.NewArray<object>(new BUTTON("Show/hide stats for {0}".Fmt(playerName)) { onclick = "document.getElementById('{0}').style.display = (document.getElementById('{0}').style.display == 'none') ? 'block' : 'none';".Fmt(id) }, new DIV(result) { id = id, style = "display:none" });
        }

        private static Tag[] getPlayerHtml(Player plr)
        {
            return new[] {
                new TD{class_="plr-top "+plr.Name.Replace(" ", "")}._(new DIV(plr.Name) { class_="plrname" },
                    "{0}/{1}/{2}".Fmt(plr.Kills, plr.Deaths, plr.Assists)),
                new TD{class_="plr-bot "+plr.Name.Replace(" ", "")}._(
                    new DIV(plr.LargestMultiKill <= 1 ? "" : (plr.LargestMultiKill + "x")) { class_ = "multi multi" + plr.LargestMultiKill },
                    Champions[plr.ChampionId],
                    " ", plr.Lane == Lane.Top ? "(top)" : plr.Lane == Lane.Middle ? "(mid)" : plr.Lane == Lane.Jungle ? "(jg)" : plr.Role == "DUO_CARRY" ? "(adc)" : plr.Role == "DUO_SUPPORT" ? "(sup)" : "(bot)")
            };
        }
    }

    class Game
    {
        public DateTime Date;
        public TimeSpan Duration;
        public string DetailsUrl;
        public string ReplayUrl;
        public string Subj;
        public string Summoner;
        public string Champion;
        public string Map;
        public string Type;
        public bool? Victory;
        public string Region, Id, AccountId;
        public JsonDict RawJson;
        public Team Ally, Enemy;
        public Player Me { get { return Ally.Players.Single(p => p.SummonerId.ToString() == AccountId); } }
        public Player Plr(string name) { return Ally.Players.Single(p => p.Name == name); }

        public T Detail<T>(Func<T> f) where T : class { return RawJson == null ? null : f(); }

        public void ReadJson()
        {
            Date = (new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc) + TimeSpan.FromSeconds(RawJson["gameCreation"].GetLong() / 1000.0)).ToLocalTime();
            Duration = TimeSpan.FromSeconds(RawJson["gameDuration"].GetInt());
            var players = RawJson["participantIdentities"].GetList().Select(p =>
            {
                var result = new Player();
                result.ParticipantId = p["participantId"].GetInt();
                result.AccountId = p["player"]["accountId"].GetLong();
                result.SummonerId = p["player"]["summonerId"].GetLong();
                result.Name = p["player"]["summonerName"].GetString();
                return result;
            }).ToDictionary(plr => plr.ParticipantId);
            foreach (var p in RawJson["participants"].GetList())
            {
                var pp = players[p["participantId"].GetInt()];
                pp.TeamId = p["teamId"].GetInt();
                pp.ChampionId = p["championId"].GetInt();
                pp.Spell1Id = p["spell1Id"].GetInt();
                pp.Spell2Id = p["spell2Id"].GetInt();
                pp.Role = p["timeline"]["role"].GetString();
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
                pp.CreepsAt10 = (int) (timeline["creepsPerMinDeltas"]["0-10"].GetDouble() * 10);
                pp.GoldAt10 = (int) (timeline["goldPerMinDeltas"]["0-10"].GetDouble() * 10);
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
                Team = new Team { Players = g.OrderBy(p => p.Lane).ThenBy(p => p.Role == "DUO_SUPPORT" ? 0 : p.Role == "DUO" ? 2 : p.Role == "DUO_CARRY" ? 3 : 1).ToList() },
                Json = RawJson["teams"].GetList().Single(t => t["teamId"].GetInt() == g.First().TeamId)
            });
            Ut.Assert(teams.Count == 2);
            var ourTeamId = players.Values.Single(p => p.Name == Summoner).TeamId;
            Ally = teams[ourTeamId].Team;
            Enemy = teams[teams.Keys.Where(k => k != ourTeamId).Single()].Team;
            foreach (var t in teams.Values)
            {
                var team = t.Team;
                var json = t.Json;
                team.Victory = json["win"].GetString() == "Win" ? true : json["win"].GetString() == "Fail" ? false : Ut.Throw<bool>(new Exception());
                foreach (var p in team.Players)
                {
                    p.Team = team;
                    p.Game = this;
                }
            }
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
        public int Spell1Id, Spell2Id;
        public string Role;
        public Lane Lane;
        public int Kills, Deaths, Assists;
        public int DamageToChampions, TotalHeal, TotalDamageTaken;
        public int LargestMultiKill;
        public int CreepsAt10, CreepsAt20, CreepsAt30;
        public int GoldAt10, GoldAt20, GoldAt30;
    }

    enum Lane { Top, Jungle, Middle, Bottom }
}
