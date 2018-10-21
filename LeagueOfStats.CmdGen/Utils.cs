using System;
using RT.TagSoup;

namespace LeagueOfStats.CmdGen
{
    static class Utils
    {
        public static T AddClass<T>(this T tag, string class_) where T : HtmlTag
        {
            if (tag.class_ == null || tag.class_ == "")
                tag.class_ = class_;
            else
                tag.class_ += " " + class_;
            return tag;
        }

        public static T AppendStyle<T>(this T tag, string style) where T : HtmlTag
        {
            tag.style = (tag.style ?? "") + style;
            return tag;
        }

        public static string Capitalise(this string str) => str.Substring(0, 1).ToUpper() + str.Substring(1);

        public static (double lower, double upper) WilsonConfidenceInterval(double p, int n, double z)
        {
            // https://github.com/msn0/wilson-score-interval/blob/master/index.js
            // z is 1-alpha/2 percentile of a standard normal distribution for error alpha=5%
            // 95% confidence = 0.975 percentile = 1.96
            // 67% confidence = 0.833 percentile = 0.97
            var a = p + z * z / (2 * n);
            var b = z * Math.Sqrt((p * (1 - p) + z * z / (4 * n)) / n);
            var c = 1 + z * z / n;
            return ((a - b) / c, (a + b) / c);
        }
    }
}
