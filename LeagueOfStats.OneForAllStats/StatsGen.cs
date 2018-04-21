using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LeagueOfStats.GlobalData;
using LeagueOfStats.StaticData;
using RT.Util;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;
using RT.Util.Json;
using RT.Util.Paths;

namespace LeagueOfStats.OneForAllStats
{
    class StatsGen
    {
        class Match1FA
        {
            public string MatchId, Champion1, Champion2, Winner;
            public DateTime StartTime;
            public string GameVersion;
        }

        private static List<T> LoadAllMatches<T>(string dataPath, string version, int queueId, Func<JsonValue, Region, T> loader)
        {
            var matches = new List<T>();
            foreach (var f in new PathManager(dataPath).GetFiles().OrderBy(f => f.FullName))
            {
                var match = Regex.Match(f.Name, $@"^(?<region>[A-Z]+)-matches-{version}-{queueId}\.losjs$");
                if (!match.Success)
                    continue;
                var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                Console.Write($"Loading {f.FullName}... ");
                var thread = new CountThread(10000);
                matches.AddRange(new JsonContainer(f.FullName).ReadItems().Select(json => loader(json, region)).PassthroughCount(thread.Count));
                thread.Stop();
                Console.WriteLine();
                writeLine($"Loaded {thread.Count} matches from {f.FullName} in {thread.Duration.TotalSeconds:#,0.000} s");
            }
            return matches;
        }

        public static void GenerateOneForAll(string dataPath, string dataSuffix)
        {
            LeagueStaticData.Load(Path.Combine(dataPath, "Static"));
            writeLine($"Generating stats at {DateTime.Now}...");

            // Load matches
            var matches = LoadAllMatches(Path.Combine(dataPath, $"Global{dataSuffix}"), "8.6", 1020, match1FAFromJson)
                .Concat(LoadAllMatches(Path.Combine(dataPath, $"Global{dataSuffix}"), "8.7", 1020, match1FAFromJson))
                .ToList();
            // Remove duplicates
            var hadCount = matches.Count;
            matches = matches.GroupBy(m => m.MatchId).Select(m => m.First()).ToList();
            writeLine($"Distinct matches: {matches.Count:#,0} (duplicates removed: {hadCount - matches.Count:#,0})");
            writeLine($"Distinct matchups: {matches.GroupBy(m => new { m.Champion1, m.Champion2 }).Count():#,0} / {9453 + 138:#,0}"); // 138 choose 2 + 138 mirror matchups (Teemo/Karthus not allowed)
            // Champions seen
            var champions = matches.Select(m => m.Champion1).Concat(matches.Select(m => m.Champion2)).Distinct().ToList();

            generateOneForAll("s-all", matches, champions);
            foreach (var date in matches.Select(m => m.StartTime.Date).Distinct().Order())
                generateOneForAll($"s-date-{date:yyyy-MM-dd}", matches.Where(m => m.StartTime.Date == date), champions);
            foreach (var ver in matches.Select(m => m.GameVersion).Distinct().Order())
                generateOneForAll($"s-ver-{ver}", matches.Where(m => m.GameVersion == ver), champions);
        }

