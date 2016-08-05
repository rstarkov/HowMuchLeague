using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;

namespace LeagueOfStats.PersonalData
{
    public enum Lane { Top, Jungle, Middle, Bottom }

    public enum Role { None, Solo, DuoSupport, Duo, DuoCarry }

    public class Team
    {
        public Game Game { get; private set; }
        public bool Victory { get; private set; }
        public IList<Player> Players { get; private set; }

        internal Team(JsonValue json, Dictionary<int, JsonValue> participants, Dictionary<int, JsonValue> identities, Game game)
        {
            Game = game;
            Victory = json["win"].GetString() == "Win" ? true : json["win"].GetString() == "Fail" ? false : Ut.Throw<bool>(new Exception());
            Players = participants.Values.Select(p => new Player(p, identities[p["participantId"].GetInt()], game, this))
                .OrderBy(p => p.Lane).ThenBy(p => p.Role)
                .ToList().AsReadOnly();
        }
    }

    public class Player
    {
        public Game Game { get; private set; }
        public Team Team { get; private set; }
        public int TeamId { get; private set; }
        public long AccountId { get; private set; }
        public long SummonerId { get; private set; }
        public string Name { get; private set; }
        public int ChampionId { get; private set; }
        public string Champion { get { return LeagueStaticData.Champions[ChampionId].Name; } }
        public int Spell1Id { get; private set; }
        public int Spell2Id { get; private set; }
        public Role Role { get; private set; }
        public Lane Lane { get; private set; }
        public int Kills { get; private set; }
        public int Deaths { get; private set; }
        public int Assists { get; private set; }
        public int DamageToChampions { get; private set; }
        public int TotalHeal { get; private set; }
        public int TotalDamageTaken { get; private set; }
        public int LargestMultiKill { get; private set; }
        public int WardsPlaced { get; private set; }
        public int CreepsAt10 { get; private set; }
        public int CreepsAt20 { get; private set; }
        public int CreepsAt30 { get; private set; }
        public int GoldAt10 { get; private set; }
        public int GoldAt20 { get; private set; }
        public int GoldAt30 { get; private set; }

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

        internal Player(JsonValue participant, JsonValue identity, Game game, Team team)
        {
            Game = game;
            Team = team;

            AccountId = identity["player"]["accountId"].GetLong();
            SummonerId = identity["player"].ContainsKey("summonerId") ? identity["player"]["summonerId"].GetLong() : -1; // -1 when it's a bot
            Name = identity["player"]["summonerName"].GetString();

            TeamId = participant["teamId"].GetInt();
            ChampionId = participant["championId"].GetInt();
            Spell1Id = participant["spell1Id"].GetInt();
            Spell2Id = participant["spell2Id"].GetInt();
            var role = participant["timeline"]["role"].GetString();
            Role = role == "DUO" ? Role.Duo : role == "DUO_CARRY" ? Role.DuoCarry : role == "DUO_SUPPORT" ? Role.DuoSupport : role == "SOLO" ? Role.Solo : role == "NONE" ? Role.None : Ut.Throw<Role>(new Exception());
            var lane = participant["timeline"]["lane"].GetString();
            Lane = lane == "TOP" ? Lane.Top : lane == "JUNGLE" ? Lane.Jungle : lane == "MIDDLE" ? Lane.Middle : lane == "BOTTOM" ? Lane.Bottom : Ut.Throw<Lane>(new Exception());
            var stats = participant["stats"].GetDict();
            Kills = stats["kills"].GetInt();
            Deaths = stats["deaths"].GetInt();
            Assists = stats["assists"].GetInt();
            DamageToChampions = stats["totalDamageDealtToChampions"].GetInt();
            TotalHeal = stats["totalHeal"].GetInt();
            TotalDamageTaken = stats["totalDamageTaken"].GetInt();
            LargestMultiKill = stats["largestMultiKill"].GetInt();
            WardsPlaced = stats.ContainsKey("wardsPlaced") ? stats["wardsPlaced"].GetInt() : 0;
            var timeline = participant["timeline"].GetDict();
            if (game.Duration > TimeSpan.FromMinutes(10))
            {
                CreepsAt10 = (int) (timeline["creepsPerMinDeltas"]["0-10"].GetDouble() * 10);
                GoldAt10 = (int) (timeline["goldPerMinDeltas"]["0-10"].GetDouble() * 10);
            }
            if (game.Duration > TimeSpan.FromMinutes(20))
            {
                CreepsAt20 = CreepsAt10 + (int) (timeline["creepsPerMinDeltas"]["10-20"].GetDouble() * 10);
                GoldAt20 = GoldAt10 + (int) (timeline["goldPerMinDeltas"]["10-20"].GetDouble() * 10);
            }
            if (game.Duration > TimeSpan.FromMinutes(30))
            {
                CreepsAt30 = CreepsAt20 + (int) (timeline["creepsPerMinDeltas"]["20-30"].GetDouble() * 10);
                GoldAt30 = GoldAt20 + (int) (timeline["goldPerMinDeltas"]["20-30"].GetDouble() * 10);
            }
        }
    }

