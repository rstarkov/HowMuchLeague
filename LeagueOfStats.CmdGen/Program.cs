using System;
using System.IO;
using LeagueOfStats.StaticData;
using RT.Util.Json;
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

            if (settings.ItemSetsReportPath != null && settings.LeagueInstallPath != null)
            {
                GlobalStats.GenerateRecentItemStats(settings.DataPath);
                JsonValue preferredSlots = null;
                if (settings.ItemSetsSlotsJson != null && settings.ItemSetsSlotsName != null)
                    preferredSlots = GlobalStats.LoadPreferredSlots(settings.ItemSetsSlotsJson, settings.ItemSetsSlotsName);
                GlobalStats.GenerateItemSets(settings.DataPath, settings.LeagueInstallPath, settings.ItemSetsReportPath, preferredSlots);
            }
        }
    }
}
