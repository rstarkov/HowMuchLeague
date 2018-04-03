using System.Collections.Generic;

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
}
