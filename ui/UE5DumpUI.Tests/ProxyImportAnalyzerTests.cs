using System.Buffers.Binary;
using System.IO;
using System.Text;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Unit tests for the offline PE import-table parser + proxy suggestion logic.
/// The parser is exercised against a hand-built minimal PE32+ image (no live
/// file), which validates the DOS→NT→section→RVA→file-offset math end to end.
/// </summary>
public class ProxyImportAnalyzerTests
{
    // ── Recommendation logic (pure, no PE) ──

    [Fact]
    public void Recommend_NoHistoryNoImports_DefaultsToVersion()
    {
        var s = ProxyImportAnalyzer.Recommend(null, null, injected: false);
        Assert.Equal(ProxyType.Version, s.Type);
        Assert.Equal("version · default", s.Display);
    }

    [Fact]
    public void Recommend_RememberedPick_Wins_OverImports()
    {
        // Even when the exe imports dxgi/dinput8, a remembered manual pick is used.
        var imports = new ProxyImportAnalyzer.ProxyImportInfo(false, true, true);
        var s = ProxyImportAnalyzer.Recommend(imports, ProxyType.Dxgi, injected: false);
        Assert.Equal(ProxyType.Dxgi, s.Type);
        Assert.Equal("dxgi.dll · last used", s.Display);
    }

    [Fact]
    public void Recommend_Injected_NoProxy_SurfacesInjectionKnownGood()
    {
        var s = ProxyImportAnalyzer.Recommend(null, null, injected: true);
        Assert.Null(s.Type); // injection has no proxy type
        Assert.Equal("injection · no proxy deployed", s.Display);
    }

    [Fact]
    public void Recommend_RememberedProxy_WinsOverInjection()
    {
        // A deployed proxy is a stronger known-good than "also injected once".
        var s = ProxyImportAnalyzer.Recommend(null, ProxyType.Version, injected: true);
        Assert.Equal(ProxyType.Version, s.Type);
        Assert.Equal("version.dll · last used", s.Display);
    }

    [Fact]
    public void Recommend_ImportsDxgi_AnnotatesAlternative_ButKeepsVersionDefault()
    {
        var imports = new ProxyImportAnalyzer.ProxyImportInfo(false, false, true);
        var s = ProxyImportAnalyzer.Recommend(imports, null, injected: false);
        Assert.Equal(ProxyType.Version, s.Type);   // never auto-escalates to dxgi
        Assert.Equal("version · default · alt: dxgi", s.Display);
    }

    [Fact]
    public void Recommend_ImportsBoth_ListsBothAlternatives()
    {
        var imports = new ProxyImportAnalyzer.ProxyImportInfo(false, true, true);
        var s = ProxyImportAnalyzer.Recommend(imports, null, injected: false);
        Assert.Equal("version · default · alt: dxgi, dinput8", s.Display);
    }

    [Fact]
    public void Recommend_ImportsNeither_FlagsHardCase()
    {
        var imports = new ProxyImportAnalyzer.ProxyImportInfo(false, false, false);
        var s = ProxyImportAnalyzer.Recommend(imports, null, injected: false);
        Assert.Equal(ProxyType.Version, s.Type);
        Assert.Equal("version · default · no dxgi/dinput8", s.Display);
    }

    // ── PE import parsing (synthetic image) ──

    [Fact]
    public void Analyze_ExeImportingDxgi_DetectsDxgiOnly()
    {
        var pe = BuildPe64WithImports("dxgi.dll");
        var info = ProxyImportAnalyzer.Analyze(new MemoryStream(pe));
        Assert.NotNull(info);
        Assert.True(info!.Value.ImportsDxgi);
        Assert.False(info.Value.ImportsDinput8);
        Assert.False(info.Value.ImportsVersion);
    }

    [Fact]
    public void Analyze_ExeImportingDinput8AndDxgi_DetectsBoth()
    {
        var pe = BuildPe64WithImports("dinput8.dll", "dxgi.dll");
        var info = ProxyImportAnalyzer.Analyze(new MemoryStream(pe));
        Assert.NotNull(info);
        Assert.True(info!.Value.ImportsDxgi);
        Assert.True(info.Value.ImportsDinput8);
    }

    [Fact]
    public void Analyze_IsCaseInsensitive()
    {
        var pe = BuildPe64WithImports("DXGI.DLL");
        var info = ProxyImportAnalyzer.Analyze(new MemoryStream(pe));
        Assert.True(info!.Value.ImportsDxgi);
    }

    [Fact]
    public void Analyze_ExeImportingUnrelatedDll_DetectsNone()
    {
        var pe = BuildPe64WithImports("kernel32.dll");
        var info = ProxyImportAnalyzer.Analyze(new MemoryStream(pe));
        Assert.NotNull(info);
        Assert.False(info!.Value.ImportsDxgi);
        Assert.False(info.Value.ImportsDinput8);
        Assert.False(info.Value.ImportsVersion);
    }

    [Fact]
    public void Analyze_NonIdentityRvaMapping_ResolvesCorrectly()
    {
        // Real exes almost never have VirtualAddress == PointerToRawData; this
        // exercises the RVA→file-offset conversion (rva - VA + PointerToRawData)
        // that the identity-mapped cases above cannot catch (e.g. a va/rawPtr swap).
        var pe = BuildPe64(0x1000, new[] { "dinput8.dll" });
        var info = ProxyImportAnalyzer.Analyze(new MemoryStream(pe));
        Assert.NotNull(info);
        Assert.True(info!.Value.ImportsDinput8);
        Assert.False(info.Value.ImportsDxgi);
    }

