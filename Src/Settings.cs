using System.Collections.Generic;
using RT.Util;
using RT.Util.Serialization;

namespace LeagueGenMatchHistory
{
    [Settings("LeagueGenMatchHistory", SettingsKind.Global)]
    class Settings : SettingsBase
    {
        public string EmailPath = null;
        public string MatchHistoryPath = null;
        public string OutputPathTemplate = null;
        public HashSet<string> KnownPlayers = new HashSet<string>();
        public List<HumanInfo> Humans = new List<HumanInfo>();
        public List<SummonerInfo> Summoners = new List<SummonerInfo>();
    }

    class SummonerInfo
    {
        public string Region = null;
        public string RegionFull = null;
        public string Name = null;
        public long AccountId = -1;
        public long SummonerId = -1;
        public string AuthorizationHeader = null;
        public Dictionary<string, string> GamesAndReplays = new Dictionary<string, string>();

        [ClassifyIgnore]
        public HumanInfo Human;
    }

    class HumanInfo
    {
        public string Name = null;
        public string TimeZone = null;
        public HashSet<string> SummonerNames = new HashSet<string>();
    }
}
