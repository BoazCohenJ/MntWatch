using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MntWatch;

/// <summary>One drvfs mount as the helper reported it from /proc/mounts.</summary>
internal sealed class DrvfsMount
{
    /// <summary>Linux mount point, for example /mnt/c.</summary>
    public string MountPoint;

    /// <summary>The Windows folder it shows, for example C:\ (a drive root for the automatic mounts).</summary>
    public string WindowsPath;

    /// <summary>The 9p cache mode, or null when the options don't say.</summary>
    public int? Cache;

    /// <summary>
    /// True when the kernel listed the cache mode as flags (cache=0x5). Kernels that only know named modes
    /// (cache=mmap, loose) can't be given the metadata bit on its own.
    /// </summary>
    public bool CacheFlagsSupported;

    /// <summary>The drvfs settings worth copying to an overmount (uid, gid, metadata, umask, fmask, dmask, case).</summary>
    public readonly List<string> DrvfsOptions = new();

    /// <summary>Generic mount flags worth copying (ro, noatime, relatime).</summary>
    public readonly List<string> Flags = new();

    public bool IsDriveRoot => WindowsPath != null && WindowsPath.Length == 3 && WindowsPath[1] == ':' && WindowsPath[2] == '\\';
}

/// <summary>
/// Turns a drive's drvfs mount into the overmount MntWatch puts on a watched folder. The only change is the 9p
/// cache mode: adding the metadata bit (0x2) makes the kernel keep one inode per file, which is what inotify
/// needs to deliver events at all. Everything else (owner, permissions, case handling) is copied so files look
/// exactly as they did before.
/// </summary>
internal static class Drvfs
{
    public const int CacheMetadata = 0x2;
    public const int DefaultCache = 0x5;
    // Cache flags that skip revalidation (loose) or need fscache; never copied because Windows edits would go unseen.
    private const int UnsafeCacheBits = 0x8 | 0x80;

    private static readonly string[] CopiedDrvfsKeys = { "uid", "gid", "metadata", "umask", "fmask", "dmask", "case" };
    private static readonly string[] CopiedFlags = { "ro", "rw", "noatime", "relatime", "nodev", "nosuid", "noexec" };

    /// <summary>Parses the mount point and options fields of a /proc/mounts line (as the helper's DRVFS lines carry them).</summary>
    public static DrvfsMount Parse(string mountPoint, string options)
    {
        var m = new DrvfsMount { MountPoint = UnescapeProcMounts(mountPoint) };
        // 9p options are comma separated; drvfs packs its own settings into aname, separated by semicolons:
        // rw,noatime,aname=drvfs;path=C:\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,...
        foreach (string part in (options ?? "").Split(','))
        {
            if (part.StartsWith("aname=", StringComparison.Ordinal))
            {
                foreach (string item in part.Substring(6).Split(';'))
                {
                    if (item.StartsWith("path=", StringComparison.Ordinal)) m.WindowsPath = UnescapeProcMounts(item.Substring(5));
                    else if (CopiedDrvfsKeys.Contains(Key(item))) m.DrvfsOptions.Add(item);
                }
            }
            else if (part.StartsWith("cache=", StringComparison.Ordinal))
            {
                string value = part.Substring(6);
                m.Cache = ParseCache(value);
                m.CacheFlagsSupported = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            }
            else if (CopiedFlags.Contains(part))
            {
                m.Flags.Add(part);
            }
            else if (CopiedDrvfsKeys.Contains(Key(part)))
            {
                // Older WSL builds list drvfs settings as plain options.
                m.DrvfsOptions.Add(part);
            }
        }
        return m;
    }

    private static string Key(string item)
    {
        int eq = item.IndexOf('=');
        return eq < 0 ? item : item.Substring(0, eq);
    }

    public static int? ParseCache(string value)
    {
        value = value.Trim();
        switch (value)
        {
            case "none": return 0x0;
            case "loose": return 0xf;
            case "fscache": return 0x80;
            case "mmap": return 0x1 | 0x4;
        }
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex))
            return hex;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec) ? dec : null;
    }

    /// <summary>The cache mode for the overmount: the drive's own mode plus the metadata bit.</summary>
    public static int OvermountCache(int? driveCache) => ((driveCache ?? DefaultCache) & ~UnsafeCacheBits) | CacheMetadata;

    /// <summary>The -o string passed to mount -t drvfs for a folder of this drive.</summary>
    public static string OvermountOptions(DrvfsMount drive)
    {
        var parts = new List<string>(drive.Flags);
        parts.AddRange(drive.DrvfsOptions);
        parts.Add("cache=0x" + OvermountCache(drive.Cache).ToString("x", CultureInfo.InvariantCulture));
        return string.Join(",", parts);
    }

    /// <summary>The automatic mount for a folder's drive, or null when the drive isn't mounted in this distro.</summary>
    public static DrvfsMount FindDrive(IEnumerable<DrvfsMount> mounts, string windowsFolder)
    {
        if (windowsFolder == null || windowsFolder.Length < 2 || windowsFolder[1] != ':') return null;
        char drive = char.ToUpperInvariant(windowsFolder[0]);
        return mounts.FirstOrDefault(m => m.IsDriveRoot && char.ToUpperInvariant(m.WindowsPath[0]) == drive);
    }

    /// <summary>Maps C:\dev\app to /mnt/c/dev/app through the drive's mount.</summary>
    public static string ToLinux(DrvfsMount drive, string windowsPath)
    {
        string rest = windowsPath.Length > 3 ? windowsPath.Substring(3).TrimEnd('\\') : "";
        string root = drive.MountPoint.TrimEnd('/');
        return rest.Length == 0 ? (root.Length == 0 ? "/" : root) : root + "/" + rest.Replace('\\', '/');
    }

    /// <summary>/proc/mounts writes space, tab, newline and backslash as \040, \011, \012 and \134.</summary>
    public static string UnescapeProcMounts(string s)
    {
        if (s == null || s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && IsOctal(s, i + 1))
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    private static bool IsOctal(string s, int start)
    {
        if (start + 3 > s.Length) return false;
        for (int i = start; i < start + 3; i++)
            if (s[i] < '0' || s[i] > '7') return false;
        return true;
    }
}