    [Fact]
    public void Analyze_RealOnDiskPe_ParsesWithoutThrowing()
    {
        // Smoke test against a genuine multi-section PE (this assembly's own file):
        // real RVA→offset conversion, real section table, real import directory —
        // the parser must return a value (not null / not throw). We don't assert
        // WHICH imports (a managed assembly's import table is minimal), only that a
        // real binary parses cleanly, which the synthetic fixtures cannot guarantee.
        var path = typeof(ProxyImportAnalyzer).Assembly.Location;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return; // single-file/trimmed host with no on-disk location — skip

        using var fs = File.OpenRead(path);
        var info = ProxyImportAnalyzer.Analyze(fs);
        Assert.NotNull(info); // valid PE → parsed; our proxy DLLs simply aren't imported
    }

    [Fact]
    public void Analyze_NotAPe_ReturnsNull()
    {
        Assert.Null(ProxyImportAnalyzer.Analyze(new MemoryStream(new byte[] { 1, 2, 3, 4, 5 })));

        // Valid MZ but no PE signature.
        var bogus = new byte[0x100];
        bogus[0] = 0x4D; bogus[1] = 0x5A;
        BinaryPrimitives.WriteUInt32LittleEndian(bogus.AsSpan(0x3C), 0x80);
        Assert.Null(ProxyImportAnalyzer.Analyze(new MemoryStream(bogus)));
    }

    private static byte[] BuildPe64WithImports(params string[] dllNames) =>
        BuildPe64(0x200, dllNames);   // 0x200 == PointerToRawData → identity mapping

    // ── Synthetic PE32+ builder ──
    //
    // Minimal but structurally valid: DOS stub → PE sig → 20-byte FileHeader →
    // 240-byte OptionalHeader (PE32+) → one ".idata" section at file offset 0x200.
    // The import descriptor array lives at the start of the section and the DLL
    // name strings at +0xA0. <paramref name="sectionVa"/> is the section's
    // VirtualAddress: pass 0x200 for identity mapping (RVA == file offset) or any
    // other value (e.g. 0x1000) to exercise the RVA→file-offset conversion.
    private static byte[] BuildPe64(uint sectionVa, string[] dllNames)
    {
        const uint rawPtr = 0x200;                    // section raw data at file 0x200
        var buf = new byte[0x400];

        // DOS header
        buf[0] = 0x4D; buf[1] = 0x5A;                 // 'MZ'
        WriteU32(buf, 0x3C, 0x80);                    // e_lfanew

        // PE signature
        buf[0x80] = 0x50; buf[0x81] = 0x45;           // 'PE\0\0'

        // IMAGE_FILE_HEADER @ 0x84
        WriteU16(buf, 0x84, 0x8664);                  // Machine = x64
        WriteU16(buf, 0x86, 1);                       // NumberOfSections
        WriteU16(buf, 0x94, 0xF0);                    // SizeOfOptionalHeader = 240
        WriteU16(buf, 0x96, 0x22);                    // Characteristics

        // IMAGE_OPTIONAL_HEADER64 @ 0x98 (opt)
        WriteU16(buf, 0x98, 0x20B);                   // Magic = PE32+
        WriteU32(buf, 0xD4, rawPtr);                  // SizeOfHeaders   (opt+60)
        WriteU32(buf, 0x104, 16);                     // NumberOfRvaAndSizes (opt+108)

        // Data directory index 1 (import) @ opt+112 + 8 = 0x110
        int numDesc = dllNames.Length;
        WriteU32(buf, 0x110, sectionVa);              // import table RVA
        WriteU32(buf, 0x114, (uint)((numDesc + 1) * 20)); // import table size

        // Section header @ opt + 240 = 0x188
        var secName = Encoding.ASCII.GetBytes(".idata");
        Array.Copy(secName, 0, buf, 0x188, secName.Length);
        WriteU32(buf, 0x190, 0x200);                  // VirtualSize
        WriteU32(buf, 0x194, sectionVa);              // VirtualAddress
        WriteU32(buf, 0x198, 0x200);                  // SizeOfRawData
        WriteU32(buf, 0x19C, rawPtr);                 // PointerToRawData

        // Import descriptors @ file rawPtr (RVA sectionVa), 20 bytes each; a null
        // descriptor terminates the array (left zero).
        for (int i = 0; i < numDesc; i++)
        {
            int d = (int)rawPtr + i * 20;
            uint nameRva = sectionVa + 0xA0 + (uint)(i * 0x10);
            int nameFileOff = (int)(nameRva - sectionVa + rawPtr);
            WriteU32(buf, d + 12, nameRva);           // IMAGE_IMPORT_DESCRIPTOR.Name
            WriteU32(buf, d + 16, 0x100);             // FirstThunk (nonzero ≠ terminator)

            var nb = Encoding.ASCII.GetBytes(dllNames[i]);
            Array.Copy(nb, 0, buf, nameFileOff, nb.Length); // string (null-term already)
        }

        return buf;
    }

    private static void WriteU16(byte[] b, int off, ushort v) =>
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off), v);

    private static void WriteU32(byte[] b, int off, uint v) =>
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), v);
}
