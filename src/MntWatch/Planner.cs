using System;
using System.Collections.Generic;

namespace MntWatch;

internal enum ChangeKind
{
    Changed,
    Created,
    Deleted,
    Renamed,
}

/// <summary>What Windows says a file looks like now: enough to tell a real change from a repeated event.</summary>
internal readonly struct FileStamp : IEquatable<FileStamp>
{
    public readonly long LastWriteTicks;
    public readonly long Length;
    public readonly int Attributes;

    public FileStamp(long lastWriteTicks, long length, int attributes)
    {
        LastWriteTicks = lastWriteTicks;
        Length = length;
        Attributes = attributes;
    }

    public bool Equals(FileStamp other) =>
        LastWriteTicks == other.LastWriteTicks && Length == other.Length && Attributes == other.Attributes;

    public override bool Equals(object obj) => obj is FileStamp other && Equals(other);

    public override int GetHashCode() => LastWriteTicks.GetHashCode() ^ Length.GetHashCode() ^ Attributes;
}

/// <summary>
/// Decides which Linux paths to nudge for each change Windows reports in a watched folder. Kept free of I/O so
/// it can be unit tested: the caller passes a stamp lookup and collects the batch.
///
/// Every reported path is nudged, including .git and node_modules. While a folder is overmounted, Linux keeps
/// file attributes cached until they're nudged, so skipping a path would leave its size and mtime stale there.
/// The one thing that's skipped is a repeat Changed event for a file whose stamp hasn't moved since the last
/// nudge: Windows often reports one save two or three times, and Linux already has those attributes.
/// </summary>
internal sealed class Planner
{
    // Bounds memory on huge trees; forgetting a stamp only costs one extra nudge.
    private const int MaxRemembered = 50_000;

    private readonly string _windowsRoot;
    private readonly string _linuxRoot;
    private readonly Func<string, FileStamp?> _stamp;
    private readonly Dictionary<string, FileStamp> _sent = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _batch = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public Planner(string windowsRoot, string linuxRoot, Func<string, FileStamp?> stamp)
    {
        _windowsRoot = windowsRoot.TrimEnd('\\');
        _linuxRoot = linuxRoot.TrimEnd('/');
        if (_linuxRoot.Length == 0) _linuxRoot = "/";
        _stamp = stamp;
    }

    public int Pending => _order.Count;

    /// <summary>Set when Windows lost events (its buffer overflowed); the whole folder needs a sweep.</summary>
    public bool NeedsSweep { get; private set; }

    public string LinuxRoot => _linuxRoot;

    public void Add(ChangeKind kind, string windowsPath, string oldWindowsPath = null)
    {
        switch (kind)
        {
            case ChangeKind.Changed:
                var stamp = _stamp(windowsPath);
                if (stamp is FileStamp now)
                {
                    if (_sent.TryGetValue(windowsPath, out var last) && last.Equals(now)) return;
                    Remember(windowsPath, now);
                }
                Queue(windowsPath);
                break;
            case ChangeKind.Created:
                Queue(Parent(windowsPath));
                Queue(windowsPath);
                if (_stamp(windowsPath) is FileStamp created) Remember(windowsPath, created);
                break;
            case ChangeKind.Deleted:
                // The parent tells directory watchers to rescan; the path itself drops Linux's cached entry.
                Queue(Parent(windowsPath));
                Queue(windowsPath);
                _sent.Remove(windowsPath);
                break;
            case ChangeKind.Renamed:
                if (oldWindowsPath != null)
                {
                    Queue(Parent(oldWindowsPath));
                    Queue(oldWindowsPath);
                    _sent.Remove(oldWindowsPath);
                }
                Queue(Parent(windowsPath));
                Queue(windowsPath);
                if (_stamp(windowsPath) is FileStamp renamed) Remember(windowsPath, renamed);
                break;
        }
    }

    public void LostEvents()
    {
        NeedsSweep = true;
        _sent.Clear();
    }

    /// <summary>Returns the queued Linux paths in arrival order and empties the queue.</summary>
    public List<string> Take()
    {
        var result = new List<string>(_order);
        _order.Clear();
        _batch.Clear();
        NeedsSweep = false;
        return result;
    }

    /// <summary>C:\dev\app\src\a.ts -> /mnt/c/dev/app/src/a.ts, or null for paths outside the folder.</summary>
    public string ToLinux(string windowsPath)
    {
        if (windowsPath == null) return null;
        string p = windowsPath.TrimEnd('\\');
        if (p.Equals(_windowsRoot, StringComparison.OrdinalIgnoreCase)) return _linuxRoot;
        if (!p.StartsWith(_windowsRoot + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        string rel = p.Substring(_windowsRoot.Length + 1).Replace('\\', '/');
        return (_linuxRoot == "/" ? "" : _linuxRoot) + "/" + rel;
    }

    private string Parent(string windowsPath)
    {
        string p = windowsPath.TrimEnd('\\');
        int slash = p.LastIndexOf('\\');
        return slash < 0 ? _windowsRoot : p.Substring(0, slash);
    }

    private void Queue(string windowsPath)
    {
        string linux = ToLinux(windowsPath);
        // The helper reads one command per line, tab separated; such names can't be sent (and are very rare).
        if (linux == null || linux.IndexOfAny(new[] { '\n', '\r', '\t' }) >= 0) return;
        if (_batch.Add(linux)) _order.Add(linux);
    }

    private void Remember(string windowsPath, FileStamp stamp)
    {
        if (_sent.Count >= MaxRemembered) _sent.Clear();
        _sent[windowsPath] = stamp;
    }
}
