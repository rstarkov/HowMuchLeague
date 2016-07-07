using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RT.Util.Json;
using RT.Util.ExtensionMethods;
using RT.Util.Serialization;
using RT.Util;

namespace LeagueGenMatchHistory
{
    public class HumanInfo
    {
        public string Name = null;
        public string TimeZone = null;
        public HashSet<string> SummonerNames = new HashSet<string>();

        public override string ToString()
        {
            return Name;
        }
    }

    public class SummonerInfo
    {
        public string Region = null;
        public string RegionFull = null;
        public string Name = null;
        public HashSet<string> PastNames = new HashSet<string>();
        public long AccountId = -1;
        public long SummonerId = -1;
        public string AuthorizationHeader = null;
        public Dictionary<string, string> GamesAndReplays = new Dictionary<string, string>();

        [ClassifyIgnore]
        public HumanInfo Human;

        public override string ToString()
        {
            return "{0}/{1} ({2})".Fmt(Name, Region, Human == null ? "?" : Human.Name);
        }
    }

    public enum Lane { Top, Jungle, Middle, Bottom }

    public enum Role { None, Solo, DuoSupport, Duo, DuoCarry }

    public class Team
    {
        public bool Victory;
        public List<Player> Players;
    }

    public class Player
    {
        public Team Team;
        public Game Game;
        public int ParticipantId, TeamId;
        public long AccountId, SummonerId;
        public string Name;
        public int ChampionId;
        public string Champion { get { return Program.Champions[ChampionId]; } }
        public bool Victory { get { return Team.Victory; } }
        public int Spell1Id, Spell2Id;
        public Role Role;
        public Lane Lane;
        public int Kills, Deaths, Assists;
        public int DamageToChampions, TotalHeal, TotalDamageTaken;
        public int LargestMultiKill;
        public int WardsPlaced;
        public int CreepsAt10, CreepsAt20, CreepsAt30;
        public int GoldAt10, GoldAt20, GoldAt30;

        public int RankOf(Func<Player, double> prop)
        {
            var groups = Game.AllPlayers().GroupBy(p => prop(p)).OrderByDescending(g => g.Key).Select(g => new { count = g.Count(), containsThis = g.Contains(this) }).ToList();
            int rank = 1;
            foreach (var g in groups)
            {
                if (g.containsThis)
                    return rank;
                rank += g.count;
            }
            throw new Exception();
        }

        public int TeamRankOf(Func<Player, double> prop)
        {
            var groups = Game.Ally.Players.GroupBy(p => prop(p)).OrderByDescending(g => g.Key).Select(g => new { count = g.Count(), containsThis = g.Contains(this) }).ToList();
            int rank = 1;
            foreach (var g in groups)
            {
                if (g.containsThis)
                    return rank;
                rank += g.count;
            }
            throw new Exception();
        }

        public double KillParticipation
        {
            get { return (Kills + Assists) / (double) Game.Ally.Players.Sum(p => p.Kills) * 100; }
        }

        public override string ToString()
        {
            return Name + " - " + Champion;
        }
    }

    public class Game
    {
        public string Id;
        public DateTime DateUtc;
        public TimeSpan Duration;
        public DateTime Date(string timeZoneId) { return TimeZoneInfo.ConvertTimeFromUtc(DateUtc, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)); }
        public DateTime DateDayOnly(string timeZoneId) { var d = Date(timeZoneId); return d.TimeOfDay.TotalHours < 5 ? d.Date.AddDays(-1) : d.Date; }
        public string DetailsUrl;
        public int MapId;
        public int QueueId;
        public string Map;
        public string Type;
        public bool? Victory { get { return Ally.Victory ? true : Enemy.Victory ? false : (bool?) null; } }
        public Team Ally, Enemy;
        public Player Plr(string summonerName) { return Ally.Players.Single(p => p.Name == summonerName); }
        public Player Plr(HumanInfo human) { return Ally.Players.Single(p => human.SummonerNames.Contains(p.Name)); }
        public Player Plr(object playerId) { return playerId is string ? Plr(playerId as string) : playerId is HumanInfo ? Plr(playerId as HumanInfo) : Ut.Throw<Player>(new Exception()); }
        public string MicroType { get { return Regex.Matches((Map == "Summoner's Rift" ? "" : " " + Map) + " " + Type, @"\s\(?(.)").Cast<Match>().Select(m => m.Groups[1].Value).JoinString(); } }
        public IEnumerable<Player> AllPlayers() { return Enemy.Players.Concat(Ally.Players); }
        public IEnumerable<Player> OtherPlayers() { return AllPlayers().Where(p => !Program.AllKnownPlayers.Contains(p.Name)); }

