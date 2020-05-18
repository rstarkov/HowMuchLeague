using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using LeagueOfStats.GlobalData;
using LeagueOfStats.StaticData;
using RT.TagSoup;
using RT.Util;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;
using RT.Util.Json;
using RT.Util.Paths;

namespace LeagueOfStats.CmdGen
{
    static class ItemSets
    {
        public static void GenerateRecentItemStats(string dataPath, string itemStatsFile, double includeLastDays)
        {
            Console.WriteLine($"Loading basic match infos...");
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(includeLastDays);
            var counts = new AutoDictionary<string, string, int, int>();
            foreach (var match in DataStore.ReadMatchesByBasicInfo(mi => mi.GameCreationDate >= cutoff && (mi.QueueId == 420 || mi.QueueId == 400 || mi.QueueId == 430)))
            {
                foreach (var plr in match.json["participants"].GetList())
                {
                    var lane = plr["timeline"]["lane"].GetString();
                    var role = plr["timeline"]["role"].GetString();
                    var lanerole =
                        lane == "MIDDLE" && role == "SOLO" ? "mid" :
                        lane == "TOP" && role == "SOLO" ? "top" :
                        lane == "JUNGLE" && role == "NONE" ? "jungle" :
                        lane == "BOTTOM" && role == "DUO_CARRY" ? "adc" :
                        lane == "BOTTOM" && role == "DUO_SUPPORT" ? "sup" : null;
                    if (lanerole == null)
                        continue;
                    var champ = LeagueStaticData.Champions[plr["championId"].GetInt()].Name;
                    counts[champ][lanerole][plr["stats"]["item0"].GetInt()]++;
                    counts[champ][lanerole][plr["stats"]["item1"].GetInt()]++;
                    counts[champ][lanerole][plr["stats"]["item2"].GetInt()]++;
                    counts[champ][lanerole][plr["stats"]["item3"].GetInt()]++;
                    counts[champ][lanerole][plr["stats"]["item4"].GetInt()]++;
                    counts[champ][lanerole][plr["stats"]["item5"].GetInt()]++;
                    counts[champ][lanerole][-999]++; // number of games
                }
            }

            File.Delete(itemStatsFile);
            foreach (var champ in counts.Keys)
                foreach (var lanerole in counts[champ].Keys)
                    foreach (var item in counts[champ][lanerole].Keys)
                        File.AppendAllText(itemStatsFile, $"{champ},{lanerole},{item},{counts[champ][lanerole][item]}\r\n");
        }

        public static void Generate(string dataPath, string leagueInstallPath, ItemSetsSettings settings)
        {
            Directory.CreateDirectory(settings.ItemStatsCachePath);
            var itemStatsFile = Path.Combine(settings.ItemStatsCachePath, "item-popularity.csv");
            if (!File.Exists(itemStatsFile) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(itemStatsFile)).TotalHours > settings.ItemStatsCacheExpiryHours)
                GenerateRecentItemStats(dataPath, itemStatsFile, settings.IncludeLastDays);
            var refreshTime = File.GetLastWriteTime(itemStatsFile);

            Console.WriteLine("Generating item sets...");

            var generatedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byName = LeagueStaticData.Items.Values.Where(i => i.Purchasable && i.MapSummonersRift && !i.ExcludeFromStandardSummonerRift && !i.HideFromAll)
                .GroupBy(i => i.Name).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());

            JsonValue preferredSlots = null;
            if (settings.SlotsJsonFile != null && settings.SlotsName != null)
            {
                var json = JsonDict.Parse(File.ReadAllText(settings.SlotsJsonFile));
                preferredSlots = json["itemSets"].GetList().First(l => l["title"].GetString() == settings.SlotsName)["preferredItemSlots"];
            }

            var toprow = settings.TopRowItems.Select(name => byName[name]).ToArray();
            var boots = new[] { "Boots of Swiftness", "Boots of Mobility", "Ionian Boots of Lucidity", "Berserker's Greaves", "Sorcerer's Shoes", "Ninja Tabi", "Mercury's Treads" }
                .Select(n => byName[n]).ToArray();
            var starting = new[] { "Refillable Potion", "Corrupting Potion", "Dark Seal", "Doran's Ring", "Steel Shoulderguards", "Spectral Sickle", "Relic Shield", "Spellthief's Edge", "Doran's Shield", "Doran's Blade", "Cull", "Hunter's Talisman", "Hunter's Machete" }
                .Select(n => byName[n]).ToArray();

            var itemStatsLines = File.ReadAllLines(itemStatsFile)
                .Where(l => l != "").Select(l => l.Split(','))
                .Select(p => (champ: p[0], role: p[1], itemId: int.Parse(p[2]), count: int.Parse(p[3])));
            var itemStats = itemStatsLines
                .ToLookup(p => p.champ)
                .ToDictionary(grp => grp.Key, grp => grp.ToLookup(p => p.role));

