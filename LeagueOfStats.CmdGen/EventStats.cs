using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeagueOfStats.GlobalData;
using LeagueOfStats.StaticData;
using RT.TagSoup;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;
using RT.Json;

namespace LeagueOfStats.CmdGen
{
    class EventStatsSettings
    {
#pragma warning disable 649 // field is never assigned to
        public string OutputPath;
        public HashSet<int> GenerateOnDayOfMonth = new HashSet<int> { 1 };
#pragma warning restore 649
    }

    class EventStats : StatsBase
    {
        class winrateStat
        {
            public int Total, Wins;
            public double Winrate => Wins / (double) Total;
        }

        private EventStatsSettings _settings;

        public EventStats(EventStatsSettings settings)
        {
            _settings = settings;
        }

        public void Generate()
        {
            var verqueues = DataStore.LosMatchJsons
                .SelectMany(kvp1 => kvp1.Value.SelectMany(kvp2 => kvp2.Value.Select(kvp3 => (version: kvp2.Key, queueId: kvp3.Key))))
                .Distinct()
                .ToLookup(x => x.queueId, x => x.version);
            var region = DataStore.LosMatchJsons.Keys.Contains(Region.EUW) ? Region.EUW : DataStore.LosMatchJsons.Keys.First();
            var results = new AutoDictionary<int, List<eventResult>>(_ => new List<eventResult>());
            foreach (var queue in Queues.AllQueues.Where(q => q.IsPvp && q.IsEvent))
            {
                if (queue.Id == 700 /* Clash */)
                    continue;
                foreach (var versionGroup in verqueues[queue.Id].Select(v => Version.Parse(v)).Order().GroupConsecutive((v1, v2) => v1.Major == v2.Major && v1.Minor == v2.Minor - 1))
                {
                    var versions = Enumerable.Range(0, versionGroup.Count).Reverse().Select(offset => new Version(versionGroup.Key.Major, versionGroup.Key.Minor - offset)).Select(v => v.ToString()).ToList();
                    var firstDate = DataStore.LosMatchJsons[region][versions.First()][queue.Id].ReadItems().Select(json => new BasicMatchInfo(json)).Take(50).Min(b => b.GameCreationDate);
                    var curQueue = queue;
                    while (curQueue.ReplacedBy != null)
                        curQueue = Queues.GetInfo(curQueue.ReplacedBy.Value);
                    // Riot messed up URF queues by merging URF and ARURF into 900
                    if (queue.Id == 76 || queue.Id == 900 && $"{firstDate:yyMM}" == "1910")
                        curQueue = Queues.GetInfo(76);
                    Console.WriteLine($"{queue.Id} - {queue.QueueName} - {firstDate:MMMM yyyy} - {versions.JoinString(", ")}");
                    var result = GenerateEvent(queue.Id, versions, $"{curQueue.QueueName} - {firstDate:MMMM yyyy}", $"{curQueue.MicroName}--{firstDate:yyyy'-'MM}--{queue.Id}");
                    result.FirstDate = firstDate;
                    results[curQueue.Id].Add(result);
                }
            }

            var html = new List<object>();
            html.Add(new H1("League PvP Event Stats"));
            html.Add(new P($"Last updated: {DateTime.UtcNow:d' 'MMM' 'yyyy' at 'HH:mm' UTC'}"));
            foreach (var kvp in results.OrderByDescending(k => k.Value.Max(r => r.MatchCount)))
            {
                html.Add(new H3(Queues.GetInfo(kvp.Key).QueueName));
                html.Add(makeSortableTable(
                    new TR(colAsc("Date", true), colAsc("Version(s)"), colAsc("Winrates"), colAsc("Bans"), colDesc("Games"), colAsc("Best champ"), colDesc("Winrate"), colAsc("Worst champ"), colAsc("Winrate")),
                    kvp.Value.Select(result => new TR(
                        cell($"{result.FirstDate:MMMM yyyy}", $"{result.FirstDate:yyyy-MM}", false),
                        cell(result.Versions.JoinString(", "), $"{result.FirstDate:yyyy-MM}", true),
                        cell(new A("Winrates") { href = result.LinkWinrates }, "", true),
                        cell(new A("Bans") { href = result.LinkBans }, "", true),
                        cellInt(result.MatchCount),
                        cellStr(result.BestChamp < 0 ? "" : LeagueStaticData.Champions[result.BestChamp].Name),
                        cellPrc(result.BestChampWinrate, 1),
                        cellStr(result.WorstChamp < 0 ? "" : LeagueStaticData.Champions[result.WorstChamp].Name),
                        cellPrc(result.WorstChampWinrate, 1)
                    ))));
            }
            GenerateHtmlToFile(Path.Combine(_settings.OutputPath, $"index.html"), html, true);
        }

        class eventResult
        {
            public DateTime FirstDate;
            public string LinkWinrates;
            public string LinkBans;
            public int BestChamp = -1;
            public double BestChampWinrate;
            public int WorstChamp = -1;
            public double WorstChampWinrate;
            public int MatchCount;
            public List<string> Versions;
        }

