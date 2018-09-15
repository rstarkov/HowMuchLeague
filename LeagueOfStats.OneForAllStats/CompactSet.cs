using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RT.Util;

namespace LeagueOfStats.OneForAllStats
{
    class CompactSetOfLong : IEnumerable<long>
    {
        private class bucket
        {
            public long From;
            public long To;
            public uint[] Values;
            public int Count;
        }

        private List<bucket> _buckets = new List<bucket>();

        public CompactSetOfLong()
        {
        }

        public CompactSetOfLong(IEnumerable<long> items)
        {
            var list = items.ToList();
            if (list.Count == 0)
                return;
            list.Sort();
            var perBucket = (int) Math.Ceiling(Math.Sqrt(list.Count));
            if (perBucket < 1)
                perBucket = 1;
            bucket curBucket = null;
            long prevItem = long.MaxValue;
            foreach (var item in list)
            {
                if (curBucket != null && item == prevItem)
                    continue; // already added
                prevItem = item;
                // Do we need a new bucket?
                if (curBucket == null || curBucket.Count == perBucket || item > curBucket.To)
                {
                    // Finalise previous bucket
                    if (_buckets.Count > 0)
                        _buckets[_buckets.Count - 1].To = Math.Min(_buckets[_buckets.Count - 1].To, item - 1);
                    // Start a new bucket
                    curBucket = new bucket
                    {
                        From = item,
                        To = item + uint.MaxValue,
                        Count = 0,
                        Values = new uint[perBucket],
                    };
                    _buckets.Add(curBucket);
                }
                // Add item
                curBucket.Values[curBucket.Count] = (uint) (item - curBucket.From);
                curBucket.Count++;
                Count++;
            }
#if DEBUG
            checkConsistency();
#endif
        }

        public int Count { get; private set; } = 0;

        public bool Add(long item)
        {
            lock (_buckets)
            {
                if (_buckets.Count == 0)
                    return addFirst(item);
                int min = 0;
                int max = _buckets.Count - 1;
                while (min <= max)
                {
                    int cur = (min + max) / 2;
                    if (item < _buckets[cur].From)
                        max = cur - 1;
                    else if (item > _buckets[cur].To)
                        min = cur + 1;
                    else
                    {
                        var arr = _buckets[cur].Values;
                        var c = _buckets[cur].Count;
                        var tgt = (uint) (item - _buckets[cur].From);
                        for (int i = 0; i < c; i++)
                            if (arr[i] == tgt)
                                return false;
                        if (arr.Length == c)
                        {
                            Array.Resize(ref arr, arr.Length * 4 / 3 + 1);
                            _buckets[cur].Values = arr;
                        }
                        _buckets[cur].Values[_buckets[cur].Count] = tgt;
                        _buckets[cur].Count++;
                        if (_buckets[cur].Count > _buckets.Count)
                            splitBucket(cur);
                        Count++;
                        return true;
                    }
                }
                // No suitable bucket found. We have to insert one between existing buckets, or at one of the two ends.
                Ut.Assert(min == max + 1);
                var bucket = new bucket();
                if (min == 0)
                {
                    // Append one at the start
                    bucket.To = _buckets[0].From - 1;
                    bucket.From = bucket.To - uint.MaxValue;
                    if (item < bucket.From)
                    {
                        bucket.From = item - int.MaxValue;
                        bucket.To = item + int.MaxValue;
                    }
                    _buckets.Insert(0, bucket);
                }
                else if (max == _buckets.Count - 1)
                {
                    // Append one at the end
                    bucket.From = _buckets[_buckets.Count - 1].To + 1;
                    bucket.To = bucket.From + uint.MaxValue;
                    if (item > bucket.To)
                    {
                        bucket.From = item - int.MaxValue;
                        bucket.To = item + int.MaxValue;
                    }
                    _buckets.Add(bucket);
                }
                else
                {
                    // Insert one between buckets max and max+1
                    bucket.From = Math.Max(item - int.MaxValue, _buckets[max].To + 1);
                    bucket.To = Math.Min(item + int.MaxValue, _buckets[max + 1].From - 1);
                    _buckets.Insert(max + 1, bucket);
                }
                bucket.Count = 1;
                bucket.Values = new uint[16];
                bucket.Values[0] = (uint) (item - bucket.From);
                Count++;
#if DEBUG
                Ut.Assert(bucket.From <= bucket.To);
#endif
                return true;
            }
        }

