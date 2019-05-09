using System.Collections.Generic;
using System.Linq;
using RT.Util;

namespace LeagueOfStats.StaticData
{
    public class QueueInfo
    {
        public int Id { get; private set; }
        public MapId Map { get; private set; }
        public string ModeName { get; private set; }
        public string Variant { get; private set; }
        public int? ReplacedBy { get; private set; }
        public bool Deprecated { get; private set; }
        public bool IsPvp { get; private set; }
        public bool IsEvent { get; private set; }

        public QueueInfo(int id, MapId map, string modeName, string variant, int? replacedBy = null, bool deprecated = false, bool pvp = true, bool isEvent = false)
        {
            Id = id;
            Map = map;
            ModeName = modeName;
            Variant = variant;
            ReplacedBy = replacedBy;
            Deprecated = deprecated;
            IsPvp = pvp;
            IsEvent = isEvent;
        }
    }

    public enum MapId
    {
        SummonersRiftOldSummer = 1,
        SummonersRiftOldAutumn = 2,
        ProvingGrounds = 3,
        TwistedTreelineOld = 4,
        CrystalScar = 8,
        TwistedTreeline = 10,
        SummonersRift = 11,
        HowlingAbyss = 12,
        ButchersBridge = 14,
        CosmicRuins = 16,
        ValoranCityPark = 18,
        Substructure43 = 19,
        CrashSite = 20,
        NexusBlitz = 21,
    }

