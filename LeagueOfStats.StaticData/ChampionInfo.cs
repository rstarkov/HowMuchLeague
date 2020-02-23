using System;
using RT.Util.Json;

namespace LeagueOfStats.StaticData
{
    public enum ChampionResource
    {
        None,
        Mana,
        Energy,
        Fury,
        Rage,
        Ferocity,
        Heat,
        BloodWell,
        Flow,
        Courage,
        Shield,
        CrimsonRush,
        Grit,
    }

    public class ChampionInfo
    {
        public int Id { get; private set; }
        public string InternalName { get; private set; }
        public string Name { get; private set; }
        public string Title { get; private set; }
        public string Blurb { get; private set; }
        public ChampionResource Resource { get; private set; }
        public string ImageUrl { get; private set; }

        public decimal BaseHealth { get; private set; }
        public decimal PerLevelHealth { get; private set; }
        public decimal BaseMana { get; private set; }
        public decimal PerLevelMana { get; private set; }
        public decimal BaseArmor { get; private set; }
        public decimal PerLevelArmor { get; private set; }
        public decimal BaseMR { get; private set; }
        public decimal PerLevelMR { get; private set; }
        public decimal BaseHealthRegen { get; private set; }
        public decimal PerLevelHealthRegen { get; private set; }
        public decimal BaseManaRegen { get; private set; }
        public decimal PerLevelManaRegen { get; private set; }
        public decimal BaseCrit { get; private set; }
        public decimal PerLevelCrit { get; private set; }
        public decimal BaseAttackDamage { get; private set; }
        public decimal PerLevelAttackDamage { get; private set; }
        public decimal BaseAttackSpeedDelay { get; private set; }
        public decimal PerLevelAttackSpeed { get; private set; }
        public decimal BaseMoveSpeed { get; private set; }
        public decimal BaseAttackRange { get; private set; }

        public ChampionInfo(string internalName, JsonDict json)
        {
            Id = json["key"].GetIntLenient();
            InternalName = internalName;
            Name = json["name"].GetString();
            Title = json["title"].GetString();
            Blurb = json["blurb"].GetString();
            switch (json["partype"].GetString())
            {
                case "None": Resource = ChampionResource.None; break;
                case "Mana": Resource = ChampionResource.Mana; break;
                case "Energy": Resource = ChampionResource.Energy; break;
                case "Fury": Resource = ChampionResource.Fury; break;
                case "Rage": Resource = ChampionResource.Rage; break;
                case "Ferocity": Resource = ChampionResource.Ferocity; break;
                case "Heat": Resource = ChampionResource.Heat; break;
                case "Blood Well": Resource = ChampionResource.BloodWell; break;
                case "Flow": Resource = ChampionResource.Flow; break;
                case "Courage": Resource = ChampionResource.Courage; break;
                case "Shield": Resource = ChampionResource.Shield; break;
                case "Crimson Rush": Resource = ChampionResource.CrimsonRush; break;
                case "Grit": Resource = ChampionResource.Grit; break;
                default: throw new Exception($"Unrecognized champion resource type: '{json["partype"].GetString()}'.");
            }
            ImageUrl = $"https://ddragon.leagueoflegends.com/cdn/{json["version"].GetString()}/img/champion/{json["image"]["full"].GetString()}";

            var stats = json["stats"];
            BaseHealth = stats["hp"].GetDecimal();
            PerLevelHealth = stats["hpperlevel"].GetDecimal();
            BaseMana = stats["mp"].GetDecimal();
            PerLevelMana = stats["mpperlevel"].GetDecimal();
            BaseArmor = stats["armor"].GetDecimal();
            PerLevelArmor = stats["armorperlevel"].GetDecimal();
            BaseMR = stats["spellblock"].GetDecimal();
            PerLevelMR = stats["spellblockperlevel"].GetDecimal();
            BaseHealthRegen = stats["hpregen"].GetDecimal();
            PerLevelHealthRegen = stats["hpregenperlevel"].GetDecimal();
            BaseManaRegen = stats["mpregen"].GetDecimal();
            PerLevelManaRegen = stats["mpregenperlevel"].GetDecimal();
            BaseCrit = stats["crit"].GetDecimal();
            PerLevelCrit = stats["critperlevel"].GetDecimal();
            BaseAttackDamage = stats["attackdamage"].GetDecimal();
            PerLevelAttackDamage = stats["attackdamageperlevel"].GetDecimal();
            BaseAttackSpeedDelay = stats.Safe["attackspeedoffset"].GetDecimalSafe() ?? -1; // mysteriously gone in 8.21
            PerLevelAttackSpeed = stats["attackspeedperlevel"].GetDecimal();
            BaseMoveSpeed = stats["movespeed"].GetDecimal();
            BaseAttackRange = stats["attackrange"].GetDecimal();
        }
    }
}
