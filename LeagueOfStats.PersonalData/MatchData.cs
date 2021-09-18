using System;
using System.Collections.Generic;
using System.Linq;
using LeagueOfStats.StaticData;
using RT.Json;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.PersonalData
{
    public enum Lane { None, Top, Jungle, Middle, Bottom }

    public enum Role { None, Solo, DuoSupport, Duo, DuoCarry }

    public class Team
    {
        public Game Game { get; private set; }
        public bool Victory { get; private set; }
        public IList<Player> Players { get; private set; }

        internal Team(JsonValue json, Dictionary<int, JsonValue> participants, Dictionary<int, JsonValue> identities, Game game)
        {
            Game = game;
            if ((Game.Queue.Map == MapId.ValoranCityPark || Game.Queue.Map == MapId.CrashSite) && !json.ContainsKey("win"))
                Victory = true;
            else
                Victory = json["win"].GetString() == "Win" ? true : json["win"].GetString() == "Fail" ? false : throw new Exception();
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
        public int[] ItemIds { get; private set; }
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
        public bool? Afk { get; private set; }
        public bool? Leaver { get; private set; }
        public int? VisionScore { get; private set; }

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
            SummonerId = identity["player"].Safe["summonerId"].GetLongSafe() ?? -1; // -1 when it's a bot
            Name = identity["player"]["summonerName"].GetString();

            TeamId = participant["teamId"].GetInt();
            ChampionId = participant["championId"].GetInt();
            Spell1Id = participant["spell1Id"].GetInt();
            Spell2Id = participant["spell2Id"].GetInt();
            var role = participant["timeline"]["role"].GetString();
            Role = role == "DUO" ? Role.Duo : role == "DUO_CARRY" ? Role.DuoCarry : role == "DUO_SUPPORT" ? Role.DuoSupport : role == "SOLO" ? Role.Solo : role == "NONE" ? Role.None : throw new Exception();
            var lane = participant["timeline"]["lane"].GetString();
            Lane = lane == "TOP" ? Lane.Top : lane == "JUNGLE" ? Lane.Jungle : lane == "MIDDLE" ? Lane.Middle : lane == "BOTTOM" ? Lane.Bottom : lane == "NONE" ? Lane.None : throw new Exception();
            var stats = participant["stats"].GetDict();
            ItemIds = new[] { stats["item0"].GetInt(), stats["item1"].GetInt(), stats["item2"].GetInt(), stats["item3"].GetInt(), stats["item4"].GetInt(), stats["item5"].GetInt(), stats["item6"].GetInt() }.Where(i => i != 0).ToArray();
            Kills = stats["kills"].GetInt();
            Deaths = stats["deaths"].GetInt();
            Assists = stats["assists"].GetInt();
            DamageToChampions = stats["totalDamageDealtToChampions"].GetInt();
            TotalHeal = stats["totalHeal"].GetInt();
            TotalDamageTaken = stats["totalDamageTaken"].GetInt();
            LargestMultiKill = stats["largestMultiKill"].GetInt();
            WardsPlaced = stats.ContainsKey("wardsPlaced") ? stats["wardsPlaced"].GetInt() : 0;
            VisionScore = stats.ContainsKey("visionScore") ? stats["visionScore"].GetInt() : (int?) null;
            Afk = stats.ContainsKey("wasAfk") ? stats["wasAfk"].GetBool() : (bool?) null;
            Leaver = stats.ContainsKey("leaver") ? stats["leaver"].GetBool() : (bool?) null;
            var timeline = participant["timeline"].GetDict();
            if (game.Duration > TimeSpan.FromMinutes(10) && game.Queue.Id != 610 /*Dark Star*/)
            {
                CreepsAt10 = (int) (timeline["creepsPerMinDeltas"]["0-10"].GetDouble() * 10);
                GoldAt10 = (int) (timeline["goldPerMinDeltas"]["0-10"].GetDouble() * 10);
            }
            if (game.Duration > TimeSpan.FromMinutes(20) && game.Queue.Id != 610 /*Dark Star*/)
            {
                if (timeline["creepsPerMinDeltas"].ContainsKey("10-20"))
                {
                    CreepsAt20 = CreepsAt10 + (int) (timeline["creepsPerMinDeltas"]["10-20"].GetDouble() * 10);
                    GoldAt20 = GoldAt10 + (int) (timeline["goldPerMinDeltas"]["10-20"].GetDouble() * 10);
                }
                else if (game.Duration < TimeSpan.FromMinutes(20.5))
                {
                    CreepsAt20 = CreepsAt10 + 0;
                    GoldAt20 = GoldAt10 + 0;
                }
                else
                    throw new Exception();
            }
            if (game.Duration > TimeSpan.FromMinutes(30) && game.Queue.Id != 610 /*Dark Star*/)
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
        public QueueInfo Queue { get; private set; }
        public bool? Victory { get { return Ally.Victory ? true : Enemy.Victory ? false : (bool?) null; } }
        public Team Ally { get; private set; }
        public Team Enemy { get; private set; }
        public Player Plr(long id) { return Ally.Players.Single(p => p.SummonerId == id || p.AccountId == id); }
        public IEnumerable<Player> AllPlayers() { return Enemy.Players.Concat(Ally.Players); }

        internal Game(JsonDict json, SummonerInfo summoner)
        {
            Summoner = summoner;
            Id = json["gameId"].GetLong().ToString();
            Queue = Queues.GetInfo(json["queueId"].GetInt());
            if (Queue.Id != 0 /* Custom */) // this is the only queue type for which the map can't be inferred from the queue, but we don't care about those games when computing personal stats
                Ut.Assert((int) Queue.Map == json["mapId"].GetInt());
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
    }
}
