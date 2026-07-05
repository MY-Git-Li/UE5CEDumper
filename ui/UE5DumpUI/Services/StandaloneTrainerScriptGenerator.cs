using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>One generated CE address-list entry (an [ENABLE]/[DISABLE] AA script).</summary>
public readonly record struct TrainerEntry(string Description, string Script, bool AutoActivate);

/// <summary>
/// Emits a NO-DLL standalone CE-Lua trainer for ONE game+version from baked
/// offsets (see project-standalone-ce-lua-trainer). Each entry is a self-contained
/// [ENABLE]/[DISABLE] AA script pushed into CE via AOBMaker's CreateAAScript; all
/// entries share the CE Lua state, so the Setup entry defines globals the feature
/// entries reuse. The trainer re-walks [[GWorld]+..] every tick (restart/respawn
/// safe) and re-asserts via CE timers — the freeze half the DLL's worker did.
///
/// Portable subset only: direct memory reads/writes (Move Speed / Gravity / Super
/// Jump / GodMode bit / coordinate TP). Anything needing a game-thread ProcessEvent
/// call (cursor-trace TP, StopMovement settle, POV set) stays DLL-only by design.
/// Follows the CE-Lua output-hygiene MUST-rule via <see cref="CeLuaHygiene"/>.
/// </summary>
public static class StandaloneTrainerScriptGenerator
{
    public const string SetupDescription = "UE5 Trainer: Setup (auto — runs on activate)";
    private const string SaveDescription = "UE5 Trainer: TP Save position";
    private const string RecallDescription = "UE5 Trainer: TP Recall position";

    private const string Close = CeLuaHygiene.CloseCall;

    /// <summary>Build the ordered trainer entries. Setup is first and auto-activates
    /// (it AOB-scans GWorld + defines globals); the rest are opt-in toggles that
    /// no-op until Setup has run. Features whose offsets didn't resolve are omitted.</summary>
    public static List<TrainerEntry> Generate(TrainerOffsets o)
    {
        var entries = new List<TrainerEntry>
        {
            new(SetupDescription, BuildSetup(o), AutoActivate: true),
        };

        if (o.PawnToCmc >= 0 && o.WalkSpeedOff >= 0)
            entries.Add(new("UE5 Trainer: Move Speed x3", BuildKnob("speed", o.WalkSpeedOff, "3.0"), false));
        if (o.PawnToCmc >= 0 && o.GravityOff >= 0)
            entries.Add(new("UE5 Trainer: Low Gravity x0.25", BuildKnob("grav", o.GravityOff, "0.25"), false));
        if (o.PawnToCmc >= 0 && o.JumpOff >= 0)
            entries.Add(new("UE5 Trainer: Super Jump (4x height)", BuildJump(o.JumpOff, "4.0"), false));
        if (o.GodBits.Count > 0)
            entries.Add(new("UE5 Trainer: God Mode", BuildGodMode(), false));
        // Pure-Lua velocity fly (no DLL, no ProcessEvent) — needs the CMC MovementMode
        // + Velocity offsets and a Controller back-ref for the view-relative basis.
        if (o.PawnToCmc >= 0 && o.MoveModeOff >= 0 && o.VelocityOff >= 0
            && o.CtrlRotOff >= 0 && o.PawnToController >= 0)
            entries.Add(new("UE5 Trainer: Fly (velocity, view-relative)", BuildFly(), false));

        // Coordinate teleport (Save/Recall) — IsUsable already guarantees these.
        entries.Add(new(SaveDescription, BuildTpSave(), false));
        entries.Add(new(RecallDescription, BuildTpRecall(), false));

        return entries;
    }