        private static void generateOneForAll(string prefix, IEnumerable<Match1FA> matches, List<string> champions)
        {
            // All the match IDs
            writeAllLines($"{prefix}-match-ids.txt", matches.Select(m => m.MatchId).Order());

            // All possible matchup stats
            {
                var grps = newDict(new { ch1 = "", ch2 = "" }, new List<Match1FA>(), _ => new List<Match1FA>());
                foreach (var m in matches)
                    grps[new { ch1 = m.Champion1, ch2 = m.Champion2 }].Add(m);
                var empty = new List<Match1FA>();
                var statsMatchupsAll = champions.SelectMany(ch1 => champions.Select(ch2 => new { ch1, ch2 })).Where(key => key.ch1.CompareTo(key.ch2) <= 0).Select(key =>
                {
                    if (!grps.ContainsKey(key))
                        return new { Champion1 = key.ch1, Champion2 = key.ch2, Count = 0, WinRate = 0.5, Lower95 = 0.0, Upper95 = 1.0, Lower67 = 0.0, Upper67 = 1.0 };
                    var n = grps[key].Count;
                    if (key.ch1 == key.ch2)
                        return new { Champion1 = key.ch1, Champion2 = key.ch2, Count = n, WinRate = 0.5, Lower95 = 0.5, Upper95 = 0.5, Lower67 = 0.5, Upper67 = 0.5 };
                    var p = winrate(grps[key], key.ch1);
                    var conf95 = getWilson(p, n, 1.96);
                    var conf67 = getWilson(p, n, 0.97);
                    return new { Champion1 = key.ch1, Champion2 = key.ch2, Count = n, WinRate = p, Lower95 = conf95.lower, Upper95 = conf95.upper, Lower67 = conf67.lower, Upper67 = conf67.upper };
                }).ToList();
                statsMatchupsAll = statsMatchupsAll.Concat(statsMatchupsAll
                        .Where(m => m.Champion1 != m.Champion2)
                        .Select(m => new { Champion1 = m.Champion2, Champion2 = m.Champion1, m.Count, WinRate = 1 - m.WinRate, Lower95 = 1 - m.Upper95, Upper95 = 1 - m.Lower95, Lower67 = 1 - m.Upper67, Upper67 = 1 - m.Lower67 })
                    ).OrderBy(m => m.Champion1).ThenBy(m => m.Champion2).ToList();
                writeAllLines($"{prefix}-matchupsall.csv", statsMatchupsAll.Select(l => $"{l.Champion1},{l.Champion2},{l.Count},{l.WinRate},{l.Lower95},{l.Upper95},{l.Lower67},{l.Upper67}"));
            }

            // Champion stats at champ select stage (unknown enemy)
            {
                var grps = new AutoDictionary<string, List<Match1FA>>(_ => new List<Match1FA>());
                foreach (var m in matches)
                {
                    grps[m.Champion1].Add(m);
                    if (m.Champion1 != m.Champion2)
                        grps[m.Champion2].Add(m);
                }
                var statsChampSelect = champions.Select(champion =>
                {
                    var n = grps[champion].Count;
                    var p = winrate(grps[champion], champion);
                    var conf95 = getWilson(p, n, 1.96);
                    // Bans
                    var remaining = grps[champion]; // bans calculation is destructive as we won't need the original list anyway
                    var bans = new string[5];
                    var banWR = new double[5];
                    for (int i = 0; i < 5; i++)
                    {
                        var banResult = champions.Where(ban => ban != champion).Select(ban => new { ban, wr = winrate(matches1FAWithout(remaining, ban), champion) }).MaxElement(x => x.wr);
                        bans[i] = banResult.ban;
                        banWR[i] = banResult.wr;
                        remaining = matches1FAWithout(remaining, bans[i]).ToList();
                    }
                    return new { champion, Count = n, WinRate = p, Lower95 = conf95.lower, Upper95 = conf95.upper, bans, banWR };
                }).OrderBy(r => r.champion).ToList();
                writeAllLines($"{prefix}-champselect.csv", statsChampSelect.Select(l => $"{l.champion},{l.Count},{l.WinRate},{l.Lower95},{l.Upper95},{l.bans[0]},{l.banWR[0]},{l.bans[1]},{l.banWR[1]},{l.bans[2]},{l.banWR[2]},{l.bans[3]},{l.banWR[3]},{l.bans[4]},{l.banWR[4]}"));
            }
        }

        private static AutoDictionary<TKey, TValue> newDict<TKey, TValue>(TKey _, TValue __, Func<TKey, TValue> init)
        {
            return new AutoDictionary<TKey, TValue>(init);
        }

        private static void writeAllLines(string filename, IEnumerable<string> lines)
        {
            Ut.WaitSharingVio(() => File.WriteAllLines(filename, lines), onSharingVio: () => Console.WriteLine($"File \"{filename}\" is in use; waiting..."));
        }

