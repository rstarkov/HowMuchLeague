using System.Collections.Generic;
using System.IO;
using System.Linq;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;

// Assumptions:
// - a human may have accounts with identical names in several regions (but in this case some stats will be grouped together - fixable if this is ever a concern)
// - no other people have accounts with the same names in _any_ region as any defined humans
// - the summoner-to-human mapping is genuine, i.e. no games can contain more than one of the multiple accounts belonging to the same human

namespace LeagueGenMatchHistory
{
    class Program
    {
        public static Settings Settings;
        public static Dictionary<int, string> Champions = new Dictionary<int, string>();
        public static HashSet<string> AllKnownPlayers;

        static void Main(string[] args)
        {
            SettingsUtil.LoadSettings(out Settings);
            Settings.KnownPlayers.RemoveWhere(name => Settings.Humans.Any(h => h.SummonerNames.Contains(name)));
            Settings.Save();
            Directory.CreateDirectory(Path.Combine(Settings.MatchHistoryPath, "json"));

            AllKnownPlayers = Settings.KnownPlayers.Concat(Settings.Humans.SelectMany(h => h.SummonerNames)).ToHashSet();
            foreach (var sm in Settings.Summoners)
            {
                sm.Human = Settings.Humans.Single(h => h.SummonerNames.Contains(sm.Name));
                sm.PastNames.Add(sm.Name);
            }
            var generators = Settings.Summoners.ToDictionary(sm => sm, sm => new Generator(sm));

            // Load champion id to name map
            var champs = JsonDict.Parse(File.ReadAllText(Path.Combine(Settings.MatchHistoryPath, "champions.json")));
            foreach (var kvp in champs["data"].GetDict())
                Champions[kvp.Value["key"].GetIntLenient()] = kvp.Value["name"].GetString();

            // Load known game IDs by querying Riot
#if !DEBUG
            Console.WriteLine("Querying Riot...");
            foreach (var gen in generators.Values)
            {
                if (gen.Summoner.AuthorizationHeader == "")
                    continue;
                gen.DiscoverGameIds(false);
                Settings.Save();
            }
#endif

            foreach (var gen in generators.Values)
                gen.LoadGames();
            foreach (var gen in generators.Values)
            {
                gen.ProduceGamesTable();
                gen.ProduceStats();
                gen.ProduceStats(200);
            }
            foreach (var human in Settings.Humans)
            {
                var gen = new Generator(human, generators.Values);
                gen.ProduceGamesTable();
                gen.ProduceStats();
                gen.ProduceStats(200);
            }
        }
    }
}
