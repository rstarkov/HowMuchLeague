using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Json;
using RT.Serialization;
using System.Net.Http;
using System.Net.Http.Headers;

namespace LeagueOfStats.PersonalData
{
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
        public IReadOnlyList<string> PastNames { get; private set; }

        public string Username { get; private set; }

        /// <summary>All games played by this summoner. This list is read-only.</summary>
        [ClassifyIgnore]
        public IReadOnlyList<Game> Games { get; private set; }

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
            var hClient = new HttpClient();
            hClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            hClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB,en;q=0.5");
            hClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 6.1; WOW64; rv:40.0) Gecko/20100101 Firefox/40.0");
            hClient.DefaultRequestHeaders.Referrer = new Uri($"http://matchhistory.{Region.ToLower()}.leagueoflegends.com/en/");
            hClient.DefaultRequestHeaders.Host = "acs.leagueoflegends.com";
            //hClient.DefaultRequestHeaders["DNT"] = "1";
            hClient.DefaultRequestHeaders.Add("Region", Region.ToUpper());
            hClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(AuthorizationHeader);
            hClient.DefaultRequestHeaders.Add("Origin", $"http://matchhistory.{Region.ToLower()}.leagueoflegends.com");

            discoverGameIds(false, hClient, getAuthHeader, logger);

            var games = new List<Game>();
            foreach (var gameId in _GameIds)
            {
                var json = loadGameJson(gameId, hClient, getAuthHeader, logger);
                if (json != null)
                {
                    if (json["gameType"].GetString() == "CUSTOM_GAME")
                        continue;
                    var queueId = json["queueId"].GetStringLenientSafe();
                    if (queueId == "2000" || queueId == "2010" || queueId == "2020")
                        continue; // tutorial game
                    games.Add(new Game(json, this));
                }
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

        private HttpResponseMessage retryOnAuthHeaderFail(string url, HttpClient hClient, Func<SummonerInfo, string> getAuthHeader)
        {
            while (true)
            {
                var resp = hClient.GetAsync(url).GetAwaiter().GetResult();
                if (resp.StatusCode != HttpStatusCode.Unauthorized)
                    return resp;

                var newHeader = getAuthHeader(this);
                if (newHeader == null)
                    return null;
                AuthorizationHeader = newHeader;
                hClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(newHeader);
                save();
            }
        }

        private JsonDict loadGameJson(string gameId, HttpClient hClient, Func<SummonerInfo, string> getAuthHeader, Action<string> logger)
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
                    rawJson = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var tryJson = JsonDict.Parse(rawJson);
                    assertHasParticipantIdentities(tryJson);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, rawJson);
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

        private void discoverGameIds(bool full, HttpClient hClient, Func<SummonerInfo, string> getAuthHeader, Action<string> logger)
        {
            int step = 15;
            int count = step;
            int index = 0;
            while (true)
            {
                logger?.Invoke("{0}/{1}: retrieving games at {2} of {3}".Fmt(Name, Region, index, count));
                var url = $"https://acs.leagueoflegends.com/v1/stats/player_history/auth?begIndex={index}&endIndex={index + step}&";
                var json = JsonValue.Parse(retryOnAuthHeaderFail(url, hClient, getAuthHeader).Content.ReadAsStringAsync().GetAwaiter().GetResult());

                Ut.Assert(json["accountId"].GetLongLenient() == AccountId);
                // json["platformId"] may be different to Region for accounts that have been transferred to another region

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
