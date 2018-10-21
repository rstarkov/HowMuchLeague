using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;
using RT.Util.Paths;

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
                    LosMatchJsons[region][version][queueId].EnableAutoRewrite = false;
                    LosMatchJsons[region][version][queueId].Initialise();
                }
                else if ((match = Regex.Match(file.Name, @"^(?<region>[A-Z]+)-match-infos\.losbi$")).Success)
                {
                    var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                    LosMatchInfos[region].EnableAutoRewrite = false;
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
        public static IEnumerable<JsonValue> ReadMatchesByBasicInfo(Func<BasicMatchInfo, bool> filter)
        {
            var matchFiles = LosMatchInfos
                .SelectMany(kvp => kvp.Value
                    .ReadItems()
                    .Where(filter)
                    .Select(mi => (region: kvp.Key, info: mi)))
                .ToLookup(item => item.info.LosjsFileName(DataPath, Suffix, item.region))
                .Select(grp => (jsons: new JsonContainer(grp.Key), matchIds: grp.Select(item => item.info.MatchId).ToHashSet()))
                .ToList();

            var total = matchFiles.Sum(f => f.matchIds.Count);
            Console.WriteLine($"Processing {total:#,0} matches...");
            foreach (var file in matchFiles)
            {
                Console.WriteLine($"  processing {file.matchIds.Count:#,0} matches from {file.jsons.FileName}...");
                foreach (var json in file.jsons.ReadItems().Where(m => file.matchIds.Contains(m["gameId"].GetLong())))
                {
                    file.matchIds.Remove(json["gameId"].GetLong()); // it's possible for a data file to contain duplicates, so make sure we don't return them twice
                    yield return json;
                }
            }
        }

        /// <summary>
        ///     Enumerates full match JSONs for all matches available for specific region, game version, and/or queue IDs.
        ///     Caller is responsible for filtering out duplicate matches.</summary>
        public static IEnumerable<JsonValue> ReadMatchesByRegVerQue(Func<(Region region, string version, int queueId), bool> fileFilter)
        {
            foreach (var f in new DirectoryInfo(LosPath).GetFiles().OrderBy(f => f.FullName))
            {
                var match = Regex.Match(f.Name, $@"^(?<region>[A-Z]+)-matches-(?<version>[0-9.]+)-(?<queue>\d+)\.losjs$");
                if (!match.Success)
                    continue;
                var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                var version = match.Groups["version"].Value;
                var queueId = int.Parse(match.Groups["queue"].Value);
                if (!fileFilter((region, version, queueId)))
                    continue;
                Console.Write($"Loading {f.FullName}... ");
                var thread = new CountThread(10000);
                foreach (var m in new JsonContainer(f.FullName).ReadItems().PassthroughCount(thread.Count))
                    yield return m;
                thread.Stop();
                Console.WriteLine();
                Console.WriteLine($"Loaded {thread.Count} matches from {f.FullName} in {thread.Duration.TotalSeconds:#,0.000} s");
            }
        }

        /// <summary>Caller is responsible for filtering out duplicate matches.</summary>
        public static List<T> LoadMatchesByVerQue<T>(string version, int queueId, Func<JsonValue, Region, T> loader)
        {
            var matches = new List<T>();
            foreach (var f in new DirectoryInfo(LosPath).GetFiles().OrderBy(f => f.FullName))
            {
                var match = Regex.Match(f.Name, $@"^(?<region>[A-Z]+)-matches-{version}-{queueId}\.losjs$");
                if (!match.Success)
                    continue;
                var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                Console.Write($"Loading {f.FullName}... ");
                var thread = new CountThread(10000);
                matches.AddRange(new JsonContainer(f.FullName).ReadItems().Select(json => loader(json, region)).PassthroughCount(thread.Count));
                thread.Stop();
                Console.WriteLine();
                Console.WriteLine($"Loaded {thread.Count} matches from {f.FullName} in {thread.Duration.TotalSeconds:#,0.000} s");
            }
            return matches;
        }
    }
}
