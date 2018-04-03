using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;

namespace LeagueOfStats.StaticData
{
    public static class LeagueStaticData
    {
        public static string GameVersion { get; private set; }
        public static IReadOnlyDictionary<int, ChampionInfo> Champions { get; private set; }

        public static void Load(string path)
        {
            var hc = new HClient();

            // Load version info
            var versionsStr = hc.Get("https://ddragon.leagueoflegends.com/api/versions.js").Expect(HttpStatusCode.OK).DataString;
            versionsStr = versionsStr.Replace("Riot.DDragon.versions = ", "").Replace(";", "");
            var versions = JsonList.Parse(versionsStr);
            GameVersion = versions.First().GetString();

            // Load champion data
            var championDataUrl = $"https://ddragon.leagueoflegends.com/cdn/{GameVersion}/data/en_US/champion.json";
            var championDataPath = Path.Combine(path, championDataUrl.FilenameCharactersEscape());
            string championDataStr;
            Directory.CreateDirectory(path);
            if (File.Exists(championDataPath))
                championDataStr = File.ReadAllText(championDataPath);
            else
            {
                championDataStr = hc.Get(championDataUrl).Expect(HttpStatusCode.OK).DataString;
                File.WriteAllText(championDataPath, championDataStr);
            }
            var championData = JsonDict.Parse(championDataStr);
            Champions = new ReadOnlyDictionary<int, ChampionInfo>(
                championData["data"].GetDict().Values.Select(js => new ChampionInfo(js.GetDict())).ToDictionary(ch => ch.Id, ch => ch)
            );
        }
    }
}
