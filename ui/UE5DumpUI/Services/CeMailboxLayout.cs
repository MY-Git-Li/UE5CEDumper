namespace UE5DumpUI.Services;

/// <summary>
/// Canonical Mimic mailbox layout tokens shared by every CE Lua generator that
/// talks to the UE5Dumper.dll mailbox directly (Invoke / DebugCamera / Movement /
/// Protection / Teleport). These string offsets and opcode / timeout ints are
/// emitted VERBATIM into the generated Lua, so they MUST match
/// <c>dll/src/Mimic.h</c> (MailboxData struct + Cmd enum).
///
/// <para>Offsets are kept as pre-formatted hex STRINGS so the emitted text is
/// byte-identical everywhere. The canonical form is the compact 2-digit hex
/// (e.g. <c>"0x10"</c>) historically emitted by DebugCamera / Movement / Protection /
/// Teleport (the majority). Lua parses <c>0x10</c> and <c>0x010</c> to the same
/// number, so <see cref="InvokeScriptGenerator"/> — which previously wrote the
/// zero-padded <c>0x010</c> — now emits this compact form (same address, cosmetic).</para>
/// </summary>
internal static class CeMailboxLayout
{
    // Mailbox field offsets (must match Mimic.h MailboxData) — emitted as
    // `mb + {Off...}` in the generated Lua.
    public const string OffCmd          = "0x00";   // cmd (write LAST to trigger)
    public const string OffStatus       = "0x04";   // status (poll == 1)
    public const string OffResult       = "0x08";   // result / observed state
    public const string OffInstanceAddr = "0x10";   // instanceAddr / op / knobId / request
    public const string OffUfuncAddr    = "0x18";   // ufuncAddr / value / slot / show flag
    public const string OffParamsData   = "0x328";  // params_data[0..]

    // Cmd opcodes (must match Mimic.h Cmd enum).
    public const int CmdSetDebugCamera = 7;   // CMD_SET_DEBUG_CAMERA
    public const int CmdProtect        = 9;   // CMD_PROTECT   (GodMode / Solitar)
    public const int CmdMovement       = 10;  // CMD_MOVEMENT  (Laufen knobs)
    public const int CmdFly            = 11;  // CMD_FLY       (Dunste — no-gravity 3D flight)
    public const int CmdForeground     = 12;  // CMD_FOREGROUND (Grausam — keep-foreground lock)
    public const int CmdQueryPtr       = 13;  // CMD_QUERY_PTR (resolve GWorld / GameEngine address)

    // Shared mailbox poll timeout (ms) — the upper bound of the `while status ~= 1`
    // busy-wait loop in every emitted mailbox round-trip.
    public const int MailboxPollTimeoutMs = 10000;
}
