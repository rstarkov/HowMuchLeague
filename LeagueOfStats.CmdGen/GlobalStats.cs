using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LeagueOfStats.GlobalData;
using LeagueOfStats.StaticData;
using RT.TagSoup;
using RT.Util;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;
using RT.Util.Json;
using RT.Util.Paths;

namespace LeagueOfStats.CmdGen
{
    static class GlobalStats
    {
        class Match1FA
        {
            public string MatchId, Champion1, Champion2, Winner;
            public DateTime StartTime;
            public string GameVersion;
        }

        struct ginfo
        {
            public long Id;
            public DateTime Date;
            public int QueueId;
        }
        class day
        {
            public double TotalMatchCount = 0;
        }

        internal static void GenerateGameCounts(string dataPath)
        {
            DataStore.Initialise(dataPath, "", autoRewrites: false);
            foreach (var region in DataStore.LosMatchInfos.Keys)
            {
                var entries = DataStore.LosMatchInfos[region].ReadItems().Select(m => new ginfo { Id = m.MatchId, Date = m.GameCreationDate, QueueId = remapQueue(m.QueueId) })
                    .Concat(DataStore.LosMatchIdsNonExistent[region].ReadItems().Select(id => new ginfo { Id = id }))
                    .OrderBy(e => e.Id)
                    .ToArray();
                int span = 15_000;
                int iFr = 0;
                int iCur = 0;
                int iTo = 0;
                int existing = 0;
                var days = new AutoDictionary<DateTime, int, day>((_, __) => new day());
                var queues = new HashSet<int>();
                while (iCur < entries.Length)
                {
                    while (iTo < entries.Length - 1 && entries[iTo].Id - entries[iCur].Id < span)
                    {
                        if (entries[iTo].Date != default(DateTime))
                            existing++;
                        iTo++;
                    }
                    while (entries[iCur].Id - entries[iFr].Id > span)
                    {
                        if (entries[iFr].Date != default(DateTime))
                            existing--;
                        iFr++;
                    }
                    double count = iTo - iFr + 1;
                    if (entries[iCur].Date != default(DateTime))
                    {
                        queues.Add(entries[iCur].QueueId);
                        var estimatedCoverage = count / (entries[iTo].Id - entries[iFr].Id + 1);
                        var estimatedExisting = existing / count;
                        var estimatedMatchCount = (1.0 / estimatedCoverage) * estimatedExisting;
                        days[entries[iCur].Date.Date][entries[iCur].QueueId].TotalMatchCount += estimatedMatchCount;
                    }
                    iCur++;
                }

                var dateMin = days.Keys.Min();
                var dateMax = days.Keys.Max();
                var queues2 = queues.Order().ToList();
                var dates = Enumerable.Range(0, (int) (dateMax - dateMin).TotalDays + 1).Select(d => dateMin.AddDays(d));
                File.WriteAllLines($"count-daily-{region}.csv",
                    new[] { (new[] { "Date" }.Concat(queues2.Select(q => queueName(q))).JoinString(",")) }
                    .Concat(
                        dates.Select(d => new[] { $"{d:dd/MM/yyyy}" }.Concat(queues2.Select(q => $"{days[d][q].TotalMatchCount:0}")).JoinString(","))
                    ));
                File.WriteAllLines($"count-weekly-{region}.csv",
                    new[] { (new[] { "Date" }.Concat(queues2.Select(q => queueName(q))).JoinString(",")) }
                    .Concat(
                        dates.GroupBy(d => ((int) (d - dateMin).TotalDays) / 7).Select(grp => new[] { $"{grp.First():dd/MM/yyyy}" }.Concat(queues2.Select(q => $"{grp.Sum(d => days[d][q].TotalMatchCount):0}")).JoinString(","))
                    ));

                // time of day
                // day of week
                // duration over time
                // champion winrate at release for each champion in every lane
            }
        }

        private static string queueName(int queueId)
        {
            switch (queueId)
            {
                case 76: return "URF";
                case 78: return "1FA Mirr";
                case 98: return "Hexakill";
                case 310: return "Nemesis";
                case 325: return "SR ARAM";
                case 400: return "5v5 Drf";
                case 420: return "5v5 Rnk";
                case 430: return "5v5 Bli";
                case 440: return "5v5 R.Fl";
                case 450: return "ARAM";
                case 600: return "Bld Hunt";
                case 610: return "DarkStar";
                case 700: return "Clash";
                case 900: return "ARURF";
                case 920: return "PoroKing";
                case 940: return "Nx.Siege";
                case 1000: return "Ovrchg";
                case 1020: return "1FA";

                default: return queueId.ToString();
            }
        }

