using System;
using System.Runtime.InteropServices;
using System.Windows;
using RT.Util;

namespace LeagueOfStats.Downloader
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static Settings Settings;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            SettingsUtil.LoadSettings(out Settings);

            // We must build as a console app and hide the console on startup, because all components print colored text to the console.
            // It's possible to intercept the text without a console attached, but reading Console.ForegroundColor works only in "true" console apps
            ShowWindow(GetConsoleWindow(), 0);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Settings.SaveQuiet();
            base.OnExit(e);
        }
    }
}
