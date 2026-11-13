using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace MntWatch;

/// <summary>
/// The helper shell script running as root inside one distro, fed over stdin. Mounts it makes are undone by the
/// helper itself when stdin closes, so a crash or kill of MntWatch never leaves a folder overmounted.
/// </summary>
internal sealed class Bridge : IDisposable
{
    // wsl.exe passes these words to sh unchanged; no quotes, so Windows command-line parsing can't mangle them.
    // The script arrives on stdin, prefixed by its length so the protocol lines after it stay unread.
    // .NET Framework puts a UTF-8 BOM in front of a redirected stdin when the console code page is 65001 (what a
    // WSL terminal gives Windows programs), so the length line is reduced to its digits before it is used.
    internal const string Bootstrap =
        "read -r n; n=$(printf %s $n|tr -dc 0-9); f=/tmp/mntwatch-helper.$$.sh; dd bs=1 count=$n of=$f 2>/dev/null; exec sh $f";

    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly object _writeLock = new();
    private readonly object _replyLock = new();
    private readonly Queue<string> _replies = new();
    private readonly List<DrvfsMount> _drvfs = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private long _seq;

    public string Distro { get; }
    public string Kernel { get; private set; }
    public IReadOnlyList<DrvfsMount> DrvfsMounts => _drvfs;
    public bool HasExited => _process.HasExited;

    /// <summary>Raised on the reader thread for every ACK: sequence number and how many paths were nudged.</summary>
    public event Action<long, int> Acked;
    public event Action Exited;

    private Bridge(string distro, Process process, StreamWriter stdin)
    {
        Distro = distro;
        _process = process;
        _stdin = stdin;
    }

    public static string HelperScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MntWatch.helper.sh");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // A CRLF checkout would break sh; the script must reach Linux with LF line endings.
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>
    /// Runs a shell script as root in the distro with stdin, stdout and stderr redirected. The script is sent
    /// first on stdin; whatever is written to stdin afterwards is the script's own input.
    /// </summary>
    public static Process StartScript(string distro, string script, out StreamWriter stdin, byte[] stdinPrefix = null)
    {
        var args = new StringBuilder();
        if (!string.IsNullOrEmpty(distro)) args.Append("--distribution ").Append(distro).Append(' ');
        args.Append("--user root --exec sh -c \"").Append(Bootstrap).Append('"');
        var psi = new ProcessStartInfo(Wsl.Exe, args.ToString())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("wsl.exe didn't start.");
        process.EnableRaisingEvents = true;
        stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
        // Tests use this to simulate the BOM a UTF-8 console code page puts in front of everything else.
        if (stdinPrefix != null) stdin.BaseStream.Write(stdinPrefix, 0, stdinPrefix.Length);
        byte[] bytes = Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n"));
        stdin.Write(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        stdin.Flush();
        stdin.BaseStream.Write(bytes, 0, bytes.Length);
        stdin.BaseStream.Flush();
        return process;
    }

    /// <summary>Starts the helper and waits until it has listed the drvfs mounts. Throws with the reason on failure.</summary>
    public static Bridge Start(string distro, int timeoutMs = 20000)
    {
        var process = StartScript(distro, HelperScript(), out var stdin);
        var bridge = new Bridge(distro, process, stdin);
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) lock (errors) errors.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        process.Exited += (_, _) => bridge.Exited?.Invoke();
        new Thread(bridge.ReadLoop) { IsBackground = true, Name = "MntWatch helper reader" }.Start();

        if (!bridge._ready.Wait(timeoutMs) || process.HasExited)
        {
            string why;
            lock (errors) why = Wsl.Decode(Encoding.UTF8.GetBytes(errors.ToString())).Trim();
            bridge.Dispose();
            throw new InvalidOperationException(why.Length > 0 ? why : "The helper didn't answer from WSL in time.");
        }
        return bridge;
    }