    // ---- Setup: AOB-scan GWorld, bake config, define shared globals ----
    private static string BuildSetup(TrainerOffsets o)
    {
        var sb = new StringBuilder(4096);
        BeginEnable(sb);
        Line(sb, "-- ============================================================");
        Line(sb, "-- UE5 Standalone Trainer (no DLL) -- baked for ONE game+version");
        Line(sb, "-- Offsets go stale if the game is patched -- regenerate from UE5CEDumper.");
        Line(sb, "-- ============================================================");

        // Config table (baked)
        Line(sb, "UE5T = {");
        Line(sb, $"  module = {LuaModule(o.Module)},");
        Line(sb, $"  aob = '{o.GWorldAob}', pos = {o.GWorldAobPos}, aoblen = {o.GWorldAobLen},");
        Line(sb, "  sym = 'ue5t_gworld',");
        Line(sb, "  toPawn = {");
        foreach (var h in o.Chain)
            Line(sb, $"    {{ off = {Hex(h.Offset)}, deref = {(h.Deref ? "true" : "false")} }},   -- {h.Field}");
        Line(sb, "  },");
        Line(sb, $"  rootOff = {Hex(o.PawnToRoot)}, relLocOff = {Hex(o.RootToRelLoc)}, vecWidth = {o.VecElementWidth},");
        Line(sb, $"  cmcOff = {Hex(o.PawnToCmc)}, maxWalkOff = {Hex(o.WalkSpeedOff)}, gravOff = {Hex(o.GravityOff)}, jumpOff = {Hex(o.JumpOff)},");
        // Fly config: MovementMode / Velocity on the CMC, ControlRotation via the
        // Controller back-ref (yaw = ControlRotation's 2nd component = +vecWidth).
        Line(sb, $"  moveModeOff = {Hex(o.MoveModeOff)}, velOff = {Hex(o.VelocityOff)}, ctrlRotOff = {Hex(o.CtrlRotOff)}, controllerOff = {Hex(o.PawnToController)},");
        Line(sb, "  flySpeed = 1200, flyTickMs = 16,   -- fly: edit flySpeed (uu/s)");
        Line(sb, "  godBits = {");
        foreach (var b in o.GodBits)
            Line(sb, $"    {{ off = {Hex(b.ByteOffset)}, mask = {Hex(b.Mask)}, protect = {b.Protect} }},   -- {b.Name}");
        Line(sb, "  },");
        Line(sb, "  tickMs = 50,");
        Line(sb, "}");
        Line(sb);

        // Self-contained AOB scanner (pure CE, no DLL) — shared verbatim with the
        // GWorld-walk export so behaviour matches.
        AppendAobScanHelper(sb);

        Line(sb, "UE5T_ready = false");
        Line(sb, "if UE5T.aob == '' or #UE5T.toPawn == 0 then");
        Line(sb, "  showMessage('[UE5 Trainer] Config missing (no AOB / chain). Regenerate from UE5CEDumper.')");
        Line(sb, "  return");
        Line(sb, "end");
        Line(sb, "local mod = UE5T.module");
        Line(sb, "if mod == nil or mod == '' then mod = process end");
        Line(sb, "local hit = AOBScanModuleUE(mod, UE5T.aob)");
        Line(sb, "if not hit then");
        Line(sb, "  showMessage('[UE5 Trainer] GWorld AOB scan FAILED in '..tostring(mod)..'. Baked for one game+version; aborting (no fallback).')");
        Line(sb, "  return");
        Line(sb, "end");
        Line(sb, "local hitv = tonumber(hit, 16)");
        Line(sb, "local disp = readInteger(hitv + UE5T.pos, true)");
        Line(sb, "if not disp then showMessage('[UE5 Trainer] Could not read RIP displacement. Aborting.'); return end");
        Line(sb, "local gworldSlot = disp + hitv + UE5T.aoblen");
        Line(sb, "synchronize(function()");
        Line(sb, "  unregisterSymbol(UE5T.sym)");
        Line(sb, "  registerSymbol(UE5T.sym, gworldSlot)");
        Line(sb, "end)");
        Line(sb, "dbg(string.format('[UE5 Trainer] gworld slot = %X', gworldSlot))");
        Line(sb);

        // Shared globals (re-walk + read/write + freeze helpers)
        AppendGlobals(sb);

        Line(sb, "UE5T_ready = true");
        Line(sb, "dbg('[UE5 Trainer] setup OK')");
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndEnable(sb);

        // DISABLE: stop everything + drop the symbol
        BeginDisable(sb);
        Line(sb, "if UE5T then");
        Line(sb, "  if UE5T_stopKnob then UE5T_stopKnob('speed'); UE5T_stopKnob('grav'); UE5T_stopKnob('jump') end");
        Line(sb, "  local t = UE5T.god_timer");
        Line(sb, "  if t then t.destroy(); UE5T.god_timer = nil end");
        Line(sb, "  local ft = UE5T.fly_timer");
        Line(sb, "  if ft then ft.destroy(); UE5T.fly_timer = nil end");
        Line(sb, "  synchronize(function() unregisterSymbol(UE5T.sym) end)");
        Line(sb, "end");
        Line(sb, "UE5T_ready = false");
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndDisable(sb);
        return sb.ToString();
    }

