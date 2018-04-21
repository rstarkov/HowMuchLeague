﻿using System;
using System.Collections.Generic;
using System.IO;
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
        public string[] ApiKeys;
        public Region Region;
        public string Version;
        public int? QueueId;
        public long InitialMatchId, MatchIdRange;

        public int MatchCount { get; private set; } = 0;
        public long EarliestMatchDate { get; private set; } = long.MaxValue;
        public long LatestMatchDate { get; private set; } = 0;
        public long EarliestMatchId { get; private set; } = long.MaxValue;
        public long LatestMatchId { get; private set; } = 0;

        private MatchDownloader[] _downloaders;
        private int _nextDownloader = 0;

        public Downloader(string[] apiKeys, Region region, string version, int? queueId, long initialMatchId, long matchIdRange)
        {
            ApiKeys = apiKeys;
            Region = region;
            Version = version;
            QueueId = queueId;
            MatchIdRange = matchIdRange;

            _downloaders = ApiKeys.Select(key => new MatchDownloader(key, Region)).ToArray();
            foreach (var dl in _downloaders)
                dl.OnEveryResponse = (_, __) => { };

            if (Version == null && QueueId == null && DataStore.ExistingMatchIds[Region].Count > 0)
            {
                // We only really care about match IDs (also dates, but not a lot as those are only for stats printing). We need them filtered by QueueId & Version,
                // but there is currently no efficient way to do that other than read all match JSONs. Except if there are no filters - then it's just all the known match IDs.
                EarliestMatchId = DataStore.ExistingMatchIds[Region].Min();
                LatestMatchId = DataStore.ExistingMatchIds[Region].Max();
                MatchCount = DataStore.ExistingMatchIds[Region].Count;
            }
            else
            {
                foreach (var kvpVersion in DataStore.LosMatchJsons[Region])
                    if (kvpVersion.Key == Version || Version == null)
                        foreach (var kvpQueue in kvpVersion.Value)
                            if (kvpQueue.Key == QueueId || QueueId == null)
                            {
                                Console.Write($"Loading {kvpQueue.Value.FileName}... ");
                                var thread = new CountThread(10000);
                                foreach (var json in kvpQueue.Value.ReadItems().PassthroughCount(thread.Count))
                                    countMatch(json, new BasicMatchInfo(json));
                                thread.Stop();
                                Console.WriteLine();
                                Console.WriteLine($"  loaded {thread.Count.Count:#,0} matches in {thread.Duration.TotalSeconds:#,0} s ({thread.Rate:#,0}/s)");
                            }
            }
            if (LatestMatchId == 0) // means not a single match within the filter parameters was in the store
                InitialMatchId = initialMatchId;
            else
                InitialMatchId = (EarliestMatchId + LatestMatchId) / 2;
            rebuild();
            printStats();
        }

        private List<long> _idsForRebuild = new List<long>(); // only meaningful during a rebuild, but we don't want a large new list allocated and GC'd all the time (plus we never know what capacity it should be upfront, but it doesn't change much from run to run)

        private void rebuild()
        {
            Console.Write("Rebuilding... ");

            var searchMin = Math.Min(InitialMatchId, EarliestMatchId) - MatchIdRange;
            var searchMax = Math.Max(InitialMatchId, LatestMatchId) + MatchIdRange;

            _idsForRebuild.Clear();
            foreach (var id in DataStore.ExistingMatchIds[Region])
                if (id > searchMin && id < searchMax)
                    _idsForRebuild.Add(id);
            foreach (var id in DataStore.NonexistentMatchIds[Region])
                if (id > searchMin && id < searchMax)
                    _idsForRebuild.Add(id);
            _idsForRebuild.Add(searchMin);
            _idsForRebuild.Add(searchMax);
            _idsForRebuild.Sort();

            int iHeap = 0; // this is just _heapLength but it's a tight enough loop to benefit from using a local as opposed to a field
            int iId = 0;
            while (iId < _idsForRebuild.Count - 1)
            {
                var idF = _idsForRebuild[iId] + 1;
                var idT = _idsForRebuild[iId + 1] - 1;
                iId++;
                if (idF <= idT) // else: skip consecutive IDs as there is no gap between them
                {
                    if (_heap.Length <= iHeap)
                    {
                        var temp = _heap;
                        _heap = new Gap[_heap.Length * 3 / 2]; // the size of this array settles over repeated invocations of rebuild, so no need to grow it very fast; 1.5x is enough and wastes less RAM
                        Array.Copy(temp, _heap, iHeap);
                    }
                    _heap[iHeap] = new Gap { From = idF, To = idT };
                    iHeap++;
                }
            }
            _heapLength = iHeap;
            heapifyFull();
            Console.WriteLine("done");
        }

        private (bool added, bool rangeExpanded) countMatch(JsonValue json, BasicMatchInfo info)
        {
            bool rangeExpanded = info.MatchId < EarliestMatchId || info.MatchId > LatestMatchId;
            bool added = false;

            if ((info.GameVersion == Version || Version == null) && (info.QueueId == QueueId || QueueId == null))
            {
                MatchCount++;
                EarliestMatchDate = Math.Min(EarliestMatchDate, info.GameCreation);
                LatestMatchDate = Math.Max(LatestMatchDate, info.GameCreation);
                EarliestMatchId = Math.Min(EarliestMatchId, info.MatchId);
                LatestMatchId = Math.Max(LatestMatchId, info.MatchId);
                added = true;
            }

            return (added, rangeExpanded);
        }

        private DateTime _noPrintStatsUntil;

        private void printStats()
        {
            if (!(EarliestMatchDate < long.MaxValue && LatestMatchDate > 0 && _heapLength > 0))
                return;
            if (DateTime.UtcNow < _noPrintStatsUntil)
                return;
            long searchMin = long.MaxValue;
            long searchMax = long.MinValue;
            for (int i = 0; i < _heapLength; i++)
            {
                if (searchMin > _heap[i].From)
                    searchMin = _heap[i].From;
                if (searchMax < _heap[i].To)
                    searchMax = _heap[i].To;
            }
            var covered = DataStore.ExistingMatchIds[Region].Concat(DataStore.NonexistentMatchIds[Region]).Count(id => id >= searchMin && id <= searchMax);
            var gapstat = new ValueStat();
            foreach (var gap in _heap.Take(_heapLength))
                gapstat.AddObservation(gap.Length);
            Console.ForegroundColor = Program.Colors[Region];
            Console.WriteLine();
            Console.WriteLine($"{Region}: {MatchCount:#,0}; {EarliestMatchId:#,0} - {LatestMatchId:#,0}; {new DateTime(1970, 1, 1).AddSeconds(EarliestMatchDate / 1000)} - {new DateTime(1970, 1, 1).AddSeconds(LatestMatchDate / 1000)}");
            Console.WriteLine($"    Coverage: {covered:#,0} of {searchMax - searchMin:#,0} ({covered / (double) (searchMax - searchMin) * 100:0.000}%).  Gaps: min {gapstat.Min:#,0}, max: {gapstat.Max:#,0}, mean: {gapstat.Mean:#,0.000}, stdev: {gapstat.StdDev:#,0.000}");
            Console.ForegroundColor = ConsoleColor.Gray;
            _noPrintStatsUntil = DateTime.UtcNow.AddSeconds(60);
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
            if (_heapLength == 0)
            {
                Console.WriteLine($"{Region}: NOTHING LEFT TO DOWNLOAD");
                Thread.Sleep(TimeSpan.FromSeconds(10));
                return;
            }
            again:;
            int gapIndex = Rnd.Next(0, Math.Min(5, _heapLength));
            if (_heap[gapIndex].Length < _heap[0].Length / 2.0)
                goto again;
            Ut.WaitSharingVio(() => File.AppendAllText($"gaps-split-{Region}.txt", $"{_heap[gapIndex].Length} "));
            long matchId = randomLong(_heap[gapIndex].From, _heap[gapIndex].To);

            // Download it and add the outcome to the data store
            again2:;
            var downloader = _downloaders[_nextDownloader];
            _nextDownloader = (_nextDownloader + 1) % _downloaders.Length;
            var dl = downloader.DownloadMatch(matchId);
            if (dl.result == MatchDownloadResult.OverQuota)
            {
                Thread.Sleep(Rnd.Next(500, 1500)); // slowly de-sync multiple Downloaders over time
                goto again2;
            }
            // Downloading is inherently single-threaded to avoid complications related to dealing with several different match IDs being "in flight" and what that means for the gap heap.

            if (dl.result == MatchDownloadResult.NonExistent)
            {
                splitGapAt(gapIndex, matchId);
                DataStore.AddNonExistentMatch(Region, matchId);
            }
            else if (dl.result == MatchDownloadResult.Failed)
                Console.WriteLine($"Download failed: {matchId}");
            else if (dl.result == MatchDownloadResult.OK)
            {
                splitGapAt(gapIndex, matchId);
                var info = DataStore.AddMatch(Region, dl.json);
                var wasEarliest = EarliestMatchId;
                var wasLatest = LatestMatchId;
                var (added, rangeExpanded) = countMatch(dl.json, info);
                if (added)
                {
                    if (rangeExpanded)
                        rebuild();
                    printStats();
                }
                Console.ForegroundColor = Program.Colors[Region] - (added ? 0 : 8);
                if (matchId < (wasEarliest + wasLatest) / 2)
                    Console.Write($">{matchId - wasEarliest:#,0}   ");
                else
                    Console.Write($"{wasLatest - matchId:#,0}<   ");
                Console.ForegroundColor = ConsoleColor.Gray;
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
        private Gap[] _heap = new Gap[16];
        private int _heapLength;

        private void splitGapAt(int gapIndex, long matchId)
        {
            var gap = _heap[gapIndex];
            if (matchId < gap.From || matchId > gap.To)
                throw new Exception();
            _heap[gapIndex].To = matchId - 1;
            if (_heap[gapIndex].Length > 0)
                heapifyDownFrom(gapIndex);
            else
            {
                // Remove this gap altogether. Not super optimal as we could just add the other gap here, but that's more special cases and it's already more than fast enough
                _heapLength--;
                swapGaps(gapIndex, _heapLength);
                heapifyDownFrom(gapIndex);
            }
            gap = new Gap { From = matchId + 1, To = gap.To };
            if (gap.Length > 0)
                addToHeap(gap);
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
