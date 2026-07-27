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
    public const string OffInitState    = "0x0C";   // initState (DLL-written auto-start readiness)
    public const string OffInstanceAddr = "0x10";   // instanceAddr / op / knobId / request
    public const string OffUfuncAddr    = "0x18";   // ufuncAddr / value / slot / show flag
    public const string OffParamsData   = "0x328";  // params_data[0..]

    // Auto-start readiness values written to OffInitState (must match Mimic.h
    // InitState). Unlike every other field here this one is NOT part of a
    // command round-trip — the DLL publishes it once during start-up so a CE Lua
    // bootstrap can poll for readiness with a pure memory read instead of
    // sleeping a fixed budget (no executeCodeEx ⇒ no CreateRemoteThread, which
    // games block).
    public const int InitIdle    = 0;   // DLL mapped; auto-start has not begun
    public const int InitRunning = 1;   // AOB scan / pipe-server start in progress
    public const int InitReady   = 2;   // init finished AND the pipe server is up
    public const int InitFailed  = 3;   // init finished but the pipe server failed
    public const int InitSkipped = 4;   // deliberately skipped — CE plugin host, or
                                        // another instance already owns the pipe

    // Cmd opcodes (must match Mimic.h Cmd enum).
    public const int CmdSetDebugCamera = 7;   // CMD_SET_DEBUG_CAMERA
    public const int CmdProtect        = 9;   // CMD_PROTECT   (GodMode / Solitar)
    public const int CmdMovement       = 10;  // CMD_MOVEMENT  (Laufen knobs)
    public const int CmdFly            = 11;  // CMD_FLY       (Dunste — no-gravity 3D flight)
    public const int CmdForeground     = 12;  // CMD_FOREGROUND (Grausam — keep-foreground lock)
    public const int CmdQueryPtr       = 13;  // CMD_QUERY_PTR (resolve GWorld / GameEngine address)
    public const int CmdSeeThrough     = 14;  // CMD_SEETHROUGH (Schlacht — see-through occluders toggle)
    public const int CmdTime           = 15;  // CMD_TIME      (Hemmung — time dilation hold)

    // CMD_TIME op codes (Mimic.h TimeOp): instanceAddr = op, ufuncAddr = target
    // (0 global / 1 pawn), paramsData[0..7] = double value (SET only).
    public const int TimeOpSet   = 0;
    public const int TimeOpReset = 1;

    // Shared mailbox poll timeout (ms) — the upper bound of the `while status ~= 1`
    // busy-wait loop in every emitted mailbox round-trip.
    public const int MailboxPollTimeoutMs = 10000;

    /// <summary>How long an emitted script waits for the mailbox to go IDLE before
    /// declaring it busy, in ms (sleep(1) per iteration).
    ///
    /// <para>This is NOT the same thing as <see cref="MailboxPollTimeoutMs"/>, and it
    /// exists because of an ordering detail: <c>SetDone</c>/<c>SetError</c> publish
    /// <c>status = DONE</c> BEFORE clearing <c>cmd</c> (deliberately — see Mimic.cpp).
    /// A script that issues two round-trips back to back can therefore exit its
    /// status-poll and still observe the PREVIOUS command in <c>cmd</c> for an instant.
    /// Sampling <c>cmd</c> once would report "busy" and abandon the second query, so
    /// the wait is bounded rather than a single read. 100 ms is far more than the
    /// one-instruction window and still fails fast when CE really is mid-command.</para></summary>
    public const int MailboxIdleWaitMs = 100;
}
