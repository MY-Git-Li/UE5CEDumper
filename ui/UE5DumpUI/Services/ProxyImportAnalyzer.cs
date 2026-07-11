using System.Buffers.Binary;
using System.IO;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Offline, IO-light analysis of a game executable's PE import table to decide
/// which of our proxy DLLs can actually be loaded by that process, and to build
/// a per-game proxy suggestion for the Proxy Deploy panel.
///
/// WHY imports, not history: which proxy WORKS is largely gated by which system
/// DLL the .exe pulls in. <c>dxgi.dll</c> and <c>dinput8.dll</c> are STATIC
/// imports — an .exe that never imports <c>dinput8.dll</c> will never load a
/// <c>dinput8.dll</c> proxy, so suggesting it is pointless. <c>version.dll</c> is
/// the deliberate exception: it is loaded DYNAMICALLY by virtually every process
/// (GetFileVersionInfo / COM / manifest parsing) and so is almost never a static
/// import — therefore its absence from the import table says NOTHING about
/// whether the version.dll proxy works, and it stays the safe universal default.
/// We deliberately do NOT auto-escalate to dxgi merely because version isn't a
/// static import (that pattern matches nearly every D3D game and would push dxgi
/// into games where it destabilises them, e.g. Octopath). Import parsing here
/// only reports VIABILITY (dxgi/dinput8 importable) as advisory context.
///
/// The parser is pure (operates on a seekable <see cref="Stream"/>) so it is unit
/// testable against a synthetic PE with no live file. All OS file access lives in
/// <c>ProxyDeployService</c>.
/// </summary>
internal static class ProxyImportAnalyzer
{
    /// <summary>Which of our proxy-relevant system DLLs the .exe statically (or
    /// delay-) imports. <c>version.dll</c> is tracked for completeness but is
    /// almost never a static import (see class remarks) and is never used to
    /// downgrade the version default.</summary>
    public readonly record struct ProxyImportInfo(bool ImportsVersion, bool ImportsDinput8, bool ImportsDxgi);

    /// <summary>A per-game proxy suggestion: the recommended type (null when the
    /// known-good method is injection, which has no proxy type) plus a concise,
    /// self-contained column string (kept short so a plain sortable text column can
    /// show it without a tooltip).</summary>
    public readonly record struct ProxySuggestion(ProxyType? Type, string Display);

    private const int MZ = 0x5A4D;            // 'MZ'
    private const uint PE = 0x00004550;       // 'PE\0\0'
    private const ushort PE32 = 0x10B;
    private const ushort PE32PLUS = 0x20B;
    private const int MaxDescriptors = 4096;  // runaway guard on a malformed table
    private const int MaxNameLen = 256;

    /// <summary>
    /// Parse the PE import + delay-import directories of <paramref name="pe"/> and
    /// report which of {version,dinput8,dxgi}.dll it imports. Returns null on any
    /// malformation (not a PE, truncated, out-of-range RVA) — the caller then
    /// simply shows no viability hint. Never throws.
    /// </summary>
    public static ProxyImportInfo? Analyze(Stream pe)
    {
        try
        {
            long len = pe.Length;
            if (len < 0x40 || ReadU16(pe, 0) != MZ)
                return null;

            long lfanew = ReadU32(pe, 0x3C);
            if (lfanew <= 0 || lfanew + 24 > len || ReadU32(pe, lfanew) != PE)
                return null;

            long fileHeader = lfanew + 4;
            int numSections = ReadU16(pe, fileHeader + 2);
            int sizeOfOptional = ReadU16(pe, fileHeader + 16);

            long opt = lfanew + 24;
            ushort magic = (ushort)ReadU16(pe, opt);
            bool is64 = magic == PE32PLUS;
            if (magic != PE32PLUS && magic != PE32)
                return null;

            uint sizeOfHeaders = ReadU32(pe, opt + 60);
            long numRvaOff = opt + (is64 ? 108 : 92);
            long dataDirOff = opt + (is64 ? 112 : 96);
            uint numRvaAndSizes = ReadU32(pe, numRvaOff);

            // Section table follows the optional header.
            long secOff = opt + sizeOfOptional;
            var sections = new List<(uint Va, uint VSize, uint RawSize, uint RawPtr)>(numSections);
            for (int i = 0; i < numSections; i++)
            {
                long s = secOff + (long)i * 40;
                if (s + 40 > len) break;
                uint vSize = ReadU32(pe, s + 8);
                uint va = ReadU32(pe, s + 12);
                uint rawSize = ReadU32(pe, s + 16);
                uint rawPtr = ReadU32(pe, s + 20);
                sections.Add((va, vSize, rawSize, rawPtr));
            }

            long? RvaToOffset(uint rva)
            {
                if (rva < sizeOfHeaders) return rva; // headers map 1:1
                foreach (var (va, vSize, rawSize, rawPtr) in sections)
                {
                    uint span = Math.Max(vSize, rawSize);
                    if (rva >= va && rva < va + span)
                    {
                        long off = (long)rva - va + rawPtr;
                        return (off >= 0 && off < len) ? off : (long?)null;
                    }
                }
                return null;
            }

            bool ver = false, di8 = false, dxgi = false;
            void Classify(string name)
            {
                if (name.Equals("version.dll", StringComparison.OrdinalIgnoreCase)) ver = true;
                else if (name.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase)) di8 = true;
                else if (name.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)) dxgi = true;
            }

