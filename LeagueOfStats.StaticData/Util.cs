using System;
using System.Diagnostics;
using System.Net.Http;
using RT.Json;

namespace LeagueOfStats.StaticData
{
    static class Extension
    {
        public static JsonDict GetDictAndRemove(this JsonDict json, string key)
        {
            var result = json[key].GetDict();
            json.Remove(key);
            return result;
        }

        public static JsonDict GetDictAndRemoveOrNull(this JsonDict json, string key)
        {
            if (!json.ContainsKey(key))
                return null;
            var result = json[key].GetDict();
            json.Remove(key);
            return result;
        }

        public static JsonList GetListAndRemove(this JsonDict json, string key)
        {
            var result = json[key].GetList();
            json.Remove(key);
            return result;
        }

        public static JsonList GetListAndRemoveOrNull(this JsonDict json, string key)
        {
            if (!json.ContainsKey(key))
                return null;
            var result = json[key].GetList();
            json.Remove(key);
            return result;
        }

        public static string GetStringAndRemove(this JsonDict json, string key)
        {
            var result = json[key].GetString();
            json.Remove(key);
            return result;
        }

        public static string GetStringAndRemoveOrNull(this JsonDict json, string key)
        {
            if (!json.ContainsKey(key))
                return null;
            var result = json[key].GetString();
            json.Remove(key);
            return result;
        }

        public static int GetIntAndRemove(this JsonDict json, string key)
        {
            var result = json[key].GetInt();
            json.Remove(key);
            return result;
        }

        public static int? GetIntAndRemoveOrNull(this JsonDict json, string key)
        {
            if (!json.ContainsKey(key))
                return null;
            var result = json[key].GetInt();
            json.Remove(key);
            return result;
        }

        public static bool GetBoolAndRemove(this JsonDict json, string key)
        {
            var result = json[key].GetBool();
            json.Remove(key);
            return result;
        }

        public static bool? GetBoolAndRemoveOrNull(this JsonDict json, string key)
        {
            if (!json.ContainsKey(key))
                return null;
            var result = json[key].GetBool();
            json.Remove(key);
            return result;
        }

        public static decimal? GetDecimalAndRemoveOrNull(this JsonDict json, string key)
        {
            if (!json.ContainsKey(key))
                return null;
            var result = json[key].GetDecimal();
            json.Remove(key);
            return result;
        }

        [DebuggerHidden]
        public static void EnsureEmpty(this JsonDict json)
        {
            if (json.Count > 0)
                throw new Exception();
        }

        public static string GetString(this HttpClient hc, string url)
        {
            return hc.GetStringAsync(url).GetAwaiter().GetResult();
        }
    }
}
