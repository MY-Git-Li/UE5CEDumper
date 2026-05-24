--[[
  ue5_freeze_helper.lua
  UE5CEDumper -- Property Freeze Helper (class-wide horizontal freeze)

  Companion to ue5_invoke_helper.lua. Generated AA Scripts produced by
  UE5DumpUI's "Copy Freeze Script" button (PropertySearch row) depend
  on THIS file being embedded in your CE table.

  What this helper does, that a plain CE pointer-list freeze does NOT:

    * Plain CE freeze locks ONE address (one instance, one offset).
      If the game frees that instance (respawn, level reload, death),
      the address becomes a dangling pointer and the freeze silently
      breaks.

    * This helper locks a PROPERTY (offset + type) on ALL live
      instances of a given UE class. It re-enumerates instances every
      few seconds via the DLL mailbox (CMD_LIST_INSTANCES), so newly
      spawned teammates / pickups / NPCs are picked up automatically
      and freed instances drop off.

  Setup once per .CT:
    Option A (one-click, requires AOBMaker CE Plugin):
      UE5DumpUI -> Tools -> Inject Freeze Helper into Current CE Table
    Option B (manual):
      UE5DumpUI -> Tools -> Export Freeze Helper Lua File...
      then in Cheat Engine: Table -> Add File... -> select this file
    Save the .CT to bake the file into the table.

  Public API (re-declaration-safe, syntax-highlighted):
    handle = freezeProperty(cfg)
    handle.start()       -- begin tick + rescan timers
    handle.stop()        -- cancel both timers cleanly

  cfg fields:
    className          (req) string  exact UE class name (e.g. 'BP_Teammate_C')
                                     -- exact match, case-insensitive
    propOffset         (req) number  byte offset of property within instance
    valueType          (req) string  see TYPE_WRITERS table below
    value              (req) number  target value to write each tick
                                     -- for 'bool' accepts true/false or 0/1
    tickIntervalMs     opt   number  default 50  -- 20 writes/sec per instance
    refreshIntervalSec opt   number  default 5   -- rescan instances every 5s
    filter             opt   fn      function(addr) -> bool
                                     -- return true to include, false to skip

  Constants exposed:
    UE5_FREEZE_HELPER_VERSION = '1.0'

  =========================================================================
  SAMPLES -- copy/paste into your AA Script's [ENABLE] block, modify in place.
  =========================================================================

  ----- SAMPLE 1: Basic teammate HP freeze -----
    local h = freezeProperty({
      className  = 'BP_Teammate_C',
      propOffset = 0x4F8,
      valueType  = 'float',
      value      = 100.0,
    })
    h.start()

  ----- SAMPLE 2: God mode (bool) -----
    local h = freezeProperty({
      className  = 'PlayerCharacter',
      propOffset = 0x328,
      valueType  = 'bool',
      value      = false,    -- e.g. bCanBeDamaged = false  (or 0)
    })
    h.start()

  ----- SAMPLE 3: Filter -- only freeze teammates, NOT the local player -----
    -- localPawn is whatever pointer identifies "me". You'd discover
    -- this either by reading a known LocalPlayer chain or by capturing
    -- the address from CE before starting the freeze. Adjust the
    -- filter body to your game.
    local localPawn = 0x12345678  -- resolved elsewhere
    local h = freezeProperty({
      className  = 'BP_Teammate_C',
      propOffset = 0x4F8,
      valueType  = 'float',
      value      = 9999.0,
      filter     = function(addr) return addr ~= localPawn end,
    })
    h.start()

  ----- SAMPLE 4: Multi-property freeze in one script (HP + MP) -----
    local hp = freezeProperty({
      className='BP_Teammate_C', propOffset=0x4F8, valueType='float', value=100,
    })
    local mp = freezeProperty({
      className='BP_Teammate_C', propOffset=0x4FC, valueType='float', value=50,
    })
    hp.start(); mp.start()
    -- In [DISABLE]: hp.stop(); mp.stop()

  ----- SAMPLE 5: Editing className / offset / value after generation -----
    -- Generated AA Scripts contain a `local CFG = { ... }` block near
    -- the top. Edit any field there and reactivate the script -- the
    -- new cfg is read fresh on every [ENABLE]. Use UE5DumpUI's
    -- PropertySearch panel to discover a new offset, copy the value
    -- into CFG, done.
]]