    private void ReadLoop()
    {
        try
        {
            string line;
            while ((line = _process.StandardOutput.ReadLine()) != null)
            {
                var f = line.Split('\t');
                switch (f[0])
                {
                    case "HELLO":
                        Kernel = f.Length > 2 ? f[2] : "";
                        break;
                    case "DRVFS" when f.Length >= 3:
                        _drvfs.Add(Drvfs.Parse(f[1], f[2]));
                        break;
                    case "READY":
                        _ready.Set();
                        break;
                    case "ACK" when f.Length >= 3:
                        if (long.TryParse(f[1], out long seq) && int.TryParse(f[2], out int count)) Acked?.Invoke(seq, count);
                        break;
                    default:
                        lock (_replyLock)
                        {
                            _replies.Enqueue(line);
                            Monitor.PulseAll(_replyLock);
                        }
                        break;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        lock (_replyLock) Monitor.PulseAll(_replyLock);
    }

    /// <summary>Overmounts a folder; returns null on success, otherwise the error mount printed.</summary>
    public string Mount(string target, string windowsPath, string options)
    {
        Send($"MOUNT\t{target}\t{windowsPath}\t{options}");
        string reply = WaitReply(r => r.StartsWith("OK\tMOUNT\t") || r.StartsWith("ERR\tMOUNT\t"), 15000);
        if (reply == null) return "No answer from the helper.";
        if (reply.StartsWith("OK")) return null;
        var f = reply.Split('\t');
        return f.Length > 3 ? f[3].Trim() : "mount failed";
    }

    /// <summary>Unmounts; true when it had to detach lazily because a process still uses the folder.</summary>
    public bool Unmount(string target)
    {
        Send($"UMOUNT\t{target}");
        string reply = WaitReply(r => r.StartsWith("OK\tUMOUNT\t"), 10000);
        return reply != null && reply.EndsWith("\tlazy");
    }

    /// <summary>Queues the paths and flushes them; returns the sequence number the ACK will carry.</summary>
    public long Nudge(IReadOnlyCollection<string> linuxPaths)
    {
        long seq = Interlocked.Increment(ref _seq);
        var sb = new StringBuilder();
        foreach (string p in linuxPaths) sb.Append("N\t").Append(p).Append('\n');
        sb.Append("F\t").Append(seq).Append('\n');
        SendRaw(sb.ToString());
        return seq;
    }

    public long Sweep(string target)
    {
        long seq = Interlocked.Increment(ref _seq);
        Send($"SWEEP\t{target}\t{seq}");
        return seq;
    }

    /// <summary>The size Linux currently reports for a path, or -1. Used by the self-test.</summary>
    public long LinuxSize(string linuxPath)
    {
        Send($"STAT\t{linuxPath}");
        string reply = WaitReply(r => r.StartsWith("STAT\t"), 10000);
        return reply != null && long.TryParse(reply.Substring(5), out long size) ? size : -1;
    }

    private void Send(string line) => SendRaw(line + "\n");

    private void SendRaw(string text)
    {
        lock (_writeLock)
        {
            try
            {
                _stdin.Write(text);
                _stdin.Flush();
            }
            catch (IOException)
            {
                // The helper is gone; Exited reports it.
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private string WaitReply(Func<string, bool> match, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        lock (_replyLock)
        {
            while (true)
            {
                while (_replies.Count > 0)
                {
                    string r = _replies.Dequeue();
                    if (match(r)) return r;
                }
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || _process.HasExited) return null;
                Monitor.Wait(_replyLock, left);
            }
        }
    }

    public void Dispose()
    {
        // Closing stdin makes the helper unmount everything and exit.
        lock (_writeLock)
        {
            try { _stdin.Close(); } catch (IOException) { }
        }
        if (!_process.WaitForExit(8000))
        {
            try { _process.Kill(); } catch (InvalidOperationException) { }
        }
        _process.Dispose();
    }
}
