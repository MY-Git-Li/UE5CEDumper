--[[
  ue5_invoke_helper.lua
  UE5CEDumper -- UFunction Invoker (mailbox protocol)

  This is the runtime helper required by AA Scripts produced via
  "Copy AA Script (Baked)" in UE5DumpUI. The generated AA Script
  uses findTableFile('ue5_invoke_helper.lua') to locate this code,
  so this file MUST be embedded in your CE table:

    Setup once per .CT:
      1. Save this file next to your .CT (e.g. via Tools ->
         Export CE Helper Lua File... in UE5DumpUI)
      2. In Cheat Engine: Table -> Add File... -> select this file
      3. Save the .CT to bake the file into the table

  All baked AA Scripts will then resolve the helper through
  findTableFile and call invokeUFunction(...).

  Public API (re-declaration-safe, syntax-highlighted):
    ok, err = invokeUFunction(className, funcName, parmsSize, params)
    value   = readUFunctionReturn(offset, valueType)
    state   = setDebugCamera(enable)   -- robust force on/off (1=on,0=off,-1=err)
    state   = getDebugCameraState()    -- 1=on, 0=off, -1=unknown

  Debug Camera memory-record example (one checkbox = camera on/off).
  Both blocks call the SAME DLL export, only the arg differs; the DLL
  reads state, toggles only when needed, and on a disable that the game's
  stripped ToggleDebugCamera can't honour, switches the local player's
  controller back to the original PlayerController:

    [ENABLE]
    {$lua}
    if syntaxcheck then return end
    setDebugCamera(1)
    {$asm}

    [DISABLE]
    {$lua}
    if syntaxcheck then return end
    setDebugCamera(0)
    {$asm}

  Constants exposed:
    UE5_INVOKE_HELPER_VERSION  = '1.0'
    UE5_INVOKE_PARAMS_OFFSET   = 0x328  (params_data offset within mailbox)
]]

-- ============================================================
-- Version (callers can sanity-check after load)
-- ============================================================

if not UE5_INVOKE_HELPER_VERSION then
  UE5_INVOKE_HELPER_VERSION = '1.1'
end

-- ============================================================
-- Reentrancy guard
-- ============================================================
-- The DLL exposes a single-slot mailbox (g_invokeMailbox in
-- Mimic.cpp). If two CE-Lua callers write the className /
-- funcName / paramsData fields concurrently the in-flight call
-- gets corrupted -- both callers may observe status=DONE while
-- the DLL only executed one (Frankenstein) request. CE's Lua
-- `sleep(1)` pumps Windows messages, so timer / synchronize
-- callbacks CAN fire reentrantly inside waitDone's poll loop.
--
-- This flag serializes invokes within a single Lua engine. The
-- DLL side has no busy-rejection of its own; this is the only
-- guard. The `if nil` init pattern preserves the flag across
-- helper re-loads (multiple AA Scripts loading the helper file
-- in the same session) so a concurrent in-flight call isn't
-- silently cleared.
if _ue5_invoke_busy == nil then
  _ue5_invoke_busy = false
end

-- ============================================================
-- Mailbox layout (must match dll/src/Mimic.h MailboxData struct)
-- ============================================================

local OFF_CMD       = 0x000  -- int32: command (write LAST to trigger)
local OFF_STATUS    = 0x004  -- int32: status (poll for STATUS_DONE=1)
local OFF_RESULT    = 0x008  -- int32: result code (0 = success)
local OFF_INSTANCE  = 0x010  -- uint64: UObject*
local OFF_UFUNC     = 0x018  -- uint64: UFunction*
local OFF_PARMS_SZ  = 0x020  -- uint16: ParmsSize
local OFF_NUM_PARMS = 0x022  -- uint16: NumParms
local OFF_FLAGS     = 0x024  -- uint32: FunctionFlags
local OFF_CLASS     = 0x028  -- char[256]: class name
local OFF_FUNC      = 0x128  -- char[256]: function name
local OFF_ERR       = 0x228  -- char[256]: error message
local OFF_PARAMS    = 0x328  -- uint8[1024]: inline params buffer

local CMD_INVOKE_BY_NAME = 4
local STATUS_DONE        = 1

-- Default invoke timeout (ms). UE5DumpUI's per-game override only
-- affects the DLL side; this Lua-side timeout guards against the
-- mailbox poll loop hanging if the game thread stops responding.
local DEFAULT_TIMEOUT_MS = 10000

-- Exported so callers can do `local p = mb + UE5_INVOKE_PARAMS_OFFSET`
-- to read return values directly (advanced usage; prefer
-- readUFunctionReturn for typed reads).
if not UE5_INVOKE_PARAMS_OFFSET then
  UE5_INVOKE_PARAMS_OFFSET = OFF_PARAMS
end

-- ============================================================
-- Internal helpers (file-local -- no global pollution)
-- ============================================================

