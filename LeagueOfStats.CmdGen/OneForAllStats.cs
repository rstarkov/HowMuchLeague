using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeagueOfStats.GlobalData;
using LeagueOfStats.StaticData;
using RT.Util;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;
using RT.Util.Json;

namespace LeagueOfStats.CmdGen
{
    static class OneForAllStats
    {
        private class Match1FA
        {
            public string MatchId, Champion1, Champion2, Winner;
            public DateTime StartTime;
            public string GameVersion;
        }

        public static void Generate()
        {
            Console.WriteLine($"Generating stats at {DateTime.Now}...");

            // Load matches
            var matches = DataStore.ReadMatchesByRegVerQue(f => f.queueId == 1020 && (f.version == "8.6" || f.version == "8.7"))
                .Select(m => match1FAFromJson(m.json, m.region))
                .ToList();
            Console.WriteLine($"Distinct matches: {matches.Count:#,0}");
            Console.WriteLine($"Distinct matchups: {matches.GroupBy(m => new { m.Champion1, m.Champion2 }).Count():#,0} / {9453 + 138:#,0}"); // 138 choose 2 + 138 mirror matchups (Teemo/Karthus not allowed)
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
                    var conf95 = Utils.WilsonConfidenceInterval(p, n, 1.96);
                    var conf67 = Utils.WilsonConfidenceInterval(p, n, 0.97);
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
                    var conf95 = Utils.WilsonConfidenceInterval(p, n, 1.96);
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

        private static AutoDictionary<TKey, TValue> newDict<TKey, TValue>(TKey _, TValue __, Func<TKey, TValue> init)
        {
            return new AutoDictionary<TKey, TValue>(init);
        }

        private static void writeAllLines(string filename, IEnumerable<string> lines)
        {
            Ut.WaitSharingVio(() => File.WriteAllLines(filename, lines), onSharingVio: () => Console.WriteLine($"File \"{filename}\" is in use; waiting..."));
        }
    }
}
