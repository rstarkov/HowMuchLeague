using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using LeagueOfStats.GlobalData;
using LeagueOfStats.StaticData;
using RT.TagSoup;
using RT.Util;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;
using RT.Util.Json;

namespace LeagueOfStats.CmdGen
{
    class SummonerRift5v5Stats
    {
        private SummonerRift5v5StatsSettings _settings;

        public SummonerRift5v5Stats(SummonerRift5v5StatsSettings settings)
        {
            _settings = settings;
        }

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

        public void Generate()
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(_settings.IncludeLastDays);
            Generate((BasicMatchInfo mi) => mi.GameCreationDate >= cutoff, $"over the last {_settings.IncludeLastDays:0} days");
        }

        public void Generate(string version)
        {
            Generate(m => m.version == version, $"for version {version}");
        }

        public void Generate(Func<(Region region, string version), bool> filter, string filterDesc = null)
        {
            generate(
                DataStore.ReadMatchesByRegVerQue(f => filter((f.region, f.version)) && (f.queueId == 420 /*ranked solo*/ || f.queueId == 400 /*draft pick*/ || f.queueId == 430 /*blind pick*/)),
                filterDesc);
        }

        public void Generate(Func<BasicMatchInfo, bool> filter, string filterDesc = null)
        {
            generate(
                DataStore.ReadMatchesByBasicInfo(m => filter(m) && (m.QueueId == 420 /*ranked solo*/ || m.QueueId == 400 /*draft pick*/ || m.QueueId == 430 /*blind pick*/)),
                filterDesc);
        }

        private void generate(IEnumerable<(JsonValue json, Region region)> jsons, string filterDesc)
        {
            Console.WriteLine($"Generating stats at {DateTime.Now}...");
            Directory.CreateDirectory(_settings.OutputPath);

            // Load matches
            var matches = jsons
                .Select(m => matchSRFromJson(m.json, m.region))
                .ToList();
            Console.WriteLine($"Distinct matches: {matches.Count:#,0}");

            genDuos(matches);

            var indexHtml = new List<object>();
            indexHtml.Add(new H1("Champion winrates and matchups"));
            indexHtml.Add(new P($"Analysed {matches.Count:#,0} matches " + (filterDesc ?? "using a custom filter")));
            indexHtml.Add(new P("Generated on ", DateTime.Now.ToString("dddd', 'dd'.'MM'.'yyyy' at 'HH':'mm':'ss")));

            genSRLaneMatchups("mid", matches.Select(m => m.Mid).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList(), indexHtml);
            genSRLaneMatchups("top", matches.Select(m => m.Top).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList(), indexHtml);
            genSRLaneMatchups("adc", matches.Select(m => m.Adc).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList(), indexHtml);
            genSRLaneMatchups("jun", matches.Select(m => m.Jun).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList(), indexHtml);
            genSRLaneMatchups("sup", matches.Select(m => m.Sup).Where(m => m.ChampW != null && m.ChampL != null && m.ChampW != m.ChampL).ToList(), indexHtml);

            string css, sorttable;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LeagueOfStats.CmdGen.Css.GlobalStats.css"))
                css = stream.ReadAllText();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LeagueOfStats.CmdGen.Css.sorttable.js"))
                sorttable = stream.ReadAllText();
            var html = new HTML(
                new HEAD(
                    new META { charset = "utf-8" },
                    new STYLELiteral(css),
                    new SCRIPTLiteral(sorttable)
                ),
                new BODY(indexHtml)
            );
            File.WriteAllText(Path.Combine(_settings.OutputPath, "Index.html"), html.ToString());
        }

