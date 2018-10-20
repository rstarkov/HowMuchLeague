using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LeagueOfStats.GlobalData;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.Downloader
{
    class Program
    {
        public static Dictionary<Region, ConsoleColor> Colors;

        static void Main(string[] args)
        {
            if (args[0] == "download")
                DownloadMatches(dataPath: args[1], version: args[2], queueId: args[3], apiKeys: args.Subarray(4));
            else if (args[0] == "download-ids")
                DownloadIds(apiKey: args[1], dataPath: args[2], idFilePath: args[4]);
            else if (args[0] == "merge-ids")
                MergeMatches(outputPath: args[1], searchPath: args[2], mergeJsons: false);
            else if (args[0] == "merge-all")
                MergeMatches(outputPath: args[1], searchPath: args[2], mergeJsons: true);
            else
                Console.WriteLine("Unknown command");
        }

        private static void MergeIds(string region, string outputFile, string[] inputFiles)
        {
            var output = new MatchIdContainer(outputFile, EnumStrong.Parse<Region>(region));
            var files = new[] { outputFile }.Concat(inputFiles).Select(file => new { file, count = new CountResult() }).ToList();
            var ids = files.SelectMany(f => new MatchIdContainer(f.file).ReadItems().PassthroughCount(f.count)).ToHashSet();
            foreach (var f in files)
                Console.WriteLine($"Read input {f.file}: {f.count.Count:#,0} items");
            File.Delete(outputFile);
            output.AppendItems(ids.Order(), LosChunkFormat.LZ4HC);
            output.Rewrite();
        }

        private static void DownloadMatches(string dataPath, string version, string queueId, string[] apiKeys)
        {
            using (var p = Process.GetCurrentProcess())
                p.PriorityClass = ProcessPriorityClass.Idle;
            var regionLimits = new Dictionary<Region, (long initial, long range)>
            {
                [Region.EUW] = ((3_582_500_000L + 3_587_650_000) / 2, 500_000),
                [Region.EUNE] = ((1_939_500_000L + 1_942_550_000) / 2, 300_000),
                [Region.KR] = ((3_159_900_000L + 3_163_700_000) / 2, 300_000),
                [Region.NA] = ((2_751_200_000L + 2_754_450_000) / 2, 300_000),
            };
            Colors = new Dictionary<Region, ConsoleColor>
            {
                [Region.EUW] = ConsoleColor.Green,
                [Region.EUNE] = ConsoleColor.Red,
                [Region.NA] = ConsoleColor.Yellow,
                [Region.KR] = ConsoleColor.Magenta,
            };

            Console.WriteLine("Initialising data store ...");
            DataStore.Initialise(dataPath, "");
            Console.WriteLine("    ... done.");

            var downloaders = new List<Downloader>();
            foreach (var region in regionLimits.Keys)
                downloaders.Add(new Downloader(apiKeys, region, version == "" ? null : version, queueId == "" ? (int?) null : int.Parse(queueId), regionLimits[region].initial, regionLimits[region].range));
            Console.WriteLine();
            foreach (var dl in downloaders) // separate step because the constructor prints some stats when it finishes
                dl.DownloadForever();

            while (true)
                Thread.Sleep(9999);
        }

        private static void DownloadIds(string apiKey, string dataPath, string idFilePath)
        {
            var region = EnumStrong.Parse<Region>(Path.GetFileName(idFilePath).Split('-')[0]);
            Console.WriteLine($"Initialising...");
            DataStore.Initialise(dataPath, "");
            Console.WriteLine($"Downloading...");
            var downloader = new MatchDownloader(apiKey, region);
            downloader.OnEveryResponse = (_, __) => { };
            var ids = File.ReadAllLines(idFilePath).Select(l => l.Trim()).Where(l => long.TryParse(l, out _)).Select(l => long.Parse(l)).ToList();
            foreach (var matchId in ids)
            {
                var dl = downloader.DownloadMatch(matchId);
                if (dl.result == MatchDownloadResult.NonExistent)
                {
                    Console.WriteLine($"{matchId:#,0}: non-existent");
                    DataStore.AddNonExistentMatch(region, matchId);
                }
                else if (dl.result == MatchDownloadResult.Failed)
                    Console.WriteLine($"Download failed: {matchId}");
                else if (dl.result == MatchDownloadResult.OK)
                {
                    var info = DataStore.AddMatch(region, dl.json);
                    Console.WriteLine($"{matchId:#,0}: queue {info.QueueId}");
                }
                else
                    throw new Exception();
            }
        }

        private static void MergeMatches(string outputPath, string searchPath, bool mergeJsons)
        {
            if (Directory.Exists(outputPath))
            {
                Console.WriteLine("This command requires the output directory not to exist; it will be created.");
                return;
            }
            Directory.CreateDirectory(outputPath);
            MergeDataStores.MergePreVer(outputPath, searchPath, mergeJsons);
        }

        private static void RewriteBasicInfos(string dataPath)
        {
            DataStore.Initialise(dataPath, "");
            foreach (var region in DataStore.LosMatchJsons.Keys)
            {
                var existing = new HashSet<long>();
                var countRead = new CountThread(10000);
                countRead.OnInterval = count => { Console.Write($"R:{count:#,0} ({countRead.Rate:#,0}/s)  "); };
                var countWrite = new CountThread(10000);
                countWrite.OnInterval = count => { Console.Write($"W:{count:#,0} ({countWrite.Rate:#,0}/s)  "); };
                var matchInfos = DataStore.LosMatchJsons[region].Values
                    .SelectMany(x => x.Values)
                    .SelectMany(container => container.ReadItems())
                    .Select(json => new BasicMatchInfo(json))
                    .PassthroughCount(countRead.Count)
                    .OrderBy(m => m.MatchId)
                    .Where(m => existing.Add(m.MatchId))
                    .PassthroughCount(countWrite.Count);
                if (File.Exists(DataStore.LosMatchInfos[region].FileName))
                    DataStore.LosMatchInfos[region].Rewrite(_ => matchInfos);
                else
                    DataStore.LosMatchInfos[region].AppendItems(matchInfos, LosChunkFormat.LZ4HC);
                countRead.Stop();
                countWrite.Stop();
            }
        }

        private static void GenRedownloadList(string dataPath)
        {
            DataStore.Initialise(dataPath, "");
            foreach (var region in DataStore.LosMatchJsons.Keys)
            {
                var minId = DataStore.LosMatchInfos[region].ReadItems().Where(m => m.GameCreationDate >= new DateTime(2018, 3, 1)).Min(m => m.MatchId);
                File.WriteAllLines($"redo-{region}.txt", DataStore.NonexistentMatchIds[region].Where(id => id > minId).Distinct().Order().Select(id => id.ToString()));
            }
        }

        private static void RecheckNonexistentIds(string dataPath, string[] apiKeys)
        {
            Console.WriteLine($"Initialising...");
            DataStore.Initialise(dataPath, "");
            Console.WriteLine($"Downloading...");
            var threads = new List<Thread>();
            foreach (var region in DataStore.NonexistentMatchIds.Keys)
            {
                var t = new Thread(() => { RecheckNonexistentIdsRegion(region, apiKeys); });
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads)
                t.Join();
        }
        private static void RecheckNonexistentIdsRegion(Region region, string[] apiKeys)
        {
            var path = @"P:\LeagueOfStats\LeagueOfStats\Builds\";
            var doneFile = Path.Combine(path, $"redone-{region}.txt");
            long maxDoneId = 0;
            int hits = 0;
            if (File.Exists(doneFile))
                foreach (var line in File.ReadLines(doneFile).Select(s => s.Trim()).Where(s => s != ""))
                {
                    if (line.StartsWith("hits:"))
                        hits = int.Parse(line.Substring("hits:".Length));
                    else
                        maxDoneId = Math.Max(maxDoneId, long.Parse(line));
                }
            var idsToProcess = File.ReadAllLines(Path.Combine(path, $"redo-{region}.txt")).Select(l => long.Parse(l)).Where(id => id > maxDoneId).ToList();
            var downloaders = apiKeys.Select(apiKey => new MatchDownloader(apiKey, region) { OnEveryResponse = (_, __) => { } }).ToList();
            var nextDownloader = 0;
            int remaining = idsToProcess.Count;
            foreach (var matchId in idsToProcess)
            {
                again:;
                var dl = downloaders[nextDownloader].DownloadMatch(matchId);
                nextDownloader = (nextDownloader + 1) % downloaders.Count;
                if (dl.result == MatchDownloadResult.OverQuota)
                {
                    Thread.Sleep(Rnd.Next(500, 1500));
                    goto again;
                }
                remaining--;
                if (dl.result == MatchDownloadResult.NonExistent)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"{region}:{remaining:#,0}  ");
                    //DataStore.AddNonExistentMatch(region, matchId); - it's already in there, as that's how we built the list for rechecking
                }
                else if (dl.result == MatchDownloadResult.Failed)
                    Console.WriteLine($"Download failed: {matchId}");
                else if (dl.result == MatchDownloadResult.OK)
                {
                    hits++;
                    File.AppendAllLines(doneFile, new[] { $"hits:{hits}" });
                    DataStore.AddMatch(region, dl.json);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write($"{region}:{remaining:#,0}:{hits:#,0}  ");
                }
                else
                    throw new Exception();
                File.AppendAllLines(doneFile, new[] { matchId.ToString() });
            }
        }

    }
}
