using System;
using System.IO;
using System.Linq;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Pins the shipped <c>scripts/UE5CEDumper.CT</c>'s "where is UE5Dumper.dll" block.
///
/// <para>The bug being guarded (2026-08-04): a user opened the table from Cheat Engine's
/// recent-files menu with the DLL in the same folder, and the script reported it missing.
/// A CE table script cannot read its own <c>.CT</c> path — no such API exists — so the
/// folder is inferred, and every source it used (the Open/Save dialog objects) is empty
/// unless the table came through <c>File &gt; Open</c>. The script also told the user to
/// "place UE5Dumper.dll in the same folder as this CT file", which is what they had
/// already done.</para>
///
/// <para>None of this can be executed here — it is CE Lua — so these assertions are
/// structural: the sources are present, the risky ones are guarded, the order puts
/// table-derived folders above CE's own install folder, and the misleading text is gone.</para>
/// </summary>
public class CtDllDiscoveryTests
{
    private static string Ct()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "scripts", "UE5CEDumper.CT");
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new FileNotFoundException("scripts/UE5CEDumper.CT not found from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Reads_CEs_recent_files_list_because_that_is_the_only_channel_a_double_click_fills()
    {
        var s = Ct();
        // getSettings() is CE's documented registry accessor (celua.txt:3504); the MRU
        // is REG_MULTI_SZ at HKCU\Software\Cheat Engine, so the byte reader is required —
        // a plain Value[] string read typically refuses a MULTI_SZ.
        Assert.Contains("getSettings()", s, StringComparison.Ordinal);
        Assert.Contains("Recent Files", s, StringComparison.Ordinal);
        Assert.Contains("getBinaryValue", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Picks_the_MRU_entry_by_this_tables_own_filename()
    {
        // Measured on a real machine: UE5CEDumper.CT was entry 2, not entry 1. Taking
        // entry[0] blindly would bind to an unrelated game's folder and could load a
        // stale UE5Dumper.dll sitting beside it.
        var s = Ct();
        Assert.Contains("\"ue5cedumper.ct\"", s, StringComparison.Ordinal);
        Assert.Contains("extractFileName", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_the_UI_breadcrumb_file()
    {
        var s = Ct();
        // Must agree with Constants.DllPathBreadcrumbFile / DumperDllPathStore.
        Assert.Contains("dll-path.txt", s, StringComparison.Ordinal);
        Assert.Equal("dll-path.txt", UE5DumpUI.Constants.DllPathBreadcrumbFile);
    }

    [Fact]
    public void Table_derived_folders_outrank_the_CE_install_folder()
    {
        // A UE5Dumper.dll in CE's own folder can only have been hand-placed, and is
        // most likely a stale build. Silently loading it is worse than failing.
        var s = Ct();
        int mru = s.IndexOf("Recent Files", StringComparison.Ordinal);
        int crumb = s.IndexOf("dll-path.txt", StringComparison.Ordinal);
        int ceDir = s.IndexOf("getCheatEngineDir", StringComparison.Ordinal);
        Assert.True(mru > 0 && crumb > 0 && ceDir > 0);
        Assert.True(mru < ceDir, "the recent-files slot must be probed before CE's install folder");
        Assert.True(crumb < ceDir, "the UI breadcrumb must be probed before CE's install folder");
    }

    [Fact]
    public void A_miss_no_longer_aborts_the_whole_table_script()
    {
        // The old code returned out of the [ENABLE] chunk, leaving ue5_inject /
        // ue5_shutdown / ue5_log undefined — so the next tick failed with "attempt to
        // call a nil value", a second error that looked unrelated to the first.
        var s = Ct();
        Assert.Contains("function ue5_renderDllSearch()", s, StringComparison.Ordinal);
        Assert.Contains("function ue5_pickDllManually()", s, StringComparison.Ordinal);
        Assert.Contains("DLL_PATH = ue5_pickDllManually()", s, StringComparison.Ordinal);
    }

    [Fact]
    public void The_failure_message_no_longer_tells_the_user_to_do_what_they_already_did()
    {
        var s = Ct();
        Assert.DoesNotContain("Please place UE5Dumper.dll in the same folder as this CT file",
            s, StringComparison.Ordinal);
        // ...and names the real cause plus every way out.
        Assert.Contains("has no way to read its own .CT path", s, StringComparison.Ordinal);
        Assert.Contains("Launch UE5DumpUI.exe once", s, StringComparison.Ordinal);
        Assert.Contains("File &gt; Open", s, StringComparison.Ordinal);
        Assert.Contains("file picker", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_undocumented_CE_probe_is_pcall_guarded()
    {
        // These are undocumented published components / optional APIs. An unguarded
        // read that throws aborts the chunk — which is how the Save-dialog read used to
        // kill the script before the log was even open.
        // Comment lines mention these APIs deliberately, to explain why they are
        // guarded — match CODE only. And the guard is not always on the same line:
        // `pcall(function()` may open a block a few lines above, which is how the
        // recent-files reader is written.
        var lines = Ct().Split('\n');
        foreach (var api in new[] { "OpenDialog1.FileName", "SaveDialog1.FileName",
                                    "getCurrentScriptPath()", "getSettings()",
                                    "createOpenDialog(" })
        {
            int at = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("--", StringComparison.Ordinal)) continue;
                if (lines[i].Contains(api, StringComparison.Ordinal)) { at = i; break; }
            }
            Assert.True(at >= 0, $"{api} missing from the .CT's code");

            bool guarded = false;
            for (int i = Math.Max(0, at - 3); i <= at && !guarded; i++)
                guarded = lines[i].Contains("pcall", StringComparison.Ordinal);
            Assert.True(guarded,
                $"{api} at line {at + 1} is not inside a pcall — a throw there aborts the " +
                "whole chunk, which is how the bare SaveDialog1 read used to kill the script " +
                "before the log was open.");
        }
    }

    [Fact]
    public void Stale_auto_run_comment_is_gone()
    {
        // The block is the [ENABLE] of CheatEntry 102, not a table-load LuaScript —
        // the file has no <LuaScript> element at all.
        var s = Ct();
        Assert.DoesNotContain("Auto-runs when this CT is opened", s, StringComparison.Ordinal);
        Assert.DoesNotContain("<LuaScript>", s, StringComparison.Ordinal);
    }
}
