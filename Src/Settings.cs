using System.Collections.Generic;
using RT.Util;
using RT.Util.Serialization;
using RT.Util.ExtensionMethods;
using LeagueOfStats.PersonalData;

namespace LeagueGenMatchHistory
{
    [Settings("LeagueGenMatchHistory", SettingsKind.Global)]
    class Settings : SettingsBase
    {
        public string MatchHistoryPath = null;
        public string OutputPathTemplate = null;
        public HashSet<string> KnownPlayers = new HashSet<string>();
        public List<HumanInfo> Humans = new List<HumanInfo>();
        public List<SummonerInfo> Summoners = new List<SummonerInfo>();
    }
}
