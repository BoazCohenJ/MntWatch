using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MntWatch;

internal static class Native
{
    public const int AttachParentProcess = -1;
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int size, int flags);

    /// <summary>The real path of a folder: on-disk letter case, junctions and symlinks followed. Null if it can't be opened.</summary>
    public static string FinalPath(string folder)
    {
        using var handle = CreateFile(folder, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        int n = GetFinalPathNameByHandle(handle, sb, sb.Capacity, 0);
        if (n <= 0 || n >= sb.Capacity) return null;
        return sb.ToString();
    }
}

internal static class Paths
{
    /// <summary>
    /// The folder as drvfs sees it: C:\Dev\App with the case stored on disk (Linux lookups are case sensitive
    /// in folders marked so) and links resolved, since the overmount has to sit on the real directory.
    /// </summary>
    public static string Resolve(string folder)
    {
        try
        {
            string full = Path.GetFullPath(folder).TrimEnd('\\');
            if (full.Length == 2) full += "\\";
            string final = Native.FinalPath(full);
            return final == null ? full : StripPrefix(final);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
        {
            return null;
        }
    }

    public static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
        return path.Length > 3 ? path.TrimEnd('\\') : path;
    }
}
