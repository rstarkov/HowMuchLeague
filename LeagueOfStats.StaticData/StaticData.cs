using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using RT.Json;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.StaticData
{
    public static class LeagueStaticData
    {
        public static string GameVersion { get; private set; }
        public static IReadOnlyDictionary<int, ChampionInfo> Champions { get; private set; }
        public static IReadOnlyDictionary<int, ItemInfo> Items { get; private set; }

        public static void Load(string path)
        {
            var hc = new HttpClient();

            // Load version info
            var versionsStr = hc.GetString("https://ddragon.leagueoflegends.com/api/versions.js");
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
                championDataStr = hc.GetString(championDataUrl);
                File.WriteAllText(championDataPath, championDataStr);
            }
            var championData = JsonDict.Parse(championDataStr);
            Champions = new ReadOnlyDictionary<int, ChampionInfo>(
                championData["data"].GetDict().Select(kvp => new ChampionInfo(kvp.Key, kvp.Value.GetDict())).ToDictionary(ch => ch.Id, ch => ch)
            );

            // Load item data
            var itemDataUrl = $"https://ddragon.leagueoflegends.com/cdn/{GameVersion}/data/en_US/item.json";
            var itemDataPath = Path.Combine(path, itemDataUrl.FilenameCharactersEscape());
            string itemDataStr;
            if (File.Exists(itemDataPath))
                itemDataStr = File.ReadAllText(itemDataPath);
            else
            {
                itemDataStr = hc.GetString(itemDataUrl);
                File.WriteAllText(itemDataPath, itemDataStr);
            }
            var itemData = JsonDict.Parse(itemDataStr);
            Items = new ReadOnlyDictionary<int, ItemInfo>(
                itemData["data"].GetDict().Select(kvp => new ItemInfo(kvp.Key, kvp.Value.GetDict(), GameVersion)).ToDictionary(ch => ch.Id, ch => ch)
            );
            foreach (var item in Items.Values)
            {
                item.NoUnconditionalChildren = item.BuildsInto.All(ch => Items[ch].RequiredAlly != null || Items[ch].RequiredChampion != null);

                var allFrom = (item.SpecialRecipeFrom == null ? item.BuildsFrom : item.BuildsFrom.Concat(item.SpecialRecipeFrom.Value)).Concat(item.AllFrom).Where(id => Items.ContainsKey(id)).Select(id => Items[id]).ToList();
                foreach (var fr in allFrom)
                {
                    item.AllFrom.Add(fr.Id);
                    fr.AllInto.Add(item.Id);
                }
                foreach (var into in item.BuildsInto.Concat(item.AllInto).Where(id => Items.ContainsKey(id)).Select(id => Items[id]).ToList())
                {
                    item.AllInto.Add(into.Id);
                    into.AllFrom.Add(item.Id);
                }
            }
            IEnumerable<int> recursiveItems(int item, bool children)
            {
                foreach (var child in children ? Items[item].AllInto : Items[item].AllFrom)
                {
                    yield return child;
                    foreach (var sub in recursiveItems(child, children))
                        yield return sub;
                }
            }
            foreach (var item in Items.Values)
            {
                item.AllIntoTransitive = recursiveItems(item.Id, children: true).Distinct().Select(id => Items[id]).ToList().AsReadOnly();
                item.AllFromTransitive = recursiveItems(item.Id, children: false).Distinct().Select(id => Items[id]).ToList().AsReadOnly();
                item.NoPurchasableChildren = item.AllIntoTransitive.All(ch => !ch.Purchasable);
            }
        }
    }
}
