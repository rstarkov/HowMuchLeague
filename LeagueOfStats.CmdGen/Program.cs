using System;
using System.IO;
using System.Linq;
using LeagueOfStats.PersonalData;
using LeagueOfStats.StaticData;
using RT.Util.Dialogs;
using RT.Util.ExtensionMethods;
using RT.Util.Serialization;

namespace LeagueOfStats.CmdGen
{
    class Program
    {
        public static Settings Settings;

        static void Main(string[] args)
        {
            Console.WriteLine($"Loading settings from {args[0]}...");
            Settings = ClassifyXml.DeserializeFile<Settings>(args[0]);
            ClassifyXml.SerializeToFile(Settings, args[0]);
            Directory.CreateDirectory(Path.Combine(Settings.DataPath, "Static"));
            Directory.CreateDirectory(Path.Combine(Settings.DataPath, "Summoners"));

            LeagueStaticData.Load(Path.Combine(Settings.DataPath, "Static"));
            foreach (var human in Settings.Humans)
                human.Summoners = human.SummonerIds.Where(si => si.LoadData).Select(si => new SummonerInfo(Path.Combine(Settings.DataPath, "Summoners", $"{si.RegionServer.ToLower()}-{si.AccountId}.xml"))).ToList();
            foreach (var summoner in Settings.Humans.SelectMany(h => h.Summoners))
            {
                Console.WriteLine($"Loading game data for {summoner}");
                if (summoner.AuthorizationHeader == "")
                    summoner.LoadGamesOffline();
                else
                    summoner.LoadGamesOnline(
                        sm => InputBox.GetLine($"Please enter Authorization header value for {sm.Region}/{sm.Name}:", sm.AuthorizationHeader, "League of Stats"),
                        str => Console.WriteLine(str));
            }

            var generator = new Generator();
            generator.KnownPlayersAccountIds = Settings.Humans.SelectMany(h => h.SummonerIds).Select(s => s.AccountId).ToHashSet();
            foreach (var human in Settings.Humans.Where(h => h.Summoners.Count > 0))
            {
                generator.TimeZone = human.TimeZone;
                generator.Games = human.Summoners.SelectMany(s => s.Games).ToList();
                generator.ThisPlayerAccountIds = human.Summoners.Select(s => s.AccountId).ToHashSet();
                generator.GamesTableFilename = Settings.OutputPathTemplate.Fmt("Games-All", human.Name, "");
                generator.ProduceGamesTable();
                generator.ProduceLaneTable(Settings.OutputPathTemplate.Fmt("LaneCompare", human.Name, ""));
                generator.ProduceStats(Settings.OutputPathTemplate.Fmt("All", human.Name, ""));
                generator.ProduceStats(Settings.OutputPathTemplate.Fmt("All", human.Name, "-200"), 200);
                foreach (var summoner in human.Summoners)
                {
                    generator.Games = summoner.Games.ToList();
                    generator.ThisPlayerAccountIds = new[] { summoner.AccountId }.ToHashSet();
                    generator.GamesTableFilename = Settings.OutputPathTemplate.Fmt("Games-" + summoner.Region, summoner.Name, "");
                    generator.ProduceGamesTable();
                    generator.ProduceStats(Settings.OutputPathTemplate.Fmt(summoner.Region, summoner.Name, ""));
                    generator.ProduceStats(Settings.OutputPathTemplate.Fmt(summoner.Region, summoner.Name, "-200"), 200);
                }
            }
        }
    }
}
