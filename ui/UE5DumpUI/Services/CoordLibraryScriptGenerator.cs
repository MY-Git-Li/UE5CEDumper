using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Emits the coordinate library as a self-contained CE AA Script: a fenced,
/// hand-editable data block followed by a generic picker form that filters the list
/// and teleports to the chosen entry via the DLL mailbox.
///
/// Format and its rationale: docs/teleport-coord-library-spec.md §4.
///
/// The fenced block is SIMULTANEOUSLY the runtime data and the round-trip source —
/// there is no second copy to drift. Shape adapted from AOBMaker's
/// <c>@AOBMAKER:AA_TOGGLE v1</c> block (named fields, brace-balanced parse), which
/// solves the same problem, but in OUR namespace: AOBMaker's end marker is the
/// feature-less, shared <c>-- @AOBMAKER:END</c> matched by unanchored IndexOf, so a
/// block of ours pasted into an AA Toggle script would make AOBMaker slice from its
/// own start marker to our END, parse zero entries, and silently untick every record
/// in the tree.
///
/// UI is built only from CE controls verified in the wild (CrimsonDesert.CT
/// CheatEntry 357): createForm / createPanel / createLabel / createEdit /
/// createRadioGroup / createButton / createListView. Notably NOT createListBox or
/// createComboBox — neither appears anywhere in this repo and the project rule is to
/// never invent a CE Lua API.
/// </summary>
public static class CoordLibraryScriptGenerator
{
    public const string MarkerStart = "-- @UE5CD:COORDS v1";
    public const string MarkerEnd = "-- @UE5CD:END";
    public const string GeneratedSeparator = "---- GENERATED CODE (do not edit below) ----";

    /// <summary>Current data-format version, emitted in <see cref="MarkerStart"/>.</summary>
    public const int FormatVersion = 1;

    /// <summary>Groups offered as RadioGroup buttons besides "All". Beyond this the
    /// overflow stays reachable via All + the filter box (the group name is part of
    /// the search key), and the caller is told what was folded.</summary>
    public const int MaxGroupButtons = 8;

    // Mailbox layout — dll/src/Mimic.h (MailboxData / TeleportOp).
    private const int CmdTeleport = 8;
    private const int OpExplicit = 13;   // TP_OP_EXPLICIT
    private const int OpGetPose = 0;     // TP_OP_GET_POSE (also yields the map name)

    /// <summary>Description used for the CE memory record.</summary>
    public const string RecordDescription = "Teleport: Coordinate Library (picker)";

    /// <summary>
    /// Build the full [ENABLE]/[DISABLE] AA script.
    /// </summary>
    /// <param name="entries">Entries to bake. Pre-sorted here, so the Lua never sorts.</param>
    /// <param name="foldedGroups">Receives group names that did NOT get a radio button.</param>
    public static string Generate(IEnumerable<CoordEntry> entries, out List<string> foldedGroups)
    {
        var list = entries?.ToList() ?? new List<CoordEntry>();

        // Sort at generation time: Group asc, then Label natural-ascending so
        // "Chest 2" precedes "Chest 10".
        list.Sort((a, b) =>
        {
            int g = string.Compare(a.Group, b.Group, StringComparison.OrdinalIgnoreCase);
            return g != 0 ? g : ViewModels.TeleportViewModel.NaturalCompare(a.Label, b.Label);
        });

        var groups = list
            .Select(e => e.Group)
            .Where(g => !string.IsNullOrEmpty(g))
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .ToList();

        var shownGroups = groups.Take(MaxGroupButtons).ToList();
        foldedGroups = groups.Skip(MaxGroupButtons).ToList();

        var sb = new StringBuilder(4096 + list.Count * 128);
        Line(sb, "[ENABLE]");
        Line(sb, "{$lua}");
        Line(sb, "if syntaxcheck then return end");
        CeLuaHygiene.AppendDebugPreamble(sb);
        Line(sb, $"-- {CeLuaHygiene.Attribution}");
        Line(sb);
        AppendDataBlock(sb, list, shownGroups);
        Line(sb);
        Line(sb, GeneratedSeparator);
        Line(sb);
        AppendPickerForm(sb, shownGroups.Count);
        Line(sb, "{$asm}");
        Line(sb, "[DISABLE]");
        Line(sb, "{$lua}");
        Line(sb, "-- Close the picker if it is still open.");
        Line(sb, "if UE5CD_form ~= nil and not UE5CD_form.Destroyed then UE5CD_form.close() end");
        Line(sb, "{$asm}");
        return sb.ToString();
    }