if not UE5_FREEZE_HELPER_VERSION then
  UE5_FREEZE_HELPER_VERSION = '1.0'
end

-- ============================================================
-- Mailbox layout (MUST match dll/src/Mimic.h MailboxData)
-- Duplicated from ue5_invoke_helper.lua so this helper is
-- loadable on its own (no cross-helper dependency at load time).
-- ============================================================

local OFF_CMD        = 0x000
local OFF_STATUS     = 0x004
local OFF_RESULT     = 0x008
local OFF_PARMS_SZ   = 0x020  -- uint16: total count (LIST_INSTANCES output)
local OFF_NUM_PARMS  = 0x022  -- uint16: returned this page
local OFF_FUNC_FLAGS = 0x024  -- uint32: total pages
local OFF_CLASS      = 0x028
local OFF_ERR        = 0x228
local OFF_PARAMS     = 0x328

local CMD_LIST_INSTANCES = 6
local STATUS_DONE        = 1

-- Mailbox round-trip is bounded: GObjects walk + memcpy of <=1024 bytes.
-- 5 s is generous for a 2000-instance cap.
local DEFAULT_TIMEOUT_MS = 5000

-- Shared reentrancy flag with ue5_invoke_helper.lua. Whichever helper
-- loads first initialises it; subsequent loads see it already defined
-- and keep its current value (preserving any in-flight call state).
if _ue5_invoke_busy == nil then
  _ue5_invoke_busy = false
end

-- ============================================================
-- Type writers
-- ============================================================
-- valueType -> function(addr, value). Aliases (byte, dword, etc.) are
-- normalised through TYPE_ALIASES before lookup. v1 supports numeric +
-- bool only; FString / FName / struct fields are out of scope.

local function writeBool(addr, v)
  -- UE TBoolBase / bitfield-free bool occupies one byte (0 or 1).
  -- We do NOT support packed bitfield bools (multiple bools sharing
  -- a byte via MASK/BITMASK). PropertySearch surfaces those as
  -- BoolProperty too -- generating a freeze script for one will
  -- overwrite the whole byte, clobbering sibling bools.
  writeByte(addr, (v == true or v == 1) and 1 or 0)
end

local TYPE_WRITERS = {
  bool    = writeBool,
  int8    = function(addr, v) writeByte(addr, math.floor(v) % 256) end,
  uint8   = function(addr, v) writeByte(addr, math.floor(v) % 256) end,
  int16   = function(addr, v) writeSmallInteger(addr, math.floor(v)) end,
  uint16  = function(addr, v) writeSmallInteger(addr, math.floor(v)) end,
  int32   = function(addr, v) writeInteger(addr, math.floor(v)) end,
  uint32  = function(addr, v) writeInteger(addr, math.floor(v)) end,
  int64   = function(addr, v) writeQword(addr, v) end,
  uint64  = function(addr, v) writeQword(addr, v) end,
  float   = function(addr, v) writeFloat(addr, v) end,
  double  = function(addr, v) writeDouble(addr, v) end,
}

local TYPE_ALIASES = {
  byte        = 'uint8',
  sbyte       = 'int8',
  word        = 'int16',
  dword       = 'int32',
  qword       = 'uint64',
  int         = 'int32',
  long        = 'int64',
  boolean     = 'bool',
}

