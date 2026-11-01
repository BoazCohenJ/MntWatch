using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace MntWatch;

/// <summary>Asks wsl.exe which distros exist and which are running, without starting any.</summary>
internal static class Wsl
{
    public static string Exe
    {
        get
        {
            // A 32-bit process would be redirected away from System32; use Sysnative then.
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string native = Path.Combine(windir, "Sysnative", "wsl.exe");
            if (!Environment.Is64BitProcess && File.Exists(native)) return native;
            return Path.Combine(windir, "System32", "wsl.exe");
        }
    }

    /// <summary>Names of running distros, or null when wsl.exe couldn't be run.</summary>
    public static List<string> Running() => ParseNames(Run("--list --running --quiet"));

    public static List<string> Installed() => ParseNames(Run("--list --quiet"));

    /// <summary>The default distro, read from the line wsl --list --verbose marks with an asterisk.</summary>
    public static string Default() => ParseDefault(Run("--list --verbose"));

    public static List<string> ParseNames(string output)
    {
        if (output == null) return null;
        return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.Contains(' '))
            .ToList();
    }

    public static string ParseDefault(string output)
    {
        if (output == null) return null;
        foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.TrimStart();
            if (!line.StartsWith("*")) continue;
            var fields = line.Substring(1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0) return fields[0];
        }
        return null;
    }

    /// <summary>wsl.exe prints its own messages in UTF-16; strip the BOM and stray NULs either way.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        bool utf16 = bytes.Length >= 2 && (bytes[1] == 0 || (bytes[0] == 0xFF && bytes[1] == 0xFE));
        string s = utf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        return s.Replace("﻿", "").Replace("\0", "");
    }

    private static string Run(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(Exe, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            copy.Wait(2000);
            // "No installed distributions" also exits non-zero; an empty list is the right answer then.
            return p.ExitCode == 0 ? Decode(ms.ToArray()) : "";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException)
        {
            return null;
        }
    }
}