    // ── The fenced, hand-editable data block ────────────────────────────

    private static void AppendDataBlock(
        StringBuilder sb, List<CoordEntry> list, List<string> shownGroups)
    {
        Line(sb, MarkerStart);
        Line(sb, "-- Edit the rows below, or paste this whole script back into UE5CEDumper");
        Line(sb, "-- (Coordinate Library -> Import Lua) to load them into the editor.");
        Line(sb, "-- Field order does not matter and unknown keys are ignored, so a newer");
        Line(sb, "-- build's extra field will not break an older one. Keep uid if you want an");
        Line(sb, "-- edited row to UPDATE its library entry instead of adding a new one.");
        Line(sb, "local COORDS = {");
        foreach (var e in list)
        {
            sb.Append("  { uid='").Append(CeLuaHygiene.EscapeLuaString(e.Uid)).Append('\'')
              .Append(", label='").Append(CeLuaHygiene.EscapeLuaString(e.Label)).Append('\'')
              .Append(", group='").Append(CeLuaHygiene.EscapeLuaString(e.Group)).Append('\'')
              .Append(", map='").Append(CeLuaHygiene.EscapeLuaString(e.Map)).Append('\'')
              .Append(", x=").Append(Num(e.X))
              .Append(", y=").Append(Num(e.Y))
              .Append(", z=").Append(Num(e.Z))
              .Append(", pitch=").Append(Num(e.Pitch))
              .Append(", yaw=").Append(Num(e.Yaw))
              .Append(", roll=").Append(Num(e.Roll))
              .Append(" },\n");
        }
        Line(sb, "}");
        Line(sb);
        Line(sb, "local GROUPS = {");
        foreach (var g in shownGroups)
            Line(sb, $"  '{CeLuaHygiene.EscapeLuaString(g)}',");
        Line(sb, "}");
        Line(sb, MarkerEnd);
    }

    /// <summary>Canonical numeric text — rounded, then shortest-round-trippable, so a
    /// value exported here matches the CSV and the JSON store byte for byte.</summary>
    private static string Num(double v) => CoordPrecision.Text(CoordPrecision.Round(v));

    // ── The generic picker form ─────────────────────────────────────────

