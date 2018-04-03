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
        static void Main(string[] args)
        {
            if (args[0] == "merge-ids")
                MergeIds(args[1], args[2], args.Subarray(3));
            else if (args[0] == "stats")
                ComputeStats(args.Subarray(1));
            else if (args[0] == "download")
                DownloadMatches(args.Subarray(1));
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
            output.AppendItems(ids.Order(), compressed: true);
            output.Rewrite();
        }

        private static void DownloadMatches(string[] args)
        {
            var apiKey = args[0];
            var dataPath = args[1];
            var suffix = args[2];

            var regionLimits = new Dictionary<Region, (long min, long max)>
            {
                [Region.EUW] = (3_582_500_000, 3_587_650_000),
                [Region.EUNE] = (1_939_500_000, 1_942_550_000),
                [Region.KR] = (3_159_900_000, 3_163_700_000),
                [Region.NA] = (2_751_200_000, 2_754_450_000),
            };

            DataStore.Initialise(dataPath, suffix, regionLimits.Keys);

            var downloaders = new List<Downloader>();
            foreach (var region in regionLimits.Keys)
                downloaders.Add(new Downloader(apiKey, region, 1020, regionLimits[region].min, regionLimits[region].max));
            Console.WriteLine();
            foreach (var dl in downloaders) // separate step because the constructor prints some stats when it finishes
                dl.DownloadForever();

            while (true)
                Thread.Sleep(9999);
        }

        private static void ComputeStats(string[] args)
        {
            var dataPath = args[0];
            StatsGen.Generate(dataPath);
        }
    }
}
