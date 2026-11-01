using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MntWatch;

/// <summary>
/// --self-test: shows the bug, then the fix. A Linux inotify listener watches a temporary Windows folder while
/// Windows edits files in it; first on the plain /mnt/c mount (where it hears nothing), then with MntWatch.
/// </summary>
internal static class SelfTest
{
    // Uses whatever the distro already has: inotifywait, python3 (ctypes) or busybox inotifyd. Watches one folder,
    // prints "EV <what> <name>" per event and stops when stdin closes.
    private const string ListenerScript = @"rm -f ""$0""
IFS= read -r d
PY='
import ctypes, os, struct, sys
libc = ctypes.CDLL(None, use_errno=True)
fd = libc.inotify_init()
if fd < 0 or libc.inotify_add_watch(fd, sys.argv[1].encode(), 0x2 | 0x4 | 0x8 | 0x40 | 0x80 | 0x100 | 0x200) < 0:
    print(""FAIL inotify_add_watch errno"", ctypes.get_errno(), flush=True); sys.exit(1)
names = {0x2: ""MODIFY"", 0x4: ""ATTRIB"", 0x8: ""CLOSE_WRITE"", 0x40: ""MOVED_FROM"", 0x80: ""MOVED_TO"", 0x100: ""CREATE"", 0x200: ""DELETE""}
while True:
    b = os.read(fd, 65536); i = 0
    while i < len(b):
        w, m, c, l = struct.unpack_from(""iIII"", b, i)
        n = b[i + 16:i + 16 + l].rstrip(b""\0"").decode(errors=""replace""); i += 16 + l
        print(""EV"", ""|"".join(v for k, v in names.items() if m & k) or hex(m), n or ""."", flush=True)
'
if command -v inotifywait >/dev/null 2>&1; then
  echo ""LISTENER inotifywait""
  inotifywait -m -q --format 'EV %e %f' ""$d"" &
elif command -v python3 >/dev/null 2>&1; then
  echo ""LISTENER python3""
  python3 -u -c ""$PY"" ""$d"" &
elif busybox inotifyd --help >/dev/null 2>&1 || command -v inotifyd >/dev/null 2>&1; then
  echo ""LISTENER inotifyd""
  inotifyd - ""$d"":cwendmy | while IFS= read -r l; do echo ""EV $l""; done &
else
  echo ""NOLISTENER""
  exit 0
fi
P=$!
sleep 0.7
echo READY
cat > /dev/null
kill $P 2>/dev/null
pkill -P $P 2>/dev/null
exit 0
";

    private sealed class Listener : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _stdin;
        private readonly List<(long at, string line)> _events = new();
        private readonly ManualResetEventSlim _ready = new(false);
        public string Tool = "";

