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
        public string ItemSetsReportPath = @"C:\Temp\League";
        public string ItemSetsSlotsJson = @"C:\Games\League\Config\ItemSets.json";
        public string ItemSetsSlotsName = @"My Preferred Slots";
        public List<HumanInfo> Humans = new List<HumanInfo>();
    }

    public class HumanInfo
    {
        public string Name = null;
        public string TimeZone = null;
        public List<SummonerId> SummonerIds = new List<SummonerId>();
        [ClassifyIgnore]
        public List<SummonerInfo> Summoners;

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
