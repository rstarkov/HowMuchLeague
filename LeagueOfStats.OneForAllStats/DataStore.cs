using System.Collections.Generic;
using System.IO;
using LeagueOfStats.GlobalData;
using LeagueOfStats.StaticData;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;
using RT.Util.Json;

namespace LeagueOfStats.OneForAllStats
{
    static class DataStore
    {
        public static string DataPath;
        public static string Suffix;

        public static AutoDictionary<Region, HashSet<long>> ExistingMatchIds = new AutoDictionary<Region, HashSet<long>>(_ => new HashSet<long>());
        public static AutoDictionary<Region, HashSet<long>> NonexistentMatchIds = new AutoDictionary<Region, HashSet<long>>(_ => new HashSet<long>());

        public static AutoDictionary<Region, AutoDictionary<int, JsonContainer>> LosMatchJsons = new AutoDictionary<Region, AutoDictionary<int, JsonContainer>>(region => 
            new AutoDictionary<int, JsonContainer>(queueId => new JsonContainer(Path.Combine(DataPath, $"Global{Suffix}", $"{region}-matches-{queueId}.losjs"))));
        public static AutoDictionary<Region, MatchIdContainer> LosMatchIdsExisting = new AutoDictionary<Region, MatchIdContainer>(region => new MatchIdContainer(Path.Combine(DataPath, $"Global{Suffix}", $"{region}-match-id-existing.losmid"), region));
        public static AutoDictionary<Region, MatchIdContainer> LosMatchIdsNonExistent = new AutoDictionary<Region, MatchIdContainer>(region => new MatchIdContainer(Path.Combine(DataPath, $"Global{Suffix}", $"{region}-match-id-nonexistent.losmid"), region));

        public static void Initialise(string dataPath, string suffix, IEnumerable<Region> regions)
        {
            LeagueStaticData.Load(Path.Combine(dataPath, "Static"));
            DataPath = dataPath;
            Suffix = suffix;
            foreach (var region in regions)
            {
                LosMatchIdsExisting[region].Initialise(compact: true);
                LosMatchIdsNonExistent[region].Initialise(compact: true);

                ExistingMatchIds[region] = LosMatchIdsExisting[region].ReadItems().ToHashSet();
                NonexistentMatchIds[region] = LosMatchIdsNonExistent[region].ReadItems().ToHashSet();
            }
        }

        public static void AddNonExistentMatch(Region region, long matchId)
        {
            NonexistentMatchIds[region].Add(matchId);
            LosMatchIdsNonExistent[region].AppendItems(new[] { matchId }, LosChunkFormat.Raw);
        }

        public static void AddMatch(Region region, int queueId, long matchId, JsonValue json)
        {
            ExistingMatchIds[region].Add(matchId);
            LosMatchJsons[region][queueId].AppendItems(new[] { json }, LosChunkFormat.LZ4HC);
            LosMatchIdsExisting[region].AppendItems(new[] { matchId }, LosChunkFormat.Raw);
        }
    }
}
