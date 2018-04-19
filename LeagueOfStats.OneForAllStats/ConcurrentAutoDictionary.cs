using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LeagueOfStats.OneForAllStats
{
    // A bit of a hack because the caller is not prevented from using the base dictionary's indexer, which does not have the "auto" behaviour.
    class CcAutoDictionary<TKey, TValue> : ConcurrentDictionary<TKey, TValue>
    {
        private Func<TKey, TValue> _initializer;

        public CcAutoDictionary(Func<TKey, TValue> initializer = null)
        {
            _initializer = initializer;
        }

        public CcAutoDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, Func<TKey, TValue> initializer = null)
            : base(collection)
        {
            _initializer = initializer;
        }

        public CcAutoDictionary(IEqualityComparer<TKey> comparer, Func<TKey, TValue> initializer = null)
            : base(comparer)
        {
            _initializer = initializer;
        }

        public CcAutoDictionary(int concurrencyLevel, int capacity, Func<TKey, TValue> initializer = null)
            : base(concurrencyLevel, capacity)
        {
            _initializer = initializer;
        }

        public CcAutoDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey> comparer, Func<TKey, TValue> initializer = null)
            : base(collection, comparer)
        {
            _initializer = initializer;
        }

        public CcAutoDictionary(int concurrencyLevel, IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey> comparer, Func<TKey, TValue> initializer = null)
            : base(concurrencyLevel, collection, comparer)
        {
            _initializer = initializer;
        }

        public CcAutoDictionary(int concurrencyLevel, int capacity, IEqualityComparer<TKey> comparer, Func<TKey, TValue> initializer = null)
            : base(concurrencyLevel, capacity, comparer)
        {
            _initializer = initializer;
        }

        public new TValue this[TKey key]
        {
            get
            {
                if (TryGetValue(key, out var val))
                    return val;
                val = _initializer == null ? default(TValue) : _initializer(key);
                if (!TryAdd(key, val)) // then another thread has already added something, and we prefer that value over the newly created one
                    TryGetValue(key, out val);
                return val;
            }
            set
            {
                base[key] = value;
            }
        }
    }

    class CcAutoDictionary<TKey1, TKey2, TValue> : CcAutoDictionary<TKey1, CcAutoDictionary<TKey2, TValue>>
    {
        public CcAutoDictionary(Func<TKey1, TKey2, TValue> initializer = null)
            : base(key1 => new CcAutoDictionary<TKey2, TValue>(key2 => initializer == null ? default(TValue) : initializer(key1, key2)))
        { }

        public CcAutoDictionary(IEqualityComparer<TKey1> comparer1, IEqualityComparer<TKey2> comparer2, Func<TKey1, TKey2, TValue> initializer = null)
            : base(comparer1, key1 => new CcAutoDictionary<TKey2, TValue>(comparer2, key2 => initializer == null ? default(TValue) : initializer(key1, key2)))
        { }
    }

    class CcAutoDictionary<TKey1, TKey2, TKey3, TValue> : CcAutoDictionary<TKey1, CcAutoDictionary<TKey2, CcAutoDictionary<TKey3, TValue>>>
    {
        public CcAutoDictionary(Func<TKey1, TKey2, TKey3, TValue> initializer = null)
            : base(key1 => new CcAutoDictionary<TKey2, CcAutoDictionary<TKey3, TValue>>(key2 => new CcAutoDictionary<TKey3, TValue>(key3 => initializer == null ? default(TValue) : initializer(key1, key2, key3))))
        { }

        public CcAutoDictionary(IEqualityComparer<TKey1> comparer1, IEqualityComparer<TKey2> comparer2, IEqualityComparer<TKey3> comparer3, Func<TKey1, TKey2, TKey3, TValue> initializer = null)
            : base(comparer1, key1 => new CcAutoDictionary<TKey2, CcAutoDictionary<TKey3, TValue>>(comparer2, key2 => new CcAutoDictionary<TKey3, TValue>(comparer3, key3 => initializer == null ? default(TValue) : initializer(key1, key2, key3))))
        { }
    }

    static class CcExtensions
    {
        public static ConcurrentBag<T> ToBag<T>(this IEnumerable<T> items)
        {
            return new ConcurrentBag<T>(items);
        }
    }
}
