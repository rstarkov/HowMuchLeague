using System;
using System.Threading;
using LeagueOfStats.GlobalData;
using RT.Util;
using RT.Util.Json;

namespace LeagueOfStats.OneForAllStats
{
    class Downloader
    {
        public string ApiKey;
        public Region Region;
        public int QueueId;
        public long MinMatchId, MaxMatchId;

        public int MatchCount { get; private set; } = 0;
        public long EarliestMatchDate { get; private set; } = long.MaxValue;
        public long LatestMatchDate { get; private set; } = 0;
        public long EarliestMatchId { get; private set; } = long.MaxValue;
        public long LatestMatchId { get; private set; } = 0;

        private MatchDownloader _downloader;

        public Downloader(string apiKey, Region region, int queueId, long minMatchId, long maxMatchId)
        {
            ApiKey = apiKey;
            Region = region;
            QueueId = queueId;
            MinMatchId = minMatchId;
            MaxMatchId = maxMatchId;

            _downloader = new MatchDownloader(ApiKey, Region);
            _downloader.OnEveryResponse = (_, __) => { };

            foreach (var json in DataStore.LosMatchJsons[Region][QueueId].ReadItems())
                countMatch(json);
            printStats();
        }

        private void countMatch(JsonValue json)
        {
            MatchCount++;
            EarliestMatchDate = Math.Min(EarliestMatchDate, json.Safe["gameCreation"].GetLongLenientSafe() ?? long.MaxValue);
            LatestMatchDate = Math.Max(LatestMatchDate, json.Safe["gameCreation"].GetLongLenientSafe() ?? long.MaxValue);
            EarliestMatchId = Math.Min(EarliestMatchId, json["gameId"].GetLong());
            LatestMatchId = Math.Max(LatestMatchId, json["gameId"].GetLong());
        }

        private void printStats()
        {
            if (EarliestMatchDate < long.MaxValue && LatestMatchDate > 0)
                Console.WriteLine($"{Region}: OFA = {MatchCount:#,0}, ID = {EarliestMatchId:#,0} - {LatestMatchId:#,0} ({EarliestMatchId - MinMatchId:#,0} - {MaxMatchId - LatestMatchId:#,0}), date = {new DateTime(1970, 1, 1).AddSeconds(EarliestMatchDate / 1000)} - {new DateTime(1970, 1, 1).AddSeconds(LatestMatchDate / 1000)}");
        }

        private void whileWaitingRateLimit()
        {
            //DataStore.LosMatchJsons[Region][QueueId].Initialise(compact: true);
            //foreach (var store in DataStore.LosMatchJsons[Region].Values)
            //    store.Initialise(compact: true);
        }

        public void DownloadForever(bool background = false)
        {
            new Thread(() =>
            {
                while (true)
                    DownloadMatch();
            })
            { IsBackground = background }.Start();
        }

        public void DownloadMatch()
        {
            ulong range = (ulong) (MaxMatchId - MinMatchId + 1);
            ulong maxRnd = range * (ulong.MaxValue / range);

            // Generate a random match ID
            again:;
            var random = BitConverter.ToUInt64(Rnd.NextBytes(8), 0);
            if (random > maxRnd)
                goto again;
            random = (ulong) MinMatchId + random % range;
            if (DataStore.ExistingMatchIds[Region].Contains((long) random) || DataStore.NonexistentMatchIds[Region].Contains((long) random) || DataStore.FailedMatchIds[Region].Contains((long) random))
                goto again;
            var matchId = (long) random;

            // Download it and add the outcome to the data store
            var dl = _downloader.DownloadMatch(matchId, whileWaitingRateLimit);
            if (dl.result == MatchDownloadResult.NonExistent)
                DataStore.AddNonExistentMatch(Region, matchId);
            else if (dl.result == MatchDownloadResult.Failed)
                DataStore.AddFailedMatch(Region, matchId);
            else if (dl.result == MatchDownloadResult.OK)
            {
                var queueId = dl.json.Safe["queueId"].GetIntLenient();
                if (queueId == QueueId)
                {
                    countMatch(dl.json);
                    printStats();
                }
                DataStore.AddMatch(Region, queueId, matchId, dl.json);
            }
            else
                throw new Exception();
        }
    }
}
