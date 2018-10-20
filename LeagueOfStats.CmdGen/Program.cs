using System;
using System.IO;
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

            if (settings.ItemsOutputPath != null)
                ItemSheet.Generate(settings.ItemsOutputPath);

            if (settings.PersonalOutputPathTemplate != null)
                PersonalStats.Generate(settings.DataPath, settings.PersonalOutputPathTemplate, settings.Humans);

            if (settings.ItemSetsSettings != null && settings.LeagueInstallPath != null)
            {
                GlobalStats.GenerateRecentItemStats(settings.DataPath);
                GlobalStats.GenerateItemSets(settings.DataPath, settings.LeagueInstallPath, settings.ItemSetsSettings);
            }
        }
    }
}