        private static Match1FA match1FAFromJson(JsonValue json, Region region)
        {
            Ut.Assert(json["gameMode"].GetString() == "ONEFORALL");
            var teamW = json["teams"].GetList().Single(tj => tj["win"].GetString() == "Win")["teamId"].GetInt();
            var teamL = json["teams"].GetList().Single(tj => tj["win"].GetString() == "Fail")["teamId"].GetInt();
            Ut.Assert(teamW != teamL);
            var champW = LeagueStaticData.Champions[json["participants"].GetList().Where(pj => pj["teamId"].GetInt() == teamW).First()["championId"].GetInt()].Name;
            var champL = LeagueStaticData.Champions[json["participants"].GetList().Where(pj => pj["teamId"].GetInt() == teamL).First()["championId"].GetInt()].Name;
            var ver = Version.Parse(json["gameVersion"].GetString());
            return new Match1FA
            {
                MatchId = region + json["gameId"].GetStringLenient(),
                Champion1 = champW.CompareTo(champL) <= 0 ? champW : champL,
                Champion2 = champW.CompareTo(champL) <= 0 ? champL : champW,
                Winner = champW,
                GameVersion = string.Intern(ver.Major + "." + ver.Minor),
                StartTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) + TimeSpan.FromSeconds(json["gameCreation"].GetLong() / 1000.0),
            };
        }

        private static void writeLine(string line)
        {
            Console.WriteLine(line);
            File.AppendAllLines("StatsGen.output.txt", new[] { line });
        }

        private static double winrate(IEnumerable<Match1FA> matches, string champion)
        {
            double sum = 0;
            int count = 0;
            foreach (var m in matches)
            {
                count++;
                if (m.Winner == champion)
                    sum += m.Champion1 == m.Champion2 ? 0.5 : 1;
            }
            return sum / count;
        }

        private static IEnumerable<Match1FA> matches1FAWithout(IEnumerable<Match1FA> matches, string champion)
        {
            return matches.Where(m => m.Champion1 != champion && m.Champion2 != champion);
        }

        private static (double lower, double upper) getWilson(double p, int n, double z)
        {
            // https://github.com/msn0/wilson-score-interval/blob/master/index.js
            // z is 1-alpha/2 percentile of a standard normal distribution for error alpha=5%
            // 95% confidence = 0.975 percentile = 1.96
            // 67% confidence = 0.833 percentile = 0.97
            var a = p + z * z / (2 * n);
            var b = z * Math.Sqrt((p * (1 - p) + z * z / (4 * n)) / n);
            var c = 1 + z * z / n;
            return ((a - b) / c, (a + b) / c);
        }

        public static void GenerateSR5v5(string dataPath, string dataSuffix, string version)
        {
            LeagueStaticData.Load(Path.Combine(dataPath, "Static"));
            writeLine($"Generating stats at {DateTime.Now}...");

            // Load matches
            dataPath = Path.Combine(dataPath, $"Global{dataSuffix}");
            var matches = LoadAllMatches(dataPath, version, 420, matchSRFromJson) // ranked solo
                .Concat(LoadAllMatches(dataPath, version, 400, matchSRFromJson)) // draft pick
                .Concat(LoadAllMatches(dataPath, version, 430, matchSRFromJson)) // blind pick
                .ToList();
            // Remove duplicates
            var hadCount = matches.Count;
            matches = matches.GroupBy(m => m.MatchId).Select(m => m.First()).ToList();
            writeLine($"Distinct matches: {matches.Count:#,0} (duplicates removed: {hadCount - matches.Count:#,0})");

            var champions = LeagueStaticData.Champions.Values.Select(ch => ch.Name).Order().ToList();

            genSRLaneMatchups("mid", matches.Select(m => m.Mid).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList());
            genSRLaneMatchups("top", matches.Select(m => m.Top).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList());
            genSRLaneMatchups("adc", matches.Select(m => m.Adc).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList());
            genSRLaneMatchups("jun", matches.Select(m => m.Jun).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList());
            genSRLaneMatchups("sup", matches.Select(m => m.Sup).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList());
        }

        private static void genSRLaneMatchups(string lane, List<LaneSR> matches)
        {
            writeLine($"Usable {lane} matchups: {matches.Count:#,0}");
            var byChamp = new AutoDictionary<string, List<LaneSR>>(_ => new List<LaneSR>());
            foreach (var m in matches)
            {
                byChamp[m.ChampW].Add(m);
                byChamp[m.ChampL].Add(m);
            }

            var overallPopularity = byChamp.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count / (double) matches.Count / 2);

