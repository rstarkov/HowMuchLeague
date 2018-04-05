﻿using System;
using System.Linq;
using System.Threading;
using LeagueOfStats.GlobalData;
using RT.KitchenSink;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;

namespace LeagueOfStats.OneForAllStats
{
    class Downloader
    {
        public string ApiKey;
        public Region Region;
        public int QueueId;
        public long InitialMatchId, MatchIdRange;

        public int MatchCount { get; private set; } = 0;
        public long EarliestMatchDate { get; private set; } = long.MaxValue;
        public long LatestMatchDate { get; private set; } = 0;
        public long EarliestMatchId { get; private set; } = long.MaxValue;
        public long LatestMatchId { get; private set; } = 0;

        private MatchDownloader _downloader;

        public Downloader(string apiKey, Region region, int queueId, long initialMatchId, long matchIdRange)
        {
            ApiKey = apiKey;
            Region = region;
            QueueId = queueId;
            InitialMatchId = initialMatchId;
            MatchIdRange = matchIdRange;

            _downloader = new MatchDownloader(ApiKey, Region);
            _downloader.OnEveryResponse = (_, __) => { };

            foreach (var json in DataStore.LosMatchJsons[Region][QueueId].ReadItems())
                countMatch(json);
            rebuild();
            printStats();
        }

        private void rebuild()
        {
            Console.Write("Rebuilding... ");
            var ids = DataStore.ExistingMatchIds[Region].Concat(DataStore.NonexistentMatchIds[Region]);

            // Add an extra gap entry if the search range limit exceeds existing IDs
            var searchMin = Math.Min(InitialMatchId, EarliestMatchId) - MatchIdRange;
            if (searchMin < ids.MinOrDefault(long.MaxValue))
                ids = ids.Concat(searchMin);
            var searchMax = Math.Max(InitialMatchId, LatestMatchId) + MatchIdRange;
            if (searchMax > ids.MaxOrDefault(0))
                ids = ids.Concat(searchMax);

            var sorted = ids.Order().ToList();
            _heap = sorted.SelectConsecutivePairs(false, (id1, id2) => new Gap { From = id1 + 1, To = id2 - 1 }).Where(g => g.Length > 0).ToArray();
            _heapLength = _heap.Length;
            heapifyFull();
            Console.WriteLine("done");
        }

        private void countMatch(JsonValue json)
        {
            MatchCount++;
            var gameCreation = json.Safe["gameCreation"].GetLong();
            EarliestMatchDate = Math.Min(EarliestMatchDate, gameCreation);
            LatestMatchDate = Math.Max(LatestMatchDate, gameCreation);
            var matchId = json["gameId"].GetLong();
            EarliestMatchId = Math.Min(EarliestMatchId, matchId);
            LatestMatchId = Math.Max(LatestMatchId, matchId);
        }

