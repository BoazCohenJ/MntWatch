using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace MntWatch;

internal enum FolderState
{
    WaitingForWsl,
    Active,
    Error,
    Paused,
}

/// <summary>One watched Windows folder: its watcher, its planner and what the tray shows about it.</summary>
internal sealed class WatchedFolder
{
    public readonly string WindowsPath;
    public FolderState State = FolderState.WaitingForWsl;
    public string Message = "";
    public string LinuxPath;
    public long Nudged;
    public int LastLatencyMs = -1;

    internal Planner Planner;
    internal FileSystemWatcher Watcher;
    internal Timer FlushTimer;
    internal readonly object Lock = new();

    public WatchedFolder(string windowsPath) => WindowsPath = windowsPath;
}

/// <summary>
/// Keeps a set of folders bridged into one distro. It never starts WSL itself: it waits until the distro is
/// running, then starts the helper, overmounts each folder and forwards changes. When the distro stops (wsl
/// --shutdown, a reboot), everything is torn down and it waits again.
/// </summary>
internal sealed class Session : IDisposable
{
    // Short enough to feel instant, long enough that one editor save (often 2-3 events) becomes one nudge.
    private const int FlushDelayMs = 8;
    private const int MaxBatch = 4000;
    private const int PollMs = 3000;

    private readonly string _distroSetting;
    private readonly List<WatchedFolder> _folders;
    private readonly bool _startWsl;
    private readonly Thread _supervisor;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Dictionary<long, (WatchedFolder folder, long started)> _inFlight = new();
    private volatile bool _stopping;
    private volatile bool _paused;
    private Bridge _bridge;

    public string Distro { get; private set; }
    public IReadOnlyList<WatchedFolder> Folders => _folders;
    public string Kernel => _bridge?.Kernel;

    public event Action Changed;
    public event Action<string> Log;

    /// <param name="startWsl">Start the distro if it isn't running (the command-line mode and the self-test do).</param>
    public Session(string distro, IEnumerable<string> folders, bool startWsl = false, bool paused = false)
    {
        _distroSetting = string.IsNullOrWhiteSpace(distro) ? null : distro.Trim();
        _folders = folders.Select(f => new WatchedFolder(f)).ToList();
        _startWsl = startWsl;
        _paused = paused;
        _supervisor = new Thread(Supervise) { IsBackground = true, Name = "MntWatch supervisor" };
    }