        public Listener(string distro, string linuxDir)
        {
            _process = Bridge.StartScript(distro, ListenerScript, out _stdin);
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.StartsWith("LISTENER ")) Tool = e.Data.Substring(9);
                else if (e.Data == "NOLISTENER") { Tool = null; _ready.Set(); }
                else if (e.Data == "READY") _ready.Set();
                else if (e.Data.StartsWith("EV ") || e.Data.StartsWith("FAIL"))
                    lock (_events) _events.Add((Stopwatch.GetTimestamp(), e.Data));
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _stdin.WriteLine(linuxDir);
            _stdin.Flush();
        }

        public bool WaitReady() => _ready.Wait(20000);

        public void Clear()
        {
            lock (_events) _events.Clear();
        }

        /// <summary>Waits for an event line containing the text; returns its timestamp, or null on timeout.</summary>
        public long? WaitFor(Func<string, bool> match, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (_events)
                {
                    foreach (var e in _events)
                        if (match(e.line)) return e.at;
                }
                Thread.Sleep(5);
            }
            return null;
        }

        public List<string> Lines()
        {
            lock (_events) return _events.Select(e => e.line).ToList();
        }

        public void Dispose()
        {
            try { _stdin.Close(); } catch (IOException) { }
            if (!_process.WaitForExit(5000))
            {
                try { _process.Kill(); } catch (InvalidOperationException) { }
            }
            _process.Dispose();
        }
    }

    public static int Run(string distroOption)
    {
        Console.WriteLine($"MntWatch {Program.Version} self-test");
        var installed = Wsl.Installed();
        if (installed == null || installed.Count == 0)
        {
            Console.WriteLine("No WSL distro is installed, so there's nothing to test.");
            return 2;
        }
        string distro = string.IsNullOrWhiteSpace(distroOption) ? Wsl.Default() ?? installed[0] : distroOption.Trim();
        if (!installed.Contains(distro, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"There's no distro called {distro}. Installed: {string.Join(", ", installed)}");
            return 2;
        }
        Console.WriteLine($"Distro: {distro} (started if it wasn't running)");

        string folder = Path.Combine(Path.GetTempPath(), "MntWatch-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "edited.txt");
        File.WriteAllText(file, "first line\n");
        int failures = 0;
        try
        {
            string linuxDir;
            using (var probe = Bridge.Start(distro))
            {
                var drive = Drvfs.FindDrive(probe.DrvfsMounts, Paths.Resolve(folder));
                if (drive == null)
                {
                    Console.WriteLine($"Drive {folder.Substring(0, 2)} isn't mounted in {distro}; can't test.");
                    return 2;
                }
                linuxDir = Drvfs.ToLinux(drive, Paths.Resolve(folder));
                Console.WriteLine($"Test folder: {folder}");
                Console.WriteLine($"   in Linux: {linuxDir} (drive mounted with cache={Hex(drive.Cache)}, kernel {probe.Kernel})");
            }

            // 1. The problem: the plain mount.
            Console.WriteLine();
            Console.WriteLine("1. Without MntWatch: a Linux watcher on that folder, then Windows edits a file in it.");
            using (var listener = new Listener(distro, linuxDir))
            {
                if (!listener.WaitReady() || listener.Tool == null)
                {
                    Console.WriteLine("   No inotify tool in this distro (looked for inotifywait, python3, busybox inotifyd).");
                    Console.WriteLine("   Install one, for example: sudo apt install inotify-tools");
                    return 2;
                }
                Console.WriteLine($"   Listening with {listener.Tool}.");
                File.AppendAllText(file, "edited from Windows\n");
                var heard = listener.WaitFor(_ => true, 2000);
                if (heard == null) Console.WriteLine("   Linux heard nothing in 2 s. That's the bug MntWatch works around.");
                else Console.WriteLine("   Linux did hear it here, so this setup may not need MntWatch: " + listener.Lines().First());
            }

            // 2. The fix.
            Console.WriteLine();
            Console.WriteLine("2. With MntWatch: the folder is overmounted with inode caching and Windows changes are forwarded.");
            using var session = new Session(distro, new[] { folder }, startWsl: true);
            var log = new List<string>();
            session.Log += m => { lock (log) log.Add(m); };
            session.Start();
            var wf = session.Folders[0];
            var wait = Stopwatch.StartNew();
            while (wf.State != FolderState.Active && wf.State != FolderState.Error && wait.ElapsedMilliseconds < 30000) Thread.Sleep(50);
            if (wf.State != FolderState.Active)
            {
                Console.WriteLine($"   FAIL: couldn't start: {wf.Message}");
                lock (log) foreach (string m in log) Console.WriteLine("   " + m);
                return 1;
            }
            Console.WriteLine($"   Mounted {wf.LinuxPath}.");

            using (var listener = new Listener(distro, linuxDir))
            {
                listener.WaitReady();
                listener.Clear();

                long t0 = Stopwatch.GetTimestamp();
                File.AppendAllText(file, "edited again from Windows\n");
                failures += Check(listener.WaitFor(l => l.Contains("edited.txt"), 3000), t0, "Windows edit of edited.txt");

                long windowsSize = new FileInfo(file).Length;
                long linuxSize = session.Bridge?.LinuxSize(wf.LinuxPath + "/edited.txt") ?? -1;
                if (linuxSize == windowsSize) Console.WriteLine($"   PASS  Linux sees the new size ({linuxSize} bytes)");
                else
                {
                    Console.WriteLine($"   FAIL  Linux sees {linuxSize} bytes, Windows {windowsSize}");
                    failures++;
                }

                t0 = Stopwatch.GetTimestamp();
                File.WriteAllText(Path.Combine(folder, "created.txt"), "new\n");
                failures += Check(listener.WaitFor(l => l.Contains("created.txt"), 3000), t0, "Windows created created.txt");

                listener.Clear();
                t0 = Stopwatch.GetTimestamp();
                File.Delete(Path.Combine(folder, "created.txt"));
                var del = listener.WaitFor(_ => true, 2000);
                if (del != null)
                    Console.WriteLine($"   info  Windows delete reached Linux as a folder change in {Ms(t0, del.Value)} ms ({listener.Lines().First()})");
                else
                    Console.WriteLine("   info  Windows delete wasn't heard; watchers notice it on their next rescan");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex.Message);
            failures++;
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "Self-test passed. The test folder was unmounted and deleted." : $"Self-test failed ({failures}).");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(long? heardAt, long startedAt, string what)
    {
        if (heardAt is long at)
        {
            Console.WriteLine($"   PASS  {what}: Linux heard it after {Ms(startedAt, at)} ms");
            return 0;
        }
        Console.WriteLine($"   FAIL  {what}: Linux heard nothing in 3 s");
        return 1;
    }

    private static long Ms(long from, long to) => (to - from) * 1000 / Stopwatch.Frequency;

    private static string Hex(int? v) => v is int i ? "0x" + i.ToString("x") : "default";
}
