using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LeagueOfStats.GlobalData;
using RT.Util;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;
using RT.Util.Paths;

namespace LeagueOfStats.OneForAllStats
{
    static class MergeDataStores
    {
        public static void Merge(string outputPath, string searchPath)
        {
            var mergers = new AutoDictionary<Region, RegionMerger>(region => new RegionMerger { Region = region });
            foreach (var f in new PathManager(searchPath).GetFiles())
            {
                var match = Regex.Match(f.Name, @"^(?<region>[A-Z]+)-matches-(?<queueId>\d+)\.losjs$");
                var existing = Regex.Match(f.Name, @"^(?<region>[A-Z]+)-match-id-existing\.losmid$");
                var nonexistent = Regex.Match(f.Name, @"^(?<region>[A-Z]+)-match-id-nonexistent\.losmid$");

                if (match.Success)
                    mergers[EnumStrong.Parse<Region>(match.Groups["region"].Value)].MatchFiles.Add((int.Parse(match.Groups["queueId"].Value), f));
                else if (existing.Success)
                    mergers[EnumStrong.Parse<Region>(existing.Groups["region"].Value)].ExistingIdsFiles.Add(f);
                else if (nonexistent.Success)
                    mergers[EnumStrong.Parse<Region>(nonexistent.Groups["region"].Value)].NonexistentIdsFiles.Add(f);
            }

            foreach (var merger in mergers.Values)
            {
                Console.WriteLine($"===== MERGING {merger.Region} ========");
                merger.Merge(outputPath);
                Console.WriteLine();
                Console.WriteLine();
                File.WriteAllLines(Path.Combine(outputPath, $"{merger.Region}-redownload.txt"), merger.RedownloadIds.Select(id => id.ToString()));
            }

            Console.WriteLine($"TOTAL non-existent: {mergers.Values.Sum(m => m.NonexistentCount):#,0}");
            Console.WriteLine($"TOTAL re-download: {mergers.Values.Sum(m => m.RedownloadIds.Count):#,0}");
            Console.WriteLine($"TOTAL have: {mergers.Values.Sum(m => m.HaveCounts.Values.Sum()):#,0}");
            Console.WriteLine($"TOTAL have one-for-all: {mergers.Values.Sum(m => m.HaveCounts[1020]):#,0}");
         }

        private class RegionMerger
        {
            public Region Region;
            public List<(int queueId, FileInfo fi)> MatchFiles = new List<(int, FileInfo)>();
            public List<FileInfo> ExistingIdsFiles = new List<FileInfo>();
            public List<FileInfo> NonexistentIdsFiles = new List<FileInfo>();
            public List<long> RedownloadIds;
            public int NonexistentCount;
            public Dictionary<int, int> HaveCounts = new Dictionary<int, int>();

            public void Merge(string outputPath)
            {
                Console.WriteLine("Non-existent IDs:");
                var nonexistent = new HashSet<long>();
                nonexistent.AddRange(ReadContainersWithLogging(NonexistentIdsFiles.Select(fi => new MatchIdContainer(fi.FullName))));
                Console.WriteLine($"  Total unique IDs: {nonexistent.Count:#,0}");
                var nonexistentNew = new MatchIdContainer(Path.Combine(outputPath, $"{Region}-match-id-nonexistent.losmid"), Region);
                nonexistentNew.AppendItems(nonexistent.Order(), compressed: true);
                NonexistentCount = nonexistent.Count;

                Console.WriteLine();
                Console.WriteLine("Matches:");
                var existingHave = new HashSet<long>();
                foreach (var group in MatchFiles.GroupBy(x => x.queueId).OrderBy(grp => grp.Key))
                {
                    var queueId = group.Key;
                    Console.WriteLine($"  Queue {queueId}");
                    var newMatches = new JsonContainer(Path.Combine(outputPath, $"{Region}-matches-{queueId}.losjs"));
                    var count = new CountResult();
                    newMatches.AppendItems(
                        ReadContainersWithLogging(group.Select(x => new JsonContainer(x.fi.FullName)))
                            .Where(js => existingHave.Add(js["gameId"].GetLong()))
                            .PassthroughCount(count),
                        compressed: true);
                    Console.WriteLine($"    Total unique: {count.Count:#,0}");
                    HaveCounts.Add(queueId, count.Count);
                }

                Console.WriteLine();
                Console.WriteLine("Existing IDs:");
                var existing = new HashSet<long>();
                existing.AddRange(ReadContainersWithLogging(ExistingIdsFiles.Select(fi => new MatchIdContainer(fi.FullName))));
                var existingWasCount = existing.Count;
                existing.AddRange(existingHave);
                Console.WriteLine($"  IDs which were only in match files and not in match-id files: {existing.Count - existingWasCount:#,0}");
                Console.WriteLine($"  Total unique IDs: {existing.Count:#,0}");
                var existingNew = new MatchIdContainer(Path.Combine(outputPath, $"{Region}-match-id-existing.losmid"), Region);
                existingNew.AppendItems(existing.Order(), compressed: true);
                RedownloadIds = existing.Except(existingHave).Order().ToList();
                Console.WriteLine($"  Known IDs we don't have; re-download: {RedownloadIds.Count:#,0}");
            }

            private IEnumerable<T> ReadContainersWithLogging<T>(IEnumerable<LosContainer<T>> containers)
            {
                foreach (var container in containers.OrderBy(c => c.FileName))
                {
                    int count = 0;
                    Console.Write($"    Reading {container.FileName}... ");
                    foreach (var item in container.ReadItems())
                    {
                        count++;
                        yield return item;
                    }
                    Console.WriteLine($"{count:#,0} items.");
                }
            }
        }
    }
}
