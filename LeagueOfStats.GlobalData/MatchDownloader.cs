using System;
using System.Net;
using System.Threading;
using RT.Util;
using RT.Util.Json;

namespace LeagueOfStats.GlobalData
{
    public enum MatchDownloadResult { OK, NonExistent, Failed, OverQuota }

    public class MatchDownloader
    {
        private ApiKeyWrapper _apiKey;
        public Region Region;
        public int RetryCount = 10;
        public DateTime OverQuotaUntil;

        public Action<string, HResponse> OnEveryResponse = (url, resp) => { Console.WriteLine($"{url} --- {(int) resp.StatusCode}"); };
        public Action<HResponse> OnUnparseableJson = (resp) => { Console.WriteLine($"Download failed! JSON unparseable:\r\n" + resp.DataString); };
        public Action<HResponse> OnRetryLimitReached = (resp) => { Console.WriteLine($"Download failed! Retry limit reached, status: {(int) resp.StatusCode}, text: {resp.DataString}"); };

        public MatchDownloader(ApiKeyWrapper apiKey, Region region)
        {
            _apiKey = apiKey;
            Region = region;
        }

        public (MatchDownloadResult result, JsonValue json) DownloadMatch(long matchId)
        {
            if (DateTime.UtcNow < OverQuotaUntil)
                return (MatchDownloadResult.OverQuota, null);

            int attempts = 0;
            retry:;
            var url = $@"https://{Region.ToApiHost()}/lol/match/v3/matches/{matchId}?api_key={_apiKey.GetApiKey()}";
            try
            {
                attempts++;
                var resp = new HClient().Get(url);
                OnEveryResponse(url, resp);

                if (resp.StatusCode == (HttpStatusCode) 403)
                {
                    attempts--;
                    _apiKey.ReportInvalid();
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

    public abstract class ApiKeyWrapper
    {
        public abstract string GetApiKey();
        public abstract void ReportInvalid();
    }

    public class StaticApiKey : ApiKeyWrapper
    {
        private string _apiKey;

        public StaticApiKey(string apiKey)
        {
            _apiKey = apiKey;
        }

        public override string GetApiKey() => _apiKey;

        public override void ReportInvalid()
        {
            Console.WriteLine($"API key expired / invalid? Key: {_apiKey}");
            LosWinAPI.FlashConsoleTaskbarIcon(true);
            Thread.Sleep(60);
        }
    }
}
