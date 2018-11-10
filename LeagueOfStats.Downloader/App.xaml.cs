using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace LeagueOfStats.Downloader
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            //ShowWindow(GetConsoleWindow(), 0);
        }
    }
}
