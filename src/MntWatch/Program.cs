using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace MntWatch;

internal static class Program
{
    private const string MutexName = @"Local\MntWatch-8c41d2e7";
    private const string ShowEventName = @"Local\MntWatch-8c41d2e7-show";

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (Has(args, "--self-test")) return WithConsole(() => SelfTest.Run(Option(args, "--distro")));
        if (Has(args, "--run")) return WithConsole(() => RunInConsole(args));
        if (Has(args, "--distros")) return WithConsole(ListDistros);
        if (Has(args, "--export-icons")) return WithConsole(() =>
        {
            Icons.Export(Option(args, "--export-icons") ?? ".");
            return 0;
        });
        if (Has(args, "--help") || Has(args, "-h") || Has(args, "/?")) return WithConsole(Help);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!firstInstance)
        {
            // Already running: pop its menu instead of starting a second copy.
            if (!Has(args, "--tray")) showEvent.Set();
            return 0;
        }

        var app = new TrayApp(firstLaunchHint: !Has(args, "--tray"));
        var listener = new Thread(() =>
        {
            try
            {
                while (showEvent.WaitOne()) app.ShowFromOtherInstance();
            }
            catch (ObjectDisposedException)
            {
            }
        }) { IsBackground = true };
        listener.Start();

        Application.Run(app);
        return 0;
    }

    /// <summary>--run FOLDER [FOLDER...] [--distro NAME] [--verbose]: bridge folders until Ctrl+C, printing what happens.</summary>
    private static int RunInConsole(string[] args)
    {
        var folders = new List<string>();
        for (int i = Array.FindIndex(args, a => a.Equals("--run", StringComparison.OrdinalIgnoreCase)) + 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--")) break;
            folders.Add(args[i]);
        }
        if (folders.Count == 0)
        {
            Console.Error.WriteLine("Usage: MntWatch.exe --run <folder> [<folder>...] [--distro <name>] [--verbose]");
            return 2;
        }
        bool verbose = Has(args, "--verbose") || Has(args, "-v");
        using var done = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            done.Set();
        };
        using (var session = new Session(Option(args, "--distro"), folders, startWsl: true))
        {
            session.Log += m => Console.WriteLine(m);
            if (verbose) session.Nudged += (f, n, ms) => Console.WriteLine($"  {n} path(s) nudged in {f.LinuxPath} ({ms} ms)");
            session.Start();
            Console.WriteLine("Press Ctrl+C to stop (folders are unmounted again).");
            done.Wait();
            Console.WriteLine("Stopping...");
        }
        return 0;
    }

    private static int ListDistros()
    {
        var installed = Wsl.Installed();
        if (installed == null)
        {
            Console.WriteLine("wsl.exe couldn't be run. Is WSL installed?");
            return 1;
        }
        var running = Wsl.Running() ?? new List<string>();
        string def = Wsl.Default();
        foreach (string d in installed)
            Console.WriteLine($"{(d == def ? "*" : " ")} {d}{(running.Contains(d) ? "  (running)" : "")}");
        if (installed.Count == 0) Console.WriteLine("No WSL distros installed.");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine($@"MntWatch {Version}: makes Linux file watchers in WSL 2 see changes to Windows folders under /mnt/c.

  MntWatch.exe                      Start in the tray (pick folders from its menu)
  MntWatch.exe --tray               Start in the tray quietly (used for starting with Windows)
  MntWatch.exe --run <folder>...    Bridge folders from this console until Ctrl+C. Options: --distro <name>, --verbose
  MntWatch.exe --self-test          Show the problem, then fix it: edits a temp folder from Windows and checks
                                    whether Linux inotify hears it, before and with MntWatch. Option: --distro <name>
  MntWatch.exe --distros            List WSL distros, the default (*) and which are running

Settings: {Settings.DefaultFilePath}
Log: {Settings.LogPath}");
        return 0;
    }

    /// <summary>Runs a command-line mode, printing to the console the exe was started from.</summary>
    private static int WithConsole(Func<int> run)
    {
        Native.AttachConsole(Native.AttachParentProcess);
        try
        {
            int code = run();
            Console.Out.Flush();
            return code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static bool Has(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Option(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
