using System.IO;
using System.Text.RegularExpressions;
using RT.Util;
using RT.Util.Json;

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
    }
}
