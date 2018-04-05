using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using LeagueOfStats.GlobalData;
using LeagueOfStats.StaticData;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;
using RT.Util.Paths;

namespace LeagueOfStats.OneForAllStats
{
    class StatsGen
    {
        class Match
        {
            public string MatchId, Champion1, Champion2, Winner;
        }

        public static void Generate(string dataPath)
        {
            LeagueStaticData.Load(Path.Combine(dataPath, "Static"));
            writeLine($"Generating stats at {DateTime.Now}...");

            // Load matches
            var matches = new List<Match>();
            foreach (var f in new PathManager(dataPath).GetFiles().OrderBy(f => f.FullName))
            {
                var match = Regex.Match(f.Name, @"^(?<region>[A-Z]+)-matches-1020\.losjs$");
                if (!match.Success)
                    continue;
                var region = match.Groups["region"].Value;
                Console.Write($"Loading {f.FullName}... ");
                var count = new CountResult();
                var t = new Thread(() =>
                {
                    int next = 5000;
                    while (true)
                    {
                        if (count.Count > next)
                        {
                            Console.Write(count.Count + " ");
                            next += 5000;
                        }
                        Thread.Sleep(1000);
                    }
                });
                t.Start();
                var started = DateTime.UtcNow;
                matches.AddRange(new JsonContainer(f.FullName).ReadItems().Select(json => matchFromJson(json, region)).PassthroughCount(count));
                var ended = DateTime.UtcNow;
                t.Abort();
                t.Join();
                Console.WriteLine();
                writeLine($"Loaded {count} matches from {f.FullName} in {(ended - started).TotalSeconds:#,0.000} s");
            }
            // Remove duplicates
            var hadCount = matches.Count;
            matches = matches.GroupBy(m => m.MatchId).Select(m => m.First()).ToList();
            writeLine($"Distinct matches: {matches.Count:#,0} (duplicates removed: {hadCount - matches.Count:#,0})");
            writeLine($"Distinct matchups: {matches.GroupBy(m => new { m.Champion1, m.Champion2 }).Count():#,0} / {9453 + 138:#,0}"); // 138 choose 2 + 138 mirror matchups (Teemo/Karthus not allowed)
            // Champions seen
            var champions = matches.Select(m => m.Champion1).Concat(matches.Select(m => m.Champion2)).Distinct().ToList();

            // All the match IDs
            File.WriteAllLines("match-ids.txt", matches.Select(m => m.MatchId).Order());

            // Matchup stats
            var statsMatchups = matches.GroupBy(m => new { m.Champion1, m.Champion2 }).Where(grp => grp.Key.Champion1 != grp.Key.Champion2).Select(grp =>
            {
                var n = grp.Count();
                var p = grp.Count(m => m.Champion1 == m.Winner) / (double) n;
                var conf95 = getWilson(p, n, 1.96);
                var conf67 = getWilson(p, n, 0.97);
                return new { grp.Key.Champion1, grp.Key.Champion2, Count = n, WinRate = p, Lower95 = conf95.lower, Upper95 = conf95.upper, Lower67 = conf67.lower, Upper67 = conf67.upper };
            }).ToList();
            statsMatchups = statsMatchups.Concat(statsMatchups
                    .Select(m => new { Champion1 = m.Champion2, Champion2 = m.Champion1, m.Count, WinRate = 1 - m.WinRate, Lower95 = 1 - m.Upper95, Upper95 = 1 - m.Lower95, Lower67 = 1 - m.Upper67, Upper67 = 1 - m.Lower67 })
                ).OrderBy(m => m.Champion1).ThenBy(m => m.Champion2).ToList();
            File.WriteAllLines("statgen-matchups.csv", statsMatchups.Select(l => $"{l.Champion1},{l.Champion2},{l.Count},{l.WinRate},{l.Lower95},{l.Upper95},{l.Lower67},{l.Upper67}"));

            // All possible matchup stats
            var statsMatchupsAll = champions.SelectMany(ch1 => champions.Select(ch2 => new { ch1, ch2 })).Where(key => key.ch1.CompareTo(key.ch2) <= 0).Select(key =>
            {
                var grp = matches.Where(m => m.Champion1 == key.ch1 && m.Champion2 == key.ch2).ToList();
                var n = grp.Count;
                if (n == 0)
                    return new { Champion1 = key.ch1, Champion2 = key.ch2, Count = n, WinRate = 0.5, Lower95 = 0.0, Upper95 = 1.0, Lower67 = 0.0, Upper67 = 1.0 };
                if (key.ch1 == key.ch2)
                    return new { Champion1 = key.ch1, Champion2 = key.ch2, Count = n, WinRate = 0.5, Lower95 = 0.5, Upper95 = 0.5, Lower67 = 0.5, Upper67 = 0.5 };
                var p = winrate(grp, key.ch1);
                var conf95 = getWilson(p, n, 1.96);
                var conf67 = getWilson(p, n, 0.97);
                return new { Champion1 = key.ch1, Champion2 = key.ch2, Count = n, WinRate = p, Lower95 = conf95.lower, Upper95 = conf95.upper, Lower67 = conf67.lower, Upper67 = conf67.upper };
            }).ToList();
            statsMatchupsAll = statsMatchupsAll.Concat(statsMatchupsAll
                    .Where(m => m.Champion1 != m.Champion2)
                    .Select(m => new { Champion1 = m.Champion2, Champion2 = m.Champion1, m.Count, WinRate = 1 - m.WinRate, Lower95 = 1 - m.Upper95, Upper95 = 1 - m.Lower95, Lower67 = 1 - m.Upper67, Upper67 = 1 - m.Lower67 })
                ).OrderBy(m => m.Champion1).ThenBy(m => m.Champion2).ToList();
            File.WriteAllLines("statgen-matchupsall.csv", statsMatchupsAll.Select(l => $"{l.Champion1},{l.Champion2},{l.Count},{l.WinRate},{l.Lower95},{l.Upper95},{l.Lower67},{l.Upper67}"));

            // Champion stats at champ select stage (unknown enemy)
            var statsChampSelect = champions.Select(champion =>
            {
                var grp = matchesWith(matches, champion).ToList();
                var n = grp.Count;
                var p = winrate(grp, champion);
                var conf95 = getWilson(p, n, 1.96);
                // Bans
                var remaining = grp;
                var bans = new string[5];
                var banWR = new double[5];
                for (int i = 0; i < 5; i++)
                {
                    var banResult = champions.Where(ban => ban != champion).Select(ban => new { ban, wr = winrate(matchesWithout(remaining, ban), champion) }).MaxElement(x => x.wr);
                    bans[i] = banResult.ban;
                    banWR[i] = banResult.wr;
                    remaining = matchesWithout(remaining, bans[i]).ToList();
                }
                return new { champion, Count = n, WinRate = p, Lower95 = conf95.lower, Upper95 = conf95.upper, bans, banWR };
            }).OrderBy(r => r.champion).ToList();
            File.WriteAllLines("statgen-champselect.csv", statsChampSelect.Select(l => $"{l.champion},{l.Count},{l.WinRate},{l.Lower95},{l.Upper95},{l.bans[0]},{l.banWR[0]},{l.bans[1]},{l.banWR[1]},{l.bans[2]},{l.banWR[2]},{l.bans[3]},{l.banWR[3]},{l.bans[4]},{l.banWR[4]}"));
        }