            var allItems = itemStatsLines
                .Where(l => LeagueStaticData.Items.ContainsKey(l.itemId))
                .Select(l => (item: LeagueStaticData.Items[l.itemId], l.count))
                .GroupBy(l => l.item)
                .ToDictionary(grp => grp.Key, grp => grp.Sum(l => l.count));
            var allItemsSingleFinal =
                (from item in LeagueStaticData.Items.Values //allItems.Keys
                 where !(item.Purchasable && item.NoPurchasableChildren)
                 let finalChildren = item.AllIntoTransitive.Where(child => child.Purchasable && child.NoPurchasableChildren).ToList()
                 where finalChildren.Count == 1
                 select (item, finalChildren[0])).ToDictionary(x => x.item, x => x.Item2);
            ItemInfo singlePurchasableParent(ItemInfo item)
            {
                if (item.Purchasable && item.NoPurchasableChildren)
                    return item;
                if (!item.Purchasable && item.AllFrom.Count == 1)
                    return singlePurchasableParent(LeagueStaticData.Items[item.AllFrom.First()]);
                return null;
            }
            var allItemsSingleParent =
                (from item in LeagueStaticData.Items.Values //allItems.Keys
                 let parent = singlePurchasableParent(item)
                 where parent != null && parent != item
                 select (item, parent)).ToDictionary(x => x.item, x => x.parent);
            var itemCountRemap = allItems.ToDictionary(x => x.Key, x => x.Key);
            foreach (var kvp in allItemsSingleFinal.Concat(allItemsSingleParent))
                itemCountRemap[kvp.Key] = kvp.Value;
            // Override some remaps which despite having a single path are not always taken
            itemCountRemap[LeagueStaticData.Items[2031]] = LeagueStaticData.Items[2031]; // refillable potion -> corrupting potion
            itemCountRemap[LeagueStaticData.Items[1082]] = LeagueStaticData.Items[1082]; // dark seal -> mejai's

            var pagesHtml = new List<object>();
            pagesHtml.Add(new H1("All items seen"));
            pagesHtml.Add(allItems.OrderByDescending(kvp => kvp.Value).Select(kvp => ItemHtml(kvp.Key, $"{kvp.Value:#,0}")));
            pagesHtml.Add(new H1("Item count remap: predictable final item"));
            pagesHtml.Add(allItemsSingleFinal.Select(kvp => new DIV(ItemHtml(kvp.Key), " => ", ItemHtml(kvp.Value))));
            pagesHtml.Add(new H1("Item count remap: derives from a purchasable item"));
            pagesHtml.Add(allItemsSingleParent.Select(kvp => new DIV(ItemHtml(kvp.Key), " => ", ItemHtml(kvp.Value))));
            pagesHtml.Add(new H1("Items seen which are buyable and final"));
            pagesHtml.Add(allItems.Where(kvp => kvp.Key.Purchasable && kvp.Key.NoPurchasableChildren).OrderByDescending(kvp => kvp.Value).Select(kvp => ItemHtml(kvp.Key, $"{kvp.Value:#,0}")));

