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
    static class SummonerRift5v5Stats
    {
        private struct LaneSR
        {
            public string ChampW;
            public string ChampL;
            public MatchSR Match;
            public string Other(string champ) => champ == ChampW ? ChampL : champ == ChampL ? ChampW : throw new Exception();
            public bool Has(string champ) => champ == ChampW || champ == ChampL;
        }

        private class MatchSR
        {
            public string MatchId;
            public LaneSR Mid, Top, Jun, Adc, Sup;
            public string GameVersion;
            public DateTime StartTime;

            public bool Has(string champ) => Mid.Has(champ) || Top.Has(champ) || Jun.Has(champ) || Adc.Has(champ) || Sup.Has(champ);
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

        private static string findChampion(List<JsonValue> champs, string lane, string role)
        {
            var json = champs.FirstOrDefault(pj => pj["timeline"]["lane"].GetString() == lane && pj["timeline"]["role"].GetString() == role);
            if (json == null)
                return null;
            return LeagueStaticData.Champions[json["championId"].GetInt()].Name;
        }

        public static void Generate(string version)
        {
            Generate(m => m.version == version);
        }

        public static void Generate(Func<(Region region, string version), bool> filter)
        {
            generate(DataStore.ReadMatchesByRegVerQue(f => filter((f.region, f.version)) && (f.queueId == 420 /*ranked solo*/ || f.queueId == 400 /*draft pick*/ || f.queueId == 430 /*blind pick*/)));
        }

        public static void Generate(Func<BasicMatchInfo, bool> filter)
        {
            generate(DataStore.ReadMatchesByBasicInfo(m => filter(m) && (m.QueueId == 420 /*ranked solo*/ || m.QueueId == 400 /*draft pick*/ || m.QueueId == 430 /*blind pick*/)));
        }

        private static void generate(IEnumerable<(JsonValue json, Region region)> jsons)
        {
            Console.WriteLine($"Generating stats at {DateTime.Now}...");

            // Load matches
            var matches = jsons
                .Select(m => matchSRFromJson(m.json, m.region))
                .ToList();
            Console.WriteLine($"Distinct matches: {matches.Count:#,0}");

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
                    let conf = Utils.WilsonConfidenceInterval(wr, count, 1.96)
                    select new { duo = kvp.Key, count, wr, lower95 = conf.lower, upper95 = conf.upper };
                File.WriteAllLines($"duos.csv", duoStats.Select(d => $"{d.duo},{d.wr * 100:0.000}%,{d.count},{d.lower95 * 100:0.000}%,{d.upper95 * 100:0.000}%"));
            }

            genSRLaneMatchups("mid", matches.Select(m => m.Mid).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList());
            genSRLaneMatchups("top", matches.Select(m => m.Top).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList());
            genSRLaneMatchups("adc", matches.Select(m => m.Adc).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList());
            genSRLaneMatchups("jun", matches.Select(m => m.Jun).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList());
            genSRLaneMatchups("sup", matches.Select(m => m.Sup).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList());
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

        private static void genSRLaneMatchups(string lane, List<LaneSR> matches)
        {
            Console.WriteLine($"Usable {lane} matchups: {matches.Count:#,0}");
            var byChamp = new AutoDictionary<string, List<LaneSR>>(_ => new List<LaneSR>());
            foreach (var m in matches)
            {
                byChamp[m.ChampW].Add(m);
                byChamp[m.ChampL].Add(m);
            }

            var overallPopularity = byChamp.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count / (double) matches.Count / 2);

            foreach (var kvp in percentile(byChamp, 0.95, kvp => kvp.Value.Count))
            {
                Console.WriteLine($"Generating lane stats for {lane} - {kvp.Key}...");
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
                var conf95 = Utils.WilsonConfidenceInterval(overallWinrate, matches.Count, 1.96);
                writeLine($"Overall win rate: {overallWinrate * 100:0.0}% ({conf95.lower * 100:0}% - {conf95.upper * 100:0}%)");
                File.AppendAllLines($"_overall_ - {lane}.txt", new[] { $"{champ,-15}, {overallWinrate * 100:0.0}%, {matches.Count,6}, {conf95.lower * 100:0.0}%, {conf95.upper * 100:0.0}%" });
            }

            var matchups = matches.GroupBy(m => m.Other(champ)).ToDictionary(grp => grp.Key, grp => grp.ToList()).Select(kvp =>
            {
                var enemy = kvp.Key;
                var p = kvp.Value.Count(m => m.ChampW == champ) / (double) kvp.Value.Count;
                var conf95 = Utils.WilsonConfidenceInterval(p, kvp.Value.Count, 1.96);
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
    }
}
