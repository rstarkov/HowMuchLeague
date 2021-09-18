using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RT.Json;
using RT.Util;
using RT.Util.ExtensionMethods.Obsolete;

namespace LeagueOfStats.GlobalData
{
    public static class DataStore
    {
        public static string DataPath;
        public static string Suffix;
        public static string LosPath => Path.Combine(DataPath, $"Global{Suffix}");

        private static CcAutoDictionary<Region, CompactSetOfLong> _existingMatchIds = null;
        private static CcAutoDictionary<Region, CompactSetOfLong> _nonexistentMatchIds = null;

        public static CcAutoDictionary<Region, CompactSetOfLong> ExistingMatchIds
        {
            get
            {
                lock (_existingMatchIdsLock)
                {
                    if (_existingMatchIds == null)
                    {
                        _existingMatchIds = new CcAutoDictionary<Region, CompactSetOfLong>(_ => new CompactSetOfLong());
                        foreach (var kvp in LosMatchIdsExisting)
                            _existingMatchIds[kvp.Key] = new CompactSetOfLong(kvp.Value.ReadItems());
                    }
                    return _existingMatchIds;
                }
            }
        }
        private static object _existingMatchIdsLock = new object();

        public static CcAutoDictionary<Region, CompactSetOfLong> NonexistentMatchIds
        {
            get
            {
                lock (_nonexistentMatchIdsLock)
                {
                    if (_nonexistentMatchIds == null)
                    {
                        _nonexistentMatchIds = new CcAutoDictionary<Region, CompactSetOfLong>(_ => new CompactSetOfLong());
                        foreach (var kvp in LosMatchIdsNonExistent)
                            _nonexistentMatchIds[kvp.Key] = new CompactSetOfLong(kvp.Value.ReadItems());
                    }
                    return _nonexistentMatchIds;
                }
            }
        }
        private static object _nonexistentMatchIdsLock = new object();

        public static CcAutoDictionary<Region, string, int, JsonContainer> LosMatchJsons = new CcAutoDictionary<Region, string, int, JsonContainer>(
            (region, version, queueId) => new JsonContainer(Path.Combine(LosPath, $"{region}-matches-{version}-{queueId}.losjs")));
        public static CcAutoDictionary<Region, BasicMatchInfoContainer> LosMatchInfos = new CcAutoDictionary<Region, BasicMatchInfoContainer>(
            region => new BasicMatchInfoContainer(Path.Combine(LosPath, $"{region}-match-infos.losbi"), region));
        public static CcAutoDictionary<Region, MatchIdContainer> LosMatchIdsExisting = new CcAutoDictionary<Region, MatchIdContainer>(
            region => new MatchIdContainer(Path.Combine(LosPath, $"{region}-match-id-existing.losmid"), region));
        public static CcAutoDictionary<Region, MatchIdContainer> LosMatchIdsNonExistent = new CcAutoDictionary<Region, MatchIdContainer>(
            region => new MatchIdContainer(Path.Combine(LosPath, $"{region}-match-id-nonexistent.losmid"), region));

        public static void Initialise(string dataPath, string suffix, bool autoRewrites = true)
        {
            DataPath = dataPath;
            Suffix = suffix;

            var losDir = new DirectoryInfo(LosPath);
            if (!losDir.Exists)
                losDir.Create();
            foreach (var file in losDir.GetFiles())
            {
                Match match;
                if ((match = Regex.Match(file.Name, @"^(?<region>[A-Z]+)-match-id-existing\.losmid$")).Success)
                {
                    var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                    LosMatchIdsExisting[region].EnableAutoRewrite = autoRewrites;
                    LosMatchIdsExisting[region].Initialise();
                }
                else if ((match = Regex.Match(file.Name, @"^(?<region>[A-Z]+)-match-id-nonexistent\.losmid$")).Success)
                {
                    var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                    LosMatchIdsNonExistent[region].EnableAutoRewrite = autoRewrites;
                    LosMatchIdsNonExistent[region].Initialise();
                }
                else if ((match = Regex.Match(file.Name, @"^(?<region>[A-Z]+)-matches-(?<version>\d+\.\d+)-(?<queueId>\d+)\.losjs$")).Success)
                {
                    var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                    var queueId = int.Parse(match.Groups["queueId"].Value);
                    var version = match.Groups["version"].Value;
                    LosMatchJsons[region][version][queueId].EnableAutoRewrite = autoRewrites;
                    LosMatchJsons[region][version][queueId].Initialise();
                }
                else if ((match = Regex.Match(file.Name, @"^(?<region>[A-Z]+)-match-infos\.losbi$")).Success)
                {
                    var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                    LosMatchInfos[region].EnableAutoRewrite = autoRewrites;
                    LosMatchInfos[region].Initialise();
                }
            }
        }

