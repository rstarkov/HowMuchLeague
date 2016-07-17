using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;
using RT.Util.Serialization;

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
        public string Champion { get { return Program.Champions[ChampionId]; } }
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
        public IEnumerable<Player> OtherPlayers() { return AllPlayers().Where(p => !Program.AllKnownPlayers.Contains(p.Name)); }

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

    public class SummonerInfo
    {
        public string Region { get; private set; }
        public long AccountId { get; private set; }
        public long SummonerId { get; private set; }

        public string AuthorizationHeader { get; private set; }

        private HashSet<string> _GameIds = new HashSet<string>();

        public string RegionServer { get { return Region.SubstringSafe(0, 3) + "1"; } }

        /// <summary>Summoner Name as deduced from the most recent game on record. Null until games are loaded.</summary>
        [ClassifyIgnore]
        public string Name { get; private set; }

        /// <summary>All Summoner Names as deduced from all the game on record. Null until games are loaded.</summary>
        [ClassifyIgnore]
        public IList<string> PastNames { get; private set; }

        /// <summary>All games played by this summoner. This list is read-only.</summary>
        [ClassifyIgnore]
        public IList<Game> Games { get; private set; }

        [ClassifyIgnore]
        private string _filename;

        public override string ToString()
        {
            return $"{Region}/{AccountId} ({Name ?? "?"})";
        }

        private SummonerInfo() { } // for Classify

        /// <summary>
        ///     Constructs a new instance by loading an existing summoner data file.</summary>
        /// <param name="filename">
        ///     File to load the summoner data from. This filename is also used to save summoner data and to infer the
        ///     directory name for API response cache.</param>
        public SummonerInfo(string filename)
        {
            if (filename == null)
                throw new ArgumentNullException(nameof(filename));
            _filename = filename;
            if (!File.Exists(_filename))
                throw new ArgumentException($"Summoner data file not found: \"{filename}\".", nameof(filename));
            ClassifyXml.DeserializeFileIntoObject(filename, this);
            if (Region == null || AccountId == 0 || SummonerId == 0)
                throw new InvalidOperationException($"Summoner data file does not contain the minimum required data.");
            Region = Region.ToUpper();
        }

        /// <summary>
        ///     Constructs a new instance from scratch as well as the accompanying data file.</summary>
        /// <param name="filename">
        ///     File to save the summoner data to. An exception is thrown if the file already exists. This filename is also
        ///     used to save summoner data and to infer the directory name for API response cache.</param>
        public SummonerInfo(string filename, string region, long accountId, long summonerId)
            : this(filename)
        {
            if (filename == null)
                throw new ArgumentNullException(nameof(filename));
            if (region == null)
                throw new ArgumentNullException(nameof(region));
            _filename = filename;
            Region = region.ToUpper();
            AccountId = accountId;
            SummonerId = summonerId;
            if (File.Exists(_filename))
                throw new ArgumentException($"Summoner data file already exists: \"{filename}\".", nameof(filename));
            save();
        }

        /// <summary>
        ///     Loads game data cached by an earlier call to <see cref="LoadGamesOnline"/> without accessing Riot servers.</summary>
        public void LoadGamesOffline()
        {
            var games = new List<Game>();
            foreach (var gameId in _GameIds)
            {
                var json = loadGameJson(gameId, null, null, null);
                if (json != null)
                    games.Add(new Game(json, this));
            }
            postLoad(games);
        }

        /// <summary>
        ///     Loads game data. Queries Riot servers to retrieve any new games, caches them, and loads all the previously
        ///     cached games.</summary>
        /// <param name="getAuthHeader">
        ///     Invoked if Riot responds with a "not authorized" response. Should return an updated Authorization header for
        ///     this summoner.</param>
        /// <param name="logger">
        ///     An optional function invoked to log progress.</param>
#warning TODO: async
        public void LoadGamesOnline(Func<SummonerInfo, string> getAuthHeader, Action<string> logger)
        {
            var hClient = new HClient();
            hClient.ReqAccept = "application/json, text/javascript, */*; q=0.01";
            hClient.ReqAcceptLanguage = "en-GB,en;q=0.5";
            hClient.ReqUserAgent = "Mozilla/5.0 (Windows NT 6.1; WOW64; rv:40.0) Gecko/20100101 Firefox/40.0";
            hClient.ReqReferer = $"http://matchhistory.{Region.ToLower()}.leagueoflegends.com/en/";
            hClient.ReqHeaders[HttpRequestHeader.Host] = "acs.leagueoflegends.com";
            hClient.ReqHeaders["DNT"] = "1";
            hClient.ReqHeaders["Region"] = Region.ToUpper();
            hClient.ReqHeaders["Authorization"] = AuthorizationHeader;
            hClient.ReqHeaders["Origin"] = $"http://matchhistory.{Region.ToLower()}.leagueoflegends.com";

            discoverGameIds(false, hClient, getAuthHeader, logger);

            var games = new List<Game>();
            foreach (var gameId in _GameIds)
            {
                var json = loadGameJson(gameId, hClient, getAuthHeader, logger);
                if (json != null)
                    games.Add(new Game(json, this));
            }
            postLoad(games);
        }

        private void postLoad(List<Game> games)
        {
            games.Sort(CustomComparer<Game>.By(g => g.DateUtc));
            Games = games.AsReadOnly();
            Name = Games.Last().Plr(SummonerId).Name;
            PastNames = Games.Select(g => g.Plr(SummonerId).Name).ToList().AsReadOnly();
        }

        private void save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filename));
            ClassifyXml.SerializeToFile(this, _filename);
        }

        private HResponse retryOnAuthHeaderFail(string url, HClient hClient, Func<SummonerInfo, string> getAuthHeader)
        {
            while (true)
            {
                var resp = hClient.Get(url);
                if (resp.StatusCode != HttpStatusCode.Unauthorized)
                    return resp;

                var newHeader = getAuthHeader(this);
                if (newHeader == null)
                    return null;
                AuthorizationHeader = newHeader;
                hClient.ReqHeaders["Authorization"] = newHeader;
                save();
            }
        }

        private JsonDict loadGameJson(string gameId, HClient hClient, Func<SummonerInfo, string> getAuthHeader, Action<string> logger)
        {
            // If visibleAccountId isn't equal to Account ID of any of the players in the match, participantIdentities will not contain any identities at all.
            // If it is but the AuthorizationHeader isn't valid for that Account ID, only that player's info will be populated in participantIdentities.
            // Full participantIdentities are returned only if the visibleAccountId was a participant in the match and is logged in via AuthorizationHeader.
            var fullHistoryUrl = $"https://acs.leagueoflegends.com/v1/stats/game/{RegionServer}/{gameId}?visiblePlatformId={RegionServer}&visibleAccountId={AccountId}";
            var path = Path.Combine(Path.GetDirectoryName(_filename), Path.GetFileNameWithoutExtension(_filename), fullHistoryUrl.FilenameCharactersEscape());
            string rawJson = null;
            if (File.Exists(path))
            {
                logger?.Invoke("Loading cached " + fullHistoryUrl + " ...");
                rawJson = File.ReadAllText(path);
            }
            else
            {
                logger?.Invoke("Retrieving " + fullHistoryUrl + " ...");
                var resp = retryOnAuthHeaderFail(fullHistoryUrl, hClient, getAuthHeader);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    File.WriteAllText(path, "404");
                else
                {
                    var data = resp.Expect(HttpStatusCode.OK).DataString;
                    var tryJson = JsonDict.Parse(data);
                    assertHasParticipantIdentities(tryJson);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, data);
                }
            }
            var json = rawJson == "404" ? null : JsonDict.Parse(rawJson);
            if (json != null)
                assertHasParticipantIdentities(json);
            return json;
        }

        private void assertHasParticipantIdentities(JsonDict json)
        {
            if (!json["participantIdentities"].GetList().All(id => id.ContainsKey("participantId") && id.ContainsKey("player") && id["player"].ContainsKey("summonerName")
                && (id["player"].ContainsKey("summonerId") || id["player"].Safe["accountId"].GetIntSafe() == 0)))
                throw new Exception("Match history JSON does not contain all participant identities.");
        }

        private void discoverGameIds(bool full, HClient hClient, Func<SummonerInfo, string> getAuthHeader, Action<string> logger)
        {
            int step = 15;
            int count = step;
            int index = 0;
            while (true)
            {
                logger?.Invoke("{0}/{1}: retrieving games at {2} of {3}".Fmt(Name, Region, index, count));
                var url = $"https://acs.leagueoflegends.com/v1/stats/player_history/auth?begIndex={index}&endIndex={index + step}&queue=0&queue=2&queue=4&queue=6&queue=7&queue=8&queue=9&queue=14&queue=16&queue=17&queue=25&queue=31&queue=32&queue=33&queue=41&queue=42&queue=52&queue=61&queue=65&queue=70&queue=73&queue=76&queue=78&queue=83&queue=91&queue=92&queue=93&queue=96&queue=98&queue=100&queue=300&queue=313&queue=400&queue=410";
                var json = retryOnAuthHeaderFail(url, hClient, getAuthHeader).Expect(HttpStatusCode.OK).DataJson;

                Ut.Assert(json["accountId"].GetLongLenient() == AccountId);
                Ut.Assert(json["platformId"].GetString().EqualsNoCase(Region) || json["platformId"].GetString().EqualsNoCase(RegionServer));

                index += step;
                count = json["games"]["gameCount"].GetInt();

                bool anyNew = false;
                foreach (var gameId in json["games"]["games"].GetList().Select(js => js["gameId"].GetLong().ToString()))
                    anyNew |= _GameIds.Add(gameId);

                if (index >= count)
                    break;
                if (!anyNew && !full)
                    break;
            }
            logger?.Invoke($"{Name}/{Region}: done.");
            save();
        }
    }
}
