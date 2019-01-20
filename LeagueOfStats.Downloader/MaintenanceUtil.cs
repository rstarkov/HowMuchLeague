﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using LeagueOfStats.GlobalData;
using RT.Util;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;
using RT.Util.Paths;

namespace LeagueOfStats.Downloader
{
    static class MaintenanceUtil
    {
        public static void MergeFailed(string dataPath, string mergeLosjs, Region region)
        {
            DataStore.Initialise(dataPath, "");
            foreach (var js in new JsonContainer(mergeLosjs).ReadItems())
                DataStore.AddMatch(region, js);
        }

        public static void MergePreVerToPostVer(string dataPath, string dataSuffix, string searchPath)
        {
            DataStore.Initialise(dataPath, dataSuffix);
            var pm = new PathManager(searchPath);
            pm.AddExcludePath(DataStore.LosPath);
            foreach (var f in pm.GetFiles())
            {
                Console.WriteLine(f.FullName);
                Match match;
                if ((match = Regex.Match(f.Name, @"^(?<region>[A-Z]+)-match-id-nonexistent\.losmid$")).Success)
                {
                    var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                    DataStore.LosMatchIdsNonExistent[region].AppendItems(new MatchIdContainer(f.FullName, region).ReadItems(), LosChunkFormat.LZ4HC);
                }
                else if ((match = Regex.Match(f.Name, @"^(?<region>[A-Z]+)-match-id-existing\.losmid$")).Success)
                {
                    var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                    DataStore.LosMatchIdsExisting[region].AppendItems(new MatchIdContainer(f.FullName, region).ReadItems(), LosChunkFormat.LZ4HC);
                }
                else if ((match = Regex.Match(f.Name, @"^(?<region>[A-Z]+)-matches-(?<queueId>\d+)\.losjs$")).Success)
                {
                    var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                    var queueId = int.Parse(match.Groups["queueId"].Value);
                    foreach (var json in new JsonContainer(f.FullName).ReadItems())
                    {
                        var info = new BasicMatchInfo(json);
                        Ut.Assert(info.QueueId == queueId);
                        DataStore.LosMatchJsons[region][info.GameVersion][info.QueueId].AppendItems(new[] { json }, LosChunkFormat.LZ4);
                    }
                }
            }
        }

        public static void Recompress(string dataPath)
        {
            DataStore.Initialise(dataPath, "");

            foreach (var store in DataStore.LosMatchIdsNonExistent.Values)
                store.Rewrite();
            foreach (var store in DataStore.LosMatchInfos.Values)
                store.Rewrite();

            var haveIds = new AutoDictionary<Region, HashSet<long>>(_ => new HashSet<long>());
            var containers = DataStore.LosMatchJsons.SelectMany(v1 => v1.Value.Values.SelectMany(v2 => v2.Values.Select(c => new { container = c, region = v1.Key, stats = c.GetContainerStats() })))
                .OrderByDescending(c => c.stats.UncompressedItemsCount + c.stats.ShortChunkCount);
            foreach (var val in containers)
            {
                var stats = val.container.GetContainerStats();
                if (stats.ShortChunkCount < 5 && stats.UncompressedItemsCount < 10)
                {
                    Console.WriteLine(val.container.FileName + " - SKIPPED");
                    continue;
                }
                // Recompress as one chunk while eliminating any duplicates
                Console.WriteLine($"{val.container.FileName} - {stats.UncompressedItemsCount:#,0} uncompressed, {stats.ShortChunkCount:#,0} short chunks");
                var savedIds = new HashSet<long>();
                val.container.Rewrite(jsons => jsons.Where(json => savedIds.Add(json["gameId"].GetLong())));
                haveIds[val.region].AddRange(savedIds);
                DataStore.LosMatchIdsExisting[val.region].AppendItems(savedIds, LosChunkFormat.LZ4);
            }

            foreach (var kvp in DataStore.LosMatchIdsExisting)
            {
                kvp.Value.Rewrite();
                var redownload = kvp.Value.ReadItems().Except(haveIds[kvp.Key]).Order().Select(id => id.ToString());
                File.WriteAllLines(Path.Combine(DataStore.LosPath, $"_{kvp.Key}_redownload.txt"), redownload);
            }
        }

        public static void MergeMatches(string outputPath, string searchPath, bool mergeJsons)
        {
            if (Directory.Exists(outputPath))
            {
                Console.WriteLine("This command requires the output directory not to exist; it will be created.");
                return;
            }
            Directory.CreateDirectory(outputPath);
            MergePreVer(outputPath, searchPath, mergeJsons);
        }

        private static void MergePreVer(string outputPath, string searchPath, bool mergeJsons)
        {
            var mergers = new AutoDictionary<Region, RegionMergerPreVer>(region => new RegionMergerPreVer { Region = region });
            foreach (var f in new PathManager(searchPath).GetFiles())
            {
                var match = Regex.Match(f.Name, @"^(?<region>[A-Z]+)-matches-(?<queueId>\d+)\.losjs$");
                var existing = Regex.Match(f.Name, @"^(?<region>[A-Z]+)-match-id-existing\.losmid$");
                var nonexistent = Regex.Match(f.Name, @"^(?<region>[A-Z]+)-match-id-nonexistent\.losmid$");

                if (match.Success && mergeJsons)
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
                if (mergeJsons)
                    File.WriteAllLines(Path.Combine(outputPath, $"{merger.Region}-redownload.txt"), merger.RedownloadIds.Select(id => id.ToString()));
            }

            Console.WriteLine($"TOTAL non-existent: {mergers.Values.Sum(m => m.NonexistentCount):#,0}");
            if (mergeJsons)
            {
                Console.WriteLine($"TOTAL re-download: {mergers.Values.Sum(m => m.RedownloadIds.Count):#,0}");
                Console.WriteLine($"TOTAL have: {mergers.Values.Sum(m => m.HaveCounts.Values.Sum()):#,0}");
                Console.WriteLine($"TOTAL have one-for-all: {mergers.Values.Sum(m => m.HaveCounts[1020]):#,0}");
            }
            else
                Console.WriteLine($"TOTAL existing: {mergers.Values.Sum(m => m.RedownloadIds.Count):#,0}");
        }

        private class RegionMergerPreVer
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
                nonexistentNew.AppendItems(nonexistent.Order(), LosChunkFormat.LZ4HC);
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
                        LosChunkFormat.LZ4HC);
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
                existingNew.AppendItems(existing.Order(), LosChunkFormat.LZ4HC);
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

        public static void RewriteBasicInfos(string dataPath)
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

        public static void GenRedownloadList(string dataPath)
        {
            DataStore.Initialise(dataPath, "");
            foreach (var region in DataStore.LosMatchJsons.Keys)
            {
                var minId = DataStore.LosMatchInfos[region].ReadItems().Where(m => m.GameCreationDate >= new DateTime(2018, 3, 1)).Min(m => m.MatchId);
                File.WriteAllLines($"redo-{region}.txt", DataStore.NonexistentMatchIds[region].Where(id => id > minId).Distinct().Order().Select(id => id.ToString()));
            }
        }

        public static void RecheckNonexistentIds(string dataPath, ApiKeyWrapper[] apiKeys)
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

        public static void RecheckNonexistentIdsRegion(Region region, ApiKeyWrapper[] apiKeys)
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
                if (dl.result == MatchDownloadResult.BackOff)
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