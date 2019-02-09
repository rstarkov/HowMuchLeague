using System.Collections.Generic;
using LeagueOfStats.GlobalData;
using RT.Util;
using RT.Util.Forms;

namespace LeagueOfStats.Downloader
{
    [Settings("LeagueOfStats.Downloader", SettingsKind.UserSpecific)]
    public class Settings : SettingsBase
    {
        public ManagedWindow.Settings MainWindowSettings = new ManagedWindow.Settings();
        public List<string> LastApiKeys = new List<string>();
        public Region DownloadedByIdRegion;
        public string DownloadedByIdSummoner;
        public string DownloadedByIdHmac;

        protected override void AfterLoad()
        {
            base.AfterLoad();
            while (LastApiKeys.Count < 3)
                LastApiKeys.Add("");
        }
    }
}
