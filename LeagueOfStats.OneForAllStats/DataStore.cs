using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using LeagueOfStats.GlobalData;
using LeagueOfStats.StaticData;
using RT.Util;
using RT.Util.Json;

namespace LeagueOfStats.OneForAllStats
{
    static class DataStore
    {
        public static string DataPath;
        public static string Suffix;
        public static string LosPath => Path.Combine(DataPath, $"Global{Suffix}");

        public static CcAutoDictionary<Region, ConcurrentBag<long>> ExistingMatchIds = new CcAutoDictionary<Region, ConcurrentBag<long>>(_ => new ConcurrentBag<long>());
        public static CcAutoDictionary<Region, ConcurrentBag<long>> NonexistentMatchIds = new CcAutoDictionary<Region, ConcurrentBag<long>>(_ => new ConcurrentBag<long>());

        public static CcAutoDictionary<Region, string, int, JsonContainer> LosMatchJsons = new CcAutoDictionary<Region, string, int, JsonContainer>(
            (region, version, queueId) => new JsonContainer(Path.Combine(LosPath, $"{region}-matches-{version}-{queueId}.losjs")));
        public static CcAutoDictionary<Region, MatchIdContainer> LosMatchIdsExisting = new CcAutoDictionary<Region, MatchIdContainer>(
            region => new MatchIdContainer(Path.Combine(LosPath, $"{region}-match-id-existing.losmid"), region));
        public static CcAutoDictionary<Region, MatchIdContainer> LosMatchIdsNonExistent = new CcAutoDictionary<Region, MatchIdContainer>(
            region => new MatchIdContainer(Path.Combine(LosPath, $"{region}-match-id-nonexistent.losmid"), region));

        public static void Initialise(string dataPath, string suffix)
        {
            LeagueStaticData.Load(Path.Combine(dataPath, "Static"));
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
                    LosMatchIdsExisting[region].Initialise(compact: true);
                    ExistingMatchIds[region] = LosMatchIdsExisting[region].ReadItems().ToBag();
                }
                else if ((match = Regex.Match(file.Name, @"^(?<region>[A-Z]+)-match-id-nonexistent\.losmid$")).Success)
                {
                    var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                    LosMatchIdsNonExistent[region].Initialise(compact: true);
                    NonexistentMatchIds[region] = LosMatchIdsNonExistent[region].ReadItems().ToBag();
                }
                else if ((match = Regex.Match(file.Name, @"^(?<region>[A-Z]+)-matches-(?<version>\d+\.\d+)-(?<queueId>\d+)\.losjs$")).Success)
                {
                    var region = EnumStrong.Parse<Region>(match.Groups["region"].Value);
                    var queueId = int.Parse(match.Groups["queueId"].Value);
                    var version = match.Groups["version"].Value;
                    LosMatchJsons[region][version][queueId].EnableAutoRewrite = false;
                    LosMatchJsons[region][version][queueId].Initialise();
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
            return info;
        }
    }

    struct BasicMatchInfo
    {
        public long MatchId;
        public int QueueId;
        public string GameVersion;
        public long GameCreation;

        public BasicMatchInfo(JsonValue json)
        {
            GameCreation = json.Safe["gameCreation"].GetLong();
            MatchId = json["gameId"].GetLong();
            QueueId = json["queueId"].GetInt();
            var ver = Version.Parse(json["gameVersion"].GetString());
            GameVersion = ver.Major + "." + ver.Minor;
        }
    }
}