        private eventResult GenerateEvent(int queueId, List<string> versions, string title, string filename)
        {
            Console.WriteLine($"Generating stats at {DateTime.Now}...");
            var result = new eventResult();
            result.Versions = versions;

            var matches = DataStore.ReadMatchesByRegVerQue(f => f.queueId == queueId && versions.Contains(f.version))
                .Select(m => matchFromJson(m.json, m.region))
                .Where(m => m != null);

            int matchCount = 0;
            var champWins = new AutoDictionary<int, int>();
            var champSeens = new AutoDictionary<int, int>();
            var bans = new AutoDictionary<int, winrateStat>(ch => new winrateStat());
            foreach (var match in matches)
            {
                matchCount++;
                // Champ winrates
                foreach (var champ in match.WinChamps)
                    if (!match.LoseChamps.Contains(champ))
                    {
                        champSeens.IncSafe(champ);
                        champWins.IncSafe(champ);
                    }
                foreach (var champ in match.LoseChamps)
                    if (!match.WinChamps.Contains(champ))
                        champSeens.IncSafe(champ);

                // Bans: winrate for games where champ wasn't banned, and our team didn't pick it
                foreach (var champ in LeagueStaticData.Champions.Keys)
                    if (!match.BannedChamps.Contains(champ))
                    {
                        if (!match.WinChamps.Contains(champ))
                        {
                            bans[champ].Total++;
                            bans[champ].Wins++;
                        }
                        if (!match.LoseChamps.Contains(champ))
                        {
                            bans[champ].Total++;
                        }
                    }
            }
            var html = new List<object>();
            html.Add(new H1(title));
            html.Add(new H2("Champion Winrates"));
            html.Add(new P($"Last updated: {DateTime.UtcNow:d' 'MMM' 'yyyy' at 'HH:mm' UTC'}"));
            html.Add(new P($"Based on {matchCount:#,0} total matches. Game version(s) {versions.JoinString(", ")} in queue {queueId}."));
            html.Add(new P(new RawTag("&nbsp;")));
            html.Add(new P("Note: win rate is an unreliable measure when the number of matches is low. To correctly account for this, sort by the p95 columns."));
            html.Add(new P("One column is optimal for finding the best champions, the other is optimal for finding the worst ones."));
            html.Add(new P(new RawTag("&nbsp;")));
            var bestworst = new List<(int champ, double p95best, double p95worst, double winrate)>();
            html.Add(makeSortableTable(
                new TR(colAsc("Champion"), colDesc("Matches"), colDesc("Winrate"), colDesc("Best (p95 lower)", true), colAsc("Worst (p95 upper)")),
                LeagueStaticData.Champions.Values.Where(c => champSeens[c.Id] > 0).Select(c =>
                {
                    var winrate = champWins[c.Id] / (double) champSeens[c.Id];
                    var p95 = Utils.WilsonConfidenceInterval(winrate, champSeens[c.Id], 1.96);
                    bestworst.Add((c.Id, p95.lower, p95.upper, winrate));
                    return new TR(cellStr(c.Name), cellInt(champSeens[c.Id]), cellPrc(winrate, 1), cellPrc(p95.lower, 1), cellPrc(p95.upper, 1));
                })));
            result.LinkWinrates = $"{filename}-winrates.html";
            GenerateHtmlToFile(Path.Combine(_settings.OutputPath, result.LinkWinrates), html, true);
            if (bestworst.Count > 0)
            {
                var bestchamp = bestworst.MaxElement(x => x.p95best);
                var worstchamp = bestworst.MinElement(x => x.p95worst);
                result.BestChamp = bestchamp.champ;
                result.BestChampWinrate = bestchamp.winrate;
                result.WorstChamp = worstchamp.champ;
                result.WorstChampWinrate = worstchamp.winrate;
            }

            html = new List<object>();
            html.Add(new H1(title));
            html.Add(new H2("Best Bans"));
            html.Add(new P($"Last updated: {DateTime.UtcNow:d' 'MMM' 'yyyy' at 'HH:mm' UTC'}"));
            html.Add(new P($"Based on {matchCount:#,0} total matches. Game version(s) {versions.JoinString(", ")} in queue {queueId}."));
            html.Add(new P(new RawTag("&nbsp;")));
            html.Add(makeSortableTable(
                new TR(colAsc("Champion"), colDesc("Matchups evaluated"), colDesc("Δwinrate if banned"), colDesc("Best bans (p95 Δwr lower)", true), colAsc("Worst bans (p95 Δwr upper)")),
                LeagueStaticData.Champions.Values.Select(c =>
                {
                    var p95 = Utils.WilsonConfidenceInterval(bans[c.Id].Winrate, bans[c.Id].Total, 1.96);
                    return new TR(cellStr(c.Name), cellInt(bans[c.Id].Total), cellPrcDelta(0.5 - bans[c.Id].Winrate, 2), cellPrcDelta(0.5 - p95.upper, 2), cellPrcDelta(0.5 - p95.lower, 2));
                })));
            result.LinkBans = $"{filename}-bans.html";
            GenerateHtmlToFile(Path.Combine(_settings.OutputPath, result.LinkBans), html, true);

            result.MatchCount = matchCount;
            return result;
        }

        private class match
        {
            public List<int> WinChamps;
            public List<int> LoseChamps;
            public List<int> BannedChamps;
        }

        private static match matchFromJson(JsonValue json, Region region)
        {
            if (json["gameDuration"].GetInt() < 300)
                return null;
            var result = new match();
            var teams = json["teams"].GetList();
            var winTeamId = teams.Single(t => t["win"].GetString() == "Win")["teamId"].GetInt();
            var loseTeamId = teams.Single(t => t["win"].GetString() == "Fail")["teamId"].GetInt();
            result.WinChamps = json["participants"].GetList().Where(t => t["teamId"].GetInt() == winTeamId).Select(p => p["championId"].GetInt()).ToList();
            result.LoseChamps = json["participants"].GetList().Where(t => t["teamId"].GetInt() == loseTeamId).Select(p => p["championId"].GetInt()).ToList();
            if (result.WinChamps.Count != result.LoseChamps.Count)
                throw new Exception();
            result.BannedChamps = json["teams"].GetList().SelectMany(t => t["bans"].GetList()).Select(b => b["championId"].GetInt()).ToList();
            return result;
        }
    }
}