            foreach (var kvp in percentile(byChamp, 0.95, kvp => kvp.Value.Count))
            {
                writeLine($"Generating lane stats for {lane} - {kvp.Key}...");
                genSRLaneMatchup(kvp.Value, kvp.Key, lane, overallPopularity, (string line) => { File.AppendAllLines($"{lane} - {kvp.Key}.txt", new[] { line }); });
            }
        }

        private static void genSRLaneMatchup(List<LaneSR> matches, string champ, string lane, Dictionary<string, double> overallPopularity, Action<string> writeLine)
        {
            writeLine("");
            writeLine("");
            writeLine("");
            writeLine($"Usable matches for {lane} {champ}: {matches.Count:#,0}");
            var overallWinrate = matches.Count(m => m.ChampW == champ) / (double) matches.Count;
            {
                var conf95 = getWilson(overallWinrate, matches.Count, 1.96);
                writeLine($"Overall win rate: {overallWinrate * 100:0.0}% ({conf95.lower * 100:0}% - {conf95.upper * 100:0}%)");
                File.AppendAllLines($"_overall_ - {lane}.txt", new[] { $"{champ,-15}, {overallWinrate * 100:0.0}%, {matches.Count,6}, {conf95.lower * 100:0.0}%, {conf95.upper * 100:0.0}%" });
            }

            var matchups = matches.GroupBy(m => m.Other(champ)).ToDictionary(grp => grp.Key, grp => grp.ToList()).Select(kvp =>
            {
                var enemy = kvp.Key;
                var p = kvp.Value.Count(m => m.ChampW == champ) / (double) kvp.Value.Count;
                var conf95 = getWilson(p, kvp.Value.Count, 1.96);
                return new { enemy, winrate = p, count = kvp.Value.Count, conf95, popularity = kvp.Value.Count / (double) matches.Count };
            }).ToDictionary(m => m.enemy);

            var excessivelyPopularEnemies = percentile(matchups.Values.OrderByDescending(m => m.count), 0.95, m => m.count)
                .Select(m => new { m.enemy, excessPopularity = m.popularity - overallPopularity[m.enemy] })
                .Where(m => m.excessPopularity > 0.001)
                .OrderByDescending(m => m.excessPopularity)
                .Take(10)
                .ToList();
            writeLine("");
            writeLine("Excessively popular enemies:");
            foreach (var epe in excessivelyPopularEnemies)
                writeLine($"{champ} vs {epe.enemy,-15} pop: +{epe.excessPopularity * 100:0.0}%, wr: {(matchups[epe.enemy].winrate - overallWinrate) * 100:+0.0'% (!!!)';-0.0'%      '; 0.0'%      '}  ({matchups[epe.enemy].count:#,0} matches)");

            var bans = LeagueStaticData.Champions.Values.Select(ch => ch.Name).Where(ch => ch != champ).Select(ban =>
            {
                var ms = matches.Where(m => m.Other(champ) != ban).ToList();
                var winrate = ms.Count == 0 ? -1 : ms.Count(m => m.ChampW == champ) / (double) ms.Count;
                var msAll = matches.Where(m => !m.Match.Has(ban)).ToList();
                var winrateAll = msAll.Count == 0 ? -1 : msAll.Count(m => m.ChampW == champ) / (double) msAll.Count;
                return new { ban, winrate, winrateAll, count = ms.Count, countAll = msAll.Count };
            }).ToList();
            writeLine("");
            writeLine($"Bans for {champ}:");
            foreach (var b in bans.OrderByDescending(b => b.winrate).Take(5))
                writeLine($"  {b.ban,-15} {(b.winrate - overallWinrate) * 100:+0.0;-0.0; 0.0}% winrate ({b.count:#,0} matches, {matches.Count - b.count:#,0} banned)");

            writeLine("");
            writeLine($"All lane bans for {champ}:");
            foreach (var b in bans.OrderByDescending(b => b.winrateAll).Take(5))
                writeLine($"  {b.ban,-15} {(b.winrateAll - overallWinrate) * 100:+0.0;-0.0; 0.0}% winrate ({b.countAll:#,0} matches, {matches.Count - b.countAll:#,0} banned)");

            writeLine("");
            writeLine("Most frequent matchups (50%)");
            foreach (var mu in percentile(matchups.Values.OrderByDescending(m => m.count), 0.50, m => m.count).OrderByDescending(m => m.winrate))
                writeLine($"{champ} vs {mu.enemy,-15} {mu.winrate:0.0000}, {mu.count,5}            {mu.conf95.lower:0.0000} - {mu.conf95.upper:0.0000}");
            writeLine("");
            writeLine("Almost all matchups (95%)");
            foreach (var mu in percentile(matchups.Values.OrderByDescending(m => m.count), 0.95, m => m.count).OrderByDescending(m => m.winrate))
                writeLine($"{champ} vs {mu.enemy,-15} {mu.winrate:0.0000}, {mu.count,5}            {mu.conf95.lower:0.0000} - {mu.conf95.upper:0.0000}");
            writeLine("");
            writeLine("Remaining matchups (...5%)");
            foreach (var mu in percentile(matchups.Values.OrderBy(m => m.count), 0.05, m => m.count).OrderByDescending(m => m.winrate))
                writeLine($"{champ} vs {mu.enemy,-15} {mu.winrate:0.0000}, {mu.count,5}            {mu.conf95.lower:0.0000} - {mu.conf95.upper:0.0000}");
            writeLine("");
        }

        private static IEnumerable<T> percentile<T>(IEnumerable<T> coll, double perc, Func<T, double> by)
        {
            double max = coll.Sum(by);
            double sum = 0;
            foreach (var el in coll)
            {
                sum += by(el);
                if (sum > perc * max)
                    yield break;
                yield return el;
            }
        }

        struct LaneSR
        {
            public string ChampW;
            public string ChampL;
            public MatchSR Match;
            public string Other(string champ) => champ == ChampW ? ChampL : champ == ChampL ? ChampW : throw new Exception();
            public bool Has(string champ) => champ == ChampW || champ == ChampL;
        }

        class MatchSR
        {
            public string MatchId;
            public LaneSR Mid, Top, Jun, Adc, Sup;
            public string GameVersion;
            public DateTime StartTime;

            public bool Has(string champ) => Mid.Has(champ) || Top.Has(champ) || Jun.Has(champ) || Adc.Has(champ) || Sup.Has(champ);
        }

        private static MatchSR matchSRFromJson(JsonValue json, Region region)
        {
            Ut.Assert(json["gameMode"].GetString() == "CLASSIC");
            var teamW = json["teams"].GetList().Single(tj => tj["win"].GetString() == "Win")["teamId"].GetInt();
            var teamL = json["teams"].GetList().Single(tj => tj["win"].GetString() == "Fail")["teamId"].GetInt();
            Ut.Assert(teamW != teamL);
            var champsW = json["participants"].GetList().Where(pj => pj["teamId"].GetInt() == teamW).ToList();
            var champsL = json["participants"].GetList().Where(pj => pj["teamId"].GetInt() == teamL).ToList();
            var ver = Version.Parse(json["gameVersion"].GetString());
            var result = new MatchSR
            {
                MatchId = region + json["gameId"].GetStringLenient(),
                Mid = new LaneSR { ChampW = findChampion(champsW, "MIDDLE", "SOLO"), ChampL = findChampion(champsL, "MIDDLE", "SOLO") },
                Top = new LaneSR { ChampW = findChampion(champsW, "TOP", "SOLO"), ChampL = findChampion(champsL, "TOP", "SOLO") },
                Jun = new LaneSR { ChampW = findChampion(champsW, "JUNGLE", "NONE"), ChampL = findChampion(champsL, "JUNGLE", "NONE") },
                Adc = new LaneSR { ChampW = findChampion(champsW, "BOTTOM", "DUO_CARRY"), ChampL = findChampion(champsL, "BOTTOM", "DUO_CARRY") },
                Sup = new LaneSR { ChampW = findChampion(champsW, "BOTTOM", "DUO_SUPPORT"), ChampL = findChampion(champsL, "BOTTOM", "DUO_SUPPORT") },
                GameVersion = string.Intern(ver.Major + "." + ver.Minor),
                StartTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) + TimeSpan.FromSeconds(json["gameCreation"].GetLong() / 1000.0),
            };
            result.Mid.Match = result;
            result.Top.Match = result;
            result.Jun.Match = result;
            result.Adc.Match = result;
            result.Sup.Match = result;
            return result;
        }

        private static string findChampion(List<JsonValue> champs, string lane, string role)
        {
            var json = champs.FirstOrDefault(pj => pj["timeline"]["lane"].GetString() == lane && pj["timeline"]["role"].GetString() == role);
            if (json == null)
                return null;
            return LeagueStaticData.Champions[json["championId"].GetInt()].Name;
        }
    }
}
