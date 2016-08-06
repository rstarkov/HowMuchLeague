using System.Collections.Generic;
using LeagueOfStats.PersonalData;
using RT.Util.Serialization;

namespace LeagueOfStats.CmdGen
{
    class Settings
    {
        public string DataPath = @"C:\Temp\League\Data";
        public string OutputPathTemplate = @"C:\Temp\League\{0}-{1}{2}.html";
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
