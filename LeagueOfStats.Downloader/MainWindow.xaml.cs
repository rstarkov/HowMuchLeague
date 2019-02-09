using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LeagueOfStats.GlobalData;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.Downloader
{
    public partial class MainWindow : Window
    {
        public static Dictionary<Region, ConsoleColor> Colors = new Dictionary<Region, ConsoleColor>
        {
            [Region.EUW] = ConsoleColor.Green,
            [Region.EUNE] = ConsoleColor.Red,
            [Region.NA] = ConsoleColor.Yellow,
            [Region.KR] = ConsoleColor.Magenta,
        };
        private Dictionary<ConsoleColor, Brush> _brushes = new Dictionary<ConsoleColor, Brush>
        {
            [ConsoleColor.Black] = Brushes.Black,
            [ConsoleColor.DarkBlue] = Brushes.DarkBlue,
            [ConsoleColor.DarkGreen] = Brushes.Green,
            [ConsoleColor.DarkCyan] = Brushes.DarkCyan,
            [ConsoleColor.DarkRed] = Brushes.DarkRed,
            [ConsoleColor.DarkMagenta] = Brushes.DarkMagenta,
            [ConsoleColor.DarkYellow] = Brushes.DarkOrange,
            [ConsoleColor.Gray] = Brushes.LightGray,
            [ConsoleColor.DarkGray] = Brushes.DarkGray,
            [ConsoleColor.Blue] = Brushes.Blue,
            [ConsoleColor.Green] = Brushes.Lime,
            [ConsoleColor.Cyan] = Brushes.Cyan,
            [ConsoleColor.Red] = Brushes.Red,
            [ConsoleColor.Magenta] = Brushes.Fuchsia,
            [ConsoleColor.Yellow] = Brushes.Yellow,
            [ConsoleColor.White] = Brushes.White,
        };

        public MainWindow()
        {
            InitializeComponent();
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            if (_workThread != null)
                return;
            _workThread = new Thread(WorkThread) { IsBackground = true };
            _workThread.Start();
        }
        private Thread _workThread;

        public void AddText(string value, ConsoleColor color)
        {
            cnvConsole.AddText(value, _brushes[color]);
        }

        private void WorkThread()
        {
            Console.SetOut(new ConsoleToWpfWriter(this));

            var args = Environment.GetCommandLineArgs().Subarray(1);
            if (args[0] == "download")
            {
                var txts = new[] { txtApiKey1, txtApiKey2, txtApiKey3 }; // hardcoded to 3 because well... the rest of this code is also throwaway-quality
                var lastKeys = new[] { "", "", "" };
                try { lastKeys = File.ReadAllLines("Downloader.LastApiKeys.txt"); }
                catch { }
                var apiKeys = lastKeys.Zip(txts, (initialKey, txt) => new ApiKeyWithPrompt(initialKey, txt, this)).ToArray();
                DownloadMatches(dataPath: args[1], version: args[2], queueId: args[3], apiKeys: apiKeys);
            }
            else if (args[0] == "download-ids")
                DownloadIds(apiKey: new ApiKeyWithPrompt(args[1], txtApiKey1, this), dataPath: args[2], idFilePath: args[4]);
            else if (args[0] == "merge-ids")
                MaintenanceUtil.MergeMatches(outputPath: args[1], searchPath: args[2], mergeJsons: false);
            else if (args[0] == "merge-all")
                MaintenanceUtil.MergeMatches(outputPath: args[1], searchPath: args[2], mergeJsons: true);
            else
                Console.WriteLine("Unknown command");

            Console.WriteLine("Work thread terminated.");
        }

        public void SaveApiKeys()
        {
            File.WriteAllLines("Downloader.LastApiKeys.txt", new[] { txtApiKey1, txtApiKey2, txtApiKey3 }.Select(txt => txt.Text));
        }

        private void MergeIds(string region, string outputFile, string[] inputFiles)
        {
            var output = new MatchIdContainer(outputFile, EnumStrong.Parse<Region>(region));
            var files = new[] { outputFile }.Concat(inputFiles).Select(file => new { file, count = new CountResult() }).ToList();
            var ids = files.SelectMany(f => new MatchIdContainer(f.file).ReadItems().PassthroughCount(f.count)).ToHashSet();
            foreach (var f in files)
                Console.WriteLine($"Read input {f.file}: {f.count.Count:#,0} items");
            File.Delete(outputFile);
            output.AppendItems(ids.Order(), LosChunkFormat.LZ4HC);
            output.Rewrite();
        }

        private void DownloadMatches(string dataPath, string version, string queueId, ApiKeyWrapper[] apiKeys)
        {
            using (var p = Process.GetCurrentProcess())
                p.PriorityClass = ProcessPriorityClass.Idle;
            var regionLimits = new Dictionary<Region, (long initial, long range)>
            {
                [Region.EUW] = ((3_582_500_000L + 3_587_650_000) / 2, 500_000),
                [Region.EUNE] = ((1_939_500_000L + 1_942_550_000) / 2, 300_000),
                [Region.KR] = ((3_159_900_000L + 3_163_700_000) / 2, 300_000),
                [Region.NA] = ((2_751_200_000L + 2_754_450_000) / 2, 300_000),
            };

            Console.WriteLine("Initialising data store ...");
            DataStore.Initialise(dataPath, "");
            Console.WriteLine("    ... done.");

            var downloaders = new List<Downloader>();
            foreach (var region in regionLimits.Keys)
                downloaders.Add(new Downloader(apiKeys, region, version == "" ? null : version, queueId == "" ? (int?) null : int.Parse(queueId), regionLimits[region].initial, regionLimits[region].range));
            Console.WriteLine();
            foreach (var dl in downloaders) // separate step because the constructor prints some stats when it finishes
                dl.DownloadForever(background: true);

            while (true)
                Thread.Sleep(9999);
        }

        private void DownloadIds(ApiKeyWrapper apiKey, string dataPath, string idFilePath)
        {
            var region = EnumStrong.Parse<Region>(Path.GetFileName(idFilePath).Split('-')[0]);
            Console.WriteLine($"Initialising...");
            DataStore.Initialise(dataPath, "");
            Console.WriteLine($"Downloading...");
            var downloader = new MatchDownloader(apiKey, region);
            downloader.OnEveryResponse = (_, __) => { };
            var ids = File.ReadAllLines(idFilePath).Select(l => l.Trim()).Where(l => long.TryParse(l, out _)).Select(l => long.Parse(l)).ToList();
            foreach (var matchId in ids)
            {
                var dl = downloader.DownloadMatch(matchId);
                if (dl.result == MatchDownloadResult.NonExistent)
                {
                    Console.WriteLine($"{matchId:#,0}: non-existent");
                    DataStore.AddNonExistentMatch(region, matchId);
                }
                else if (dl.result == MatchDownloadResult.Failed)
                    Console.WriteLine($"Download failed: {matchId}");
                else if (dl.result == MatchDownloadResult.OK)
                {
                    var info = DataStore.AddMatch(region, dl.json);
                    Console.WriteLine($"{matchId:#,0}: queue {info.QueueId}");
                }
                else
                    throw new Exception();
            }
        }
    }

    class ApiKeyWithPrompt : ApiKeyWrapper
    {
        private string _apiKey;
        private TextBox _txt;
        private Window _window;
        private bool _flashing = false;

        public ApiKeyWithPrompt(string initialApiKey, TextBox txt, MainWindow window)
        {
            _apiKey = initialApiKey;
            _window = window;
            _txt = txt;
            _txt.TextChanged += delegate { _apiKey = _txt.Text.Trim(); _txt.Foreground = Brushes.MediumBlue; window.SaveApiKeys(); };
            _txt.Dispatcher.Invoke(() => { _txt.Text = initialApiKey; });
        }

        public override string GetApiKey() => _apiKey;

        public override void ReportValid(string keyUsed)
        {
            if (keyUsed != _apiKey)
                return;
            _txt.Dispatcher.Invoke(() =>
            {
                _txt.Foreground = Brushes.Green;
                if (_flashing)
                {
                    _flashing = false;
                    LosWinAPI.FlashTaskbarStop(new WindowInteropHelper(_window).Handle);
                }
            });
        }

        public override void ReportInvalid(string keyUsed)
        {
            if (keyUsed != _apiKey)
                return;
            _txt.Dispatcher.Invoke(() =>
            {
                _txt.Foreground = Brushes.Red;
                LosWinAPI.FlashTaskbarIcon(new WindowInteropHelper(_window).Handle, true);
                _flashing = true;
            });
        }
    }

    class ConsoleToWpfWriter : TextWriter
    {
        private MainWindow _window;

        public ConsoleToWpfWriter(MainWindow window)
        {
            _window = window;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\r')
                return;
            Write(new string(value, 1));
        }

        public override void WriteLine(string value)
        {
            Write(value + "\n");
        }

        public override void Write(string value)
        {
            var color = Console.ForegroundColor;
            try { _window.Dispatcher.Invoke(() => _window.AddText(value, color)); }
            catch (TaskCanceledException) { } // occurs on shutdown
        }
    }
}

