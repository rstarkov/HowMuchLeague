using RT.Util;
using RT.Util.Forms;

namespace LeagueOfStats.Downloader
{
    [Settings("LeagueOfStats.Downloader", SettingsKind.UserSpecific)]
    public class Settings : SettingsBase
    {
        public ManagedWindow.Settings MainWindowSettings = new ManagedWindow.Settings();
    }
}
