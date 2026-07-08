using UE5DumpUI;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the "is our dumper DLL already loaded, and how?" classification the
/// inject picker uses to avoid double-loading. Identity is PE ProductName ==
/// "UE5CEDumper"; the real system version.dll/dxgi.dll (Microsoft ProductName)
/// must never count.
/// </summary>
public class DumperModuleDetectorTests
{
    private const string Ours = Constants.ProxyProductName;   // "UE5CEDumper"
    private const string Microsoft = "Microsoft® Windows® Operating System";

    private static DumperModuleDetector.ModuleInfo M(string name, string? product, string? version = "1.0.0.1982")
        => new(name, product, version);

    [Fact]
    public void Empty_NotLoaded()
    {
        var (loaded, mode, version) = DumperModuleDetector.Classify(
            System.Array.Empty<DumperModuleDetector.ModuleInfo>());
        Assert.False(loaded);
        Assert.Null(mode);
        Assert.Null(version);
    }

    [Fact]
    public void SystemVersionDll_NotOurs_NotLoaded()
    {
        // The real version.dll every process loads — Microsoft ProductName, ignored.
        var (loaded, _, _) = DumperModuleDetector.Classify(new[]
        {
            M("version.dll", Microsoft),
            M("kernel32.dll", Microsoft),
        });
        Assert.False(loaded);
    }

    [Theory]
    [InlineData("version.dll", "proxy: version.dll")]
    [InlineData("dinput8.dll", "proxy: dinput8.dll")]
    [InlineData("dxgi.dll", "proxy: dxgi.dll")]
    public void OurProxy_ReportsProxyMode(string moduleName, string expectedMode)
    {
        var (loaded, mode, version) = DumperModuleDetector.Classify(new[] { M(moduleName, Ours) });
        Assert.True(loaded);
        Assert.Equal(expectedMode, mode);
        Assert.Equal("1.0.0.1982", version);
    }

    [Fact]
    public void OurUe5DumperDll_ReportsInjected()
    {
        var (loaded, mode, version) = DumperModuleDetector.Classify(new[] { M("UE5Dumper.dll", Ours) });
        Assert.True(loaded);
        Assert.Equal("injected", mode);
        Assert.Equal("1.0.0.1982", version);
    }

    [Fact]
    public void RealVersionDll_AndOurProxy_PicksOurs()
    {
        // A proxy load leaves BOTH the game-dir proxy (ours) and the forwarded
        // System32 version.dll (Microsoft) in the module list — pick ours.
        var (loaded, mode, version) = DumperModuleDetector.Classify(new[]
        {
            M("version.dll", Microsoft, "10.0.26200.1"),
            M("version.dll", Ours, "1.0.0.1982"),
        });
        Assert.True(loaded);
        Assert.Equal("proxy: version.dll", mode);
        Assert.Equal("1.0.0.1982", version);
    }

    [Fact]
    public void ProxyAndInjected_PrefersProxy()
    {
        var (loaded, mode, _) = DumperModuleDetector.Classify(new[]
        {
            M("UE5Dumper.dll", Ours),
            M("dxgi.dll", Ours),
        });
        Assert.True(loaded);
        Assert.Equal("proxy: dxgi.dll", mode);
    }

    [Fact]
    public void ProductNameAndModuleName_CaseInsensitive()
    {
        var (loaded, mode, _) = DumperModuleDetector.Classify(new[]
        {
            M("VERSION.DLL", "ue5cedumper"),
        });
        Assert.True(loaded);
        Assert.Equal("proxy: VERSION.DLL", mode);   // display keeps the original casing
    }

    [Fact]
    public void EmptyVersion_ReportedAsNull()
    {
        var (loaded, _, version) = DumperModuleDetector.Classify(new[] { M("dxgi.dll", Ours, "  ") });
        Assert.True(loaded);
        Assert.Null(version);
    }
}