    public class Game
    {
        public SummonerInfo Summoner { get; private set; }
        public string Id { get; private set; }
        public DateTime DateUtc { get; private set; }
        public TimeSpan Duration { get; private set; }
        public DateTime Date(string timeZoneId) { return TimeZoneInfo.ConvertTimeFromUtc(DateUtc, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)); }
        public DateTime DateDayOnly(string timeZoneId) { var d = Date(timeZoneId); return d.TimeOfDay.TotalHours < 5 ? d.Date.AddDays(-1) : d.Date; }
        public string DetailsUrl { get; private set; }
        public int MapId { get; private set; }
        public int QueueId { get; private set; }
        public string Map { get; private set; }
        public string Type { get; private set; }
        public bool? Victory { get { return Ally.Victory ? true : Enemy.Victory ? false : (bool?) null; } }
        public Team Ally { get; private set; }
        public Team Enemy { get; private set; }
        public Player Plr(string summonerName) { return Ally.Players.Single(p => p.Name == summonerName); }
        public Player Plr(HumanInfo human) { return Ally.Players.Single(p => human.SummonerNames.Contains(p.Name)); }
        public Player Plr(long id) { return Ally.Players.Single(p => p.SummonerId == id || p.AccountId == id); }
        public Player Plr(object playerId) { return playerId is string ? Plr(playerId as string) : playerId is HumanInfo ? Plr(playerId as HumanInfo) : Ut.Throw<Player>(new Exception()); }
        public string MicroType { get { return Regex.Matches((Map == "Summoner's Rift" ? "" : " " + Map) + " " + Type, @"\s\(?(.)").Cast<Match>().Select(m => m.Groups[1].Value).JoinString(); } }
        public IEnumerable<Player> AllPlayers() { return Enemy.Players.Concat(Ally.Players); }

        internal Game(JsonDict json, SummonerInfo summoner)
        {
            Summoner = summoner;
            Id = json["gameId"].GetLong().ToString();
            MapId = json["mapId"].GetInt();
            QueueId = json["queueId"].GetInt();
            setMapAndType();
            DetailsUrl = "http://matchhistory.{0}.leagueoflegends.com/en/#match-details/{1}/{2}/{3}".Fmt(summoner.Region.ToLower(), summoner.RegionServer, Id, summoner.AccountId);
            DateUtc = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) + TimeSpan.FromSeconds(json["gameCreation"].GetLong() / 1000.0);
            Duration = TimeSpan.FromSeconds(json["gameDuration"].GetInt());

            var teams =
                (from team in json["teams"].GetList()
                 let participants = json["participants"].GetList().Where(p => p["teamId"].GetInt() == team["teamId"].GetInt()).ToDictionary(p => p["participantId"].GetInt())
                 let identities = json["participantIdentities"].GetList().Where(pi => participants.ContainsKey(pi["participantId"].GetInt())).ToDictionary(pi => pi["participantId"].GetInt())
                 select new Team(team, participants, identities, this)
                ).ToList();
            Ut.Assert(teams.Count == 2);

            Ally = teams.Single(t => t.Players.Any(p => p.SummonerId == summoner.SummonerId));
            Enemy = teams.Single(t => t != Ally);
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