    private static void AppendGlobals(StringBuilder sb)
    {
        Line(sb, "function UE5T_pawn()");
        Line(sb, "  local addr = readQword(UE5T.sym)   -- *(gworld slot) = UWorld*");
        Line(sb, "  if not addr or addr == 0 then return nil end");
        Line(sb, "  for _, h in ipairs(UE5T.toPawn) do");
        Line(sb, "    if h.deref then addr = readQword(addr + h.off) else addr = addr + h.off end");
        Line(sb, "    if not addr or addr == 0 then return nil end");
        Line(sb, "  end");
        Line(sb, "  return addr");
        Line(sb, "end");
        Line(sb, "function UE5T_deref(base, off)");
        Line(sb, "  local a = readQword(base + off)");
        Line(sb, "  if not a or a == 0 then return nil end");
        Line(sb, "  return a");
        Line(sb, "end");
        Line(sb, "function UE5T_rdv(a)");
        Line(sb, "  if UE5T.vecWidth == 8 then return readDouble(a) else return readFloat(a) end");
        Line(sb, "end");
        Line(sb, "function UE5T_wrv(a, v)");
        Line(sb, "  if UE5T.vecWidth == 8 then writeDouble(a, v) else writeFloat(a, v) end");
        Line(sb, "end");
        // Single-bit set/clear via pure arithmetic (mask is a power of two); CE Lua
        // has no bAnd/bOr/bNot, so this stays version-proof.
        Line(sb, "function UE5T_setbit(a, mask, want)");
        Line(sb, "  local b = readByte(a)");
        Line(sb, "  if not b then return end");
        Line(sb, "  local isSet = math.floor(b / mask) % 2");
        Line(sb, "  if want == 1 and isSet == 0 then writeByte(a, b + mask)");
        Line(sb, "  elseif want == 0 and isSet == 1 then writeByte(a, b - mask) end");
        Line(sb, "end");
        Line(sb, "function UE5T_stopKnob(key)");
        Line(sb, "  local t = UE5T[key..'_timer']");
        Line(sb, "  if t then t.destroy(); UE5T[key..'_timer'] = nil end");
        Line(sb, "  -- restore the captured natural base so DISABLE reverts the value");
        Line(sb, "  -- (and a later re-enable re-captures the natural base, not a forced one)");
        Line(sb, "  local base = UE5T[key..'_base']");
        Line(sb, "  local off = UE5T[key..'_off']");
        Line(sb, "  if base and off then");
        Line(sb, "    local p = UE5T_pawn()");
        Line(sb, "    local c = p and UE5T_deref(p, UE5T.cmcOff)");
        Line(sb, "    if c then writeFloat(c + off, base) end");
        Line(sb, "  end");
        Line(sb, "  UE5T[key..'_pawn'] = nil");
        Line(sb, "  UE5T[key..'_base'] = nil");
        Line(sb, "  UE5T[key..'_off'] = nil");
        Line(sb, "end");
        Line(sb, "function UE5T_startKnob(key, off, mult)");
        Line(sb, "  UE5T_stopKnob(key)");
        Line(sb, "  UE5T[key..'_off'] = off   -- remembered so UE5T_stopKnob can restore");
        Line(sb, "  local t = createTimer(nil, false)");
        Line(sb, "  t.Interval = UE5T.tickMs");
        Line(sb, "  t.OnTimer = function()");
        Line(sb, "    local p = UE5T_pawn(); if not p then return end");
        Line(sb, "    local c = UE5T_deref(p, UE5T.cmcOff); if not c then return end");
        Line(sb, "    local a = c + off");
        Line(sb, "    if UE5T[key..'_pawn'] ~= p then");
        Line(sb, "      UE5T[key..'_base'] = readFloat(a)   -- (re)capture natural base on (re)spawn");
        Line(sb, "      UE5T[key..'_pawn'] = p");
        Line(sb, "    end");
        Line(sb, "    local base = UE5T[key..'_base']");
        Line(sb, "    if base then writeFloat(a, base * mult) end");
        Line(sb, "  end");
        Line(sb, "  t.Enabled = true");
        Line(sb, "  UE5T[key..'_timer'] = t");
        Line(sb, "end");
    }

