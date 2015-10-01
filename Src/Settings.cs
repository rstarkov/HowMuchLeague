using System.Collections.Generic;
using RT.Util;

namespace LeagueGenMatchHistory
{
    [Settings("LeagueGenMatchHistory", SettingsKind.Global)]
    class Settings : SettingsBase
    {
        public List<SummonerInfo> Summoners = new List<SummonerInfo>();
        public string EmailPath = null;
        public string MatchHistoryPath = null;
        public string OutputPathTemplate = null;
        public List<string> KnownPlayers = new List<string>();
    }

    class SummonerInfo
    {
        public string Region = null;
        public string RegionFull = null;
        public string Name = null;
        public string AccountId = null;
        public string SummonerId = null;
        public string AuthorizationHeader = null;
        public Dictionary<string, string> GamesAndReplays = new Dictionary<string, string>();
    }
}
