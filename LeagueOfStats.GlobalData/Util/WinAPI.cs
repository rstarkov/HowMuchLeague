using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LeagueOfStats.GlobalData
{
    public class LosWinAPI
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public int dwTimeout;
        }

        private const uint FLASHW_ALL = 3;
        private const uint FLASHW_TIMER = 4;

        private static bool FlashWindow(IntPtr hWnd, uint flags)
        {
            FLASHWINFO fInfo = new FLASHWINFO();

            fInfo.cbSize = Convert.ToUInt32(Marshal.SizeOf(fInfo));
            fInfo.hwnd = hWnd;
            fInfo.dwFlags = flags;
            fInfo.uCount = uint.MaxValue;
            fInfo.dwTimeout = 0;

            return FlashWindowEx(ref fInfo);
        }

        public static void FlashConsoleTaskbarIcon(bool blinky)
        {
            FlashWindow(Process.GetCurrentProcess().MainWindowHandle, FLASHW_ALL | (blinky ? FLASHW_TIMER : 0));
        }

        public static void FlashConsoleStop()
        {
            FlashWindow(Process.GetCurrentProcess().MainWindowHandle, 0);
        }
    }
}