    // ---- Movement knob (Move Speed / Low Gravity) ----
    private static string BuildKnob(string key, int off, string mult)
    {
        var sb = new StringBuilder(1024);
        BeginEnable(sb);
        Line(sb, "if not UE5T_ready then showMessage('[UE5 Trainer] Enable Setup first.'); return end");
        Line(sb, $"UE5T_startKnob('{key}', {Hex(off)}, {mult})");
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndEnable(sb);
        BeginDisable(sb);
        Line(sb, $"if UE5T_stopKnob then UE5T_stopKnob('{key}') end");
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndDisable(sb);
        return sb.ToString();
    }

    // ---- Super Jump: JumpZVelocity scales with sqrt(height) ----
    private static string BuildJump(int off, string height)
    {
        var sb = new StringBuilder(1024);
        BeginEnable(sb);
        Line(sb, "if not UE5T_ready then showMessage('[UE5 Trainer] Enable Setup first.'); return end");
        Line(sb, $"local HEIGHT = {height}            -- edit: jump-height multiplier");
        Line(sb, "local MULT = math.sqrt(HEIGHT)   -- velocity scales with sqrt(height)");
        Line(sb, $"UE5T_startKnob('jump', {Hex(off)}, MULT)");
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndEnable(sb);
        BeginDisable(sb);
        Line(sb, "if UE5T_stopKnob then UE5T_stopKnob('jump') end");
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndDisable(sb);
        return sb.ToString();
    }

    // ---- God Mode: freeze every matched protection bit ----
    private static string BuildGodMode()
    {
        var sb = new StringBuilder(1024);
        BeginEnable(sb);
        Line(sb, "if not UE5T_ready then showMessage('[UE5 Trainer] Enable Setup first.'); return end");
        Line(sb, "if #UE5T.godBits == 0 then showMessage('[UE5 Trainer] No protection bit was resolved.'); return end");
        Line(sb, "local old = UE5T.god_timer");
        Line(sb, "if old then old.destroy(); UE5T.god_timer = nil end");
        Line(sb, "local t = createTimer(nil, false)");
        Line(sb, "t.Interval = UE5T.tickMs");
        Line(sb, "t.OnTimer = function()");
        Line(sb, "  local p = UE5T_pawn(); if not p then return end");
        Line(sb, "  for _, g in ipairs(UE5T.godBits) do UE5T_setbit(p + g.off, g.mask, g.protect) end");
        Line(sb, "end");
        Line(sb, "t.Enabled = true");
        Line(sb, "UE5T.god_timer = t");
        Line(sb, "dbg('[UE5 Trainer] GodMode ON')");
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndEnable(sb);
        BeginDisable(sb);
        Line(sb, "local t = UE5T and UE5T.god_timer");
        Line(sb, "if t then t.destroy(); UE5T.god_timer = nil end");
        Line(sb, "if UE5T_ready then");
        Line(sb, "  local p = UE5T_pawn()");
        Line(sb, "  if p then for _, g in ipairs(UE5T.godBits) do UE5T_setbit(p + g.off, g.mask, 1 - g.protect) end end");
        Line(sb, "end");
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndDisable(sb);
        return sb.ToString();
    }

