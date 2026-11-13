using System.Linq;
using System.Text;
using MntWatch;
using Xunit;

namespace MntWatch.Tests;

public class BootstrapTests
{
    // A UTF-8 console code page (what a WSL terminal gives Windows programs) makes .NET Framework put a BOM in front
    // of a redirected stdin; the helper must still start (issue #1).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HelperScriptStartsWithAndWithoutBom(bool bom)
    {
        string distro = (Wsl.Installed() ?? new()).FirstOrDefault(d => !d.StartsWith("docker-desktop"));
        if (distro == null) return; // Needs a real distro; skipped on machines (and CI runners) without one.

        byte[] prefix = bom ? new UTF8Encoding(true).GetPreamble() : null;
        using var process = Bridge.StartScript(distro, "echo bootstrap-ok\n", out var stdin, prefix);
        stdin.Close();
        Assert.True(process.WaitForExit(20000));
        Assert.Equal("bootstrap-ok", process.StandardOutput.ReadToEnd().Trim());
    }

    [Fact]
    public void BootstrapKeepsOnlyTheDigitsOfTheLengthLine() => Assert.Contains("tr -dc 0-9", Bridge.Bootstrap);
}