        private static Match matchFromJson(JsonValue json, string region)
        {
            Ut.Assert(json["gameMode"].GetString() == "ONEFORALL");
            var teamW = json["teams"].GetList().Single(tj => tj["win"].GetString() == "Win")["teamId"].GetInt();
            var teamL = json["teams"].GetList().Single(tj => tj["win"].GetString() == "Fail")["teamId"].GetInt();
            Ut.Assert(teamW != teamL);
            var champW = LeagueStaticData.Champions[json["participants"].GetList().Where(pj => pj["teamId"].GetInt() == teamW).First()["championId"].GetInt()].Name;
            var champL = LeagueStaticData.Champions[json["participants"].GetList().Where(pj => pj["teamId"].GetInt() == teamL).First()["championId"].GetInt()].Name;
            return new Match
            {
                MatchId = region + json["gameId"].GetStringLenient(),
                Champion1 = champW.CompareTo(champL) <= 0 ? champW : champL,
                Champion2 = champW.CompareTo(champL) <= 0 ? champL : champW,
                Winner = champW,
            };
        }

        private static void writeLine(string line)
        {
            Console.WriteLine(line);
            File.AppendAllLines("StatsGen.output.txt", new[] { line });
        }

        private static double winrate(IEnumerable<Match> matches, string champion)
        {
            return matches.Sum(m => m.Winner == champion ? (m.Champion1 == m.Champion2 ? 0.5 : 1) : 0) / matches.Count();
        }

        private static IEnumerable<Match> matchesWith(IEnumerable<Match> matches, string champion)
        {
            return matches.Where(m => m.Champion1 == champion || m.Champion2 == champion);
        }

        private static IEnumerable<Match> matchesWithout(IEnumerable<Match> matches, string champion)
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
    }
}
