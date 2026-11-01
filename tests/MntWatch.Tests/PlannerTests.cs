using System.Collections.Generic;
using MntWatch;
using Xunit;

namespace MntWatch.Tests;

public class PlannerTests
{
    private readonly Dictionary<string, FileStamp> _disk = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly Planner _p;

    public PlannerTests()
    {
        _p = new Planner(@"C:\dev\app", "/mnt/c/dev/app", path => _disk.TryGetValue(path, out var s) ? s : null);
    }

    private void Write(string path, long ticks, long length) => _disk[path] = new FileStamp(ticks, length, 0x20);

    [Fact]
    public void EditNudgesTheFile()
    {
        Write(@"C:\dev\app\src\main.ts", 1, 10);
        _p.Add(ChangeKind.Changed, @"C:\dev\app\src\main.ts");
        Assert.Equal(new[] { "/mnt/c/dev/app/src/main.ts" }, _p.Take());
    }

    [Fact]
    public void RepeatedEventsForOneSaveNudgeOnce()
    {
        Write(@"C:\dev\app\a.ts", 1, 10);
        _p.Add(ChangeKind.Changed, @"C:\dev\app\a.ts");
        _p.Add(ChangeKind.Changed, @"C:\dev\app\a.ts");
        Assert.Single(_p.Take());
        // Windows reports the same save again after the batch went out: Linux already has these attributes.
        _p.Add(ChangeKind.Changed, @"C:\dev\app\a.ts");
        Assert.Empty(_p.Take());
    }

    [Fact]
    public void ANewWriteIsNudgedEvenIfTheSizeStaysTheSame()
    {
        Write(@"C:\dev\app\a.ts", 1, 10);
        _p.Add(ChangeKind.Changed, @"C:\dev\app\a.ts");
        _p.Take();
        Write(@"C:\dev\app\a.ts", 2, 10);
        _p.Add(ChangeKind.Changed, @"C:\dev\app\a.ts");
        Assert.Single(_p.Take());
    }

    [Fact]
    public void FilesThatCantBeStatedAreStillNudged()
    {
        // Locked or just deleted: nudging costs little, missing a change would leave Linux stale.
        _p.Add(ChangeKind.Changed, @"C:\dev\app\locked.db");
        _p.Add(ChangeKind.Changed, @"C:\dev\app\locked.db");
        Assert.Equal(new[] { "/mnt/c/dev/app/locked.db" }, _p.Take());
    }

    [Fact]
    public void CreateNudgesParentThenFile()
    {
        Write(@"C:\dev\app\src\new.ts", 1, 0);
        _p.Add(ChangeKind.Created, @"C:\dev\app\src\new.ts");
        Assert.Equal(new[] { "/mnt/c/dev/app/src", "/mnt/c/dev/app/src/new.ts" }, _p.Take());
    }

    [Fact]
    public void CreateInTheRootNudgesTheRoot()
    {
        _p.Add(ChangeKind.Created, @"C:\dev\app\x.txt");
        Assert.Equal(new[] { "/mnt/c/dev/app", "/mnt/c/dev/app/x.txt" }, _p.Take());
    }

    [Fact]
    public void DeleteNudgesParentAndForgetsTheStamp()
    {
        Write(@"C:\dev\app\a.ts", 1, 10);
        _p.Add(ChangeKind.Changed, @"C:\dev\app\a.ts");
        _p.Take();
        _p.Add(ChangeKind.Deleted, @"C:\dev\app\a.ts");
        Assert.Equal(new[] { "/mnt/c/dev/app", "/mnt/c/dev/app/a.ts" }, _p.Take());
        // Recreated with the very same stamp: still a change Linux must hear about.
        _p.Add(ChangeKind.Changed, @"C:\dev\app\a.ts");
        Assert.Single(_p.Take());
    }

    [Fact]
    public void AtomicSaveRenameNudgesBothSides()
    {
        // Many editors write a temp file and rename it over the original.
        Write(@"C:\dev\app\src\a.ts", 5, 12);
        _p.Add(ChangeKind.Renamed, @"C:\dev\app\src\a.ts", @"C:\dev\app\src\a.ts~tmp");
        Assert.Equal(new[] { "/mnt/c/dev/app/src", "/mnt/c/dev/app/src/a.ts~tmp", "/mnt/c/dev/app/src/a.ts" }, _p.Take());
    }

    [Fact]
    public void MoveBetweenFoldersNudgesBothParents()
    {
        _p.Add(ChangeKind.Renamed, @"C:\dev\app\b\x.ts", @"C:\dev\app\a\x.ts");
        Assert.Equal(new[] { "/mnt/c/dev/app/a", "/mnt/c/dev/app/a/x.ts", "/mnt/c/dev/app/b", "/mnt/c/dev/app/b/x.ts" }, _p.Take());
    }

    [Fact]
    public void GitAndNodeModulesAreNotSkipped()
    {
        // Skipping them would leave Linux with stale sizes there, which breaks git when both sides use the repo.
        _p.Add(ChangeKind.Changed, @"C:\dev\app\.git\index");
        _p.Add(ChangeKind.Changed, @"C:\dev\app\node_modules\x\index.js");
        Assert.Equal(2, _p.Take().Count);
    }

    [Fact]
    public void PathsOutsideTheFolderAreIgnored()
    {
        _p.Add(ChangeKind.Changed, @"C:\dev\application\x.ts");
        _p.Add(ChangeKind.Changed, @"D:\dev\app\x.ts");
        Assert.Empty(_p.Take());
    }

    [Fact]
    public void NamesTheHelperCantCarryAreDropped()
    {
        _p.Add(ChangeKind.Changed, "C:\\dev\\app\\bad\tname");
        Assert.Empty(_p.Take());
    }

    [Fact]
    public void PathCaseFromWindowsIsKeptButRootMatchesCaseInsensitively()
    {
        _p.Add(ChangeKind.Changed, @"c:\DEV\app\Src\Main.ts");
        Assert.Equal(new[] { "/mnt/c/dev/app/Src/Main.ts" }, _p.Take());
    }

    [Fact]
    public void LostEventsAskForASweepAndForgetStamps()
    {
        Write(@"C:\dev\app\a.ts", 1, 10);
        _p.Add(ChangeKind.Changed, @"C:\dev\app\a.ts");
        _p.Take();
        _p.LostEvents();
        Assert.True(_p.NeedsSweep);
        _p.Take();
        Assert.False(_p.NeedsSweep);
        _p.Add(ChangeKind.Changed, @"C:\dev\app\a.ts");
        Assert.Single(_p.Take());
    }

    [Fact]
    public void DriveRootFolderMapsCleanly()
    {
        var p = new Planner(@"D:\", "/mnt/d", _ => null);
        p.Add(ChangeKind.Changed, @"D:\x\y.txt");
        Assert.Equal(new[] { "/mnt/d/x/y.txt" }, p.Take());
    }
}
