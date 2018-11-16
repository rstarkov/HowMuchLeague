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
using RT.Util.Consoles;
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
                var apiKeys = args.Subarray(4).Zip(txts, (key, txt) => new ApiKeyWithPrompt(key, txt, this)).ToArray();
                DownloadMatches(dataPath: args[1], version: args[2], queueId: args[3], apiKeys: apiKeys);
            }
            else if (args[0] == "download-ids")
                DownloadIds(apiKey: new ApiKeyWithPrompt(args[1], txtApiKey1, this), dataPath: args[2], idFilePath: args[4]);
            else if (args[0] == "merge-ids")
                MergeMatches(outputPath: args[1], searchPath: args[2], mergeJsons: false);
            else if (args[0] == "merge-all")
                MergeMatches(outputPath: args[1], searchPath: args[2], mergeJsons: true);
            else
                Console.WriteLine("Unknown command");

            Console.WriteLine("Work thread terminated.");
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
                dl.DownloadForever();

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

        private void MergeMatches(string outputPath, string searchPath, bool mergeJsons)
        {
            if (Directory.Exists(outputPath))
            {
                Console.WriteLine("This command requires the output directory not to exist; it will be created.");
                return;
            }
            Directory.CreateDirectory(outputPath);
            MergeDataStores.MergePreVer(outputPath, searchPath, mergeJsons);
        }

        private void RewriteBasicInfos(string dataPath)
        {
            DataStore.Initialise(dataPath, "");
            foreach (var region in DataStore.LosMatchJsons.Keys)
            {
                var existing = new HashSet<long>();
                var countRead = new CountThread(10000);
                countRead.OnInterval = count => { Console.Write($"R:{count:#,0} ({countRead.Rate:#,0}/s)  "); };
                var countWrite = new CountThread(10000);
                countWrite.OnInterval = count => { Console.Write($"W:{count:#,0} ({countWrite.Rate:#,0}/s)  "); };
                var matchInfos = DataStore.LosMatchJsons[region].Values
                    .SelectMany(x => x.Values)
                    .SelectMany(container => container.ReadItems())
                    .Select(json => new BasicMatchInfo(json))
                    .PassthroughCount(countRead.Count)
                    .OrderBy(m => m.MatchId)
                    .Where(m => existing.Add(m.MatchId))
                    .PassthroughCount(countWrite.Count);
                if (File.Exists(DataStore.LosMatchInfos[region].FileName))
                    DataStore.LosMatchInfos[region].Rewrite(_ => matchInfos);
                else
                    DataStore.LosMatchInfos[region].AppendItems(matchInfos, LosChunkFormat.LZ4HC);
                countRead.Stop();
                countWrite.Stop();
            }
        }

        private void GenRedownloadList(string dataPath)
        {
            DataStore.Initialise(dataPath, "");
            foreach (var region in DataStore.LosMatchJsons.Keys)
            {
                var minId = DataStore.LosMatchInfos[region].ReadItems().Where(m => m.GameCreationDate >= new DateTime(2018, 3, 1)).Min(m => m.MatchId);
                File.WriteAllLines($"redo-{region}.txt", DataStore.NonexistentMatchIds[region].Where(id => id > minId).Distinct().Order().Select(id => id.ToString()));
            }
        }

        private void RecheckNonexistentIds(string dataPath, ApiKeyWrapper[] apiKeys)
        {
            Console.WriteLine($"Initialising...");
            DataStore.Initialise(dataPath, "");
            Console.WriteLine($"Downloading...");
            var threads = new List<Thread>();
            foreach (var region in DataStore.NonexistentMatchIds.Keys)
            {
                var t = new Thread(() => { RecheckNonexistentIdsRegion(region, apiKeys); });
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads)
                t.Join();
        }
        private void RecheckNonexistentIdsRegion(Region region, ApiKeyWrapper[] apiKeys)
        {
            var path = @"P:\LeagueOfStats\LeagueOfStats\Builds\";
            var doneFile = Path.Combine(path, $"redone-{region}.txt");
            long maxDoneId = 0;
            int hits = 0;
            if (File.Exists(doneFile))
                foreach (var line in File.ReadLines(doneFile).Select(s => s.Trim()).Where(s => s != ""))
                {
                    if (line.StartsWith("hits:"))
                        hits = int.Parse(line.Substring("hits:".Length));
                    else
                        maxDoneId = Math.Max(maxDoneId, long.Parse(line));
                }
            var idsToProcess = File.ReadAllLines(Path.Combine(path, $"redo-{region}.txt")).Select(l => long.Parse(l)).Where(id => id > maxDoneId).ToList();
            var downloaders = apiKeys.Select(apiKey => new MatchDownloader(apiKey, region) { OnEveryResponse = (_, __) => { } }).ToList();
            var nextDownloader = 0;
            int remaining = idsToProcess.Count;
            foreach (var matchId in idsToProcess)
            {
                again:;
                var dl = downloaders[nextDownloader].DownloadMatch(matchId);
                nextDownloader = (nextDownloader + 1) % downloaders.Count;
                if (dl.result == MatchDownloadResult.BackOff)
                {
                    Thread.Sleep(Rnd.Next(500, 1500));
                    goto again;
                }
                remaining--;
                if (dl.result == MatchDownloadResult.NonExistent)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"{region}:{remaining:#,0}  ");
                    //DataStore.AddNonExistentMatch(region, matchId); - it's already in there, as that's how we built the list for rechecking
                }
                else if (dl.result == MatchDownloadResult.Failed)
                    Console.WriteLine($"Download failed: {matchId}");
                else if (dl.result == MatchDownloadResult.OK)
                {
                    hits++;
                    File.AppendAllLines(doneFile, new[] { $"hits:{hits}" });
                    DataStore.AddMatch(region, dl.json);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write($"{region}:{remaining:#,0}:{hits:#,0}  ");
                }
                else
                    throw new Exception();
                File.AppendAllLines(doneFile, new[] { matchId.ToString() });
            }
        }
    }

    class ApiKeyWithPrompt : ApiKeyWrapper
    {
        private string _apiKey;
        private TextBox _txt;
        private Window _window;
        private bool _flashing = false;

        public ApiKeyWithPrompt(string initialApiKey, TextBox txt, Window window)
        {
            _apiKey = initialApiKey;
            _window = window;
            _txt = txt;
            _txt.TextChanged += delegate { _apiKey = _txt.Text.Trim(); _txt.Foreground = Brushes.MediumBlue; };
            _txt.Dispatcher.Invoke(() => { _txt.Text = initialApiKey; });
        }

        public override string GetApiKey() => _apiKey;

        public override void ReportValid(string keyUsed)
        {
            if (keyUsed != _apiKey)
                return;
            _txt.Dispatcher.Invoke(() => { _txt.Foreground = Brushes.Green; });
            if (_flashing)
            {
                _flashing = false;
                LosWinAPI.FlashTaskbarStop(new WindowInteropHelper(_window).Handle);
            }
        }

        public override void ReportInvalid(string keyUsed)
        {
            if (keyUsed != _apiKey)
                return;
            _txt.Dispatcher.Invoke(() => { _txt.Foreground = Brushes.Red; });
            LosWinAPI.FlashTaskbarIcon(new WindowInteropHelper(_window).Handle, true);
            _flashing = true;
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