        internal Game(JsonDict json, SummonerInfo summoner)
        {
            Id = json["gameId"].GetLong().ToString();
            MapId = json["mapId"].GetInt();
            QueueId = json["queueId"].GetInt();
            setMapAndType();
            DetailsUrl = "http://matchhistory.{0}.leagueoflegends.com/en/#match-details/{1}/{2}/{3}".Fmt(summoner.Region.ToLower(), summoner.RegionFull, Id, summoner.AccountId);
            DateUtc = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc) + TimeSpan.FromSeconds(json["gameCreation"].GetLong() / 1000.0);
            Duration = TimeSpan.FromSeconds(json["gameDuration"].GetInt());
            var players = json["participantIdentities"].GetList().Select(p =>
            {
                var result = new Player();
                result.ParticipantId = p["participantId"].GetInt();
                result.AccountId = p["player"]["accountId"].GetLong();
                result.SummonerId = p["player"].ContainsKey("summonerId") ? p["player"]["summonerId"].GetLong() : -1; // -1 when it's a bot
                result.Name = p["player"]["summonerName"].GetString();
                return result;
            }).ToDictionary(plr => plr.ParticipantId);
            foreach (var p in json["participants"].GetList())
            {
                var pp = players[p["participantId"].GetInt()];
                pp.TeamId = p["teamId"].GetInt();
                pp.ChampionId = p["championId"].GetInt();
                pp.Spell1Id = p["spell1Id"].GetInt();
                pp.Spell2Id = p["spell2Id"].GetInt();
                var role = p["timeline"]["role"].GetString();
                pp.Role = role == "DUO" ? Role.Duo : role == "DUO_CARRY" ? Role.DuoCarry : role == "DUO_SUPPORT" ? Role.DuoSupport : role == "SOLO" ? Role.Solo : role == "NONE" ? Role.None : Ut.Throw<Role>(new Exception());
                var lane = p["timeline"]["lane"].GetString();
                pp.Lane = lane == "TOP" ? Lane.Top : lane == "JUNGLE" ? Lane.Jungle : lane == "MIDDLE" ? Lane.Middle : lane == "BOTTOM" ? Lane.Bottom : Ut.Throw<Lane>(new Exception());
                var stats = p["stats"].GetDict();
                pp.Kills = stats["kills"].GetInt();
                pp.Deaths = stats["deaths"].GetInt();
                pp.Assists = stats["assists"].GetInt();
                pp.DamageToChampions = stats["totalDamageDealtToChampions"].GetInt();
                pp.TotalHeal = stats["totalHeal"].GetInt();
                pp.TotalDamageTaken = stats["totalDamageTaken"].GetInt();
                pp.LargestMultiKill = stats["largestMultiKill"].GetInt();
                pp.WardsPlaced = stats.ContainsKey("wardsPlaced") ? stats["wardsPlaced"].GetInt() : 0;
                var timeline = p["timeline"].GetDict();
                if (Duration > TimeSpan.FromMinutes(10))
                {
                    pp.CreepsAt10 = (int) (timeline["creepsPerMinDeltas"]["0-10"].GetDouble() * 10);
                    pp.GoldAt10 = (int) (timeline["goldPerMinDeltas"]["0-10"].GetDouble() * 10);
                }
                if (Duration > TimeSpan.FromMinutes(20))
                {
                    pp.CreepsAt20 = pp.CreepsAt10 + (int) (timeline["creepsPerMinDeltas"]["10-20"].GetDouble() * 10);
                    pp.GoldAt20 = pp.GoldAt10 + (int) (timeline["goldPerMinDeltas"]["10-20"].GetDouble() * 10);
                }
                if (Duration > TimeSpan.FromMinutes(30))
                {
                    pp.CreepsAt30 = pp.CreepsAt20 + (int) (timeline["creepsPerMinDeltas"]["20-30"].GetDouble() * 10);
                    pp.GoldAt30 = pp.GoldAt20 + (int) (timeline["goldPerMinDeltas"]["20-30"].GetDouble() * 10);
                }
            }
            var teams = players.Values.GroupBy(p => p.TeamId).ToDictionary(g => g.Key, g => new
            {
                Team = new Team { Players = g.OrderBy(p => p.Lane).ThenBy(p => p.Role).ToList() },
                Json = json["teams"].GetList().Single(t => t["teamId"].GetInt() == g.First().TeamId)
            });
            Ut.Assert(teams.Count == 2);
            var ourTeamId = players.Values.Single(p => p.SummonerId == summoner.SummonerId).TeamId;
            Ally = teams[ourTeamId].Team;
            Enemy = teams[teams.Keys.Where(k => k != ourTeamId).Single()].Team;
            foreach (var t in teams.Values)
            {
                t.Team.Victory = t.Json["win"].GetString() == "Win" ? true : t.Json["win"].GetString() == "Fail" ? false : Ut.Throw<bool>(new Exception());
                foreach (var p in t.Team.Players)
                {
                    p.Team = t.Team;
                    p.Game = this;
                }
            }
        }

        private void setMapAndType()
        {
            switch (MapId)
            {
                case 1: Map = "Summoner's Rift (v1 summer)"; break;
                case 2: Map = "Summoner's Rift (v1 autumn)"; break;
                case 3: Map = "Proving Grounds"; break;
                case 4: Map = "Twisted Treeline (v1)"; break;
                case 8: Map = "Crystal Scar"; break;
                case 10: Map = "Twisted Treeline"; break;
                case 11: Map = "Summoner's Rift"; break;
                case 12: Map = "Howling Abyss"; break;
                case 14: Map = "Butcher's Bridge"; break;
                default: throw new Exception("Unknown map: " + MapId);
            }
            string queueMap = null;
            switch (QueueId)
            {
                case 0: Type = "Custom"; break;
                case 8: Type = "3v3"; queueMap = "Twisted Treeline"; break;
                case 2: Type = "5v5 Blind Pick"; queueMap = "Summoner's Rift"; break;
                case 14:
                case 400: Type = "5v5 Draft Pick"; queueMap = "Summoner's Rift"; break; // 400: new dynamic queue draft
                case 4:
                case 410: Type = "5v5 Ranked Solo"; queueMap = "Summoner's Rift"; break; // 410: new dynamic queue draft
                case 6: Type = "5v5 Ranked Premade"; queueMap = "Summoner's Rift"; break;
                case 9: Type = "3v3 Ranked Premade"; queueMap = "Summoner's Rift"; break;
                case 41: Type = "3v3 Ranked Team"; break;
                case 42: Type = "5v5 Ranked Team"; queueMap = "Summoner's Rift"; break;
                case 16: Type = "5v5 Blind Pick"; queueMap = "Crystal Scar"; break;
                case 17: Type = "5v5 Draft Pick"; queueMap = "Crystal Scar"; break;
                case 7: Type = "Coop vs AI (old)"; queueMap = "Summoner's Rift"; break;
                case 25: Type = "Dominion Coop vs AI"; queueMap = "Crystal Scar"; break;
                case 31: Type = "Coop vs AI (Intro)"; queueMap = "Summoner's Rift"; break;
                case 32: Type = "Coop vs AI (Beginner)"; queueMap = "Summoner's Rift"; break;
                case 33: Type = "Coop vs AI (Intermediate)"; queueMap = "Summoner's Rift"; break;
                case 52: Type = "Coop vs AI"; queueMap = "Twisted Treeline"; break;
                case 61: Type = "Team Builder"; break;
                case 65: Type = "ARAM"; queueMap = "Howling Abyss"; break;
                case 70: Type = "One for All"; break;
                case 72: Type = "1v1 Snowdown Showdown"; break;
                case 73: Type = "2v2 Snowdown Showdown"; break;
                case 75: Type = "6v6 Hexakill"; queueMap = "Summoner's Rift"; break;
                case 76: Type = "Ultra Rapid Fire"; break;
                case 83: Type = "Ultra Rapid Fire vs AI"; break;
                case 91: Type = "Doom Bots Rank 1"; break;
                case 92: Type = "Doom Bots Rank 2"; break;
                case 93: Type = "Doom Bots Rank 5"; break;
                case 96: Type = "Ascension"; break;
                case 98: Type = "6v6 Hexakill"; queueMap = "Twisted Treeline"; break;
                case 100: Type = "ARAM"; queueMap = "Butcher's Bridge"; break;
                case 300: Type = "King Poro"; break;
                case 310: Type = "Nemesis"; break;
                case 313: Type = "Black Market Brawlers"; break;
                default: throw new Exception("Unknown queue: " + QueueId);
            }
            if (queueMap != null)
                Ut.Assert(Map == queueMap);
        }
    }
}
