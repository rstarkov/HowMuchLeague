using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RT.TagSoup;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.CmdGen
{
    class StatsBase
    {
        protected static void GenerateHtmlToFile(string filename, object bodyContent, bool includeSorttable)
        {
            object css, sorttable = null;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LeagueOfStats.CmdGen.Css.GlobalStats.css"))
                css = new STYLELiteral(stream.ReadAllText());
            if (includeSorttable)
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LeagueOfStats.CmdGen.Css.sorttable.js"))
                    sorttable = new SCRIPTLiteral(stream.ReadAllText());
            var html = new HTML(
                new HEAD(
                    new META { charset = "utf-8" },
                    css,
                    sorttable
                ),
                new BODY(bodyContent)
            );
            Directory.CreateDirectory(Path.GetDirectoryName(filename));
            File.WriteAllText(filename, html.ToString());
        }

        protected static TH colAsc(object caption, bool initial = false)
            => new TH(caption) { class_ = initial ? "sorttable_initial" : "" };

        protected static TH colDesc(object caption, bool initial = false)
            => new TH(caption) { class_ = (initial ? "sorttable_initial " : "") + "sorttable_startreversed" };

        protected static TD cell(object content, object sortkey, bool leftAlign)
            => (TD) new TD { class_ = leftAlign ? "la" : "ra" }._(content).Data("sortkey", sortkey);

        protected static TD cellStr(string value)
            => (TD) new TD { class_ = "la" }._(value);

        protected static TD cellPrc(double value, int decimalPlaces)
            => (TD) new TD { class_ = "ra" }._(("{0:0." + new string('0', decimalPlaces) + "}%").Fmt(value * 100)).Data("sortkey", value);

        protected static TD cellPrcDelta(double value, int decimalPlaces)
            => (TD) new TD { class_ = "ra" }._(value < 0 ? "−" : value > 0 ? "+" : " ", ("{0:0." + new string('0', decimalPlaces) + "}%").Fmt(value * 100).Replace("-", "")).Data("sortkey", value);

        protected static TD cellInt(int value, string class__ = null)
            => (TD) new TD { class_ = "ra " + class__ }._("{0:#,0}".Fmt(value)).Data("sortkey", value);

        protected static object makeSortableTable(TR header, IEnumerable<TR> rows)
        {
            return new TABLE { class_ = "sortable" }._(
                new THEAD(header),
                new TBODY(rows)
            );
        }

    }
}
