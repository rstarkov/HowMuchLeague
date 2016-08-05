using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;
using RT.Util.Serialization;

namespace LeagueOfStats.PersonalData
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
