using System.Text;

namespace UE5DumpUI.Services;

/// <summary>
/// Shared CE-Lua "hygiene" emit helpers so every generated AA Script follows one
/// rule set:
/// <list type="bullet">
///   <item><b>Quiet by default.</b> Diagnostic <c>print()</c> is replaced by
///     <c>dbg()</c>, which only prints when the <c>DEBUG</c> flag is set. This
///     stops the CE Lua Engine window from popping over Cheat Engine on every
///     enable/disable.</item>
///   <item><b>Errors always surface.</b> Real failures keep using bare
///     <c>print()</c> / <c>showMessage()</c> so the user still sees them, and the
///     success-close is skipped so the window stays readable.</item>
///   <item><b>Auto-close on clean success.</b> When <c>DEBUG == 0</c> and the
///     block finished without error, the Lua Engine window closes itself.</item>
/// </list>
///
/// The flag is a single global master switch with a per-script override:
/// <c>local DEBUG = UE5_DEBUG or 0</c>. Set <c>UE5_DEBUG=1</c> once in CE's Lua
/// console to make every script verbose (and keep every window open), or edit a
/// single script's line to debug just that one. Default (unset) = quiet.
///
/// The emitted <c>dbg</c> definition is byte-identical everywhere so the
/// convention can't drift between generators; each generator places its own
/// success-close (<see cref="AppendCloseOnSuccess"/>) where its lifecycle allows.
/// </summary>
public static class CeLuaHygiene
{
    /// <summary>The exact Lua expression that closes the CE Lua Engine window.
    /// Kept as a shared constant so tests and every call site agree verbatim.</summary>
    public const string CloseCall = "synchronize(function() getLuaEngine().Close() end)";

    /// <summary>Emit the DEBUG preamble. Place it right after
    /// <c>if syntaxcheck then return end</c> in each <c>{$lua}</c> block that
    /// prints diagnostics or auto-closes. Every block is its own Lua chunk in CE,
    /// so locals don't cross blocks — emit this in each block that needs it.</summary>
    public static void AppendDebugPreamble(StringBuilder sb, string indent = "")
    {
        sb.Append(indent)
          .Append("local DEBUG = UE5_DEBUG or 0   -- 1 = show diagnostics + keep this window open\n");
        sb.Append(indent)
          .Append("local function dbg(...) if DEBUG ~= 0 then print(...) end end\n");
    }

    /// <summary>Emit the success-path close: closes the Lua Engine window unless
    /// <c>DEBUG</c> is on. Pass <paramref name="extraCondition"/> to gate on an
    /// additional Lua boolean expression (e.g. <c>"not hadError"</c>, <c>"ok"</c>)
    /// so error paths keep the window open.</summary>
    public static void AppendCloseOnSuccess(
        StringBuilder sb, string? extraCondition = null, string indent = "")
    {
        var cond = string.IsNullOrEmpty(extraCondition)
            ? "DEBUG == 0"
            : $"{extraCondition} and DEBUG == 0";
        sb.Append(indent).Append("if ").Append(cond)
          .Append(" then ").Append(CloseCall).Append(" end\n");
    }
}
