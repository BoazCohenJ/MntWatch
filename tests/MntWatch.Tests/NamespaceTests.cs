using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MntWatch;
using Xunit;

namespace MntWatch.Tests;

// WSL keeps a separate mount namespace for programs started from elevated Windows processes, so a mount made on
// one side is invisible on the other (issue #2). The helper finds the other namespace and bridges it too.
public class NamespaceTests
{
    [Fact]
    public void MessagesNameTheOtherSide()
    {
        Assert.Contains("started as administrator", Session.NamespaceMessage(1, "Debian", elevated: false));
        Assert.Contains("started without administrator rights", Session.NamespaceMessage(1, "Debian", elevated: true));
        Assert.Contains("nsenter", Session.NamespaceMessage(-1, "Debian", elevated: false));
        Assert.Contains("any more", Session.NamespaceMessage(0, "Debian", elevated: false));
    }

    [Fact]
    public void HelperBridgesAnotherWslMountNamespace()
    {
        string distro = (Wsl.Installed() ?? new()).FirstOrDefault(d => !d.StartsWith("docker-desktop"));
        if (distro == null) return; // Needs a real distro; skipped on machines (and CI runners) without one.

        string folder = Path.Combine(Path.GetTempPath(), "MntWatch-nstest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "one");
        folder = Paths.Resolve(folder);

        // Stand-in for WSL's elevated side: a private mount namespace, made before MntWatch mounts anything, holding
        // a process whose executable is /init like WSL's own. It is sleep, started while sleep is bound over /init;
        // the real /init is then bound back on top so mount -t drvfs (/sbin/mount.drvfs -> /init) still works there.
        using var fake = Bridge.StartScript(distro,
            "command -v unshare >/dev/null || { echo NOUNSHARE; exit 0; }\n" +
            "S=$(readlink -f \"$(command -v sleep)\")\n" +
            "exec unshare -m sh -c 'mkdir -p /tmp/mwns; touch /tmp/mwns/realinit; mount --bind /init /tmp/mwns/realinit; " +
            "mount --bind \"$0\" /init; exec -a sleep /init 120 & p=$!; sleep 0.3; mount --bind /tmp/mwns/realinit /init; " +
            "echo PID $p; cat > /dev/null; kill $p' \"$S\"\n", out var fakeIn);
        string first = fake.StandardOutput.ReadLine() ?? "";
        if (first == "NOUNSHARE") return;
        Assert.StartsWith("PID ", first);
        string pid = first.Substring(4).Trim();

        var counts = new List<int>();
        using var nsSeen = new ManualResetEventSlim(false);
        try
        {
            using var bridge = Bridge.Start(distro);
            bridge.NamespacesChanged += n =>
            {
                lock (counts) counts.Add(n);
                if (n != 0) nsSeen.Set();
            };
            var drive = Drvfs.FindDrive(bridge.DrvfsMounts, folder);
            Assert.NotNull(drive);
            string target = Drvfs.ToLinux(drive, folder);
            Assert.Null(bridge.Mount(target, folder, Drvfs.OvermountOptions(drive)));
            Assert.True(nsSeen.Wait(10000), "The helper never reported the other namespace.");
            lock (counts) Assert.DoesNotContain(-1, counts); // nsenter must be available in the test distro.

            // A watcher inside the other namespace, the way bun or vite would run in an elevated terminal.
            using var watcher = Bridge.StartScript(distro,
                "command -v inotifywait >/dev/null || { echo NOWATCH; exit 0; }\n" +
                $"nsenter -t {pid} -m -- sh -c 'grep -c \" {target} \" /proc/mounts; inotifywait -m -q --format \"EV %e %f\" \"{target}\" & " +
                "p=$!; sleep 0.7; echo READY; cat > /dev/null; kill $p'\n",
                out var watcherIn);
            var lines = new List<string>();
            var reader = new Thread(() =>
            {
                string l;
                while ((l = watcher.StandardOutput.ReadLine()) != null) lock (lines) lines.Add(l);
            }) { IsBackground = true };
            reader.Start();
            try
            {
                Assert.True(WaitFor(lines, l => l == "READY" || l == "NOWATCH", 10), "The watcher in the other namespace didn't start.");
                lock (lines) if (lines.Contains("NOWATCH")) return;
                lock (lines) Assert.Equal("1", lines[0].Trim()); // The overmount reached the other namespace.

                File.WriteAllText(Path.Combine(folder, "a.txt"), "two, longer");
                bridge.Nudge(new[] { target + "/a.txt" });
                Assert.True(WaitFor(lines, l => l.StartsWith("EV ") && l.Contains("a.txt"), 5),
                    "A watcher in the other namespace didn't hear the Windows edit.");
            }
            finally
            {
                try { watcherIn.Close(); } catch (IOException) { }
                if (!watcher.WaitForExit(5000)) try { watcher.Kill(); } catch (InvalidOperationException) { }
            }
        }
        finally
        {
            try { fakeIn.Close(); } catch (IOException) { }
            if (!fake.WaitForExit(5000)) try { fake.Kill(); } catch (InvalidOperationException) { }
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static bool WaitFor(List<string> lines, Func<string, bool> match, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            lock (lines) if (lines.Any(match)) return true;
            Thread.Sleep(50);
        }
        return false;
    }
}
