using System;
using System.Collections.Generic;
using System.Threading;

namespace LeagueOfStats.GlobalData
{
    public class CountResult
    {
        public int Count;
        public override string ToString() => Count.ToString("#,0");
    }

    public static class Extensions
    {
        public static IEnumerable<T> PassthroughCount<T>(this IEnumerable<T> collection, CountResult count)
        {
            count.Count = 0;
            foreach (var item in collection)
            {
                count.Count++;
                yield return item;
            }
        }
    }

    public class CountThread
    {
        private Thread _t;
        public CountResult Count = new CountResult();
        public DateTime StartedAt, EndedAt;
        public TimeSpan Duration => EndedAt - StartedAt;
        public double Rate => Count.Count / Duration.TotalSeconds;
        public Action<int> OnInterval = (count) => Console.Write(count + " ");

        public CountThread(int interval)
        {
            StartedAt = DateTime.UtcNow;
            _t = new Thread(() =>
            {
                int next = interval;
                while (true)
                {
                    if (Count.Count > next)
                    {
                        OnInterval(Count.Count);
                        next += interval;
                    }
                    Thread.Sleep(1000);
                }
            });
            _t.Start();
        }
        public void Stop()
        {
            if (_t == null)
                return;
            EndedAt = DateTime.UtcNow;
            _t.Abort();
            _t.Join();
            _t = null;
        }
    }
}