    public static class Queues
    {
        public static IList<QueueInfo> AllQueues { get; } = new List<QueueInfo>(Ut.NewArray(
            new QueueInfo(0, 0, "Custom", ""),

            new QueueInfo(2, MapId.SummonersRift, "5v5", "Blind Pick", replacedBy: 430),
            new QueueInfo(4, MapId.SummonersRift, "5v5", "Ranked Solo", replacedBy: 420),
            new QueueInfo(6, MapId.SummonersRift, "5v5", "Ranked Premade", deprecated: true),
            new QueueInfo(14, MapId.SummonersRift, "5v5", "Draft Pick", replacedBy: 400),
            new QueueInfo(42, MapId.SummonersRift, "5v5", "Ranked Team", deprecated: true),
            new QueueInfo(61, MapId.SummonersRift, "5v5", "Team Builder", deprecated: true),
            new QueueInfo(400, MapId.SummonersRift, "5v5", "Draft Pick"),
            new QueueInfo(410, MapId.SummonersRift, "5v5", "Ranked Dynamic", deprecated: true),
            new QueueInfo(420, MapId.SummonersRift, "5v5", "Ranked Solo"),
            new QueueInfo(430, MapId.SummonersRift, "5v5", "Blind Pick"),
            new QueueInfo(440, MapId.SummonersRift, "5v5", "Ranked Flex"),
            new QueueInfo(7, MapId.SummonersRift, "5v5 bots", "Original", pvp: false),
            new QueueInfo(31, MapId.SummonersRift, "5v5 bots", "Intro", replacedBy: 830, pvp: false),
            new QueueInfo(32, MapId.SummonersRift, "5v5 bots", "Beginner", replacedBy: 840, pvp: false),
            new QueueInfo(33, MapId.SummonersRift, "5v5 bots", "Intermediate", replacedBy: 850, pvp: false),
            new QueueInfo(830, MapId.SummonersRift, "5v5 bots", "Intro", pvp: false),
            new QueueInfo(840, MapId.SummonersRift, "5v5 bots", "Beginner", pvp: false),
            new QueueInfo(850, MapId.SummonersRift, "5v5 bots", "Intermediate", pvp: false),

            new QueueInfo(8, MapId.TwistedTreeline, "3v3", "Blind Pick", replacedBy: 460),
            new QueueInfo(9, MapId.TwistedTreeline, "3v3", "Ranked Flex", replacedBy: 470),
            new QueueInfo(41, MapId.TwistedTreeline, "3v3", "Ranked Team", deprecated: true),
            new QueueInfo(460, MapId.TwistedTreeline, "3v3", "Blind Pick"),
            new QueueInfo(470, MapId.TwistedTreeline, "3v3", "Ranked Flex"),
            new QueueInfo(52, MapId.TwistedTreeline, "3v3 bots", "Intermediate", replacedBy: 800, pvp: false),
            new QueueInfo(800, MapId.TwistedTreeline, "3v3 bots", "Intermediate", pvp: false),
            new QueueInfo(810, MapId.TwistedTreeline, "3v3 bots", "Intro", pvp: false),
            new QueueInfo(820, MapId.TwistedTreeline, "3v3 bots", "Beginner", pvp: false),

            new QueueInfo(16, MapId.CrystalScar, "Dominion", "Blind Pick", deprecated: true),
            new QueueInfo(17, MapId.CrystalScar, "Dominion", "Draft Pick", deprecated: true),
            new QueueInfo(25, MapId.CrystalScar, "Dominion bots", "", deprecated: true, pvp: false),

            new QueueInfo(65, MapId.HowlingAbyss, "ARAM", "", replacedBy: 450),
            new QueueInfo(450, MapId.HowlingAbyss, "ARAM", ""),

            new QueueInfo(70, MapId.SummonersRift, "One for All", "Standard", replacedBy: 1020, isEvent: true),
            new QueueInfo(78, MapId.HowlingAbyss, "One For All", "Mirror Mode", isEvent: true),
            new QueueInfo(1020, MapId.SummonersRift, "One for All", "", isEvent: true),

            new QueueInfo(76, MapId.SummonersRift, "Ultra Rapid Fire", "PvP", isEvent: true),
            new QueueInfo(83, MapId.SummonersRift, "Ultra Rapid Fire", "Bots", pvp: false, isEvent: true),

            new QueueInfo(318, MapId.SummonersRift, "ARURF", "", replacedBy: 900, isEvent: true),
            new QueueInfo(900, MapId.SummonersRift, "ARURF", "", isEvent: true),
            new QueueInfo(1010, MapId.SummonersRift, "ARURF", "Snow", isEvent: true),

            new QueueInfo(75, MapId.SummonersRift, "Hexakill", "SR", isEvent: true),
            new QueueInfo(98, MapId.TwistedTreeline, "Hexakill", "TT", isEvent: true),

            new QueueInfo(72, MapId.HowlingAbyss, "Snowdown Showdown", "1v1", isEvent: true),
            new QueueInfo(73, MapId.HowlingAbyss, "Snowdown Showdown", "2v2", isEvent: true),
            new QueueInfo(91, MapId.SummonersRift, "Doom Bots", "Rank 1", replacedBy: 950, pvp: false, isEvent: true),
            new QueueInfo(92, MapId.SummonersRift, "Doom Bots", "Rank 2", replacedBy: 950, pvp: false, isEvent: true),
            new QueueInfo(93, MapId.SummonersRift, "Doom Bots", "Rank 5", replacedBy: 950, pvp: false, isEvent: true),
            new QueueInfo(96, MapId.CrystalScar, "Ascension", "", replacedBy: 910, isEvent: true),
            new QueueInfo(100, MapId.ButchersBridge, "ARAM", "Butcher's Bridge", isEvent: true),
            new QueueInfo(300, MapId.HowlingAbyss, "Legend of the Poro King", "", replacedBy: 920, isEvent: true),
            new QueueInfo(310, MapId.SummonersRift, "Nemesis", "", isEvent: true),
            new QueueInfo(313, MapId.SummonersRift, "Black Market Brawlers", "", isEvent: true),
            new QueueInfo(315, MapId.SummonersRift, "Nexus Siege", "", replacedBy: 940, isEvent: true),
            new QueueInfo(317, MapId.CrystalScar, "Definitely Not Dominion", "", isEvent: true),
            new QueueInfo(325, MapId.SummonersRift, "All Random", "", isEvent: true),
            new QueueInfo(600, MapId.SummonersRift, "Blood Hunt Assassin", "", isEvent: true),
            new QueueInfo(610, MapId.CosmicRuins, "Dark Star: Singularity", "", isEvent: true),
            new QueueInfo(700, MapId.SummonersRift, "Clash", "", isEvent: true),
            new QueueInfo(910, MapId.CrystalScar, "Ascension", "", isEvent: true),
            new QueueInfo(920, MapId.HowlingAbyss, "Legend of the Poro King", "", isEvent: true),
            new QueueInfo(940, MapId.SummonersRift, "Nexus Siege", "", isEvent: true),
            new QueueInfo(950, MapId.SummonersRift, "Doom Bots", "Voting", pvp: false, isEvent: true),
            new QueueInfo(960, MapId.SummonersRift, "Doom Bots", "Standard", pvp: false, isEvent: true),
            new QueueInfo(980, MapId.ValoranCityPark, "Star Guardian Invasion", "Normal", pvp: false, isEvent: true),
            new QueueInfo(990, MapId.ValoranCityPark, "Star Guardian Invasion", "Onslaught", pvp: false, isEvent: true),
            new QueueInfo(1000, MapId.Substructure43, "Overcharge", "", isEvent: true),
            new QueueInfo(1030, MapId.CrashSite, "Odyssey Extraction", "Intro", pvp: false, isEvent: true),
            new QueueInfo(1040, MapId.CrashSite, "Odyssey Extraction", "Cadet", pvp: false, isEvent: true),
            new QueueInfo(1050, MapId.CrashSite, "Odyssey Extraction", "Crewmember", pvp: false, isEvent: true),
            new QueueInfo(1060, MapId.CrashSite, "Odyssey Extraction", "Captain", pvp: false, isEvent: true),
            new QueueInfo(1070, MapId.CrashSite, "Odyssey Extraction", "Onslaught", pvp: false, isEvent: true),
            new QueueInfo(1200, MapId.NexusBlitz, "Nexus Blitz", "", isEvent: true),

            new QueueInfo(67, 0, "Unknown", "Unknown"),
            new QueueInfo(860, 0, "Unknown", "Unknown"),
            new QueueInfo(2000, 0, "Unknown", "Unknown"),
            new QueueInfo(2010, 0, "Unknown", "Unknown"),
            new QueueInfo(2020, 0, "Unknown", "Unknown")
        )).AsReadOnly();

        private static Dictionary<int, QueueInfo> _byId;

        static Queues()
        {
            _byId = AllQueues.ToDictionary(q => q.Id);
        }

        public static QueueInfo GetInfo(int queueId)
        {
            return _byId[queueId];
        }
    }
}
