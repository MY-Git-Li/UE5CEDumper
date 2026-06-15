using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the per-action Teleport AA records (docs/teleport-spec.md §9.2B):
/// momentary records that fire the mailbox round-trip then auto-untick, with
/// [DISABLE] a nop. Plus the standard 7-row .CT batch.
/// </summary>
public class TeleportScriptGeneratorTests
{
    [Fact]
    public void Generate_is_lf_only()
    {
        var s = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.Save, 0);
        Assert.DoesNotContain("\r", s);
    }

    [Fact]
    public void Save_record_uses_op_1_and_slot()
    {
        var s = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.Save, 2);
        Assert.Contains("writeQword(mb + 0x18, 2)", s);   // slot
        Assert.Contains("writeQword(mb + 0x10, 1)", s);   // op SAVE
        Assert.Contains("writeInteger(mb + 0x00, 8)", s); // CMD_TELEPORT
    }

    [Fact]
    public void Recall_record_uses_op_2()
    {
        var s = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.Recall, 0);
        Assert.Contains("writeQword(mb + 0x10, 2)", s);
    }

    [Fact]
    public void RecallLast_record_uses_op_7()
    {
        var s = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.RecallLast);
        Assert.Contains("writeQword(mb + 0x10, 7)", s);   // op RECALL_LAST
        Assert.Contains("writeInteger(mb + 0x00, 8)", s); // CMD_TELEPORT
        Assert.Contains("Recall last", s);
    }

    [Fact]
    public void BugIt_record_uses_op_9_and_BugItGo_uses_op_10()
    {
        var bugit = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.BugIt);
        Assert.Contains("writeQword(mb + 0x10, 9)", bugit);    // op BUGIT_SAVE
        Assert.Contains("writeInteger(mb + 0x00, 8)", bugit);  // CMD_TELEPORT

        var bugitgo = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.BugItGo);
        Assert.Contains("writeQword(mb + 0x10, 10)", bugitgo); // op BUGIT_GO
    }

    [Fact]
    public void Cursor_record_uses_op_4_and_bakes_params()
    {
        var s = TeleportScriptGenerator.Generate(
            TeleportScriptGenerator.Action.Cursor, 0, zOffset: 150.0, channel: 1, fallbackCenter: true);
        Assert.Contains("writeQword(mb + 0x10, 4)", s);
        Assert.Contains("writeDouble(mb + 0x328, 150.0)", s);
        Assert.Contains("writeBytes(mb + 0x330, 1)", s);
        Assert.Contains("writeBytes(mb + 0x331, 1)", s);
    }

    [Fact]
    public void Record_auto_unticks_and_disable_is_nop()
    {
        var s = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.Save, 0);
        Assert.Contains("createTimer", s);
        Assert.Contains("memrec.Active = false", s);
        Assert.Contains("[ENABLE]", s);
        Assert.Contains("[DISABLE]", s);
        var disable = s.Substring(s.IndexOf("[DISABLE]", System.StringComparison.Ordinal));
        Assert.Contains("nothing to undo", disable);
    }

    [Fact]
    public void GetPov_record_uses_op_11_and_prints_camera()
    {
        var s = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.GetPov);
        Assert.Contains("writeQword(mb + 0x10, 11)", s);   // op GET_POV
        Assert.Contains("writeInteger(mb + 0x00, 8)", s);  // CMD_TELEPORT
        Assert.Contains("Get camera POV", s);
        Assert.Contains("readDouble(mb + 0x328)", s);      // reads the POV block back
        Assert.Contains("camera loc=", s);
    }

    [Fact]
    public void Relative_record_uses_op_12_and_bakes_distance_and_mode()
    {
        var horiz = TeleportScriptGenerator.Generate(
            TeleportScriptGenerator.Action.Relative, distance: 250.0, horizontal: true);
        Assert.Contains("writeQword(mb + 0x10, 12)", horiz);     // op RELATIVE
        Assert.Contains("writeDouble(mb + 0x328, 250.0)", horiz); // distance
        Assert.Contains("writeBytes(mb + 0x330, 0)", horiz);      // mode 0 = horizontal

        var d3 = TeleportScriptGenerator.Generate(
            TeleportScriptGenerator.Action.Relative, distance: -50.0, horizontal: false);
        Assert.Contains("writeBytes(mb + 0x330, 1)", d3);         // mode 1 = 3D
        Assert.Contains("writeDouble(mb + 0x328, -50.0)", d3);    // negative = backward
    }

    [Fact]
    public void Explicit_record_uses_op_13_and_bakes_coords()
    {
        var s = TeleportScriptGenerator.Generate(
            TeleportScriptGenerator.Action.Explicit, coordX: 10.0, coordY: 20.0, coordZ: 30.0,
            hasRot: true, pitch: 1.0, yaw: 2.0, roll: 3.0);
        Assert.Contains("writeQword(mb + 0x10, 13)", s);     // op EXPLICIT
        Assert.Contains("writeDouble(mb + 0x328, 10.0)", s); // X
        Assert.Contains("writeDouble(mb + 0x338, 30.0)", s); // Z
        Assert.Contains("writeBytes(mb + 0x358, 1)", s);     // hasRot
    }

    [Fact]
    public void Cursor_on_off_records_use_op_14_with_show_flag()
    {
        var on = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.CursorOn);
        Assert.Contains("writeQword(mb + 0x10, 14)", on);   // op SET_CURSOR
        Assert.Contains("writeQword(mb + 0x18, 1)", on);    // show = 1

        var off = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.CursorOff);
        Assert.Contains("writeQword(mb + 0x10, 14)", off);
        Assert.Contains("writeQword(mb + 0x18, 0)", off);   // show = 0
    }

    [Fact]
    public void GetPose_record_uses_op_0_and_prints_coords()
    {
        var s = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.GetPose);
        Assert.Contains("writeQword(mb + 0x10, 0)", s);    // op GET_POSE
        Assert.Contains("writeInteger(mb + 0x00, 8)", s);  // CMD_TELEPORT
        Assert.Contains("Get current coords", s);
        Assert.Contains("readDouble(mb + 0x328)", s);      // reads the pose block back
        Assert.Contains("coords loc=", s);
    }

    [Fact]
    public void BuildBatchRows_returns_seventeen_teleport_rows()
    {
        var rows = TeleportScriptGenerator.BuildBatchRows();
        Assert.Equal(17, rows.Count);
        Assert.All(rows, r => Assert.Equal("Teleport", r.Category));
        // 3 saves, 3 recalls, recall-last, BugIt, BugItGo, cursor, Get POV,
        // Get coords, TP facing dir, TP to coords, Cursor ON/OFF, clear-all.
        Assert.All(rows, r => Assert.IsType<CtScriptRow>(r));
        Assert.Contains(rows, r => r.Description == "Save marker 1");
        Assert.Contains(rows, r => r.Description == "Recall marker 3");
        Assert.Contains(rows, r => r.Description == "Recall last");
        Assert.Contains(rows, r => r.Description == "BugIt (store pose)");
        Assert.Contains(rows, r => r.Description == "BugItGo (go to stored)");
        Assert.Contains(rows, r => r.Description == "Teleport to cursor");
        Assert.Contains(rows, r => r.Description == "Get camera POV");
        Assert.Contains(rows, r => r.Description == "Get current coords");
        Assert.Contains(rows, r => r.Description == "TP facing direction");
        Assert.Contains(rows, r => r.Description == "TP to coordinates");
        Assert.Contains(rows, r => r.Description == "Cursor ON");
        Assert.Contains(rows, r => r.Description == "Cursor OFF");
        Assert.Contains(rows, r => r.Description == "Clear all markers");
    }

    [Fact]
    public void ClearAll_record_loops_slots_with_clear_op()
    {
        var s = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.ClearAll);
        Assert.DoesNotContain("\r", s);
        Assert.Contains("for slot = 0, 2 do", s);
        Assert.Contains("writeQword(mb + 0x10, 6)", s);   // op CLEAR_MARKER
        Assert.Contains("writeInteger(mb + 0x00, 8)", s); // CMD_TELEPORT
        Assert.Contains("createTimer", s);                // auto-untick
    }

    [Fact]
    public void Batch_rows_build_into_a_valid_cheat_table()
    {
        var rows = TeleportScriptGenerator.BuildBatchRows();
        var ct = CheatTableBuilder.Build("Teleport", rows);
        Assert.Contains("<CheatTable", ct);
        Assert.Contains("Auto Assembler Script", ct);
    }
}