        private void genDuos(List<MatchSR> matches)
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
            File.WriteAllLines(Path.Combine(_settings.OutputPath, $"duos.csv"), duoStats.Select(d => $"{d.duo},{d.wr * 100:0.000}%,{d.count},{d.lower95 * 100:0.000}%,{d.upper95 * 100:0.000}%"));
        }

        private MatchSR matchSRFromJson(JsonValue json, Region region)
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

        private void genSRLaneMatchups(string lane, List<LaneSR> matches, List<object> indexHtml)
        {
            Console.WriteLine($"Usable {lane} matchups: {matches.Count:#,0}");
            var byChamp = new AutoDictionary<string, List<LaneSR>>(_ => new List<LaneSR>());
            foreach (var m in matches)
            {
                byChamp[m.ChampW].Add(m);
                byChamp[m.ChampL].Add(m);
            }

            var overallPopularity = byChamp.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count / (double) matches.Count / 2);

            var overallTable = new List<TR>();
            foreach (var kvp in percentile(byChamp, 0.95, kvp => kvp.Value.Count))
            {
                Console.WriteLine($"Generating lane stats for {lane} - {kvp.Key}...");
                genSRLaneMatchup(kvp.Value, kvp.Key, lane, overallPopularity, overallTable);
            }

            indexHtml.Add(new H3($"{lane.Capitalise()}"));
            indexHtml.Add(makeTable(
                new TR(colAsc("Champion"), colDesc("Winrate"), colDesc("Matches"), colDesc("p95 lower", true), colAsc("p95 upper")),
                overallTable));
        }

        private static TH colAsc(object caption, bool initial = false)
            => new TH(caption) { class_ = initial ? "sorttable_initial" : "" };

        private static TH colDesc(object caption, bool initial = false)
            => new TH(caption) { class_ = (initial ? "sorttable_initial " : "") + "sorttable_startreversed" };

        private static TD cell(object content, object sortkey, bool leftAlign)
            => (TD) new TD { class_ = leftAlign ? "la" : "ra" }._(content).Data("sortkey", sortkey);

        private static TD cellStr(string value)
            => (TD) new TD { class_ = "la" }._(value);

        private static TD cellPrc(double value, int decimalPlaces)
            => (TD) new TD { class_ = "ra" }._(("{0:0." + new string('0', decimalPlaces) + "}%").Fmt(value * 100)).Data("sortkey", value);

        private static TD cellPrcDelta(double value, int decimalPlaces)
            => (TD) new TD { class_ = "ra" }._(value < 0 ? "−" : value > 0 ? "+" : " ", ("{0:0." + new string('0', decimalPlaces) + "}%").Fmt(value * 100).Replace("-", "")).Data("sortkey", value);

        private static TD cellInt(int value)
            => (TD) new TD { class_ = "ra" }._("{0:#,0}".Fmt(value)).Data("sortkey", value);

        private static object makeTable(TR header, IEnumerable<TR> rows)
        {
            return new TABLE { class_ = "sortable" }._(
                new THEAD(header),
                new TBODY(rows)
            );
        }

        private void genSRLaneMatchup(List<LaneSR> matches, string champ, string lane, Dictionary<string, double> overallPopularity, List<TR> overallTable)
        {
            var championHtml = new List<object>();
            var champInfo = LeagueStaticData.Champions.Values.Single(ch => ch.Name == champ);
            var filename = $"{lane}-{champInfo.InternalName}.html";
            championHtml.Add(new IMG { src = champInfo.ImageUrl, style = "float:left; margin-right: 20px;", width = 110, height = 110 });
            championHtml.Add(new H1($"{champ} – {lane}"));
            championHtml.Add(new P($"Usable matches for {lane} {champ}: {matches.Count:#,0}"));
            var overallWinrate = matches.Count(m => m.ChampW == champ) / (double) matches.Count;
            {
                var conf95 = Utils.WilsonConfidenceInterval(overallWinrate, matches.Count, 1.96);
                championHtml.Add(new P($"Overall win rate: {overallWinrate * 100:0.0}% ({conf95.lower * 100:0}% - {conf95.upper * 100:0}%)"));
                overallTable.Add(new TR(cell(new A($"{lane.Capitalise()} {champ}") { href = filename }, champ, true), cellPrc(overallWinrate, 1), cellInt(matches.Count), cellPrc(conf95.lower, 1), cellPrc(conf95.upper, 1)));
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
            championHtml.Add(new H3("Excessively popular enemies"));
            championHtml.Add(makeTable(
                new TR(colAsc("Matchup"), colDesc("Popularity", true), colAsc("Δ winrate"), colDesc("Matches")),
                excessivelyPopularEnemies.Select(epe => new TR(
                    cellStr($"{champ} vs {epe.enemy}"),
                    cellPrcDelta(epe.excessPopularity, 1),
                    cellPrcDelta(matchups[epe.enemy].winrate - overallWinrate, 1).AppendStyle(matchups[epe.enemy].winrate > overallWinrate ? "color: #4e2" : "color: #e42"),
                    cellInt(matchups[epe.enemy].count)
                ))
            ));

            var bans = LeagueStaticData.Champions.Values.Select(ch => ch.Name).Where(ch => ch != champ).Select(ban =>
            {
                var ms = matches.Where(m => m.Other(champ) != ban).ToList();
                var winrate = ms.Count == 0 ? -1 : ms.Count(m => m.ChampW == champ) / (double) ms.Count;
                var msAll = matches.Where(m => !m.Match.Has(ban)).ToList();
                var winrateAll = msAll.Count == 0 ? -1 : msAll.Count(m => m.ChampW == champ) / (double) msAll.Count;
                return new { ban, winrate, winrateAll, count = ms.Count, countAll = msAll.Count };
            }).ToList();

            championHtml.Add(new H3($"Own lane bans for {champ}"));
            championHtml.Add(makeTable(
                new TR(colAsc("Champion"), colDesc("Δ winrate", true), colDesc("Matches"), colDesc("Banned")),
                bans.OrderByDescending(b => b.winrate).Take(5).Select(b => new TR(
                    cellStr(b.ban), cellPrcDelta(b.winrate - overallWinrate, 1), cellInt(b.count), cellInt(matches.Count - b.count)
                ))
            ));

            championHtml.Add(new H3($"All lane bans for {champ}"));
            championHtml.Add(makeTable(
                new TR(colAsc("Champion"), colDesc("Δ winrate", true), colDesc("Matches"), colDesc("Banned")),
                bans.OrderByDescending(b => b.winrateAll).Take(5).Select(b => new TR(
                    cellStr(b.ban), cellPrcDelta(b.winrateAll - overallWinrate, 1), cellInt(b.countAll), cellInt(matches.Count - b.countAll)
                ))
            ));

            championHtml.Add(new H3("Most frequent matchups (50%)"));
            championHtml.Add(makeTable(
                new TR(colAsc("Matchup"), colDesc("Winrate"), colDesc("Matches", true), colDesc("p95 lower"), colAsc("p95 upper")),
                percentile(matchups.Values.OrderByDescending(m => m.count), 0.50, m => m.count).OrderByDescending(m => m.winrate).Select(mu => new TR(
                    cellStr($"{champ} vs {mu.enemy}"), cellPrc(mu.winrate, 1), cellInt(mu.count), cellPrc(mu.conf95.lower, 1), cellPrc(mu.conf95.upper, 1)
                ))
            ));

            championHtml.Add(new H3("Almost all matchups (95%)"));
            championHtml.Add(makeTable(
                new TR(colAsc("Matchup"), colDesc("Winrate"), colDesc("Matches", true), colDesc("p95 lower"), colAsc("p95 upper")),
                percentile(matchups.Values.OrderByDescending(m => m.count), 0.95, m => m.count).OrderByDescending(m => m.winrate).Select(mu => new TR(
                    cellStr($"{champ} vs {mu.enemy}"), cellPrc(mu.winrate, 1), cellInt(mu.count), cellPrc(mu.conf95.lower, 1), cellPrc(mu.conf95.upper, 1)
                ))
            ));

            championHtml.Add(new H3("Remaining matchups (...5%)"));
            championHtml.Add(makeTable(
                new TR(colAsc("Matchup"), colDesc("Winrate"), colDesc("Matches", true), colDesc("p95 lower"), colAsc("p95 upper")),
                percentile(matchups.Values.OrderBy(m => m.count), 0.05, m => m.count).OrderByDescending(m => m.winrate).Select(mu => new TR(
                    cellStr($"{champ} vs {mu.enemy}"), cellPrc(mu.winrate, 1), cellInt(mu.count), cellPrc(mu.conf95.lower, 1), cellPrc(mu.conf95.upper, 1)
                ))
            ));

            championHtml.Add(new P { style = "margin-top: 30px;" }._("Generated on ", DateTime.Now.ToString("dddd', 'dd'.'MM'.'yyyy' at 'HH':'mm':'ss")));

            string css, sorttable;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LeagueOfStats.CmdGen.Css.GlobalStats.css"))
                css = stream.ReadAllText();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LeagueOfStats.CmdGen.Css.sorttable.js"))
                sorttable = stream.ReadAllText();
            var html = new HTML(
                new HEAD(
                    new META { charset = "utf-8" },
                    new STYLELiteral(css),
                    new SCRIPTLiteral(sorttable)
                ),
                new BODY(championHtml)
            );
            File.WriteAllText(Path.Combine(_settings.OutputPath, filename), html.ToString());
        }

        public static void TeamCompExtract()
        {
            var seenMatches = new AutoDictionary<Region, HashSet<long>>(_ => new HashSet<long>());
            foreach (var f in DataStore.LosMatchJsons.SelectMany(kvpR => kvpR.Value.SelectMany(kvpV => kvpV.Value.Select(kvpQ => (region: kvpR.Key, version: kvpV.Key, queueId: kvpQ.Key, file: kvpQ.Value)))))
            {
                if (f.queueId != 2 && f.queueId != 4 && f.queueId != 6 && f.queueId != 14 && f.queueId != 42 && f.queueId != 61 && f.queueId != 400 && f.queueId != 410 && f.queueId != 420 && f.queueId != 430 && f.queueId != 440)
                    continue;
                var fname = $"TeamCompExtract-{f.region}-{f.version}-{f.queueId}.csv";
                if (File.Exists(fname))
                    continue;
                Console.WriteLine($"Processing {f.file.FileName} ...");
                var count = new CountThread(10000);
                File.WriteAllLines(fname,
                    f.file.ReadItems()
                        .PassthroughCount(count.Count)
                        .Where(js => seenMatches[f.region].Add(js["gameId"].GetLong()))
                        .Select(js => TeamCompExtractMatch(js, f))
                        .Where(line => line != null));
                count.Stop();
            }
        }

        private static string TeamCompExtractMatch(JsonValue js, (Region region, string version, int queueId, JsonContainer file) f)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(f.region.ToString());
                sb.Append(js["gameId"].GetLong());
                sb.Append(',');
                sb.Append(f.queueId);
                sb.Append(',');
                sb.Append(js["gameCreation"].GetLong());
                sb.Append(',');
                sb.Append(js["gameDuration"].GetLong());
                sb.Append(',');
                var tw = js["participants"].GetList().Where(p => p["stats"]["win"].GetBool()).ToList();
                var tl = js["participants"].GetList().Where(p => !p["stats"]["win"].GetBool()).ToList();
                if (tw.Count != 5 || tl.Count != 5)
                {
                    File.AppendAllLines("jsons-non-5-winners-losers.txt", new[] { js.ToStringIndented() });
                    return null;
                }
                if (tw.Select(p => p["championId"].GetInt()).Distinct().Count() != 5 || tl.Select(p => p["championId"].GetInt()).Distinct().Count() != 5)
                {
                    File.AppendAllLines("jsons-duplicate-champs.txt", new[] { js.ToStringIndented() });
                    return null;
                }
                if (tw.Concat(tl.AsEnumerable()).Any(p => p["stats"].Safe["wasAfk"].GetBoolSafe() == true || p["stats"].Safe["leaver"].GetBoolSafe() == true))
                {
                    File.AppendAllLines("jsons-afk-or-leaver.txt", new[] { $"{f.region}{js["gameId"].GetLong()}" });
                    return null;
                }
                foreach (var p in tw.Concat(tl.AsEnumerable()))
                {
                    sb.Append(p["championId"].GetInt());
                    sb.Append(',');
                    if (p.ContainsKey("highestAchievedSeasonTier"))
                        sb.Append(p["highestAchievedSeasonTier"].GetString()[0]);
                    else
                        sb.Append('U');
                    sb.Append(',');
                    sb.Append(p["spell1Id"].GetInt());
                    sb.Append(',');
                    sb.Append(p["spell2Id"].GetInt());
                    sb.Append(',');
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                File.AppendAllLines("jsons-exceptions.txt", new[] { $"EXCEPTION: {e.GetType().Name} {e.Message}", e.StackTrace, js.ToStringIndented() });
                return null;
            }
        }

        public static void GameDurations()
        {
            var buckets = new AutoDictionary<int, List<int>>(_ => new List<int>());
            foreach (var bi in DataStore.LosMatchInfos.Values.SelectMany(c => c.ReadItems()))
            {
                var q = Queues.GetInfo(bi.QueueId);
                if (q.Map == MapId.SummonersRift && q.ModeName == "5v5" && (q.Variant == "Blind Pick" || q.Variant == "Draft Pick" || q.Variant.StartsWith("Ranked")))
                    buckets[(int) ((double) bi.GameCreation / 1000 / 86400 / 30.4375)].Add(bi.GameDuration);
            }
            var keys = buckets.Keys.Order().ToList();
            File.WriteAllText("gameDurations.csv", "");
            foreach (var key in keys)
            {
                var list = buckets[key];
                list.Sort();
                int prc(double p) => list[(int) (list.Count * p)];
                File.AppendAllLines("gameDurations.csv", new[] { $"{key},{prc(0.01)},{prc(0.10)},{prc(0.25)},{prc(0.50)},{prc(0.75)},{prc(0.90)},{prc(0.99)}" });
            }
        }
    }
}