    private static void AppendPickerForm(StringBuilder sb, int groupCount)
    {
        // Offsets come from CeMailboxLayout (the single source of truth mirroring
        // dll/src/Mimic.h) -- never hand-written here.
        Line(sb, $"local MB_CMD, MB_STATUS, MB_RESULT = {CeMailboxLayout.OffCmd}, " +
                 $"{CeMailboxLayout.OffStatus}, {CeMailboxLayout.OffResult}");
        Line(sb, $"local MB_UFUNC, MB_INST, MB_PARAMS = {CeMailboxLayout.OffUfuncAddr}, " +
                 $"{CeMailboxLayout.OffInstanceAddr}, {CeMailboxLayout.OffParamsData}");
        Line(sb, "local DISPLAY_CAP = 2000   -- rows rendered at once; filter to narrow");
        Line(sb);
        Line(sb, "-- Reuse the window if it is already open (re-enabling the record).");
        Line(sb, "if UE5CD_form ~= nil and not UE5CD_form.Destroyed then");
        Line(sb, "  UE5CD_form.show(); UE5CD_form.BringToFront(); return");
        Line(sb, "end");
        Line(sb);
        Line(sb, "local function mailbox()");
        Line(sb, "  local mb = getAddressSafe('g_invokeMailbox')");
        Line(sb, "  if not mb or mb == 0 then mb = getAddressSafe('UE5Dumper.g_invokeMailbox') end");
        Line(sb, "  if not mb or mb == 0 then return nil end");
        Line(sb, "  return mb");
        Line(sb, "end");
        Line(sb);
        Line(sb, "-- Round-trip one mailbox command. Returns the Wirbel result code, or nil");
        Line(sb, "-- on timeout / no DLL. A timeout is an ERROR: never fall through to a close.");
        Line(sb, "local function call(op, writeParams)");
        Line(sb, "  local mb = mailbox()");
        Line(sb, "  if mb == nil then return nil, 'g_invokeMailbox not found -- is UE5Dumper.dll injected?' end");
        Line(sb, "  if readInteger(mb + MB_CMD) ~= 0 then return nil, 'the DLL mailbox is busy' end");
        Line(sb, "  if writeParams then writeParams(mb + MB_PARAMS) end");
        Line(sb, "  writeQword(mb + MB_UFUNC, 0)");
        Line(sb, "  writeQword(mb + MB_INST, op)");
        Line(sb, "  writeInteger(mb + MB_STATUS, 0)");
        Line(sb, $"  writeInteger(mb + MB_CMD, {CmdTeleport})   -- write LAST");
        Line(sb, "  local waited = 0");
        Line(sb, "  while readInteger(mb + MB_STATUS) ~= 1 do");
        Line(sb, "    sleep(1); waited = waited + 1");
        Line(sb, $"    if waited >= {CeMailboxLayout.MailboxPollTimeoutMs} then return nil, 'the DLL did not respond' end");
        Line(sb, "  end");
        Line(sb, "  return readInteger(mb + MB_RESULT), nil, mb");
        Line(sb, "end");
        Line(sb);
        Line(sb, "-- Current map, read from GET_POSE's pose block (map name at params+48).");
        Line(sb, "local function currentMap()");
        Line(sb, $"  local code, err, mb = call({OpGetPose}, nil)");
        Line(sb, "  if code ~= 0 or mb == nil then return nil end");
        Line(sb, "  return readString(mb + MB_PARAMS + 48, 127, false)");
        Line(sb, "end");
        Line(sb);
        Line(sb, "local function teleport(e)");
        Line(sb, $"  local code, err = call({OpExplicit}, function(pd)");
        Line(sb, "    writeDouble(pd + 0,  e.x)");
        Line(sb, "    writeDouble(pd + 8,  e.y)");
        Line(sb, "    writeDouble(pd + 16, e.z)");
        Line(sb, "    writeDouble(pd + 24, e.pitch)");
        Line(sb, "    writeDouble(pd + 32, e.yaw)");
        Line(sb, "    writeDouble(pd + 40, e.roll)");
        Line(sb, "    writeBytes(pd + 48, 1)   -- hasRot: restore the saved rotation");
        Line(sb, "  end)");
        Line(sb, "  return code, err");
        Line(sb, "end");
        Line(sb);
        Line(sb, "-- space = AND over label + group + map, matching the app's filter rule.");
        Line(sb, "for i = 1, #COORDS do");
        Line(sb, "  local e = COORDS[i]");
        Line(sb, "  e.key = string.lower(e.label .. ' ' .. e.group .. ' ' .. e.map)");
        Line(sb, "end");
        Line(sb);
        Line(sb, "local function terms(text)");
        Line(sb, "  local t = {}");
        Line(sb, "  for w in string.gmatch(string.lower(text or ''), '%S+') do t[#t + 1] = w end");
        Line(sb, "  return t");
        Line(sb, "end");
        Line(sb);
        Line(sb, "local function matches(e, ts)");
        Line(sb, "  for i = 1, #ts do");
        Line(sb, "    if string.find(e.key, ts[i], 1, true) == nil then return false end");
        Line(sb, "  end");
        Line(sb, "  return true");
        Line(sb, "end");
        Line(sb);
        Line(sb, "synchronize(function()");
        Line(sb, "  local mapNow = currentMap()");
        Line(sb);
        Line(sb, "  UE5CD_form = createForm(false)");
        Line(sb, "  UE5CD_form.Caption = 'UE5CEDumper -- Coordinate Library'");
        Line(sb, "  UE5CD_form.Width = 820");
        Line(sb, "  UE5CD_form.Height = 560");
        Line(sb, "  UE5CD_form.Position = 'poScreenCenter'");
        Line(sb);
        Line(sb, "  -- Panel creation order is load-bearing: alTop panels stack in creation");
        Line(sb, "  -- order and the alClient control must be created LAST.");
        Line(sb, "  local pnlTop = createPanel(UE5CD_form)");
        Line(sb, "  pnlTop.Align = 'alTop'");
        Line(sb, "  pnlTop.Height = " + (groupCount > 0 ? "96" : "40"));
        Line(sb, "  pnlTop.BevelOuter = 'bvNone'");
        Line(sb);
        Line(sb, "  local lblFilter = createLabel(pnlTop)");
        Line(sb, "  lblFilter.Caption = 'Filter:'");
        Line(sb, "  lblFilter.Left = 10");
        Line(sb, "  lblFilter.Top = 12");
        Line(sb);
        Line(sb, "  local edtFilter = createEdit(pnlTop)");
        Line(sb, "  edtFilter.Left = 60");
        Line(sb, "  edtFilter.Top = 8");
        Line(sb, "  edtFilter.Width = 300");
        Line(sb);
        Line(sb, "  local rgMap = createRadioGroup(pnlTop)");
        Line(sb, "  rgMap.Left = 380");
        Line(sb, "  rgMap.Top = 2");
        Line(sb, "  rgMap.Width = 260");
        Line(sb, "  rgMap.Height = 36");
        Line(sb, "  rgMap.Columns = 2");
        Line(sb, "  rgMap.Items.add('Current map')");
        Line(sb, "  rgMap.Items.add('All maps')");
        Line(sb, "  rgMap.ItemIndex = (mapNow ~= nil and mapNow ~= '') and 0 or 1");
        if (groupCount > 0)
        {
            Line(sb);
            Line(sb, "  local rgGroup = createRadioGroup(pnlTop)");
            Line(sb, "  rgGroup.Left = 10");
            Line(sb, "  rgGroup.Top = 40");
            Line(sb, "  rgGroup.Width = 790");
            Line(sb, "  rgGroup.Height = 52");
            Line(sb, "  rgGroup.Caption = 'Group'");
            Line(sb, "  rgGroup.Columns = 5");
            Line(sb, "  rgGroup.Items.add('All')");
            Line(sb, "  for i = 1, #GROUPS do rgGroup.Items.add(GROUPS[i]) end");
            Line(sb, "  rgGroup.ItemIndex = 0");
        }
        Line(sb);
        Line(sb, "  local pnlStatus = createPanel(UE5CD_form)");
        Line(sb, "  pnlStatus.Align = 'alTop'");
        Line(sb, "  pnlStatus.Height = 24");
        Line(sb, "  pnlStatus.BevelOuter = 'bvNone'");
        Line(sb, "  local lblCount = createLabel(pnlStatus)");
        Line(sb, "  lblCount.Align = 'alClient'");
        Line(sb, "  lblCount.Caption = 'Type to filter (space = AND)'");
        Line(sb);
        Line(sb, "  local pnlBottom = createPanel(UE5CD_form)");
        Line(sb, "  pnlBottom.Align = 'alBottom'");
        Line(sb, "  pnlBottom.Height = 50");
        Line(sb, "  pnlBottom.BevelOuter = 'bvNone'");
        Line(sb);
        Line(sb, "  local btnGo = createButton(pnlBottom)");
        Line(sb, "  btnGo.Caption = 'Teleport'");
        Line(sb, "  btnGo.Left = 10");
        Line(sb, "  btnGo.Top = 8");
        Line(sb, "  btnGo.Width = 120");
        Line(sb, "  btnGo.Height = 32");
        Line(sb);
        Line(sb, "  local btnForce = createButton(pnlBottom)");
        Line(sb, "  btnForce.Caption = 'Force teleport'");
        Line(sb, "  btnForce.Left = 140");
        Line(sb, "  btnForce.Top = 8");
        Line(sb, "  btnForce.Width = 130");
        Line(sb, "  btnForce.Height = 32");
        Line(sb);
        Line(sb, "  local btnClose = createButton(pnlBottom)");
        Line(sb, "  btnClose.Caption = 'Close'");
        Line(sb, "  btnClose.Left = 290");
        Line(sb, "  btnClose.Top = 8");
        Line(sb, "  btnClose.Width = 90");
        Line(sb, "  btnClose.Height = 32");
        Line(sb);
        Line(sb, "  -- alClient LAST so it fills what is left.");
        Line(sb, "  local lv = createListView(UE5CD_form)");
        Line(sb, "  lv.Align = 'alClient'");
        Line(sb, "  lv.ViewStyle = 'vsReport'");
        Line(sb, "  lv.RowSelect = true");
        Line(sb, "  lv.ReadOnly = true");
        Line(sb, "  lv.Columns.add().Caption = 'Label'");
        Line(sb, "  lv.Columns[0].Width = 220");
        Line(sb, "  lv.Columns.add().Caption = 'Group'");
        Line(sb, "  lv.Columns[1].Width = 120");
        Line(sb, "  lv.Columns.add().Caption = 'Map'");
        Line(sb, "  lv.Columns[2].Width = 150");
        Line(sb, "  lv.Columns.add().Caption = 'X'");
        Line(sb, "  lv.Columns[3].Width = 100");
        Line(sb, "  lv.Columns.add().Caption = 'Y'");
        Line(sb, "  lv.Columns[4].Width = 100");
        Line(sb, "  lv.Columns.add().Caption = 'Z'");
        Line(sb, "  lv.Columns[5].Width = 90");
        Line(sb);
        Line(sb, "  local shown = {}   -- listview row index -> COORDS entry");
        Line(sb);
        Line(sb, "  local function rebuild()");
        Line(sb, "    local ts = terms(edtFilter.Text)");
        Line(sb, "    local wantGroup = nil");
        if (groupCount > 0)
        {
            Line(sb, "    if rgGroup.ItemIndex > 0 then wantGroup = GROUPS[rgGroup.ItemIndex] end");
        }
        Line(sb, "    local currentOnly = (rgMap.ItemIndex == 0) and mapNow ~= nil and mapNow ~= ''");
        Line(sb);
        Line(sb, "    lv.Items.beginUpdate()");
        Line(sb, "    lv.Items.clear()");
        Line(sb, "    shown = {}");
        Line(sb, "    local matched = 0");
        Line(sb, "    for i = 1, #COORDS do");
        Line(sb, "      local e = COORDS[i]");
        Line(sb, "      local ok = true");
        Line(sb, "      if currentOnly and string.lower(e.map) ~= string.lower(mapNow) then ok = false end");
        Line(sb, "      if ok and wantGroup ~= nil and string.lower(e.group) ~= string.lower(wantGroup) then ok = false end");
        Line(sb, "      if ok and not matches(e, ts) then ok = false end");
        Line(sb, "      if ok then");
        Line(sb, "        matched = matched + 1");
        Line(sb, "        if matched <= DISPLAY_CAP then");
        Line(sb, "          local row = lv.Items.add()");
        Line(sb, "          row.Caption = e.label");
        Line(sb, "          row.SubItems.add(e.group)");
        Line(sb, "          row.SubItems.add(e.map)");
        Line(sb, "          row.SubItems.add(string.format('%.3f', e.x))");
        Line(sb, "          row.SubItems.add(string.format('%.3f', e.y))");
        Line(sb, "          row.SubItems.add(string.format('%.3f', e.z))");
        Line(sb, "          shown[#shown + 1] = e");
        Line(sb, "        end");
        Line(sb, "      end");
        Line(sb, "    end");
        Line(sb, "    lv.Items.endUpdate()");
        Line(sb);
        Line(sb, "    if matched > DISPLAY_CAP then");
        Line(sb, "      lblCount.Caption = string.format(");
        Line(sb, "        'Matched %d (showing first %d) -- type more to narrow', matched, DISPLAY_CAP)");
        Line(sb, "    else");
        Line(sb, "      lblCount.Caption = string.format('Matched %d / %d', matched, #COORDS)");
        Line(sb, "    end");
        Line(sb, "  end");
        Line(sb);
        Line(sb, "  local function selected()");
        Line(sb, "    local idx = lv.ItemIndex");
        Line(sb, "    if idx == nil or idx < 0 then return nil end");
        Line(sb, "    return shown[idx + 1]");
        Line(sb, "  end");
        Line(sb);
        Line(sb, "  -- Confirm-before-teleport: plain Teleport refuses a cross-map jump. The");
        Line(sb, "  -- tool cannot LOAD another map -- only the game can -- so this is a guard,");
        Line(sb, "  -- not an inconvenience.");
        Line(sb, "  local function go(force)");
        Line(sb, "    local e = selected()");
        Line(sb, "    if e == nil then lblCount.Caption = 'Select an entry first.'; return end");
        Line(sb, "    if (not force) and mapNow ~= nil and mapNow ~= ''");
        Line(sb, "       and string.lower(e.map) ~= string.lower(mapNow) then");
        Line(sb, "      showMessage('\"' .. e.label .. '\" was saved on \"' .. e.map ..");
        Line(sb, "                  '\" but you are on \"' .. mapNow ..");
        Line(sb, "                  '\".\\n\\nUse Force teleport to override -- note this cannot load' ..");
        Line(sb, "                  ' the other map, it only moves you within the current one.')");
        Line(sb, "      return");
        Line(sb, "    end");
        Line(sb, "    local code, err = teleport(e)");
        Line(sb, "    if err ~= nil then showMessage('[Coordinate Library] ' .. err); return end");
        Line(sb, "    if code ~= 0 then");
        Line(sb, "      lblCount.Caption = string.format('Teleport failed (code %d).', code)");
        Line(sb, "    else");
        Line(sb, "      lblCount.Caption = 'Teleported to ' .. e.label");
        Line(sb, "      dbg('[Coordinate Library] -> ' .. e.label)");
        Line(sb, "    end");
        Line(sb, "  end");
        Line(sb);
        Line(sb, "  edtFilter.OnChange = rebuild");
        Line(sb, "  rgMap.OnClick = rebuild");
        if (groupCount > 0) Line(sb, "  rgGroup.OnClick = rebuild");
        Line(sb, "  btnGo.OnClick = function() go(false) end");
        Line(sb, "  btnForce.OnClick = function() go(true) end");
        Line(sb, "  lv.OnDblClick = function() go(false) end");
        Line(sb, "  btnClose.OnClick = function() UE5CD_form.close() end");
        Line(sb);
        Line(sb, "  -- Interactive form: the untick + window close belong on OnClose, NOT on a");
        Line(sb, "  -- momentary timer -- the window must stay open while the user is using it.");
        Line(sb, "  UE5CD_form.OnClose = function(sender)");
        Line(sb, "    if memrec ~= nil then memrec.Active = false end");
        Line(sb, "    return caFree");
        Line(sb, "  end");
        Line(sb);
        Line(sb, "  rebuild()");
        Line(sb, "  UE5CD_form.ActiveControl = edtFilter");
        Line(sb, "  UE5CD_form.show()");
        Line(sb, "end)");
    }

    private static void Line(StringBuilder sb, string text = "")
    {
        sb.Append(text);
        sb.Append('\n');
    }
}
