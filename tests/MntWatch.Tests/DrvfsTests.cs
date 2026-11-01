using System.Linq;
using MntWatch;
using Xunit;

namespace MntWatch.Tests;

public class DrvfsTests
{
    // Real /proc/mounts option fields from WSL 2.7 (Alpine, root) and a typical Ubuntu setup.
    private const string AlpineC =
        @"rw,noatime,aname=drvfs;path=C:\;uid=0;gid=0;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=5,wfd=5";
    private const string UbuntuD =
        @"rw,noatime,aname=drvfs;path=D:\;uid=1000;gid=1000;metadata;umask=22;fmask=11;case=off;symlinkroot=/mnt/,cache=0x5,access=client,msize=262144,trans=virtio";

    [Fact]
    public void ParsesDriveRootMount()
    {
        var m = Drvfs.Parse("/mnt/c", AlpineC);
        Assert.Equal("/mnt/c", m.MountPoint);
        Assert.Equal(@"C:\", m.WindowsPath);
        Assert.True(m.IsDriveRoot);
        Assert.Equal(0x5, m.Cache);
        Assert.Equal(new[] { "uid=0", "gid=0" }, m.DrvfsOptions);
        Assert.Equal(new[] { "rw", "noatime" }, m.Flags);
    }

    [Fact]
    public void OvermountCopiesOwnershipAndAddsOnlyTheMetadataBit()
    {
        var m = Drvfs.Parse("/mnt/d", UbuntuD);
        Assert.Equal("rw,noatime,uid=1000,gid=1000,metadata,umask=22,fmask=11,case=off,cache=0x7", Drvfs.OvermountOptions(m));
    }

    [Theory]
    [InlineData(0x5, 0x7)]
    [InlineData(0x0, 0x2)]
    [InlineData(0x1, 0x3)]
    [InlineData(0xf, 0x7)]  // loose never reaches the overmount: Windows edits would go unseen
    [InlineData(0x85, 0x7)] // nor fscache
    public void OvermountCache(int drive, int expected) => Assert.Equal(expected, Drvfs.OvermountCache(drive));

    [Fact]
    public void KnowsWhetherTheKernelTakesCacheFlags()
    {
        Assert.True(Drvfs.Parse("/mnt/c", AlpineC).CacheFlagsSupported);
        Assert.False(Drvfs.Parse("/mnt/c", @"rw,noatime,aname=drvfs;path=C:\;uid=1000,cache=mmap").CacheFlagsSupported);
        Assert.False(Drvfs.Parse("/mnt/c", @"rw,noatime,aname=drvfs;path=C:\;uid=1000").CacheFlagsSupported);
    }

    [Fact]
    public void MissingCacheUsesTheWslDefault() => Assert.Equal(0x7, Drvfs.OvermountCache(null));

    [Theory]
    [InlineData("loose", 0xf)]
    [InlineData("mmap", 0x5)]
    [InlineData("none", 0x0)]
    [InlineData("0x5", 0x5)]
    [InlineData("5", 5)]
    public void ParsesCacheValues(string value, int expected) => Assert.Equal(expected, Drvfs.ParseCache(value));

    [Fact]
    public void SubfolderMountIsNotADriveRoot()
    {
        var m = Drvfs.Parse("/mnt/c/dev/app", @"rw,relatime,aname=drvfs;path=C:\dev\app;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x7");
        Assert.False(m.IsDriveRoot);
        Assert.Equal(@"C:\dev\app", m.WindowsPath);
    }

    [Fact]
    public void FindsTheMountForAFoldersDrive()
    {
        var mounts = new[]
        {
            Drvfs.Parse("/mnt/c/dev/app", @"rw,aname=drvfs;path=C:\dev\app;cache=0x7"),
            Drvfs.Parse("/mnt/c", AlpineC),
            Drvfs.Parse("/mnt/d", UbuntuD),
        };
        Assert.Equal("/mnt/c", Drvfs.FindDrive(mounts, @"c:\dev\other").MountPoint);
        Assert.Equal("/mnt/d", Drvfs.FindDrive(mounts, @"D:\x").MountPoint);
        Assert.Null(Drvfs.FindDrive(mounts, @"E:\x"));
        Assert.Null(Drvfs.FindDrive(mounts, @"\\server\share"));
    }

    [Theory]
    [InlineData("/mnt/c", @"C:\dev\my app", "/mnt/c/dev/my app")]
    [InlineData("/mnt/c/", @"C:\dev\app\", "/mnt/c/dev/app")]
    [InlineData("/c", @"C:\src", "/c/src")]
    [InlineData("/mnt/c", @"C:\", "/mnt/c")]
    public void MapsWindowsPathsThroughTheMount(string mountPoint, string windows, string linux) =>
        Assert.Equal(linux, Drvfs.ToLinux(new DrvfsMount { MountPoint = mountPoint, WindowsPath = @"C:\" }, windows));

    [Fact]
    public void UnescapesProcMountsOctal()
    {
        Assert.Equal("/mnt/c/my dir", Drvfs.UnescapeProcMounts(@"/mnt/c/my\040dir"));
        Assert.Equal(@"C:\", Drvfs.UnescapeProcMounts(@"C:\134"));
        Assert.Equal(@"C:\", Drvfs.UnescapeProcMounts(@"C:\"));
        Assert.Equal(@"a\9b", Drvfs.UnescapeProcMounts(@"a\9b"));
    }

    [Fact]
    public void ParsesSpacesInMountedFolder()
    {
        var m = Drvfs.Parse(@"/mnt/c/Users/me/my\040project", @"rw,aname=drvfs;path=C:\Users\me\my project;uid=1000,cache=0x7");
        Assert.Equal("/mnt/c/Users/me/my project", m.MountPoint);
        Assert.Equal(@"C:\Users\me\my project", m.WindowsPath);
        Assert.Contains("uid=1000", m.DrvfsOptions);
    }

    [Fact]
    public void OlderStylePlainOptionsAreCopiedToo()
    {
        var m = Drvfs.Parse("/mnt/c", "rw,noatime,uid=1000,gid=1000,metadata,aname=drvfs;path=C:\\,cache=0x5");
        Assert.Equal(new[] { "uid=1000", "gid=1000", "metadata" }, m.DrvfsOptions.ToArray());
    }
}