local function findMailbox()
  local mb = getAddressSafe('g_invokeMailbox')
  if not mb or mb == 0 then
    mb = getAddressSafe('UE5Dumper.g_invokeMailbox')
  end
  if not mb or mb == 0 then
    error('[ue5_invoke] g_invokeMailbox symbol not found -- ' ..
          'is UE5Dumper.dll injected? (Check the proxy DLL or CE -> ' ..
          'Add this process / Inject DLL.)')
  end
  return mb
end

local function writeMbStr(mb, off, str)
  local b = {}
  local len = math.min(#str, 255)
  for i = 1, len do b[#b + 1] = string.byte(str, i) end
  b[#b + 1] = 0  -- null terminator
  writeBytes(mb + off, b)
end

local function writeBakedParams(mb, parmsSize, params)
  local PD = mb + OFF_PARAMS

  -- Zero-fill the params buffer (clears any stale data from previous calls).
  for i = 0, parmsSize - 1 do
    writeByte(PD + i, 0)
  end

  if not params then return end

  for _, p in ipairs(params) do
    local v   = p.value or 0
    local off = p.offset or 0
    local t   = p.type or 'int32'

    if t == 'bool' then
      writeBytes(PD + off, { (v ~= 0 and v ~= false) and 1 or 0 })
    elseif t == 'byte' then
      writeBytes(PD + off, { math.floor(v) % 256 })
    elseif t == 'int16' or t == 'uint16' then
      writeSmallInteger(PD + off, math.floor(v))
    elseif t == 'int32' or t == 'uint32' or t == 'enum' then
      writeInteger(PD + off, math.floor(v))
    elseif t == 'int64' or t == 'uint64' or t == 'qword' then
      writeQword(PD + off, v)
    elseif t == 'float' then
      writeFloat(PD + off, v)
    elseif t == 'double' then
      writeDouble(PD + off, v)
    elseif t == 'pointer' or t == 'object' or t == 'class'
           or t == 'name' or t == 'soft' or t == 'weak'
           or t == 'lazy' or t == 'interface' then
      writeQword(PD + off, v)
    else
      error(string.format(
        "[ue5_invoke] Unknown param type '%s' for '%s' -- " ..
        "supported: bool/byte/int16/int32/int64/float/double/pointer",
        tostring(t), tostring(p.name or '?')))
    end
  end
end

local function waitDone(mb, timeoutMs)
  local elapsed = 0
  local limit   = timeoutMs or DEFAULT_TIMEOUT_MS
  while readInteger(mb + OFF_STATUS) ~= STATUS_DONE do
    sleep(1)
    elapsed = elapsed + 1
    if elapsed >= limit then
      local err = readString(mb + OFF_ERR, 256) or 'timeout'
      return false, string.format(
        'Mailbox timeout after %dms (%s)', limit, err)
    end
  end
  return true
end

local function readErrMsg(mb)
  local s = readString(mb + OFF_ERR, 256)
  if s and #s > 0 then return s end
  return 'Unknown error'
end

-- ============================================================
-- Public API: invokeUFunction
-- ============================================================
-- Re-declaration guard so multiple AA scripts loading this helper
-- don't redefine functions and lose state.
if not invokeUFunction then

  --- Invoke a UFunction by class name + function name with baked params.
  ---
  --- Uses CMD_INVOKE_BY_NAME -- the DLL handles findInstance +
  --- findFunction in one mailbox round-trip.
  ---
  --- @param className string  e.g. 'PlayerCharacter' (must match a
  ---                          live, non-CDO instance's UClass name)
  --- @param funcName  string  e.g. 'AddMoney'
  --- @param parmsSize number  Total params buffer size in bytes
  ---                          (from the function metadata; zero-fill
  ---                          uses this to clear stale bytes)
  --- @param params    table   Array of param descriptors:
  ---                          { { name=..., type='int32',
  ---                              offset=0, value=1000 }, ... }
  ---                          See writeBakedParams for supported types.
  --- @return boolean ok       True on success
  --- @return string|nil err   Error message on failure (nil on success)
  function invokeUFunction(className, funcName, parmsSize, params)
    -- Input validation BEFORE the busy check so bad args from a
    -- concurrent caller surface as the real validation error
    -- instead of getting hidden by a busy state from someone else.
    if type(className) ~= 'string' or #className == 0 then
      return false, 'className must be a non-empty string'
    end
    if type(funcName) ~= 'string' or #funcName == 0 then
      return false, 'funcName must be a non-empty string'
    end
    parmsSize = parmsSize or 0
    if parmsSize < 0 or parmsSize > 1024 then
      return false, string.format(
        'parmsSize %d out of range (0..1024)', parmsSize)
    end

    -- Reentrancy guard: refuse to touch the mailbox if another
    -- invoke is mid-flight in this Lua engine. Returning a clean
    -- 'busy' error beats silently corrupting the in-flight call.
    if _ue5_invoke_busy then
      return false,
        '[ue5_invoke] busy -- another script is mid-call. ' ..
        'Serialize your AA Scripts or guard with synchronize().'
    end

    -- Set busy = true around the mailbox-touching body. The pcall
    -- wrapper ensures the flag is ALWAYS cleared, even if any
    -- write/read throws (e.g. mailbox address turned invalid).
    _ue5_invoke_busy = true
    local pok, ok_or_err, err_or_nil = pcall(function()
      local ok_mb, mb = pcall(findMailbox)
      if not ok_mb then
        return false, tostring(mb)
      end

      -- Marshal the request into the mailbox.
      writeMbStr(mb, OFF_CLASS, className)
      writeMbStr(mb, OFF_FUNC, funcName)
      local ok_p, err_p = pcall(writeBakedParams, mb, parmsSize, params)
      if not ok_p then
        return false, tostring(err_p)
      end

      -- Clear status, then write CMD last to trigger the DLL.
      writeInteger(mb + OFF_STATUS, 0)
      writeInteger(mb + OFF_CMD, CMD_INVOKE_BY_NAME)

      -- Poll until the DLL's mailbox handler reports done.
      local ok_w, err_w = waitDone(mb, DEFAULT_TIMEOUT_MS)
      if not ok_w then
        return false, err_w
      end

      local result = readInteger(mb + OFF_RESULT)
      if result ~= 0 then
        return false, string.format(
          '%s::%s -> result=%d (%s)',
          className, funcName, result, readErrMsg(mb))
      end

      return true
    end)
    _ue5_invoke_busy = false

    if not pok then
      -- Hard error inside the body (raised, not returned). pcall
      -- captured the message as ok_or_err.
      return false, tostring(ok_or_err)
    end
    -- Soft return path: inner function returned (ok, err)
    return ok_or_err, err_or_nil
  end

  registerLuaFunctionHighlight('invokeUFunction')
end

-- ============================================================
-- Public API: readUFunctionReturn
-- ============================================================
if not readUFunctionReturn then

  --- Read a return value (or out-param) from the params buffer
  --- after a successful invokeUFunction call.
  ---
  --- @param offset    number  Byte offset within params_data
  ---                          (typically the function's return-value
  ---                          offset from UFunction metadata)
  --- @param valueType string  One of: 'int32' (default), 'float',
  ---                          'double', 'bool', 'byte', 'uint64',
  ---                          'qword', 'int16', 'word'
  --- @return number|nil       The decoded value, or nil if the
  ---                          mailbox cannot be located
  function readUFunctionReturn(offset, valueType)
    local ok_mb, mb = pcall(findMailbox)
    if not ok_mb then return nil end

    local addr = mb + OFF_PARAMS + (offset or 0)

    if valueType == 'float' then
      return readFloat(addr)
    elseif valueType == 'double' then
      return readDouble(addr)
    elseif valueType == 'bool' or valueType == 'byte' then
      return readByte(addr)
    elseif valueType == 'uint64' or valueType == 'qword' then
      return readQword(addr)
    elseif valueType == 'int16' or valueType == 'word' then
      return readSmallInteger(addr)
    else
      -- Default: int32
      return readInteger(addr)
    end
  end

  registerLuaFunctionHighlight('readUFunctionReturn')
end

-- ============================================================
-- Public API: Debug Camera robust force on/off
-- ============================================================
-- Thin wrappers over the UE5_SetDebugCamera / UE5_GetDebugCameraState
-- DLL exports. The whole toggle + controller-swap fallback lives in the
-- DLL, so the UI (via pipe) and CE Lua (here) share one implementation.
-- Returns the resulting state: 1 = ON, 0 = OFF, -1 = error/unknown.
if not setDebugCamera then

  -- Resolve an export by symbol, tolerating the module-qualified form.
  local function dbgCamExport(name)
    local a = getAddressSafe('UE5Dumper.' .. name)
    if not a or a == 0 then a = getAddressSafe(name) end
    return a
  end

  function getDebugCameraState()
    local a = dbgCamExport('UE5_GetDebugCameraState')
    if not a or a == 0 then return -1 end
    return executeCodeEx(5000, nil, a)
  end
  registerLuaFunctionHighlight('getDebugCameraState')

  --- Force Debug Camera ON (enable ~= 0) or OFF. Idempotent.
  --- @param enable number|boolean
  --- @return number state  1=ON, 0=OFF, -1=error
  function setDebugCamera(enable)
    local a = dbgCamExport('UE5_SetDebugCamera')
    if not a or a == 0 then
      error('[ue5_invoke] UE5_SetDebugCamera export not found -- ' ..
            'is UE5Dumper.dll injected?')
    end
    local arg = ((enable and enable ~= 0) and 1 or 0)
    return executeCodeEx(5000, { 'integer' }, a, arg)
  end
  registerLuaFunctionHighlight('setDebugCamera')

end

-- ============================================================
-- Sentinel (visible in CE Lua engine after first load)
-- ============================================================
print(string.format('[*] ue5_invoke_helper.lua v%s loaded',
                    UE5_INVOKE_HELPER_VERSION))
