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
        private CancellationTokenSource _cancel = new CancellationTokenSource();
        public CountResult Count = new CountResult();
        public DateTime StartedAt, EndedAt;
        public TimeSpan Duration => (EndedAt == default(DateTime) ? DateTime.UtcNow : EndedAt) - StartedAt;
        public double Rate => Count.Count / Duration.TotalSeconds;
        public Action<int> OnInterval = (count) => Console.Write(count + " ");

        public CountThread(int interval)
        {
            StartedAt = DateTime.UtcNow;
            _t = new Thread(() =>
            {
                int next = interval;
                var token = _cancel.Token;
                while (!token.IsCancellationRequested)
                {
                    if (Count.Count > next)
                    {
                        OnInterval(Count.Count);
                        next += interval;
                    }
                    token.WaitHandle.WaitOne(1000);
                }
            });
            _t.IsBackground = true;
            _t.Start();
        }
        public void Stop()
        {
            if (_t == null)
                return;
            EndedAt = DateTime.UtcNow;
            _cancel.Cancel();
            _t.Interrupt();
            _t.Join();
            _t = null;
        }
    }
}
