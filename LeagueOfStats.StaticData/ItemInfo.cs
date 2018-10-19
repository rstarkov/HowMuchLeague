using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RT.Util.ExtensionMethods;
using RT.Util.Json;

namespace LeagueOfStats.StaticData
{
    public class ItemInfo
    {
        public int Id { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string Plaintext { get; private set; }
        public IReadOnlyList<int> BuildsFrom { get; private set; }
        public IReadOnlyList<int> BuildsInto { get; private set; }
        private HashSet<string> _tags;
        private ReadOnlyCollection<string> _tagsRO;
        public IReadOnlyCollection<string> Tags => _tagsRO == null ? (_tagsRO = new ReadOnlyCollection<string>(_tags.ToList())) : _tagsRO;
        public string Icon { get; private set; }

        public int TotalPrice { get; private set; }
        public int SellPrice { get; private set; }
        public bool Purchasable { get; private set; }
        public bool HideFromAll { get; private set; }
        public int Stacks { get; private set; }
        public bool Consumed { get; private set; }
        public bool ConsumeOnFull { get; private set; }
        public bool InStore { get; private set; }
        public int? SpecialRecipeId { get; private set; }
        public string RequiredChampion { get; private set; }
        public string RequiredAlly { get; private set; }
        public bool ExcludeFromStandardSummonerRift { get; private set; }

        public decimal? Stat_ArmorFlat { get; private set; }
        public decimal? Stat_AttackSpeedPrc { get; private set; }
        public decimal? Stat_CritChanceFlat { get; private set; }
        public decimal? Stat_HealthPoolFlat { get; private set; }
        public decimal? Stat_HealthRegenFlat { get; private set; }
        public decimal? Stat_LifeStealPrc { get; private set; }
        public decimal? Stat_MagicDamageFlat { get; private set; }
        public decimal? Stat_MagicResistFlat { get; private set; }
        public decimal? Stat_ManaPoolFlat { get; private set; }
        public decimal? Stat_MoveSpeedFlat { get; private set; }
        public decimal? Stat_MoveSpeedPrc { get; private set; }
        public decimal? Stat_PhysicalDamageFlat { get; private set; }

        public bool MapSummonersRift { get; private set; }
        public bool MapHowlingAbyss { get; private set; }
        public bool MapTwistedTreeline { get; private set; }
        public bool MapCrystalScar { get; private set; }

        public override string ToString() => $"{Name} ({Id})";

        public bool HasTag(string tag) => _tags.Contains(tag);

        public ItemInfo(string id, JsonDict json, string gameVersion)
        {
            json = JsonDict.Parse(json.ToString()); // clone
            Id = int.Parse(id);
            Name = json.GetStringAndRemove("name");
            Description = json.GetStringAndRemove("description");
            Plaintext = json.GetStringAndRemove("plaintext");

            ExcludeFromStandardSummonerRift = Name.Contains("Quick Charge")
                || (Name == "Siege Ballista") || (Name == "Tower: Beam of Ruination") || (Name == "Port Pad") || (Name == "Flash Zone")
                || (Name == "Vanguard Banner") || (Name == "Siege Refund") || (Name == "Entropy Field") || (Name == "Shield Totem");

            var image = json.GetDictAndRemove("image");
            Icon = $"http://ddragon.leagueoflegends.com/cdn/{gameVersion}/img/{image["group"].GetString()}/{image["full"].GetString()}";

            var from = json.GetListAndRemoveOrNull("from");
            if (from == null)
                BuildsFrom = new List<int>().AsReadOnly();
            else
                BuildsFrom = from.Select(s => int.Parse(s.GetString())).ToList().AsReadOnly();

            var into = json.GetListAndRemoveOrNull("into");
            if (into == null)
                BuildsInto = new List<int>().AsReadOnly();
            else
                BuildsInto = into.Select(s => int.Parse(s.GetString())).ToList().AsReadOnly();

            _tags = json.GetListAndRemove("tags")
                .Select(s => s.GetString()).Select(tag =>
                {
                    switch (tag)
                    {
                        case "CriticalStrike": return "CritChance";
                        case "Damage": return "PhysicalDamage";
                        case "SpellDamage": return "MagicDamage";
                        case "NonbootsMovement": return "Movement";
                        case "GoldPer": return "Gold";
                        case "SpellBlock": return "MagicResist";
                        default: return tag;
                    }
                }).ToHashSet();
            if (_tags.Contains("Boots"))
                _tags.Add("Movement");
            if (Description.Contains("<a href='Lethality'>"))
                _tags.Add("Lethality");
            if (Id == 3152) // protobelt
                _tags.Add("Movement");

            var gold = json.GetDictAndRemove("gold");
            TotalPrice = gold.GetIntAndRemove("total");
            SellPrice = gold.GetIntAndRemove("sell");
            Purchasable = gold.GetBoolAndRemove("purchasable");
            gold.GetIntAndRemove("base");
            gold.EnsureEmpty();

            HideFromAll = json.GetBoolAndRemoveOrNull("hideFromAll") ?? false;
            Stacks = json.GetIntAndRemoveOrNull("stacks") ?? 1;
            Consumed = json.GetBoolAndRemoveOrNull("consumed") ?? false;
            ConsumeOnFull = json.GetBoolAndRemoveOrNull("consumeOnFull") ?? false;
            InStore = json.GetBoolAndRemoveOrNull("inStore") ?? true;
            SpecialRecipeId = json.GetIntAndRemoveOrNull("specialRecipe");
            RequiredChampion = json.GetStringAndRemoveOrNull("requiredChampion");
            RequiredAlly = json.GetStringAndRemoveOrNull("requiredAlly");

            var stats = json.GetDictAndRemove("stats");
            addStatTag(Stat_ArmorFlat = stats.GetDecimalAndRemoveOrNull("FlatArmorMod"), "Armor");
            addStatTag(Stat_AttackSpeedPrc = stats.GetDecimalAndRemoveOrNull("PercentAttackSpeedMod"), "AttackSpeed");
            addStatTag(Stat_CritChanceFlat = stats.GetDecimalAndRemoveOrNull("FlatCritChanceMod"), "CritChance");
            addStatTag(Stat_HealthPoolFlat = stats.GetDecimalAndRemoveOrNull("FlatHPPoolMod"), "Health");
            addStatTag(Stat_HealthRegenFlat = stats.GetDecimalAndRemoveOrNull("FlatHPRegenMod"), "HealthRegen");
            addStatTag(Stat_LifeStealPrc = stats.GetDecimalAndRemoveOrNull("PercentLifeStealMod"), "LifeSteal");
            addStatTag(Stat_MagicDamageFlat = stats.GetDecimalAndRemoveOrNull("FlatMagicDamageMod"), "MagicDamage");
            addStatTag(Stat_MagicResistFlat = stats.GetDecimalAndRemoveOrNull("FlatSpellBlockMod"), "MagicResist");
            addStatTag(Stat_ManaPoolFlat = stats.GetDecimalAndRemoveOrNull("FlatMPPoolMod"), "Mana");
            addStatTag(Stat_MoveSpeedFlat = stats.GetDecimalAndRemoveOrNull("FlatMovementSpeedMod"), "Movement");
            addStatTag(Stat_MoveSpeedPrc = stats.GetDecimalAndRemoveOrNull("PercentMovementSpeedMod"), "Movement");
            addStatTag(Stat_PhysicalDamageFlat = stats.GetDecimalAndRemoveOrNull("FlatPhysicalDamageMod"), "PhysicalDamage");
            stats.EnsureEmpty();

            var maps = json.GetDictAndRemove("maps");
            MapSummonersRift = maps["11"].GetBool();
            MapHowlingAbyss = maps["12"].GetBool();
            MapTwistedTreeline = maps["10"].GetBool();

            json.GetIntAndRemoveOrNull("depth");
            json.GetDictAndRemoveOrNull("effect");
            json.GetStringAndRemove("colloq");

            json.EnsureEmpty();
        }

        private void addStatTag(decimal? stat, string tag)
        {
            if (stat != null)
                if (!_tags.Contains(tag))
                    _tags.Add(tag);
        }
    }
}
