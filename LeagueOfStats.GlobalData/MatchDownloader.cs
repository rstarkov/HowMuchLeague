using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RT.Util;
using RT.Json;
using System.Net.Http;
using System.Linq;

namespace LeagueOfStats.GlobalData
{
    public enum MatchDownloadResult { OK, NonExistent, Failed, BackOff }

    public class MatchDownloader
    {
        private ApiKeyWrapper _apiKey;
        public Region Region;
        public int RetryCount = 10;
        public DateTime BackOffUntil;

        public Action<string, HttpResponseMessage> OnEveryResponse = (url, resp) => { Console.WriteLine($"{url} --- {(int) resp.StatusCode}"); };
        public Action<HttpResponseMessage, string> OnUnparseableJson = (resp, text) => { Console.WriteLine($"Download failed! JSON unparseable:\r\n" + text); };
        public Action<HttpResponseMessage, string> OnRetryLimitReached = (resp, text) => { Console.WriteLine($"Download failed! Retry limit reached, status: {(int) resp.StatusCode}, text: {text}"); };

        public MatchDownloader(ApiKeyWrapper apiKey, Region region)
        {
            _apiKey = apiKey;
            Region = region;
        }

        public (MatchDownloadResult result, JsonValue json) DownloadMatch(long matchId)
        {
            if (DateTime.UtcNow < BackOffUntil)
                return (MatchDownloadResult.BackOff, null);

            int attempts = 0;
            retry:;
            if (attempts > 0)
                Thread.Sleep(1000);
            var keyUsed = _apiKey.GetApiKey();
            var url = $@"https://{Region.ToApiHost()}/lol/match/v4/matches/{matchId}?api_key={keyUsed}";
            try
            {
                attempts++;
                var resp = new HttpClient().GetAsync(url).GetAwaiter().GetResult();
                OnEveryResponse(url, resp);

                if (resp.StatusCode == (HttpStatusCode) 403)
                {
                    if (attempts > 3)
                    {
                        BackOffUntil = DateTime.UtcNow.AddMinutes(1);
                        _apiKey.ReportInvalid(keyUsed);
                        Console.WriteLine($"{Region}: API key appears to have expired.");
                        return (MatchDownloadResult.BackOff, null);
                    }
                    goto retry;
                }

                if (resp.StatusCode == (HttpStatusCode) 429 && int.TryParse(resp.Headers.GetValues("Retry-After").FirstOrDefault() ?? "1", out int retryAfter))
                {
                    if (retryAfter < 1)
                        retryAfter = 1;
                    Console.WriteLine($"{Region}: over quota for {retryAfter} seconds...");
                    BackOffUntil = DateTime.UtcNow.AddSeconds(retryAfter);
                    return (MatchDownloadResult.BackOff, null);
                }

                var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return (MatchDownloadResult.NonExistent, null);
                else if (resp.StatusCode == HttpStatusCode.OK)
                {
                    _apiKey.ReportValid(keyUsed);
                    JsonValue json;
                    try { json = JsonValue.Parse(text); }// make sure it can be parsed as JSON
                    catch
                    {
                        OnUnparseableJson(resp, text);
                        return (MatchDownloadResult.Failed, null);
                    }
                    json["LOS-Downloaded-By"] = _apiKey.GetDownloadedById();
                    return (MatchDownloadResult.OK, json);
                }
                if (attempts <= RetryCount)
                    goto retry;
                OnRetryLimitReached(resp, text);
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
        public abstract void ReportValid(string keyUsed);
        public abstract void ReportInvalid(string keyUsed);

        private long? _downloadedById = null;

        public long GetDownloadedById()
        {
            if (_downloadedById == null)
                throw new InvalidOperationException($"Must call {nameof(InitDownloadedById)} first");
            return _downloadedById.Value;
        }

        public void InitDownloadedById(Region region, string summonerName, string hmackey)
        {
            var key = GetApiKey();
            var url = $@"https://{region.ToApiHost()}/lol/summoner/v4/summoners/by-name/{summonerName}?api_key={key}";
            int hardErrors = 0;
            while (true)
            {
                var resp = new HttpClient().GetAsync(url).GetAwaiter().GetResult();
                if ((int) resp.StatusCode == 429)
                {
                    var sleep = 5;
                    if (int.TryParse(resp.Headers.GetValues("Retry-After").FirstOrDefault() ?? "1", out int retryAfter))
                        sleep = retryAfter;
                    Console.WriteLine($"429, sleeping for {sleep} seconds...");
                    Thread.Sleep(sleep * 1000);
                    continue;
                }
                if (resp.StatusCode != HttpStatusCode.OK)
                {
                    hardErrors++;
                    if (hardErrors >= 5)
                        throw new Exception("Unable to download; giving up");
                    Thread.Sleep(5000);
                    continue;
                }
                var id = JsonValue.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())["id"].GetString();
                var hash = new HMACSHA256(Encoding.UTF8.GetBytes(hmackey)).ComputeHash(Encoding.UTF8.GetBytes(id));
                _downloadedById = BitConverter.ToInt64(hash, 0);
                return;
            }
        }
    }

    public class StaticApiKey : ApiKeyWrapper
    {
        private string _apiKey;

        public StaticApiKey(string apiKey)
        {
            _apiKey = apiKey;
        }

        public override string GetApiKey() => _apiKey;

        public override void ReportValid(string keyUsed)
        {
        }

        public override void ReportInvalid(string keyUsed)
        {
        }
    }
}
