using System;
using System.Net;
using System.Threading;
using LeagueOfStats.GlobalData.Util;
using RT.Util;
using RT.Util.Json;

namespace LeagueOfStats.GlobalData
{
    public enum MatchDownloadResult { OK, NonExistent, Failed, OverQuota }

    public class MatchDownloader
    {
        public string ApiKey;
        public Region Region;
        public int RetryCount = 10;
        public DateTime OverQuotaUntil;

        public Action<string, HResponse> OnEveryResponse = (url, resp) => { Console.WriteLine($"{url} --- {(int) resp.StatusCode}"); };
        public Action<HResponse> OnUnparseableJson = (resp) => { Console.WriteLine($"Download failed! JSON unparseable:\r\n" + resp.DataString); };
        public Action<HResponse> OnRetryLimitReached = (resp) => { Console.WriteLine($"Download failed! Retry limit reached, status: {(int) resp.StatusCode}, text: {resp.DataString}"); };

        public MatchDownloader(string apiKey, Region region)
        {
            ApiKey = apiKey;
            Region = region;
        }

        public (MatchDownloadResult result, JsonValue json) DownloadMatch(long matchId)
        {
            if (DateTime.UtcNow < OverQuotaUntil)
                return (MatchDownloadResult.OverQuota, null);

            var url = $@"https://{Region.ToApiHost()}/lol/match/v3/matches/{matchId}?api_key={ApiKey}";
            int attempts = 0;
            retry:;
            try
            {
                attempts++;
                var resp = new HClient().Get(url);
                OnEveryResponse(url, resp);

                if (resp.StatusCode == (HttpStatusCode) 403)
                {
                    attempts--;
                    Console.WriteLine($"API key expired / invalid? Sleeping for 60s. Key: {ApiKey}");
                    LosWinAPI.FlashConsoleTaskbarIcon(true);
                    Thread.Sleep(TimeSpan.FromSeconds(60));
                    goto retry;
                }

                if (resp.StatusCode == (HttpStatusCode) 429 && int.TryParse(resp.Headers["Retry-After"], out int retryAfter))
                {
                    if (retryAfter < 1)
                        retryAfter = 1;
                    Console.WriteLine($"{Region}: over quota for {retryAfter} seconds...");
                    OverQuotaUntil = DateTime.UtcNow.AddSeconds(retryAfter);
                    return (MatchDownloadResult.OverQuota, null);
                }

                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return (MatchDownloadResult.NonExistent, null);
                else if (resp.StatusCode == HttpStatusCode.OK)
                {
                    try { return (MatchDownloadResult.OK, resp.DataJson); }// make sure it can be parsed as JSON
                    catch
                    {
                        OnUnparseableJson(resp);
                        return (MatchDownloadResult.Failed, null);
                    }
                }
                if (attempts <= RetryCount)
                    goto retry;
                OnRetryLimitReached(resp);
                return (MatchDownloadResult.Failed, null);
            }
            catch
            {
                goto retry;
            }
        }

    }
}