        private void splitBucket(int index)
        {
            var bucket1 = _buckets[index];
            var bucket2 = new bucket();
            _buckets.Insert(index + 1, bucket2);
            var arr = bucket1.Values;
            var c = bucket1.Count;
            // Find the approximate median of this bucket
            uint min = arr[0];
            uint max = arr[0];
            for (int i = 1; i < c; i++)
            {
                if (min > arr[i])
                    min = arr[i];
                if (max < arr[i])
                    max = arr[i];
            }
            uint median = (min / 2) + (max / 2);
            uint eta = (max - min) / (uint) c;
            if (eta < 1)
                eta = 1;
            for (int i = 1; i < c; i++)
            {
                if (arr[i] > median)
                    median += eta;
                else // disregard the == case for speed
                    median -= eta;
            }
            if (median < min || median > max)
                median = (min / 2) + (max / 2);
            // Split up the values
            var vals1 = new List<long>();
            var vals2 = new List<long>();
            for (int i = 0; i < c; i++)
                (arr[i] <= median ? vals1 : vals2).Add(arr[i] + bucket1.From);
            // Update the bucket limits
            bucket2.To = bucket1.To;
            bucket1.To = median + bucket1.From;
            bucket2.From = bucket1.To + 1;
            // Expand lower From if possible
            bucket1.From = bucket1.To - uint.MaxValue;
            if (index > 0)
                bucket1.From = Math.Max(bucket1.From, _buckets[index - 1].To + 1);
            // Expand upper To if possible
            bucket2.To = bucket2.From + uint.MaxValue;
            if (index + 1 < _buckets.Count - 1)
                bucket2.To = Math.Min(bucket2.To, _buckets[index + 2].From - 1);
#if DEBUG
            Ut.Assert(bucket1.From <= bucket1.To);
            Ut.Assert(bucket2.From <= bucket2.To);
#endif
            // Populate the values
            bucket1.Count = vals1.Count;
            bucket2.Count = vals2.Count;
            bucket1.Values = new uint[Math.Max(vals1.Count * 11 / 10, 16)];
            bucket2.Values = new uint[Math.Max(vals2.Count * 11 / 10, 16)];
            for (int i = 0; i < vals1.Count; i++)
                bucket1.Values[i] = (uint) (vals1[i] - bucket1.From);
            for (int i = 0; i < vals2.Count; i++)
                bucket2.Values[i] = (uint) (vals2[i] - bucket2.From);
        }

        private bool addFirst(long item)
        {
            _buckets.Add(new bucket
            {
                From = item - int.MaxValue,
                To = item + int.MaxValue,
                Count = 1,
                Values = new uint[16],
            });
            _buckets[0].Values[0] = (uint) (item - _buckets[0].From);
            Count++;
            return true;
        }

        public bool Contains(long item)
        {
            lock (_buckets)
            {
                if (_buckets.Count == 0)
                    return false;
                int min = 0;
                int max = _buckets.Count - 1;
                while (min <= max)
                {
                    int cur = (min + max) / 2;
                    if (item < _buckets[cur].From)
                        max = cur - 1;
                    else if (item > _buckets[cur].To)
                        min = cur + 1;
                    else
                    {
                        var arr = _buckets[cur].Values;
                        var c = _buckets[cur].Count;
                        var tgt = (uint) (item - _buckets[cur].From);
                        for (int i = 0; i < c; i++)
                            if (arr[i] == tgt)
                                return true;
                        return false;
                    }
                }
                return false;
            }
        }

        /// <summary>
        ///     This takes out the lock until the enumeration is complete, so... beware...</summary>
        /// <remarks>
        ///     The lock is obviously not the best idea, but considering the limited applicability of this class to a very
        ///     specific use case, it's an acceptable trade-off. This implementation can break _visibly_ by causing a
        ///     deadlock, whereas without this lock it could break invisibly due to a concurrent add.</remarks>
        public IEnumerator<long> GetEnumerator()
        {
            lock (_buckets)
                foreach (var bucket in _buckets)
                    for (int i = 0; i < bucket.Count; i++)
                        yield return bucket.Values[i] + bucket.From;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #region Tests

        public static void SelfTest(int count, long max)
        {
            Console.WriteLine($"Self-test with {count:#,0} items");
            var cset = new CompactSetOfLong();
            var hset = new HashSet<long>();
            for (int c = 0; c < count; c++)
            {
                var val = (long) (Rnd.NextDouble() * max);
                Ut.Assert(hset.Count == cset.Count);
                Ut.Assert(hset.Add(val) == cset.Add(val));
                Ut.Assert(hset.Count == cset.Count);
                if (c < 1000 || (c < 100_000 && (c % 1000 == 0)) || (c % 10_000 == 0))
                {
                    cset.checkConsistency();
                    Ut.Assert(hset.Add(val) == cset.Add(val));
                    // Verify that Contains is true for every item
                    foreach (var item in hset)
                        Ut.Assert(cset.Contains(item));
                    foreach (var item in cset)
                        Ut.Assert(hset.Contains(item));
                    // Verify that the set of items has no duplicates, relying on the fact that GetEnumerator is correct, and on the fact that we've checked that every item of hset is in it
                    Ut.Assert(cset.Count == hset.Count);
                    Ut.Assert(cset.Count == cset.Count());
                    // Verify that Contains is false for random items
                    for (int i = 0; i < 50; i++)
                    {
                        val = (long) (Rnd.NextDouble() * max);
                        Ut.Assert(hset.Contains(val) == cset.Contains(val));
                    }
                }
            }
        }

        private void checkConsistency()
        {
            foreach (var bucket in _buckets)
            {
                Ut.Assert(bucket.From <= bucket.To);
                Ut.Assert(bucket.To - bucket.From <= uint.MaxValue);
            }
            // Verify that buckets are sorted and non-overlapping
            for (int i = 0; i < _buckets.Count - 2; i++)
            {
                Ut.Assert(_buckets[i].From < _buckets[i + 1].From);
                Ut.Assert(_buckets[i].To < _buckets[i + 1].From);
            }
            // Verify that all items are within the from/to boundaries
            foreach (var bucket in _buckets)
            {
                foreach (var item in bucket.Values)
                    Ut.Assert(item + bucket.From <= bucket.To);
            }
        }

        #endregion
    }
}
