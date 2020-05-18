using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LeagueOfStats.StaticData;
using RT.TagSoup;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.CmdGen
{
    class ItemSheet
    {
        public static void Generate(string outputPath)
        {
            var itemsSR = LeagueStaticData.Items.Values.Where(i => i.NoUnconditionalChildren && i.Purchasable && i.MapSummonersRift && !i.ExcludeFromStandardSummonerRift).ToList();
            var allTags = LeagueStaticData.Items.Values.SelectMany(i => i.Tags).Distinct().Order().JoinString(", ");

            var allySpecific = itemsSR.Where(i => i.RequiredAlly != null).ToList();
            var champSpecific = itemsSR.Where(i => i.RequiredChampion != null).ToList();
            var jungleEnchants = itemsSR.Where(i => i.Name.Contains("Enchantment")).ToList();
            itemsSR = itemsSR.Except(allySpecific.Concat(champSpecific).Concat(jungleEnchants)).ToList();

            var srBuyableEnds = LeagueStaticData.Items.Values.Where(i => i.MapSummonersRift && !i.ExcludeFromStandardSummonerRift && i.Purchasable && i.NoPurchasableChildren).ToList();
            var srBuyableStarts = LeagueStaticData.Items.Values.Where(i => i.MapSummonersRift && !i.ExcludeFromStandardSummonerRift && i.Purchasable && !i.AllFrom.Any(id => LeagueStaticData.Items[id].Purchasable)).ToList();
            var srOrphans = srBuyableEnds.Intersect(srBuyableStarts).ToList();
            srBuyableEnds = srBuyableEnds.Except(srOrphans).ToList();
            srBuyableStarts = srBuyableStarts.Except(srOrphans).ToList();

            string css;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LeagueOfStats.CmdGen.Css.Items.css"))
                css = stream.ReadAllText();

            var html = new HTML(
                new HEAD(
                    new META { charset = "utf-8" },
                    new STYLELiteral(css)
                ),
                new BODY(
                    new P("Generated on ", DateTime.Now.ToString("dddd', 'dd'.'MM'.'yyyy' at 'HH':'mm':'ss")),

                    itemSection("Physical Damage", itemsSR.Where(i => i.HasTag("PhysicalDamage")), item => item.Stat_PhysicalDamageFlat),
                    itemSection("Armor Penetration", itemsSR.Where(i => i.HasTag("ArmorPenetration"))),
                    itemSection("Lethality", itemsSR.Where(i => i.HasTag("Lethality"))),
                    itemSection("Attack Speed", itemsSR.Where(i => i.HasTag("AttackSpeed")), item => item.Stat_AttackSpeedPrc),
                    itemSection("Lifesteal", itemsSR.Where(i => i.HasTag("LifeSteal")), item => item.Stat_LifeStealPrc),
                    itemSection("Crit Chance", itemsSR.Where(i => i.HasTag("CritChance")), item => item.Stat_CritChanceFlat),

                    itemSection("Magic Damage", itemsSR.Where(i => i.HasTag("MagicDamage")), item => item.Stat_MagicDamageFlat),
                    itemSection("Magic Penetration", itemsSR.Where(i => i.HasTag("MagicPenetration"))),

                    itemSection("Cooldown Reduction", itemsSR.Where(i => i.HasTag("CooldownReduction"))),

                    itemSection("Armor", itemsSR.Where(i => i.HasTag("Armor")), item => item.Stat_ArmorFlat),
                    itemSection("Magic Resist", itemsSR.Where(i => i.HasTag("MagicResist")), item => item.Stat_MagicResistFlat),
                    itemSection("Health", itemsSR.Where(i => i.HasTag("Health")), item => item.Stat_HealthPoolFlat),
                    itemSection("Health Regen", itemsSR.Where(i => i.HasTag("HealthRegen")), item => item.Stat_HealthRegenFlat),
                    itemSection("Mana", itemsSR.Where(i => i.HasTag("Mana")), item => item.Stat_ManaPoolFlat),
                    itemSection("Mana Regen", itemsSR.Where(i => i.HasTag("ManaRegen"))),

                    itemSection("Movement", itemsSR.Where(i => i.HasTag("Movement"))),
                    itemSection("Has Slow", itemsSR.Where(i => i.HasTag("Slow"))),
                    itemSection("Has On-Hit", itemsSR.Where(i => i.HasTag("OnHit"))),
                    itemSection("Has Aura", itemsSR.Where(i => i.HasTag("Aura"))),
                    itemSection("Has Active", itemsSR.Where(i => i.HasTag("Active") || i.Description.Contains("</active>"))),

                    itemSection("Consumable", itemsSR.Where(i => i.HasTag("Consumable"))),
                    itemSection("Gold", itemsSR.Where(i => i.HasTag("Gold"))),
                    itemSection("Jungle", itemsSR.Where(i => i.HasTag("Jungle"))),
                    itemSection("Laning", itemsSR.Where(i => i.HasTag("Lane"))),
                    itemSection("Stealth", itemsSR.Where(i => i.HasTag("Stealth"))),
                    itemSection("Tenacity", itemsSR.Where(i => i.HasTag("Tenacity"))),
                    itemSection("Trinket", itemsSR.Where(i => i.HasTag("Trinket"))),
                    itemSection("Vision", itemsSR.Where(i => i.HasTag("Vision"))),

                    itemSection("Ally-specific", allySpecific),
                    itemSection("Champion-specific", champSpecific),
                    itemSection("Jungle enchants", jungleEnchants),

                    itemSection("All “normal” items", itemsSR),

                    itemSection("SR: can buy; no buyable parents or children", srOrphans),
                    itemSection("SR: can buy; no buyable parents", srBuyableStarts),
                    itemSection("SR: can buy; no buyable children", srBuyableEnds)
                    // for debugging parent/child relationships
                    //new TABLE(
                    //    LeagueStaticData.Items.Values.Where(item => item.MapSummonersRift && !item.ExcludeFromStandardSummonerRift).Select(item => new TR(
                    //        new TD(item.AllFrom.Select(i => LeagueStaticData.Items[i]).Where(i => i.MapSummonersRift && !i.ExcludeFromStandardSummonerRift).Select(i => itemIcon(i))),
                    //        new TD(itemIcon(item)),
                    //        new TD(item.AllInto.Select(i => LeagueStaticData.Items[i]).Where(i => i.MapSummonersRift && !i.ExcludeFromStandardSummonerRift).Select(i => itemIcon(i)))
                    //        ))
                    //)
                )
            );

            Directory.CreateDirectory(outputPath);
            File.WriteAllText(Path.Combine(outputPath, "Items.html"), html.ToString());
        }

        private static IEnumerable<object> makeChain(ItemInfo item)
        {
            yield return itemIcon(item);
            foreach (var fr in item.AllFrom)
                foreach (var x in makeChain(LeagueStaticData.Items[fr]))
                    yield return x;
        }

        private static object itemIcon(ItemInfo item)
        {
            return new DIV { class_ = "item" }._(
                                new IMG { src = item.Icon, title = item.Name },
                                item.Purchasable ? new P(item.TotalPrice, new SPAN { class_ = "gold" }) : new P("n/a")
                            );
        }

        private static object itemSection(string heading, IEnumerable<ItemInfo> items, Func<ItemInfo, decimal?> mainProperty = null)
        {
            if (mainProperty != null)
                items = items.OrderByDescending(mainProperty).ThenBy(item => item.TotalPrice);
            else
                items = items.OrderBy(item => item.TotalPrice);

            return Ut.NewArray<object>(
                new H3(heading),
                items.Select(item => new DIV { class_ = "item" }._(
                    new IMG { src = item.Icon, title = item.Name },
                    new P(item.TotalPrice, new SPAN { class_ = "gold" }),
                    new P((mainProperty == null ? null : fmtMainProp(mainProperty(item))) ?? new RawTag("&nbsp;"))
                ))
            );
        }

        private static object fmtMainProp(decimal? value)
        {
            if (value == null)
                return null;
            if (value < 1)
                return $"{value:0%}";
            else
                return $"{value}";
        }
    }
}
