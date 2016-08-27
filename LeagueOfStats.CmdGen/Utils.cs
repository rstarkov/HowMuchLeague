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
    }
}