        private void printStats()
        {
            if (EarliestMatchDate < long.MaxValue && LatestMatchDate > 0)
            {
                var covered = DataStore.ExistingMatchIds[Region].Count + DataStore.NonexistentMatchIds[Region].Count;
                var searchMin = _heap.Take(_heapLength).Min(g => g.From);
                var searchMax = _heap.Take(_heapLength).Max(g => g.To);
                var gapstat = new ValueStat();
                foreach (var gap in _heap.Take(_heapLength))
                    gapstat.AddObservation(gap.Length);
                Console.ForegroundColor = Program.Colors[Region];
                Console.WriteLine($"{Region}: OFA = {MatchCount:#,0}, ID = {EarliestMatchId:#,0} - {LatestMatchId:#,0} ({EarliestMatchId - searchMin:#,0} - {searchMax - LatestMatchId:#,0}), date = {new DateTime(1970, 1, 1).AddSeconds(EarliestMatchDate / 1000)} - {new DateTime(1970, 1, 1).AddSeconds(LatestMatchDate / 1000)}");
                Console.WriteLine($"    Coverage: {covered:#,0} of {LatestMatchId - EarliestMatchId:#,0} ({covered / (double) (LatestMatchId - EarliestMatchId) * 100:0.000}%)");
                Console.WriteLine($"    Gaps: smallest {gapstat.Min:#,0}, largest: {gapstat.Max:#,0}, mean: {gapstat.Mean:#,0.000}, stdev: {gapstat.StdDev:#,0.000}");
                Console.ForegroundColor = ConsoleColor.Gray;
            }
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

        // both min and max are inclusive
        private long randomLong(long min, long max)
        {
            ulong range = (ulong) (max - min + 1);
            ulong maxRnd = range * (ulong.MaxValue / range);

            // Generate a random match ID
            again:;
            var random = BitConverter.ToUInt64(Rnd.NextBytes(8), 0);
            if (random > maxRnd)
                goto again;
            return (long) ((ulong) min + random % range);
        }

        public void DownloadMatch()
        {
            long matchId = randomLong(_heap[0].From, _heap[0].To);

            // Download it and add the outcome to the data store
            var dl = _downloader.DownloadMatch(matchId);
            if (dl.result == MatchDownloadResult.NonExistent)
            {
                splitRootGapAt(matchId);
                DataStore.AddNonExistentMatch(Region, matchId);
            }
            else if (dl.result == MatchDownloadResult.Failed)
                DataStore.AddFailedMatch(Region, matchId);
            else if (dl.result == MatchDownloadResult.OK)
            {
                splitRootGapAt(matchId);
                var queueId = dl.json.Safe["queueId"].GetIntLenient();
                if (queueId == QueueId)
                {
                    bool rebuildRequired = matchId < EarliestMatchId || matchId > LatestMatchId;
                    countMatch(dl.json);
                    if (rebuildRequired)
                        rebuild();
                    printStats();
                }
                DataStore.AddMatch(Region, queueId, matchId, dl.json);
            }
            else
                throw new Exception();
        }

        private struct Gap
        {
            // from and to are exclusive and point at an unchecked match id
            public long From;
            public long Length;
            public long To
            {
                get { return From + Length - 1; }
                set { Length = value - From + 1; }
            }
            public override string ToString() => $"{From:#,0} - {To:#,0} ({Length:#,0})";
        }
        private Gap[] _heap;
        private int _heapLength;

        private void splitRootGapAt(long matchId)
        {
            var root = _heap[0];
            if (matchId < root.From || matchId > root.To)
                throw new Exception();
            _heap[0].To = matchId - 1;
            heapifyDownFrom(0);
            addToHeap(new Gap { From = matchId + 1, To = root.To });
        }

        private void addToHeap(Gap gap)
        {
            if (_heapLength == _heap.Length)
            {
                var tmp = _heap;
                _heap = new Gap[_heap.Length * 2];
                Array.Copy(tmp, _heap, _heapLength);
            }
            _heap[_heapLength++] = gap;
            heapifyUpFrom(_heapLength - 1);
        }

        private void heapifyFull()
        {
            for (int index = _heapLength / 2 - 1; index >= 0; index--)
                heapifyDownFrom(index);
        }

        private void heapifyUpFrom(int index)
        {
            if (index == 0)
                return;
            int parent = (index - 1) / 2;
            if (_heap[index].Length > _heap[parent].Length)
            {
                swapGaps(index, parent);
                heapifyUpFrom(parent);
            }
        }

        private void heapifyDownFrom(int index)
        {
            int left = index * 2 + 1;
            int right = index * 2 + 2;
            if (left >= _heapLength)
                return; // no children
            if (right >= _heapLength)
            {
                if (_heap[left].Length > _heap[index].Length)
                    swapGaps(left, index);
                return; // only one child: the child therefore cannot have children of its own
            }
            // The largest of the three needs to be at [index], so pick the larger child first
            int child = _heap[left].Length > _heap[right].Length ? left : right;
            if (_heap[child].Length > _heap[index].Length)
            {
                swapGaps(child, index);
                heapifyDownFrom(child);
            }
        }

        private void swapGaps(int index1, int index2)
        {
            var tmp = _heap[index1];
            _heap[index1] = _heap[index2];
            _heap[index2] = tmp;
        }
    }
}