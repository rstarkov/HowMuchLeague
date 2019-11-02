using System;
using System.IO;
using LeagueOfStats.GlobalData;
using LeagueOfStats.StaticData;
using RT.Util.Serialization;

namespace LeagueOfStats.CmdGen
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine($"Loading settings from {args[0]}...");
            var settings = ClassifyXml.DeserializeFile<Settings>(args[0]);
            ClassifyXml.SerializeToFile(settings, args[0]);

            Console.Write("Loading static data...");
            LeagueStaticData.Load(Path.Combine(settings.DataPath, "Static"));
            Console.WriteLine(" done");

            Console.Write("Initialising global data...");
            DataStore.Initialise(settings.DataPath, "", autoRewrites: false);
            Console.WriteLine(" done");

            if (settings.ItemsOutputPath != null)
                ItemSheet.Generate(settings.ItemsOutputPath);

            if (settings.ItemSetsSettings != null && settings.LeagueInstallPath != null)
                ItemSets.Generate(settings.DataPath, settings.LeagueInstallPath, settings.ItemSetsSettings);

            if (settings.PersonalOutputPathTemplate != null)
                PersonalStats.Generate(settings.DataPath, settings.PersonalOutputPathTemplate, settings.Humans);

            if (settings.EventStatsSettings != null)
                new EventStats(settings.EventStatsSettings).Generate();

            if (settings.SummonerRift5v5StatsSettings != null)
                new SummonerRift5v5Stats(settings.SummonerRift5v5StatsSettings).Generate();
        }
    }
}