    public void Start() => _supervisor.Start();

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            _wake.Set();
        }
    }

    /// <summary>The bridge while active; the self-test uses it to read what Linux sees.</summary>
    internal Bridge Bridge => _bridge;

    private void Supervise()
    {
        while (!_stopping)
        {
            try
            {
                if (_paused)
                {
                    Detach("Paused", FolderState.Paused);
                }
                else if (_bridge == null || _bridge.HasExited)
                {
                    if (_bridge != null) Detach("WSL stopped; waiting for it to run again", FolderState.WaitingForWsl);
                    TryAttach();
                }
            }
            catch (Exception ex)
            {
                Say("Error: " + ex.Message);
            }
            _wake.Wait(PollMs);
            _wake.Reset();
        }
        Detach("Stopped", FolderState.Paused);
    }

    private void TryAttach()
    {
        string distro = _distroSetting ?? Wsl.Default();
        if (distro == null)
        {
            SetAll(FolderState.WaitingForWsl, "No WSL distro found");
            return;
        }
        Distro = distro;
        if (!_startWsl)
        {
            var running = Wsl.Running();
            if (running == null || !running.Contains(distro, StringComparer.OrdinalIgnoreCase))
            {
                SetAll(FolderState.WaitingForWsl, $"Waiting for {distro} to run");
                return;
            }
        }

        Bridge bridge;
        try
        {
            bridge = Bridge.Start(distro);
        }
        catch (Exception ex)
        {
            SetAll(FolderState.Error, $"Couldn't start the helper in {distro}: {ex.Message}");
            return;
        }
        bridge.Acked += OnAck;
        bridge.NamespacesChanged += n => Say(NamespaceMessage(n, distro, Elevation.IsElevated));
        bridge.Exited += () => _wake.Set();
        _bridge = bridge;
        Say($"Connected to {distro} (kernel {bridge.Kernel}).");

        foreach (var folder in _folders) Attach(folder, bridge);
        Changed?.Invoke();
    }

    private void Attach(WatchedFolder folder, Bridge bridge)
    {
        string path = Paths.Resolve(folder.WindowsPath);
        if (path == null || !Directory.Exists(path))
        {
            Set(folder, FolderState.Error, "Folder not found");
            return;
        }
        var drive = Drvfs.FindDrive(bridge.DrvfsMounts, path);
        if (drive == null)
        {
            Set(folder, FolderState.Error, $"Drive {path.Substring(0, 2)} isn't mounted in {bridge.Distro} (network and UNC paths aren't supported)");
            return;
        }
        if (!drive.CacheFlagsSupported)
        {
            Set(folder, FolderState.Error, $"The WSL kernel in {bridge.Distro} ({bridge.Kernel}) is too old for MntWatch. Run: wsl --update");
            return;
        }
        string target = Drvfs.ToLinux(drive, path);
        if (bridge.DrvfsMounts.Any(m => m.MountPoint == target && !m.IsDriveRoot))
        {
            // Left over from a run that was cut off before its helper could clean up.
            bridge.Unmount(target);
        }
        string error = bridge.Mount(target, path, Drvfs.OvermountOptions(drive));
        if (error != null)
        {
            Set(folder, FolderState.Error, "mount failed: " + error);
            return;
        }

        lock (folder.Lock)
        {
            folder.LinuxPath = target;
            folder.Planner = new Planner(path, target, Stamp);
            folder.FlushTimer = new Timer(_ => Flush(folder), null, Timeout.Infinite, Timeout.Infinite);
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                               NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.CreationTime,
            };
            w.Changed += (_, e) => OnEvent(folder, ChangeKind.Changed, e.FullPath, null);
            w.Created += (_, e) => OnEvent(folder, ChangeKind.Created, e.FullPath, null);
            w.Deleted += (_, e) => OnEvent(folder, ChangeKind.Deleted, e.FullPath, null);
            w.Renamed += (_, e) => OnEvent(folder, ChangeKind.Renamed, e.FullPath, e.OldFullPath);
            w.Error += (_, e) =>
            {
                Say($"{folder.WindowsPath}: Windows dropped change events ({e.GetException()?.Message}); refreshing the whole folder.");
                lock (folder.Lock) folder.Planner?.LostEvents();
                folder.FlushTimer?.Change(0, Timeout.Infinite);
            };
            w.EnableRaisingEvents = true;
            folder.Watcher = w;
        }
        // No sweep needed here: a fresh mount has nothing cached yet, so nothing on it can be stale.
        Set(folder, FolderState.Active, $"-> {target}");
        Say($"Watching {path} -> {target} ({bridge.Distro}). Restart Linux programs that were already running in it.");
    }

    private static FileStamp? Stamp(string windowsPath)
    {
        try
        {
            var info = new FileInfo(windowsPath);
            if (info.Exists) return new FileStamp(info.LastWriteTimeUtc.Ticks, info.Length, (int)info.Attributes);
            var dir = new DirectoryInfo(windowsPath);
            return dir.Exists ? new FileStamp(dir.LastWriteTimeUtc.Ticks, -1, (int)dir.Attributes) : null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            return null;
        }
    }

    private void OnEvent(WatchedFolder folder, ChangeKind kind, string path, string oldPath)
    {
        bool startTimer;
        lock (folder.Lock)
        {
            if (folder.Planner == null) return;
            int before = folder.Planner.Pending;
            folder.Planner.Add(kind, path, oldPath);
            startTimer = before == 0 && folder.Planner.Pending > 0;
            if (folder.Planner.Pending >= MaxBatch) startTimer = true;
        }
        if (startTimer) folder.FlushTimer?.Change(FlushDelayMs, Timeout.Infinite);
    }

    private void Flush(WatchedFolder folder)
    {
        List<string> paths;
        bool sweep;
        string root;
        lock (folder.Lock)
        {
            if (folder.Planner == null) return;
            sweep = folder.Planner.NeedsSweep;
            root = folder.Planner.LinuxRoot;
            paths = folder.Planner.Take();
        }
        var bridge = _bridge;
        if (bridge == null || (paths.Count == 0 && !sweep)) return;
        long started = Stopwatch.GetTimestamp();
        long seq = sweep ? bridge.Sweep(root) : bridge.Nudge(paths);
        lock (_inFlight) _inFlight[seq] = (folder, started);
        if (sweep && paths.Count > 0)
        {
            seq = bridge.Nudge(paths);
            lock (_inFlight) _inFlight[seq] = (folder, started);
        }
    }

    private void OnAck(long seq, int count)
    {
        (WatchedFolder folder, long started) entry;
        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(seq, out entry)) return;
            _inFlight.Remove(seq);
        }
        int ms = (int)((Stopwatch.GetTimestamp() - entry.started) * 1000 / Stopwatch.Frequency);
        entry.folder.LastLatencyMs = ms;
        Interlocked.Add(ref entry.folder.Nudged, count);
        Nudged?.Invoke(entry.folder, count, ms);
    }

    /// <summary>Raised after each batch reached Linux: folder, paths nudged, milliseconds from flush to done.</summary>
    public event Action<WatchedFolder, int, int> Nudged;

    private void Detach(string message, FolderState state)
    {
        foreach (var folder in _folders)
        {
            lock (folder.Lock)
            {
                folder.Watcher?.Dispose();
                folder.Watcher = null;
                folder.FlushTimer?.Dispose();
                folder.FlushTimer = null;
                folder.Planner = null;
            }
        }
        var bridge = _bridge;
        _bridge = null;
        if (bridge != null)
        {
            foreach (var folder in _folders.Where(f => f.LinuxPath != null && f.State == FolderState.Active))
            {
                if (!bridge.HasExited && bridge.Unmount(folder.LinuxPath))
                    Say($"{folder.LinuxPath} was still in use; it was detached and goes away once those programs exit.");
            }
            bridge.Dispose();
            Say("Disconnected from WSL.");
        }
        foreach (var folder in _folders)
        {
            if (folder.State != state || folder.Message != message) Set(folder, state, message, notify: false);
        }
        Changed?.Invoke();
    }

    private void SetAll(FolderState state, string message)
    {
        bool changed = false;
        foreach (var f in _folders)
        {
            if (f.State == state && f.Message == message) continue;
            f.State = state;
            f.Message = message;
            changed = true;
        }
        if (changed)
        {
            Say(message);
            Changed?.Invoke();
        }
    }

    private void Set(WatchedFolder folder, FolderState state, string message, bool notify = true)
    {
        folder.State = state;
        folder.Message = message;
        if (state == FolderState.Error) Say($"{folder.WindowsPath}: {message}");
        if (notify) Changed?.Invoke();
    }

    private void Say(string message) => Log?.Invoke(message);

    /// <summary>What to log when the helper starts or stops bridging WSL's other mount namespace (issue #2).</summary>
    internal static string NamespaceMessage(int count, string distro, bool elevated)
    {
        string other = elevated ? "started without administrator rights" : "started as administrator";
        if (count < 0)
            return $"Linux programs {other} run in a separate mount namespace in {distro}, and they won't see MntWatch's folders " +
                   "because nsenter isn't installed there (it comes with util-linux or busybox). Install it, or run MntWatch and your " +
                   "terminal at the same level.";
        if (count == 0) return $"No Linux programs {other} are running in {distro} any more.";
        return $"Also bridging the folders for Linux programs {other} in {distro} (WSL keeps a separate mount namespace for them).";
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        if (_supervisor.IsAlive) _supervisor.Join(15000);
    }
}
