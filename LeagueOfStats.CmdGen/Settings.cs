using System.Collections.Generic;
using LeagueOfStats.PersonalData;
using RT.Util.Serialization;

namespace LeagueOfStats.CmdGen
{
    class Settings
    {
        public string LeagueInstallPath = @"C:\Games\League";
        public string DataPath = @"C:\Temp\League\Data";
        public string PersonalOutputPathTemplate = @"C:\Temp\League\{0}-{1}{2}.html";
        public string ItemsOutputPath = @"C:\Temp\League";
        public ItemSetsSettings ItemSetsSettings = new ItemSetsSettings();
        public SummonerRift5v5StatsSettings SummonerRift5v5StatsSettings = new SummonerRift5v5StatsSettings();
        public EventStatsSettings EventStatsSettings = new EventStatsSettings();
        public List<HumanInfo> Humans = new List<HumanInfo>();
    }

    class ItemSetsSettings
    {
        public string ReportPath = @"C:\Temp\League";
        public string SlotsJsonFile = @"C:\Games\League\Config\ItemSets.json";
        public string SlotsName = @"My Preferred Slots";
        public int MaxItemsPerRow = 8;
        public int MinGames = 1000;
        public double UsageCutoffPercent = 0.1;
        public string ItemStatsCachePath = @"C:\Temp\League";
        public double ItemStatsCacheExpiryHours = 20;
        public string[] TopRowItems = new[] { "Health Potion", "Control Ward", "Farsight Alteration", "Oracle Lens", "Corrupting Potion", "Elixir of Iron", "Elixir of Sorcery", "Elixir of Wrath" };
        public double IncludeLastDays = 30;
    }

    class SummonerRift5v5StatsSettings
    {
        public string OutputPath = @"C:\Temp\SummonerRiftMatchups";
        public double IncludeLastDays = 30;
    }

    public class HumanInfo
    {
        /// <summary>Display name for this human.</summary>
        public string Name = null;
        /// <summary>Timezone for time of day stats and day cutoff. Currently no support for moving between timezones.</summary>
        public string TimeZone = null;
        /// <summary>
        ///     All known summoners (accounts) of this human, ordered by which is most likely to be this human if multiple
        ///     summoners are in same game.</summary>
        public List<SummonerId> SummonerIds = new List<SummonerId>();
        /// <summary>Listing only summoners (accounts) for which data loading is enabled, contains all known match infos etc.</summary>
        [ClassifyIgnore]
        public List<SummonerInfo> Summoners;
        /// <summary>All summoner names this human has used, and the number of games we know of.</summary>
        [ClassifyIgnore]
        public Dictionary<string, int> SummonerNames = new Dictionary<string, int>();

        public override string ToString()
        {
            return Name;
        }
    }

    public class SummonerId
    {
        public string RegionServer = null;
        public long AccountId = -1;
        public bool LoadData = false;
    }
}
