namespace UE5DumpUI.Core;

/// <summary>
/// Bridge to AOBMaker CE Plugin for navigating CE Memory Viewer.
/// Communicates via <c>\\.\pipe\AOBMakerCEBridge</c> named pipe.
/// </summary>
public interface IAobMakerBridge
{
    /// <summary>Cached availability — true if the last pipe connect succeeded.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Test pipe connectivity and update <see cref="IsAvailable"/>.
    /// </summary>
    Task<bool> CheckAvailabilityAsync(CancellationToken ct = default);

    /// <summary>
    /// Navigate CE Memory Viewer hex dump (bottom pane) to the specified address.
    /// Sends <c>NavigateHexView</c>.
    /// </summary>
    /// <param name="hexAddress">Bare hex address without 0x prefix (e.g. "7FF769E29110")</param>
    Task<bool> NavigateHexViewAsync(string hexAddress, CancellationToken ct = default);

    /// <summary>
    /// Navigate CE Memory Viewer disassembler (top pane) to the specified address.
    /// Sends <c>NavigateDisassembler</c>.
    /// </summary>
    /// <param name="hexAddress">Bare hex address without 0x prefix (e.g. "7FF769E29110")</param>
    Task<bool> NavigateDisassemblerAsync(string hexAddress, CancellationToken ct = default);

    /// <summary>
    /// Create an Auto Assembler script entry in CE's address list.
    /// Sends <c>CreateAAScript</c>.
    /// </summary>
    /// <param name="description">Description shown in CE address list</param>
    /// <param name="script">Full AA script content ([ENABLE]/[DISABLE] sections)</param>
    /// <param name="autoActivate">Whether to activate the script immediately after creation</param>
    /// <param name="group">Optional group-node description. Non-empty → the record is
    /// nested under a single-level <c>IsGroupHeader</c> folder of that description
    /// (created if absent). Null/empty → address-list root (back-compatible).
    /// <b>Requires an AOBMaker CE plugin that handles the <c>group</c> field</b> —
    /// older builds ignore it (records land at root).</param>
    Task<bool> CreateAAScriptAsync(string description, string script, bool autoActivate = true,
        string? group = null, CancellationToken ct = default);

    /// <summary>
    /// Create an AOB-scan-based symbol registration AA script in CE's address list.
    /// Sends <c>CreateSymbolScript</c> — the CE Plugin's <c>BuildSymbolScanScript()</c>
    /// generates the full AA script from these AOB parameters.
    /// </summary>
    /// <param name="name">Description shown in CE address list</param>
    /// <param name="aob">AOB pattern string (e.g. "48 8B 1D ?? ?? ?? ??")</param>
    /// <param name="pos">Displacement offset within AOB match (instrOffset + opcodeLen)</param>
    /// <param name="aoblen">Instruction end relative to AOB match (instrOffset + totalLen)</param>
    /// <param name="symbol">CE symbol name to register (e.g. "gworld_addr")</param>
    /// <param name="module">Game module name for AOBScanModule (e.g. "Game-Win64-Shipping.exe")</param>
    /// <param name="autoActivate">Whether to activate the script immediately after creation</param>
    Task<bool> CreateSymbolScriptAsync(string name, string aob, int pos, int aoblen,
        string symbol, string module, bool autoActivate = true, CancellationToken ct = default);

    /// <summary>
    /// Add a single typed memory record to CE's address list.
    /// Sends <c>CreateMemoryRecord</c> — the CE Plugin calls
    /// <c>addresslist.createMemoryRecord()</c>, sets Description / Address / Type /
    /// ShowAsSigned / ShowAsHex, and self-verifies before returning success.
    /// One-click alternative to "copy address, then build the record by hand in CE"
    /// (e.g. so the user can immediately run CE's "Find out what accesses this address").
    /// </summary>
    /// <param name="description">Record label in CE's address list (typically the field Name).</param>
    /// <param name="address">Bare hex address without 0x prefix (e.g. "7FF769E29110"),
    /// or a registersymbol name.</param>
    /// <param name="valueType">CE <c>TVariableType</c> code: 0=Byte, 1=Word, 2=Dword,
    /// 3=Qword, 4=Single, 5=Double, 6=String, 7=UnicodeString, 8=ByteArray, 9=Binary.</param>
    /// <param name="isSigned">Display integer types as signed.</param>
    /// <param name="showAsHex">Display as hex. <b>Requires an AOBMaker CE plugin compiled
    /// on/after 2026-06-07</b> — older builds silently ignore it (default false is back-compatible).</param>
    Task<bool> CreateMemoryRecordAsync(string description, string address, int valueType,
        bool isSigned = false, bool showAsHex = false, CancellationToken ct = default);

    /// <summary>
    /// Embed an arbitrary text/Lua file into the currently open CE table.
    /// Sends <c>InjectTableFile</c> — the CE Plugin handler runs
    /// <c>findTableFile</c> (delete-if-exists) + <c>createTableFile</c> +
    /// <c>Stream.copyFrom(createStringStream(content))</c> + a
    /// <c>Stream.Size</c> verification check.
    /// Lets users skip the manual "save .lua to disk" + "Table -> Add File..."
    /// dance for runtime helpers like <c>ue5_invoke_helper.lua</c>.
    /// </summary>
    /// <param name="fileName">Filename to register inside the .CT
    /// (case-sensitive, used by <c>findTableFile</c> from AA scripts).</param>
    /// <param name="content">Raw UTF-8 file content. Long-bracket level
    /// is chosen automatically by the CE Plugin so any payload is safe.</param>
    /// <returns>
    /// Tuple of (Ok, ErrorMessage). On success Ok=true and ErrorMessage=null.
    /// On failure Ok=false and ErrorMessage carries the plugin-side reason
    /// (e.g. "Stream size mismatch: ..."), or null if the failure was a
    /// connect/timeout on the UI side. Surface the message in status text
    /// so users can tell "wrong CE state" apart from "real plugin bug".
    /// </returns>
    Task<(bool Ok, string? ErrorMessage)> InjectTableFileAsync(string fileName, string content,
        CancellationToken ct = default);
}