    // ---- Fly: pure-Lua velocity-drive, view-relative (no DLL, no ProcessEvent) ----
    // Holds MOVE_Flying(5) on the CMC and drives its Velocity from WASD + Z/C each
    // ~60 Hz tick, projected onto the camera (ControlRotation) yaw. Turn with the
    // mouse. No Noclip here — that needs SetActorEnableCollision (a ProcessEvent
    // invoke the standalone path avoids). Best-effort: a game that overwrites
    // Velocity every frame (e.g. FF7 Rebirth) won't move — use the DLL Fly instead.
    private static string BuildFly()
    {
        var sb = new StringBuilder(2048);
        BeginEnable(sb);
        Line(sb, "if not UE5T_ready then showMessage('[UE5 Trainer] Enable Setup first.'); return end");
        Line(sb, "if not isKeyPressed then showMessage('[UE5 Trainer] Fly needs CE isKeyPressed -- update Cheat Engine.'); return end");
        Line(sb, "local old = UE5T.fly_timer");
        Line(sb, "if old then old.destroy(); UE5T.fly_timer = nil end");
        Line(sb, "UE5T.fly_pawn = nil   -- re-capture the natural MovementMode on (re)enable");
        Line(sb, "local t = createTimer(nil, false)");
        Line(sb, "t.Interval = UE5T.flyTickMs");
        Line(sb, "t.OnTimer = function()");
        Line(sb, "  local p = UE5T_pawn(); if not p then return end");
        Line(sb, "  local c = UE5T_deref(p, UE5T.cmcOff); if not c then return end");
        Line(sb, "  -- capture the pawn's natural MovementMode once, then force MOVE_Flying(5)");
        Line(sb, "  if UE5T.fly_pawn ~= p then UE5T.fly_baseMode = readByte(c + UE5T.moveModeOff) or 1; UE5T.fly_pawn = p end");
        Line(sb, "  writeByte(c + UE5T.moveModeOff, 5)");
        Line(sb, "  -- view-relative basis: yaw = ControlRotation (2nd component) on the Controller");
        Line(sb, "  local yaw = 0");
        Line(sb, "  local ctrl = UE5T_deref(p, UE5T.controllerOff)");
        Line(sb, "  if ctrl then yaw = UE5T_rdv(ctrl + UE5T.ctrlRotOff + UE5T.vecWidth) or 0 end");
        Line(sb, "  local r = math.rad(yaw)");
        Line(sb, "  local fx, fy = math.cos(r), math.sin(r)     -- forward (yaw)");
        Line(sb, "  local rx, ry = -math.sin(r), math.cos(r)    -- right");
        Line(sb, "  local vx, vy, vz = 0, 0, 0");
        Line(sb, "  if isKeyPressed(0x57) then vx = vx + fx; vy = vy + fy end   -- W forward");
        Line(sb, "  if isKeyPressed(0x53) then vx = vx - fx; vy = vy - fy end   -- S back");
        Line(sb, "  if isKeyPressed(0x44) then vx = vx + rx; vy = vy + ry end   -- D right");
        Line(sb, "  if isKeyPressed(0x41) then vx = vx - rx; vy = vy - ry end   -- A left");
        Line(sb, "  if isKeyPressed(0x5A) then vz = vz + 1 end                  -- Z up");
        Line(sb, "  if isKeyPressed(0x43) then vz = vz - 1 end                  -- C down");
        Line(sb, "  local len = math.sqrt(vx*vx + vy*vy + vz*vz)");
        Line(sb, "  if len > 0.0001 then local s = UE5T.flySpeed / len; vx = vx*s; vy = vy*s; vz = vz*s end");
        Line(sb, "  UE5T_wrv(c + UE5T.velOff, vx)");
        Line(sb, "  UE5T_wrv(c + UE5T.velOff + UE5T.vecWidth, vy)");
        Line(sb, "  UE5T_wrv(c + UE5T.velOff + 2 * UE5T.vecWidth, vz)");
        Line(sb, "end");
        Line(sb, "t.Enabled = true");
        Line(sb, "UE5T.fly_timer = t");
        Line(sb, "dbg('[UE5 Trainer] Fly ON (WASD + Z/C, turn with mouse)')");
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndEnable(sb);
        BeginDisable(sb);
        Line(sb, "local t = UE5T and UE5T.fly_timer");
        Line(sb, "if t then t.destroy(); UE5T.fly_timer = nil end");
        Line(sb, "if UE5T_ready and UE5T.fly_pawn then");
        Line(sb, "  local p = UE5T_pawn()");
        Line(sb, "  local c = p and UE5T_deref(p, UE5T.cmcOff)");
        Line(sb, "  if c then");
        Line(sb, "    writeByte(c + UE5T.moveModeOff, UE5T.fly_baseMode or 1)   -- restore MovementMode");
        Line(sb, "    UE5T_wrv(c + UE5T.velOff, 0)");
        Line(sb, "    UE5T_wrv(c + UE5T.velOff + UE5T.vecWidth, 0)");
        Line(sb, "    UE5T_wrv(c + UE5T.velOff + 2 * UE5T.vecWidth, 0)");
        Line(sb, "  end");
        Line(sb, "end");
        Line(sb, "UE5T.fly_pawn = nil");
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndDisable(sb);
        return sb.ToString();
    }