local function resolveWriter(valueType)
  if type(valueType) ~= 'string' then
    return nil, '[ue5_freeze] valueType must be a string'
  end
  local t = valueType:lower()
  t = TYPE_ALIASES[t] or t
  local w = TYPE_WRITERS[t]
  if not w then
    return nil, string.format(
      "[ue5_freeze] unsupported valueType '%s' -- supported: " ..
      'bool, int8/uint8(byte), int16/uint16(word), ' ..
      'int32/uint32(dword), int64/uint64(qword), float, double',
      valueType)
  end
  return w, nil
end

-- ============================================================
-- Internal: mailbox helpers
-- ============================================================

local function findMailbox()
  -- getAddressSafe (not getAddress) -- returns nil on missing symbol
  -- instead of raising. Either name is valid depending on whether the
  -- DLL exports its symbols module-qualified.
  local mb = getAddressSafe('g_invokeMailbox')
  if not mb or mb == 0 then
    mb = getAddressSafe('UE5Dumper.g_invokeMailbox')
  end
  if not mb or mb == 0 then
    return nil,
      '[ue5_freeze] g_invokeMailbox symbol not found -- is ' ..
      'UE5Dumper.dll injected?'
  end
  return mb, nil
end

local function writeMbStr(mb, off, str)
  local b = {}
  local len = math.min(#str, 255)
  for i = 1, len do b[#b + 1] = string.byte(str, i) end
  b[#b + 1] = 0  -- null terminator
  writeBytes(mb + off, b)
end

local function waitDone(mb, timeoutMs)
  local elapsed = 0
  local limit = timeoutMs or DEFAULT_TIMEOUT_MS
  while readInteger(mb + OFF_STATUS) ~= STATUS_DONE do
    sleep(1)
    elapsed = elapsed + 1
    if elapsed >= limit then
      return false, string.format('mailbox timeout after %dms', limit)
    end
  end
  return true
end

-- Pull one page of instance pointers via CMD_LIST_INSTANCES.
-- Returns: addrsArray (or nil), totalPages, errMsg (nil on success)
local function fetchInstancePage(className, pageIndex)
  local mb, ferr = findMailbox()
  if not mb then return nil, 0, ferr end

  if _ue5_invoke_busy then
    -- Don't corrupt a concurrent invoke. Caller (rescan) treats this
    -- as "skip this cycle"; tick keeps writing the existing cache.
    return nil, 0, 'mailbox busy (concurrent invoke or rescan)'
  end

  _ue5_invoke_busy = true
  local pok, addrs, totalPages, err = pcall(function()
    writeMbStr(mb, OFF_CLASS, className)
    -- Page index goes in paramsData[0..3].
    writeInteger(mb + OFF_PARAMS, pageIndex)
    -- Status cleared, THEN cmd written last as the trigger.
    writeInteger(mb + OFF_STATUS, 0)
    writeInteger(mb + OFF_CMD, CMD_LIST_INSTANCES)

    local wok, werr = waitDone(mb, DEFAULT_TIMEOUT_MS)
    if not wok then return nil, 0, werr end

    local result = readInteger(mb + OFF_RESULT)
    if result ~= 0 then
      local em = readString(mb + OFF_ERR, 256) or ''
      return nil, 0, string.format(
        'CMD_LIST_INSTANCES result=%d (%s)', result, em)
    end

    local returned = readSmallInteger(mb + OFF_NUM_PARMS) or 0
    -- readSmallInteger returns signed; we packed an unsigned uint16.
    if returned < 0 then returned = returned + 65536 end
    local totalPagesLocal = readInteger(mb + OFF_FUNC_FLAGS) or 1

    local out = {}
    for i = 0, returned - 1 do
      local a = readQword(mb + OFF_PARAMS + (i * 8))
      if a and a ~= 0 then out[#out + 1] = a end
    end
    return out, totalPagesLocal, nil
  end)
  _ue5_invoke_busy = false

  if not pok then
    -- Body raised; pcall captured the error in the first slot.
    return nil, 0, tostring(addrs)
  end
  return addrs, totalPages or 0, err
end

-- Full rescan: page through CMD_LIST_INSTANCES until all instances
-- of the class are collected. Caps at 16 pages (16*128 = 2048
-- instances) to match the DLL's hard cap.
local function rescanInstances(className, filter)
  local all = {}
  local pageIndex = 0
  local maxPages = 16
  local firstErr = nil

  while pageIndex < maxPages do
    local addrs, totalPages, err = fetchInstancePage(className, pageIndex)
    if not addrs then
      if pageIndex == 0 then firstErr = err end
      break
    end
    for i = 1, #addrs do all[#all + 1] = addrs[i] end
    pageIndex = pageIndex + 1
    if totalPages <= pageIndex then break end
  end

  if filter then
    local filtered = {}
    for i = 1, #all do
      if filter(all[i]) then filtered[#filtered + 1] = all[i] end
    end
    all = filtered
  end

  return all, firstErr
end

-- ============================================================
-- Public API: freezeProperty
-- ============================================================

if not freezeProperty then

  --- Build a freeze handle for one (class, offset, type, value) tuple.
  ---
  --- @param cfg table  see header docs for fields
  --- @return table     handle with .start(), .stop(), and internals
  function freezeProperty(cfg)
    if type(cfg) ~= 'table' then
      error('[ue5_freeze] freezeProperty: cfg must be a table')
    end
    if type(cfg.className) ~= 'string' or #cfg.className == 0 then
      error('[ue5_freeze] cfg.className must be a non-empty string')
    end
    if type(cfg.propOffset) ~= 'number' then
      error('[ue5_freeze] cfg.propOffset must be a number')
    end
    if cfg.value == nil then
      error('[ue5_freeze] cfg.value must be provided')
    end

    local writer, werr = resolveWriter(cfg.valueType)
    if not writer then error(werr) end

    local handle = {
      cfg          = cfg,
      _writer      = writer,
      _cache       = {},
      _tickTimer   = nil,
      _rescanTimer = nil,
      _lastError   = nil,
    }

    local function tick()
      local offset = handle.cfg.propOffset
      local value  = handle.cfg.value
      local w      = handle._writer
      local cache  = handle._cache
      for i = 1, #cache do
        local addr = cache[i]
        -- Liveness guard: if the vtable slot is zero, the instance has
        -- been freed (UObject's first qword is its vtable). Skipping
        -- avoids writing into freed/recycled pages -- not strictly
        -- required (rescan will drop dead entries soon) but cheap.
        local vt = readQword(addr)
        if vt and vt ~= 0 then
          w(addr + offset, value)
        end
      end
    end

    local function rescan()
      local addrs, err = rescanInstances(handle.cfg.className, handle.cfg.filter)
      if err then
        handle._lastError = err
        -- Keep the previous cache so tick keeps working if the
        -- rescan failed due to a transient busy state.
      else
        handle._cache = addrs
        handle._lastError = nil
      end
    end

    handle.start = function()
      -- Initial scan happens synchronously so tick has data on the
      -- very first fire.
      rescan()

      local tickMs   = handle.cfg.tickIntervalMs or 50
      local rescanMs = (handle.cfg.refreshIntervalSec or 5) * 1000

      handle._tickTimer = createTimer(getMainForm(), false)
      handle._tickTimer.Interval = tickMs
      handle._tickTimer.OnTimer  = tick
      handle._tickTimer.Enabled  = true

      handle._rescanTimer = createTimer(getMainForm(), false)
      handle._rescanTimer.Interval = rescanMs
      handle._rescanTimer.OnTimer  = rescan
      handle._rescanTimer.Enabled  = true
    end

    handle.stop = function()
      if handle._tickTimer then
        handle._tickTimer.Enabled = false
        handle._tickTimer.destroy()
        handle._tickTimer = nil
      end
      if handle._rescanTimer then
        handle._rescanTimer.Enabled = false
        handle._rescanTimer.destroy()
        handle._rescanTimer = nil
      end
      handle._cache = {}
    end

    return handle
  end

  registerLuaFunctionHighlight('freezeProperty')
end
