using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace MntWatch;

/// <summary>
/// Settings kept in %LOCALAPPDATA%\MntWatch\settings.ini, a plain key=value file you can edit by hand while
/// MntWatch is closed: which distro to bridge into and one folder= line per watched folder.
/// </summary>
internal sealed class Settings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "MntWatch";

    public static readonly string AppDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MntWatch");

    public static string DefaultFilePath => Path.Combine(AppDir, "settings.ini");
    public static string LogPath => Path.Combine(AppDir, "mntwatch.log");

    public string FilePath;
    /// <summary>Distro name; empty means whichever distro is the WSL default.</summary>
    public string Distro = "";
    public bool Paused;
    public bool FirstRun = true;
    public readonly List<string> Folders = new();

    public static Settings Load(string path = null)
    {
        path ??= DefaultFilePath;
        var s = new Settings { FilePath = path };
        try
        {
            if (File.Exists(path)) s.Parse(File.ReadAllLines(path));
        }
        catch (IOException)
        {
            // Fall back to defaults.
        }
        return s;
    }

    public void Parse(IEnumerable<string> lines)
    {
        FirstRun = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string value = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "distro":
                    Distro = value;
                    break;
                case "paused":
                    Paused = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "folder":
                    AddFolder(value);
                    break;
            }
        }
    }

    /// <summary>Adds a folder unless it, a folder above it or a folder inside it is already watched.</summary>
    public bool AddFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        string f = folder.Trim().TrimEnd('\\');
        if (f.Length == 2 && f[1] == ':') f += "\\";
        if (Folders.Any(existing => Overlaps(existing, f))) return false;
        Folders.Add(f);
        return true;
    }

    /// <summary>Nested overmounts would hide each other, so a folder and its parent can't both be watched.</summary>
    public static bool Overlaps(string a, string b)
    {
        string x = a.TrimEnd('\\') + "\\", y = b.TrimEnd('\\') + "\\";
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<string> Serialize()
    {
        yield return "# MntWatch settings. Edit while MntWatch is closed.";
        yield return "# distro: the WSL distro to bridge into; leave empty for your default distro.";
        yield return "distro=" + Distro;
        yield return "paused=" + (Paused ? "true" : "false");
        yield return "# One line per Windows folder whose changes Linux file watchers should see.";
        foreach (string f in Folders) yield return "folder=" + f;
    }

    public void Save()
    {
        if (FilePath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temp = FilePath + ".tmp";
            File.WriteAllLines(temp, Serialize());
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
            FirstRun = false;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --tray");
            else key.DeleteValue(RunValue, false);
        }
    }
}

/// <summary>A small rolling log next to the settings; the tray's "Open log" shows it.</summary>
internal static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.AppDir);
                var info = new FileInfo(Settings.LogPath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string old = Settings.LogPath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(Settings.LogPath, old);
                }
                File.AppendAllText(Settings.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
