using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LeagueOfStats.GlobalData;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.OneForAllStats
{
    class Program
    {
        public static Dictionary<Region, ConsoleColor> Colors;

        static void Main(string[] args)
        {
            if (args[0] == "stats-1fa")
                StatsGen.GenerateOneForAll(dataPath: args[1]);
            else if (args[0] == "download")
                DownloadMatches(apiKey: args[1], dataPath: args[2]);
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

        private static void DownloadMatches(string apiKey, string dataPath)
        {
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

            DataStore.Initialise(dataPath, "");

            var downloaders = new List<Downloader>();
            foreach (var region in regionLimits.Keys)
                downloaders.Add(new Downloader(new[] { apiKey }, region, null, 1020, regionLimits[region].initial, regionLimits[region].range));
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
    }
}
