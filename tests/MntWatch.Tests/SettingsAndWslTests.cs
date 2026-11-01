using System.Linq;
using System.Text;
using MntWatch;
using Xunit;

namespace MntWatch.Tests;

public class SettingsAndWslTests
{
    [Fact]
    public void SettingsRoundTrip()
    {
        var s = new Settings { Distro = "Ubuntu-24.04", Paused = true };
        s.AddFolder(@"C:\dev\app");
        s.AddFolder(@"D:\work\site\");
        var back = new Settings();
        back.Parse(s.Serialize());
        Assert.Equal("Ubuntu-24.04", back.Distro);
        Assert.True(back.Paused);
        Assert.Equal(new[] { @"C:\dev\app", @"D:\work\site" }, back.Folders);
    }

    [Fact]
    public void NestedFoldersAreRefused()
    {
        var s = new Settings();
        Assert.True(s.AddFolder(@"C:\dev\app"));
        Assert.False(s.AddFolder(@"C:\dev\app\src"));
        Assert.False(s.AddFolder(@"C:\dev"));
        Assert.False(s.AddFolder(@"c:\DEV\APP\"));
        Assert.True(s.AddFolder(@"C:\dev\application"));
        Assert.True(s.AddFolder(@"D:\"));
        Assert.False(s.AddFolder(@"D:\anything"));
    }

    [Fact]
    public void DecodesWslUtf16Output()
    {
        byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Ubuntu\r\nmntwatch-test\r\n")).ToArray();
        Assert.Equal(new[] { "Ubuntu", "mntwatch-test" }, Wsl.ParseNames(Wsl.Decode(bytes)));
    }

    [Fact]
    public void DecodesUtf8Output() => Assert.Equal("Debian\n", Wsl.Decode(Encoding.UTF8.GetBytes("Debian\n")));

    [Fact]
    public void FindsDefaultDistroInVerboseList()
    {
        const string output = "  NAME            STATE           VERSION\r\n* Ubuntu-24.04    Running         2\r\n  docker-desktop  Stopped         2\r\n";
        Assert.Equal("Ubuntu-24.04", Wsl.ParseDefault(output));
        Assert.Null(Wsl.ParseDefault("  NAME STATE VERSION\r\n"));
    }

    [Fact]
    public void NoDistrosGivesAnEmptyList()
    {
        // wsl --list --quiet prints an explanation (with spaces) instead of names.
        Assert.Empty(Wsl.ParseNames("Windows Subsystem for Linux has no installed distributions.\r\n"));
    }

    [Fact]
    public void HelperScriptIsEmbeddedWithUnixLineEndings()
    {
        string script = Bridge.HelperScript();
        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.DoesNotContain("\r", script);
    }

    [Theory]
    [InlineData(@"\\?\C:\dev\app", @"C:\dev\app")]
    [InlineData(@"\\?\C:\", @"C:\")]
    [InlineData(@"\\?\UNC\server\share", @"\\server\share")]
    public void StripsFinalPathPrefix(string raw, string expected) => Assert.Equal(expected, Paths.StripPrefix(raw));
}