            foreach (var champ in LeagueStaticData.Champions.Values.OrderBy(ch => ch.Name))
            {
                foreach (var role in new[] { "mid", "top", "jungle", "adc", "sup" })
                {
                    if (!itemStats.ContainsKey(champ.Name) || !itemStats[champ.Name].Contains(role))
                        continue;
                    var total = itemStats[champ.Name][role].Single(x => x.itemId == -999).count;
                    if (total < settings.MinGames)
                        continue;
                    var itemCounts = itemStats[champ.Name][role]
                        .Where(p => LeagueStaticData.Items.ContainsKey(p.itemId))
                        .ToDictionary(p => LeagueStaticData.Items[p.itemId], p => p.count);
                    foreach (var item in itemCounts.Keys.ToList())
                        if (itemCountRemap[item] != item)
                            itemCounts.IncSafe(itemCountRemap[item], itemCounts[item]);
                    var items = itemCounts
                        .Select(kvp => (count: kvp.Value, item: kvp.Key))
                        .OrderByDescending(p => p.count)
                        .ToList();
                    var minUsage = total * settings.UsageCutoffPercent / 100.0;
                    var sections = new List<List<(int count, ItemInfo item)>>();
                    var titles = new List<string>();

                    // Section 1: preset for trinkets, pots, wards, elixirs
                    sections.Add(toprow.Select(t => (0, t)).ToList());
                    titles.Add($"Consumables and trinkets ({refreshTime:dd MMM yyyy})");

                    // Section 2: starting items
                    sections.Add(items.Where(i => starting.Contains(i.item) && i.count >= minUsage).Take(settings.MaxItemsPerRow).ToList());
                    titles.Add("Starting:  " + relCounts(sections.Last()));

                    // Section 3: boots
                    if (champ.InternalName != "Cassiopeia")
                    {
                        sections.Add(new[] { (0, byName["Boots of Speed"]) }.Concat(items.Where(i => boots.Contains(i.item) && i.count >= minUsage)).ToList());
                        titles.Add("Boots:  " + relCounts(sections.Last().Skip(1)));
                    }

                    // Remaining items above a certain threshold of usage
                    var alreadyListed = sections.SelectMany(s => s.Select(si => si.item)).ToList();
                    var toList = items.Where(i => i.count >= minUsage && i.item.Purchasable && i.item.NoUnconditionalChildren && !alreadyListed.Contains(i.item) && !starting.Contains(i.item)).ToQueue();
                    var mostUsed = toList.Max(i => i.count);
                    sections.Add(new List<(int, ItemInfo)>());
                    while (toList.Count > 0)
                    {
                        var last = sections[sections.Count - 1];
                        if (last.Count == settings.MaxItemsPerRow)
                        {
                            last = new List<(int, ItemInfo)>();
                            sections.Add(last);
                        }
                        last.Add(toList.Dequeue());
                    }
                    for (int i = titles.Count; i < sections.Count; i++)
                        titles.Add("Items:  " + relCounts(sections[i], mostUsed));
                    var blocks = sections.Zip(titles, (section, title) => (title: title, items: section.Select(s => s.item).ToList())).ToList();
                    var caption = $"LoS - {champ.InternalName.SubstringSafe(0, 4)} - {role} - {total:#,0} games";

                    string relCounts(IEnumerable<(int count, ItemInfo item)> sec, int rel = 0)
                    {
                        if (rel == 0)
                            rel = sec.First().count;
                        return sec.Select(s => $"{s.count * 100.0 / rel:0}").JoinString("  ");
                    }

                    // Save to HTML for review / reference
                    pagesHtml.Add(Ut.NewArray<object>(
                        new H1(role.Substring(0, 1).ToUpper() + role.Substring(1) + " " + champ.Name, new SMALL(caption)),
                        new DIV { class_ = "set" }._(
                            blocks.Select(b => Ut.NewArray<object>(
                                new H3(b.title),
                                new DIV { class_ = "row" }._(
                                    b.items.Select(item => ItemHtml(item, item.TotalPrice, new SPAN { class_ = "gold" }))
                                )
                            ))
                        )
                    ));

                    // Generate the item set
                    var itemSet = new JsonDict();
                    itemSet["associatedChampions"] = new JsonList { champ.Id };
                    itemSet["associatedMaps"] = new JsonList { 11 };
                    itemSet["map"] = "SR";
                    itemSet["title"] = caption;
                    itemSet["mode"] = "any";
                    itemSet["type"] = "custom";
                    itemSet["sortrank"] = 1;
                    itemSet["startedFrom"] = "blank";
                    // preferred item slots don't work unless a UID is present; must be stable for League to remember the last selected item set
                    itemSet["uid"] = new Guid(MD5.Create().ComputeHash($"SR/{champ.Name}/{role}".ToUtf8())).ToString().ToLower();
                    itemSet["blocks"] = new JsonList();
                    foreach (var block in blocks)
                    {
                        var blk = new JsonDict();
                        itemSet["blocks"].Add(blk);
                        blk["type"] = block.title;
                        blk["items"] = new JsonList();
                        foreach (var item in block.items)
                            blk["items"].Add(new JsonDict { { "id", item.Id.ToString() }, { "count", 1 } });
                    }
                    if (preferredSlots != null)
                        itemSet["preferredItemSlots"] = preferredSlots;

                    var fileName = Path.Combine(leagueInstallPath, "Config", "Champions", champ.InternalName, "Recommended", $"LOS_{champ.InternalName}_{role}.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(fileName));
                    File.WriteAllText(fileName, itemSet.ToStringIndented());
                    generatedFiles.Add(fileName);
                }
            }
            // On successful completion, delete all item sets matching our naming scheme which we did not generate
            foreach (var file in new PathManager(Path.Combine(leagueInstallPath, "Config", "Champions")).GetFiles())
                if (file.Name.StartsWith("LOS_") && file.Name.EndsWith(".json") && !generatedFiles.Contains(file.FullName))
                {
                    Console.WriteLine($"Deleting obsolete item set at {file.FullName}");
                    file.Delete();
                }
            // Generate HTML with all results
            string css;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LeagueOfStats.CmdGen.Css.ItemSets.css"))
                css = stream.ReadAllText();
            var html = new HTML(
                new META { charset = "utf-8" },
                new HEAD(new STYLELiteral(css)),
                new BODY(new P("Generated on ", DateTime.Now.ToString("dddd', 'dd'.'MM'.'yyyy' at 'HH':'mm':'ss")), pagesHtml)
            );
            Directory.CreateDirectory(settings.ReportPath);
            File.WriteAllText(Path.Combine(settings.ReportPath, "ItemSets.html"), html.ToString());
        }

        private static Tag ItemHtml(ItemInfo item, params object[] label)
        {
            return new DIV { class_ = "item" }._(new IMG { src = item.Icon, title = item.Name }, new P(label));
        }
    }
}