        public static void AddNonExistentMatch(Region region, long matchId)
        {
            NonexistentMatchIds[region].Add(matchId);
            LosMatchIdsNonExistent[region].AppendItems(new[] { matchId }, LosChunkFormat.Raw);
        }

        public static BasicMatchInfo AddMatch(Region region, JsonValue json)
        {
            var info = new BasicMatchInfo(json);
            ExistingMatchIds[region].Add(info.MatchId);
            LosMatchJsons[region][info.GameVersion][info.QueueId].AppendItems(new[] { json }, LosChunkFormat.LZ4HC);
            LosMatchIdsExisting[region].AppendItems(new[] { info.MatchId }, LosChunkFormat.Raw);
            LosMatchInfos[region].AppendItems(new[] { info }, LosChunkFormat.LZ4HC);
            return info;
        }

        /// <summary>Enumerates full match JSONs for matches that satisfy a filter on basic match properties.</summary>
        public static IEnumerable<(JsonValue json, Region region)> ReadMatchesByBasicInfo(Func<BasicMatchInfo, bool> filter)
        {
            var matchFiles = LosMatchInfos
                .SelectMany(kvp => kvp.Value
                    .ReadItems()
                    .Where(filter)
                    .Select(mi => (region: kvp.Key, info: mi)))
                .ToLookup(item => (region: item.region, version: item.info.GameVersion, queue: item.info.QueueId))
                .Select(grp => (jsons: LosMatchJsons[grp.Key.region][grp.Key.version][grp.Key.queue], matchIds: grp.Select(item => item.info.MatchId).ToHashSet(), region: grp.Key.region))
                .ToList();

            var total = matchFiles.Sum(f => f.matchIds.Count);
            Console.WriteLine($"Processing {total:#,0} matches...");
            foreach (var file in matchFiles)
            {
                Console.WriteLine($"  processing {file.matchIds.Count:#,0} matches from {file.jsons.FileName}...");
                foreach (var json in file.jsons.ReadItems().Where(m => file.matchIds.Contains(m["gameId"].GetLong())))
                {
                    file.matchIds.Remove(json["gameId"].GetLong()); // it's possible for a data file to contain duplicates, so make sure we don't return them twice
                    yield return (json, file.region);
                }
            }
        }

        /// <summary>
        ///     Enumerates full match JSONs for all matches available for specific region, game version, and/or queue IDs.</summary>
        public static IEnumerable<(JsonValue json, Region region)> ReadMatchesByRegVerQue(Func<(Region region, string version, int queueId), bool> fileFilter)
        {
            foreach (var f in LosMatchJsons.SelectMany(kvpR => kvpR.Value.SelectMany(kvpV => kvpV.Value.Select(kvpQ => (region: kvpR.Key, version: kvpV.Key, queueId: kvpQ.Key, file: kvpQ.Value)))))
            {
                if (!fileFilter((f.region, f.version, f.queueId)))
                    continue;
                Console.Write($"Loading {f.file.FileName}... ");
                var thread = new CountThread(10000);
                var matchIds = new HashSet<long>();
                foreach (var m in f.file.ReadItems().PassthroughCount(thread.Count).Where(js => matchIds.Add(js["gameId"].GetLong())))
                    yield return (m, f.region);
                thread.Stop();
                Console.WriteLine();
                Console.WriteLine($"Loaded {thread.Count} matches from {f.file.FileName} in {thread.Duration.TotalSeconds:#,0.000} s");
            }
        }

        /// <summary>
        ///     Re-initialises existing match IDs by dropping the cached ID collection and optionally immediately
        ///     re-loading it from disk. Use after making changes directly to the data file.</summary>
        public static void ReinitExistingMatchIds(Region region, bool immediateReload = false)
        {
            lock (_existingMatchIdsLock)
            {
                _existingMatchIds[region] = new CompactSetOfLong(LosMatchIdsExisting[region].ReadItems());
            }
        }

        /// <summary>
        ///     Re-initialises non-existent match IDs by dropping the cached ID collection and optionally immediately
        ///     re-loading it from disk. Use after making changes directly to the data file.</summary>
        public static void ReloadNonexistentMatchIds(Region region)
        {
            lock (_nonexistentMatchIdsLock)
            {
                if (_nonexistentMatchIds == null)
                    return; // hasn't been used, so no need to force a reload
                _nonexistentMatchIds[region] = new CompactSetOfLong(LosMatchIdsNonExistent[region].ReadItems());
            }
        }
    }
}