        private static int remapQueue(int queueId)
        {
            switch (queueId)
            {
                case 2: return 430;
                case 4: return 420;
                //case 7: return 32, 33;
                case 8: return 460;
                case 9: return 470;
                case 14: return 400;
                case 31: return 830;
                case 32: return 840;
                case 33: return 850;
                case 52: return 800;
                case 65: return 450;
                case 70: return 1020;
                //case 91: case 92: case 93: return 950;
                case 96: return 910;
                case 300: return 920;
                case 315: return 940;
                case 318: return 900;
                case 1010: return 900; // snow arurf
                default: return queueId;
            }
        }

        private static IEnumerable<JsonValue> LoadMatches(string dataPath, Func<(Region region, string version, int queueId), bool> fileFilter)
        {
            foreach (var f in new PathManager(dataPath).GetFiles().OrderBy(f => f.FullName))
            {
                var match = Regex.Match(f.Name, $@"^(?<region>[A-Z]+)-matches-(?<version>[0-9.]+)-(?<queue>\d+)\.losjs$");
                if (!match.Success)
                    continue;
                var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                var version = match.Groups["version"].Value;
                var queueId = int.Parse(match.Groups["queue"].Value);
                if (!fileFilter((region, version, queueId)))
                    continue;
                Console.Write($"Loading {f.FullName}... ");
                var thread = new CountThread(10000);
                foreach (var m in new JsonContainer(f.FullName).ReadItems().PassthroughCount(thread.Count))
                    yield return m;
                thread.Stop();
                Console.WriteLine();
                writeLine($"Loaded {thread.Count} matches from {f.FullName} in {thread.Duration.TotalSeconds:#,0.000} s");
            }
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

        public static void GenerateTotalKDA(string dataPath)
        {
            var kills = new Dictionary<string, int>();
            var deaths = new Dictionary<string, int>();
            var games = new Dictionary<string, int>();
            foreach (var m in LoadMatches(Path.Combine(dataPath, $"Global"), f => f.queueId == 4 || f.queueId == 6 || f.queueId == 410 || f.queueId == 420 || f.queueId == 440))
            {
                foreach (var p in m["participants"].GetList())
                {
                    var champ = LeagueStaticData.Champions[p["championId"].GetInt()].Name;
                    var stats = p["stats"];
                    games.IncSafe(champ);
                    kills.IncSafe(champ, stats["kills"].GetInt());
                    deaths.IncSafe(champ, stats["deaths"].GetInt());
                }
            }
            writeLine($"");
            writeLine($"Champion,Games,Kills,Deaths");
            foreach (var c in LeagueStaticData.Champions.Values.Select(c => c.Name).Order())
                writeLine($"{c},{games[c]},{kills[c]},{deaths[c]}");
        }

        public static void GenerateOneForAll(string dataPath)
        {
            writeLine($"Generating stats at {DateTime.Now}...");

            // Load matches
            string dataSuffix = "";
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

        public static void GenerateSR5v5(string dataPath, string version)
        {
            writeLine($"Generating stats at {DateTime.Now}...");

            // Load matches
            var dataSuffix = "";
            dataPath = Path.Combine(dataPath, $"Global{dataSuffix}");
            var matches = LoadAllMatches(dataPath, version, 420, matchSRFromJson) // ranked solo
                .Concat(LoadAllMatches(dataPath, version, 400, matchSRFromJson)) // draft pick
                .Concat(LoadAllMatches(dataPath, version, 430, matchSRFromJson)) // blind pick
                .ToList();
            // Remove duplicates
            var hadCount = matches.Count;
            matches = matches.GroupBy(m => m.MatchId).Select(m => m.First()).ToList();
            writeLine($"Distinct matches: {matches.Count:#,0} (duplicates removed: {hadCount - matches.Count:#,0})");

            {
                var duos = new AutoDictionary<string, (int winCount, int totalCount)>(_ => (0, 0));
                void addDuo2(string duoDesc, bool win)
                {
                    var stat = duos[duoDesc];
                    stat.totalCount++;
                    if (win)
                        stat.winCount++;
                    duos[duoDesc] = stat;
                }
                void addDuo(LaneSR lane1, LaneSR lane2, string lane1type, string lane2type)
                {
                    if (lane1.ChampW != null && lane2.ChampW != null)
                        addDuo2($"{lane1type}:{lane1.ChampW} + {lane2type}:{lane2.ChampW}", true);
                    if (lane1.ChampL != null && lane2.ChampL != null)
                        addDuo2($"{lane1type}:{lane1.ChampL} + {lane2type}:{lane2.ChampL}", false);
                }
                foreach (var match in matches)
                {
                    addDuo(match.Adc, match.Sup, "adc", "sup");
                    addDuo(match.Adc, match.Jun, "adc", "jun");
                    addDuo(match.Adc, match.Mid, "adc", "mid");
                    addDuo(match.Adc, match.Top, "adc", "top");
                    addDuo(match.Sup, match.Jun, "sup", "jun");
                    addDuo(match.Sup, match.Mid, "sup", "mid");
                    addDuo(match.Sup, match.Top, "sup", "top");
                    addDuo(match.Jun, match.Mid, "jun", "mid");
                    addDuo(match.Jun, match.Top, "jun", "top");
                    addDuo(match.Mid, match.Top, "mid", "top");
                }
                var duoStats =
                    from kvp in duos
                    let wr = kvp.Value.winCount / (double) kvp.Value.totalCount
                    let count = kvp.Value.totalCount
                    let conf = getWilson(wr, count, 1.96)
                    select new { duo = kvp.Key, count, wr, lower95 = conf.lower, upper95 = conf.upper };
                File.WriteAllLines($"duos.csv", duoStats.Select(d => $"{d.duo},{d.wr * 100:0.000}%,{d.count},{d.lower95 * 100:0.000}%,{d.upper95 * 100:0.000}%"));
            }

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

        public static void GenerateRecentItemStats(string dataPath, string itemStatsFile)
        {
            writeLine("Initialising global data...");
            DataStore.Initialise(dataPath, "", autoRewrites: false);

            writeLine($"Loading basic match infos...");
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(30);
            var matchFiles = DataStore.LosMatchInfos
                .SelectMany(kvp => kvp.Value
                    .ReadItems()
                    .Where(mi => mi.GameCreationDate >= cutoff && (mi.QueueId == 420 || mi.QueueId == 400 || mi.QueueId == 430))
                    .Select(mi => (region: kvp.Key, info: mi)))
                .ToLookup(item => item.info.LosjsFileName(dataPath, "", item.region))
                .Select(grp => (jsons: new JsonContainer(grp.Key), matchIds: grp.Select(item => item.info.MatchId).ToHashSet()))
                .ToList();

            var total = matchFiles.Sum(f => f.matchIds.Count);
            writeLine($"Processing full matches... {total} total matches");
            var counts = new AutoDictionary<string, string, int, int>();
            foreach (var file in matchFiles)
            {
                writeLine($"Processing {file.matchIds.Count:#,0} matches from {file.jsons.FileName}...");
                foreach (var json in file.jsons.ReadItems().Where(m => file.matchIds.Contains(m["gameId"].GetLong())))
                {
                    foreach (var plr in json["participants"].GetList())
                    {
                        var lane = plr["timeline"]["lane"].GetString();
                        var role = plr["timeline"]["role"].GetString();
                        var lanerole =
                            lane == "MIDDLE" && role == "SOLO" ? "mid" :
                            lane == "TOP" && role == "SOLO" ? "top" :
                            lane == "JUNGLE" && role == "NONE" ? "jungle" :
                            lane == "BOTTOM" && role == "DUO_CARRY" ? "adc" :
                            lane == "BOTTOM" && role == "DUO_SUPPORT" ? "sup" : null;
                        if (lanerole == null)
                            continue;
                        var champ = LeagueStaticData.Champions[plr["championId"].GetInt()].Name;
                        counts[champ][lanerole][plr["stats"]["item0"].GetInt()]++;
                        counts[champ][lanerole][plr["stats"]["item1"].GetInt()]++;
                        counts[champ][lanerole][plr["stats"]["item2"].GetInt()]++;
                        counts[champ][lanerole][plr["stats"]["item3"].GetInt()]++;
                        counts[champ][lanerole][plr["stats"]["item4"].GetInt()]++;
                        counts[champ][lanerole][plr["stats"]["item5"].GetInt()]++;
                    }
                }
            }

            File.Delete(itemStatsFile);
            foreach (var champ in counts.Keys)
                foreach (var lanerole in counts[champ].Keys)
                    foreach (var item in counts[champ][lanerole].Keys)
                        File.AppendAllText(itemStatsFile, $"{champ},{lanerole},{item},{counts[champ][lanerole][item]}\r\n");
        }

        public static void GenerateItemSets(string dataPath, string leagueInstallPath, ItemSetsSettings settings)
        {
            Directory.CreateDirectory(settings.ItemStatsCachePath);
            var itemStatsFile = Path.Combine(settings.ItemStatsCachePath, "item-popularity.csv");
            if (!File.Exists(itemStatsFile) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(itemStatsFile)).TotalHours > settings.ItemStatsCacheExpiryHours)
                GenerateRecentItemStats(dataPath, itemStatsFile);
            var refreshTime = File.GetLastWriteTime(itemStatsFile);

            writeLine("Generating item sets...");

            var generatedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byName = LeagueStaticData.Items.Values.Where(i => i.Purchasable && i.MapSummonersRift && !i.ExcludeFromStandardSummonerRift && !i.HideFromAll).ToDictionary(i => i.Name);

            JsonValue preferredSlots = null;
            if (settings.SlotsJsonFile != null && settings.SlotsName != null)
            {
                var json = JsonDict.Parse(File.ReadAllText(settings.SlotsJsonFile));
                preferredSlots = json["itemSets"].GetList().First(l => l["title"].GetString() == settings.SlotsName)["preferredItemSlots"];
            }

            var toprow = settings.TopRowItems.Select(name => byName[name]).ToArray();
            var boots = new[] { byName["Boots of Swiftness"], byName["Boots of Mobility"], byName["Ionian Boots of Lucidity"], byName["Berserker's Greaves"], byName["Sorcerer's Shoes"], byName["Ninja Tabi"], byName["Mercury's Treads"] };
            var starting = new[] { byName["Refillable Potion"], byName["Corrupting Potion"], byName["The Dark Seal"], byName["Doran's Ring"], byName["Ancient Coin"], byName["Relic Shield"], byName["Spellthief's Edge"], byName["Doran's Shield"], byName["Doran's Blade"], byName["Cull"], byName["Hunter's Potion"], byName["Hunter's Talisman"], byName["Hunter's Machete"] };

            var itemsSR = LeagueStaticData.Items.Values
                .Where(i => i.Purchasable && i.MapSummonersRift && !i.ExcludeFromStandardSummonerRift && (i.NoUnconditionalChildren || boots.Contains(i) || starting.Contains(i)))
                .ToDictionary(i => i.Id);

            var itemStats = File.ReadAllLines(itemStatsFile)
                .Where(l => l != "").Select(l => l.Split(','))
                .Select(p => (champ: p[0], role: p[1], itemId: int.Parse(p[2]), count: int.Parse(p[3])))
                .Where(p => itemsSR.ContainsKey(p.itemId))
                .ToLookup(p => p.champ)
                .ToDictionary(grp => grp.Key, grp => grp.ToLookup(p => p.role));

            var pagesHtml = new List<object>();

            foreach (var champ in LeagueStaticData.Champions.Values.OrderBy(ch => ch.Name))
            {
                foreach (var role in new[] { "mid", "top", "jungle", "adc", "sup" })
                {
                    if (!itemStats.ContainsKey(champ.Name) || !itemStats[champ.Name].Contains(role))
                        continue;
                    var items = itemStats[champ.Name][role].OrderByDescending(p => p.count).Select(i => (count: i.count, item: itemsSR[i.itemId])).ToList();
                    var total = items.Sum(i => i.count);
                    if (total < settings.MinGames)
                        continue;
                    var minUsage = total * settings.UsageCutoffPercent / 100.0;
                    var sections = new List<List<(int count, ItemInfo item)>>();
                    var titles = new List<string>();

                    // Section 1: preset for trinkets, pots, wards, elixirs
                    sections.Add(toprow.Select(t => (0, t)).ToList());
                    titles.Add($"Consumables and trinkets ({refreshTime:dd MMM yyyy})");

                    // Section 2: starting items
                    sections.Add(items.Where(i => starting.Contains(i.item) && i.count >= minUsage).Take(settings.MaxItemsPerRow).ToList());
                    titles.Add("Starting:  " + relCounts(sections.Last()));

                    // Section 3: boots
                    if (champ.InternalName != "Cassiopeia")
                    {
                        sections.Add(new[] { (0, byName["Boots of Speed"]) }.Concat(items.Where(i => boots.Contains(i.item) && i.count >= minUsage)).ToList());
                        titles.Add("Boots:  " + relCounts(sections.Last().Skip(1)));
                    }

                    // Remaining items above a certain threshold of usage
                    var alreadyListed = sections.SelectMany(s => s.Select(si => si.item)).ToList();
                    var toList = items.Where(i => i.count >= minUsage && i.item.NoUnconditionalChildren && !alreadyListed.Contains(i.item) && !starting.Contains(i.item)).ToQueue();
                    var mostUsed = toList.Max(i => i.count);
                    sections.Add(new List<(int, ItemInfo)>());
                    while (toList.Count > 0)
                    {
                        var last = sections[sections.Count - 1];
                        if (last.Count == settings.MaxItemsPerRow)
                        {
                            last = new List<(int, ItemInfo)>();
                            sections.Add(last);
                        }
                        last.Add(toList.Dequeue());
                    }
                    for (int i = titles.Count; i < sections.Count; i++)
                        titles.Add("Items:  " + relCounts(sections[i], mostUsed));
                    var blocks = sections.Zip(titles, (section, title) => (title: title, items: section.Select(s => s.item).ToList())).ToList();
                    var caption = $"LoS - {champ.InternalName.SubstringSafe(0, 4)} - {role} - {total:#,0} games";

                    string relCounts(IEnumerable<(int count, ItemInfo item)> sec, int rel = 0)
                    {
                        if (rel == 0)
                            rel = sec.First().count;
                        return sec.Select(s => $"{s.count * 100.0 / rel:0}").JoinString("  ");
                    }

                    // Save to HTML for review / reference
                    pagesHtml.Add(Ut.NewArray<object>(
                        new H1(role.Substring(0, 1).ToUpper() + role.Substring(1) + " " + champ.Name, new SMALL(caption)),
                        new DIV { class_ = "set" }._(
                            blocks.Select(b => Ut.NewArray<object>(
                                new H3(b.title),
                                new DIV { class_ = "row" }._(
                                    b.items.Select(item => new DIV { class_ = "item" }._(
                                        new IMG { src = item.Icon, title = item.Name }, new P(item.TotalPrice, new SPAN { class_ = "gold" })
                                    ))
                                )
                            ))
                        )
                    ));

                    // Generate the item set
                    var itemSet = new JsonDict();
                    itemSet["associatedChampions"] = new JsonList { champ.Id };
                    itemSet["associatedMaps"] = new JsonList { 11 };
                    itemSet["map"] = "SR";
                    itemSet["title"] = caption;
                    itemSet["mode"] = "any";
                    itemSet["type"] = "custom";
                    itemSet["sortrank"] = 1;
                    itemSet["startedFrom"] = "blank";
                    // preferred item slots don't work unless a UID is present; must be stable for League to remember the last selected item set
                    itemSet["uid"] = new Guid(MD5.Create().ComputeHash($"SR/{champ.Name}/{role}".ToUtf8())).ToString().ToLower();
                    itemSet["blocks"] = new JsonList();
                    foreach (var block in blocks)
                    {
                        var blk = new JsonDict();
                        itemSet["blocks"].Add(blk);
                        blk["type"] = block.title;
                        blk["items"] = new JsonList();
                        foreach (var item in block.items)
                            blk["items"].Add(new JsonDict { { "id", item.Id.ToString() }, { "count", 1 } });
                    }
                    if (preferredSlots != null)
                        itemSet["preferredItemSlots"] = preferredSlots;

                    var fileName = Path.Combine(leagueInstallPath, "Config", "Champions", champ.InternalName, "Recommended", $"LOS_{champ.InternalName}_{role}.json");
                    File.WriteAllText(fileName, itemSet.ToStringIndented());
                    generatedFiles.Add(fileName);
                }
            }
            // On successful completion, delete all item sets matching our naming scheme which we did not generate
            foreach (var file in new PathManager(Path.Combine(leagueInstallPath, "Config", "Champions")).GetFiles())
                if (file.Name.StartsWith("LOS_") && file.Name.EndsWith(".json") && !generatedFiles.Contains(file.FullName))
                {
                    writeLine($"Deleting obsolete item set at {file.FullName}");
                    file.Delete();
                }
            // Generate HTML with all results
            string css;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LeagueOfStats.CmdGen.Css.ItemSets.css"))
                css = stream.ReadAllText();
            var html = new HTML(
                new HEAD(new STYLELiteral(css)),
                new BODY(pagesHtml)
            );
            Directory.CreateDirectory(settings.ReportPath);
            File.WriteAllText(Path.Combine(settings.ReportPath, "ItemSets.html"), html.ToString());
        }
    }
}
