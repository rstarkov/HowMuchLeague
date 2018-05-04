using System;
using RT.Util.Json;

namespace LeagueOfStats.GlobalData
{
    public class BasicMatchInfo
    {
        public long MatchId;
        public int QueueId;
        public byte GameVersionMajor, GameVersionMinor;
        public string GameVersion => $"{GameVersionMajor}.{GameVersionMinor}";
        public long GameCreation;

        public BasicMatchInfo()
        {
        }

        public BasicMatchInfo(JsonValue json)
        {
            GameCreation = json.Safe["gameCreation"].GetLong();
            MatchId = json["gameId"].GetLong();
            QueueId = json["queueId"].GetInt();
            if (!json.ContainsKey("gameVersion"))
                GameVersionMajor = GameVersionMinor = 0;
            else
            {
                var ver = Version.Parse(json["gameVersion"].GetString());
                GameVersionMajor = checked((byte) ver.Major);
                GameVersionMinor = checked((byte) ver.Minor);
            }
        }
    }
}