            // ── Standard import directory (data directory index 1) ──
            if (numRvaAndSizes > 1)
            {
                uint importRva = ReadU32(pe, dataDirOff + 1 * 8);
                if (importRva != 0 && RvaToOffset(importRva) is long impOff)
                {
                    for (int i = 0; i < MaxDescriptors; i++)
                    {
                        long d = impOff + (long)i * 20;
                        if (d + 20 > len) break;
                        uint nameRva = ReadU32(pe, d + 12);   // IMAGE_IMPORT_DESCRIPTOR.Name
                        uint firstThunk = ReadU32(pe, d + 16);
                        if (nameRva == 0 && firstThunk == 0) break; // null terminator
                        if (nameRva != 0 && RvaToOffset(nameRva) is long nOff)
                            Classify(ReadAsciiZ(pe, nOff, len));
                    }
                }
            }

            // ── Delay-load import directory (data directory index 13) ──
            // Some games delay-load dxgi; a delay-loaded DLL is still loaded (just
            // later), so its proxy still activates. Modern linkers store RVAs here.
            if (numRvaAndSizes > 13)
            {
                uint delayRva = ReadU32(pe, dataDirOff + 13 * 8);
                if (delayRva != 0 && RvaToOffset(delayRva) is long delOff)
                {
                    for (int i = 0; i < MaxDescriptors; i++)
                    {
                        long d = delOff + (long)i * 32;   // IMAGE_DELAYLOAD_DESCRIPTOR = 32 bytes
                        if (d + 32 > len) break;
                        uint nameRva = ReadU32(pe, d + 4); // DllNameRVA
                        if (nameRva == 0) break;
                        if (RvaToOffset(nameRva) is long nOff)
                            Classify(ReadAsciiZ(pe, nOff, len));
                    }
                }
            }

            return new ProxyImportInfo(ver, di8, dxgi);
        }
        catch
        {
            return null; // any IO / bounds error → no hint
        }
    }

    /// <summary>
    /// Build the per-game suggestion. Order of preference:
    /// 1. a remembered proxy the user last deployed for this game (mini-LKG) — the
    ///    closest honest "last known good" we have without a DLL change;
    /// 2. otherwise <see cref="ProxyType.Version"/>, the safe universal default.
    /// The parsed <paramref name="imports"/> (if any) are surfaced as advisory
    /// "importable" context and are NEVER used to override the version default.
    /// </summary>
    public static ProxySuggestion Recommend(
        ProxyImportInfo? imports, ProxyType? confirmedPick, ProxyType? rememberedPick, bool injected)
    {
        // 0. A proxy the DLL CONFIRMED actually loaded this game (and the session
        //    stayed alive past the stability dwell) is the strongest known-good.
        if (confirmedPick is ProxyType confirmed)
            return new ProxySuggestion(confirmed, $"{confirmed.GetDisplayName()} · confirmed working");

        // 1. A proxy the user actually deployed for this game — weaker than a
        //    confirmed load, but still an honest "last known good".
        if (rememberedPick is ProxyType pick)
            return new ProxySuggestion(pick, $"{pick.GetDisplayName()} · last used");

        // 2. Injection is itself a known-good load method. If the user has
        //    successfully injected into this game via the UI and never deployed a
        //    proxy, surface that instead of guessing a proxy — injection is often
        //    the more reliable path (and the only one for launcher games that strip
        //    the exe's DLL search directory). No proxy type applies here.
        if (injected)
            return new ProxySuggestion(null, "injection · no proxy deployed");

        // 3. No history → the safe default. version.dll loads dynamically almost
        //    everywhere, so it stays the broadest-compatible pick regardless of the
        //    static import table. The imports only annotate the fallback options.
        string alt = DescribeImportable(imports);
        string display = alt.Length > 0
            ? $"version · default · alt: {alt}"
            : (imports is ProxyImportInfo i && !i.ImportsDxgi && !i.ImportsDinput8
                ? "version · default · no dxgi/dinput8"
                : "version · default");
        return new ProxySuggestion(ProxyType.Version, display);
    }

    /// <summary>Short list of the non-version proxies the .exe imports (the ones
    /// that can actually activate), or "" when unknown/none. Extension trimmed for
    /// compactness ("dxgi", "dinput8").</summary>
    private static string DescribeImportable(ProxyImportInfo? imports)
    {
        if (imports is not ProxyImportInfo i) return "";
        var parts = new List<string>(2);
        if (i.ImportsDxgi) parts.Add("dxgi");
        if (i.ImportsDinput8) parts.Add("dinput8");
        return string.Join(", ", parts);
    }

    // ── Little-endian PE field readers (seekable stream) ──
    private static int ReadU16(Stream s, long off)
    {
        s.Seek(off, SeekOrigin.Begin);
        Span<byte> b = stackalloc byte[2];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    private static uint ReadU32(Stream s, long off)
    {
        s.Seek(off, SeekOrigin.Begin);
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    /// <summary>Read a null-terminated ASCII DLL name at a file offset, bounded.</summary>
    private static string ReadAsciiZ(Stream s, long off, long len)
    {
        s.Seek(off, SeekOrigin.Begin);
        Span<byte> buf = stackalloc byte[MaxNameLen];
        int n = 0;
        for (; n < MaxNameLen && off + n < len; n++)
        {
            int c = s.ReadByte();
            if (c <= 0) break; // null terminator or EOF
            buf[n] = (byte)c;
        }
        return System.Text.Encoding.ASCII.GetString(buf[..n]);
    }
}
