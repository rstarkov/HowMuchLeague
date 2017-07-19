using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;

namespace LeagueOfStats.PersonalData
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
            versionsStr = versionsStr.Replace("Riot.DDragon.versions=", "").Replace(";", "");
            var versions = JsonList.Parse(versionsStr);
            GameVersion = versions.First().GetString();

            // Load champion data
            var championDataUrl = $"https://ddragon.leagueoflegends.com/cdn/{GameVersion}/data/en_US/champion.json";
            var championDataPath = Path.Combine(path, championDataUrl.FilenameCharactersEscape());
            string championDataStr;
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
    }

    public class ChampionInfo
    {
        public int Id { get; private set; }
        public string Name { get; private set; }
        public string Title { get; private set; }
        public string Blurb { get; private set; }
        public ChampionResource Resource { get; private set; }

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

        public ChampionInfo(JsonDict json)
        {
            Id = json["key"].GetIntLenient();
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
                default: throw new Exception($"Unrecognized champion resource type: '{json["partype"].GetString()}'.");
            }

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
            BaseAttackSpeedDelay = stats["attackspeedoffset"].GetDecimal();
            PerLevelAttackSpeed = stats["attackspeedperlevel"].GetDecimal();
            BaseMoveSpeed = stats["movespeed"].GetDecimal();
            BaseAttackRange = stats["attackrange"].GetDecimal();
        }
    }
}
