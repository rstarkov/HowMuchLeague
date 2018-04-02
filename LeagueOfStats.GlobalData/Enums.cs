using System;

namespace LeagueOfStats.GlobalData
{
    public enum Region
    {
        BR = 1,
        EUNE = 2,
        EUW = 3,
        JP = 4,
        KR = 5,
        LAN = 6,
        LAS = 7,
        NA = 8,
        OCE = 9,
        TR = 10,
        RU = 11,
        PBE = 12,
    }

    static class EnumConversion
    {
        public static byte ToByte(this Region region)
        {
            var result = (int) region;
            if (result >= 0 && result <= 255)
                return (byte) result;
            throw new Exception("Unexpected region type");
        }

        public static Region ToRegion(this byte region)
        {
            if (region < 0 || region > 255)
                throw new Exception();
            return Check((Region) region);
        }

        public static string ToApiHost(this Region region)
        {
            switch (region)
            {
                case Region.BR: return "br1.api.riotgames.com";
                case Region.EUNE: return "eun1.api.riotgames.com";
                case Region.EUW: return "euw1.api.riotgames.com";
                case Region.JP: return "jp1.api.riotgames.com";
                case Region.KR: return "kr.api.riotgames.com";
                case Region.LAN: return "la1.api.riotgames.com";
                case Region.LAS: return "la2.api.riotgames.com";
                case Region.NA: return "na1.api.riotgames.com";
                case Region.OCE: return "oc1.api.riotgames.com";
                case Region.TR: return "tr1.api.riotgames.com";
                case Region.RU: return "ru.api.riotgames.com";
                case Region.PBE: return "pbe1.api.riotgames.com";
                default: throw new Exception();
            }
        }

        public static T Check<T>(T value)
        {
            if (!Enum.IsDefined(typeof(T), value))
                throw new Exception();
            return value;
        }
    }
}
