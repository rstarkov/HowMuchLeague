using System;
using System.Collections.Generic;
using System.Linq;
using LeagueOfStats.GlobalData;
using LeagueOfStats.StaticData;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.CmdGen
{
    static class MiscStats
    {
        public static void GenerateTotalKDA()
        {
            var kills = new Dictionary<string, int>();
            var deaths = new Dictionary<string, int>();
            var games = new Dictionary<string, int>();
            foreach (var m in DataStore.ReadMatchesByRegVerQue(f => f.queueId == 4 || f.queueId == 6 || f.queueId == 410 || f.queueId == 420 || f.queueId == 440))
            {
                foreach (var p in m.json["participants"].GetList())
                {
                    var champ = LeagueStaticData.Champions[p["championId"].GetInt()].Name;
                    var stats = p["stats"];
                    games.IncSafe(champ);
                    kills.IncSafe(champ, stats["kills"].GetInt());
                    deaths.IncSafe(champ, stats["deaths"].GetInt());
                }
            }
            Console.WriteLine($"");
            Console.WriteLine($"Champion,Games,Kills,Deaths");
            foreach (var c in LeagueStaticData.Champions.Values.Select(c => c.Name).Order())
                Console.WriteLine($"{c},{games[c]},{kills[c]},{deaths[c]}");
        }
    }
}
