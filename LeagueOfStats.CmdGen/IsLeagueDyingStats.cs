using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeagueOfStats.GlobalData;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.CmdGen
{
    static class IsLeagueDyingStats
    {
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

        public static void GenerateGameCounts()
        {
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

        public static void EstimateActivePlayers_Extract()
        {
            var seenMatches = new AutoDictionary<Region, HashSet<long>>(_ => new HashSet<long>());
            foreach (var f in DataStore.LosMatchJsons.SelectMany(kvpR => kvpR.Value.SelectMany(kvpV => kvpV.Value.Select(kvpQ => (region: kvpR.Key, version: kvpV.Key, queueId: kvpQ.Key, file: kvpQ.Value)))))
            {
                if (f.queueId == 0)
                    continue;
                Console.WriteLine($"Processing {f.file.FileName} ...");
                var count = new CountThread(10000);
                File.WriteAllLines($"ActivePlayersExtract-{f.region}-{f.version}-{f.queueId}.csv",
                    f.file.ReadItems()
                        .PassthroughCount(count.Count)
                        .Where(js => seenMatches[f.region].Add(js["gameId"].GetLong()) && js.ContainsKey("participantIdentities") && js["participantIdentities"].Count > 0)
                        .Select(js => $"{js["gameId"].GetLong()},{js["gameCreation"].GetLong()},{js["gameDuration"].GetLong()},{js["participantIdentities"].GetList().Select(p => p["player"]["accountId"].GetLong()).JoinString(",")}"));
                count.Stop();
            }
        }
    }
}