    // ---- Coordinate TP: Save current RelativeLocation ----
    private static string BuildTpSave()
    {
        var sb = new StringBuilder(1024);
        BeginEnable(sb);
        AppendTpWeakNote(sb);
        AppendUntick(sb, SaveDescription);
        Line(sb, "if not UE5T_ready then showMessage('[UE5 Trainer] Enable Setup first.'); return end");
        Line(sb, "local p = UE5T_pawn()");
        Line(sb, "local rc = p and UE5T_deref(p, UE5T.rootOff)");
        Line(sb, "if rc then");
        Line(sb, "  local a = rc + UE5T.relLocOff");
        Line(sb, "  UE5T.savedX = UE5T_rdv(a)");
        Line(sb, "  UE5T.savedY = UE5T_rdv(a + UE5T.vecWidth)");
        Line(sb, "  UE5T.savedZ = UE5T_rdv(a + 2 * UE5T.vecWidth)");
        Line(sb, "  dbg('[UE5 Trainer] saved position')");
        Line(sb, "else");
        Line(sb, "  showMessage('[UE5 Trainer] Could not resolve pawn/RootComponent to save.')");
        Line(sb, "end");
        Line(sb, $"if DEBUG == 0 and rc then {Close} end");
        EndEnable(sb);
        BeginDisable(sb);
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndDisable(sb);
        return sb.ToString();
    }

    // ---- Coordinate TP: Recall saved RelativeLocation ----
    private static string BuildTpRecall()
    {
        var sb = new StringBuilder(1024);
        BeginEnable(sb);
        AppendTpWeakNote(sb);
        AppendUntick(sb, RecallDescription);
        Line(sb, "if not UE5T_ready then showMessage('[UE5 Trainer] Enable Setup first.'); return end");
        Line(sb, "if not UE5T.savedX then showMessage('[UE5 Trainer] No saved position -- run TP Save first.'); return end");
        Line(sb, "local p = UE5T_pawn()");
        Line(sb, "local rc = p and UE5T_deref(p, UE5T.rootOff)");
        Line(sb, "if rc then");
        Line(sb, "  local a = rc + UE5T.relLocOff");
        Line(sb, "  UE5T_wrv(a, UE5T.savedX)");
        Line(sb, "  UE5T_wrv(a + UE5T.vecWidth, UE5T.savedY)");
        Line(sb, "  UE5T_wrv(a + 2 * UE5T.vecWidth, UE5T.savedZ)");
        Line(sb, "  dbg('[UE5 Trainer] recalled')");
        Line(sb, "else");
        Line(sb, "  showMessage('[UE5 Trainer] Could not resolve pawn/RootComponent to recall.')");
        Line(sb, "end");
        Line(sb, $"if DEBUG == 0 and rc then {Close} end");
        EndEnable(sb);
        BeginDisable(sb);
        Line(sb, $"if DEBUG == 0 then {Close} end");
        EndDisable(sb);
        return sb.ToString();
    }

    // ---- shared emit helpers ----

    private static void BeginEnable(StringBuilder sb)
    {
        Line(sb, "[ENABLE]");
        Line(sb, "{$lua}");
        Line(sb, "if syntaxcheck then return end");
        CeLuaHygiene.AppendDebugPreamble(sb);
        CeLuaHygiene.AppendAttribution(sb);
    }

    // Coordinate TP here is a RAW memory write (no DLL) — call out that it's the
    // weaker path so users understand a non-moving character isn't a bug.
    private static void AppendTpWeakNote(StringBuilder sb)
    {
        Line(sb, "-- NOTE: this coordinate teleport is a RAW memory write (no DLL) and is");
        Line(sb, "-- WEAKER than the in-app Teleport tab: on games that don't refresh the");
        Line(sb, "-- cached world transform from RelativeLocation, the coords change but the");
        Line(sb, "-- character may not visibly move. The DLL path calls the engine's own");
        Line(sb, "-- teleport functions (ProcessEvent) and is more reliable.");
    }

    private static void EndEnable(StringBuilder sb)
    {
        Line(sb, "{$asm}");
        Line(sb);
    }

    private static void BeginDisable(StringBuilder sb)
    {
        Line(sb, "[DISABLE]");
        Line(sb, "{$lua}");
        Line(sb, "if syntaxcheck then return end");
        CeLuaHygiene.AppendDebugPreamble(sb);
    }

    private static void EndDisable(StringBuilder sb) => Line(sb, "{$asm}");

    // Momentary: after the action runs, untick this entry ~50ms later (fires once).
    private static void AppendUntick(StringBuilder sb, string desc)
    {
        Line(sb, "createTimer(50, function()");
        Line(sb, $"  local mr = getAddressList().getMemoryRecordByDescription('{desc}')");
        Line(sb, "  if mr then mr.Active = false end");
        Line(sb, "end)");
    }

    // AOBScanModuleUE — idempotent self-contained AOB scanner (mirrors the GWorld-walk
    // export's helper so behaviour is identical).
    private static void AppendAobScanHelper(StringBuilder sb)
    {
        Line(sb, "if not AOBScanModuleUE then");
        Line(sb, "  function AOBScanModuleUE(moduleName, signature)");
        Line(sb, "    local baseAddr = nil");
        Line(sb, "    local maxAddr = 0");
        Line(sb, "    local modList");
        Line(sb, "    synchronize(function() modList = enumModules() end)");
        Line(sb, "    for _, mod in ipairs(modList) do");
        Line(sb, "      if string.lower(mod.Name) == string.lower(moduleName) then");
        Line(sb, "        baseAddr = mod.Address");
        Line(sb, "        maxAddr = baseAddr + mod.Size");
        Line(sb, "        break");
        Line(sb, "      end");
        Line(sb, "    end");
        Line(sb, "    if not baseAddr then return nil end");
        Line(sb, "    local ms = createMemScan()");
        Line(sb, "    synchronize(function()");
        Line(sb, "      ms.firstScan(soExactValue, vtByteArray, nil, signature,");
        Line(sb, "        nil, baseAddr, maxAddr, '+X-C-W', fsmNotAligned, '1', true, true, false, false)");
        Line(sb, "    end)");
        Line(sb, "    ms.waitTillDone()");
        Line(sb, "    local results = createFoundList(ms)");
        Line(sb, "    results.initialize()");
        Line(sb, "    local addr");
        Line(sb, "    synchronize(function()");
        Line(sb, "      if results.getCount() ~= 0 then addr = results[0] end");
        Line(sb, "    end)");
        Line(sb, "    results.destroy()");
        Line(sb, "    ms.destroy()");
        Line(sb, "    return addr");
        Line(sb, "  end");
        Line(sb, "end");
        Line(sb);
    }

    private static string LuaModule(string? module) =>
        string.IsNullOrWhiteSpace(module) ? "nil" : $"'{module}'";

    private static string Hex(int v) =>
        v < 0 ? "-1" : "0x" + v.ToString("X", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder sb, string text = "")
    {
        sb.Append(text);
        sb.Append('\n');   // LF-only for CE compatibility
    }
}
