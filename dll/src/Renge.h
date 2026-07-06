#pragma once

// ============================================================
// Renge — 蓮格 (連絡人 — Liaison)
// PipeProtocol: IPC command/event definitions
// ============================================================

#include <json.hpp>
#include <string>
#include <cstdint>
#include <sstream>
#include <iomanip>

#include "Stark.h"   // game-thread liveness for the shared response envelope

namespace Renge {

// Command strings
constexpr const char* CMD_INIT             = "init";
constexpr const char* CMD_GET_POINTERS     = "get_pointers";
constexpr const char* CMD_GET_OBJECT_COUNT = "get_object_count";
constexpr const char* CMD_GET_OBJECT_LIST  = "get_object_list";
constexpr const char* CMD_GET_OBJECT       = "get_object";
constexpr const char* CMD_FIND_OBJECT      = "find_object";
constexpr const char* CMD_SEARCH_OBJECTS   = "search_objects";
constexpr const char* CMD_WALK_CLASS       = "walk_class";
constexpr const char* CMD_WALK_CLASS_BATCH = "walk_class_batch";
constexpr const char* CMD_READ_MEM         = "read_mem";
constexpr const char* CMD_WRITE_MEM        = "write_mem";
constexpr const char* CMD_WALK_INSTANCE    = "walk_instance";
constexpr const char* CMD_WALK_WORLD       = "walk_world";
constexpr const char* CMD_FIND_INSTANCES   = "find_instances";
constexpr const char* CMD_FIND_BY_ADDRESS  = "find_by_address";
constexpr const char* CMD_FIND_REFS_TO_UOBJ = "find_refs_to_uobject";
constexpr const char* CMD_FIND_PATH_FROM_GWORLD = "find_path_from_gworld";
constexpr const char* CMD_GET_RELATED_OBJECTS = "get_related_objects";
constexpr const char* CMD_GET_CURRENT_TARGET = "get_current_target";
constexpr const char* CMD_DETECT_NOISE_CLASSES = "detect_noise_classes";
constexpr const char* CMD_RESOLVE_GAME_ENGINE = "resolve_game_engine";
constexpr const char* CMD_FIND_PROPERTY_XREFS = "find_property_xrefs";
constexpr const char* CMD_FIND_FUNCTIONS_BY_CLASS = "find_functions_by_class";
constexpr const char* CMD_GET_FUNCTION_CODE_ADDR = "get_function_code_addr";
constexpr const char* CMD_WALK_FUNCTION_PROPS = "walk_function_props";
constexpr const char* CMD_GET_CE_PTR_INFO  = "get_ce_pointer_info";
constexpr const char* CMD_GET_OFFSETS      = "get_offsets";
constexpr const char* CMD_READ_ARRAY_ELEMS = "read_array_elements";
constexpr const char* CMD_LIST_ENUMS       = "list_enums";
constexpr const char* CMD_WALK_FUNCTIONS   = "walk_functions";
constexpr const char* CMD_WATCH             = "watch";
constexpr const char* CMD_UNWATCH           = "unwatch";
constexpr const char* CMD_SEARCH_PROPERTIES = "search_properties";
constexpr const char* CMD_SEARCH_PROPERTIES_BATCH = "search_properties_batch";
constexpr const char* CMD_LIST_CLASSES      = "list_classes";
constexpr const char* CMD_LIST_ALL_FUNCTIONS = "list_all_functions";
constexpr const char* CMD_RESCAN            = "rescan";
constexpr const char* CMD_RESCAN_STATUS     = "rescan_status";
constexpr const char* CMD_APPLY_RESCAN      = "apply_rescan";
constexpr const char* CMD_TRIGGER_SCAN      = "trigger_scan";
constexpr const char* CMD_INVOKE_FUNCTION       = "invoke_function";
constexpr const char* CMD_WALK_DATATABLE_ROWS   = "walk_datatable_rows";
constexpr const char* CMD_SCAN_STATUS           = "scan_status";
constexpr const char* CMD_SET_UE_VERSION_OVERRIDE = "set_ue_version_override";
constexpr const char* CMD_SET_INVOKE_TIMEOUT       = "set_invoke_timeout";
constexpr const char* CMD_BEGIN_VALUE_SCAN         = "begin_value_scan";
constexpr const char* CMD_REFINE_VALUE_SCAN        = "refine_value_scan";
constexpr const char* CMD_END_VALUE_SCAN           = "end_value_scan";
constexpr const char* CMD_QUERY_CANDIDATES         = "query_candidates";
// Multiple values group scan (build 1276) — object-aware "group scan".
constexpr const char* CMD_BEGIN_GROUP_SCAN         = "begin_group_scan";
constexpr const char* CMD_REFINE_GROUP_SCAN        = "refine_group_scan";
constexpr const char* CMD_END_GROUP_SCAN           = "end_group_scan";
constexpr const char* CMD_QUERY_GROUP_CANDIDATES   = "query_group_candidates";
constexpr const char* CMD_GET_DEBUG_CAMERA_STATE   = "get_debug_camera_state";
constexpr const char* CMD_SET_DEBUG_CAMERA         = "set_debug_camera";
// UE5.7+ packed FUObjectItem calibration (runtime tune of the reconstruction constants +
// optional force-enable) — lets the first real packed game be calibrated without a rebuild.
constexpr const char* CMD_SET_PACKED_CONSTS        = "set_packed_consts";

// Teleport (Wirbel) — marker save/recall + cursor teleport (docs/teleport-spec.md §7)
constexpr const char* CMD_TELEPORT_GET_POSE        = "teleport_get_pose";
constexpr const char* CMD_TELEPORT_SAVE_MARKER     = "teleport_save_marker";
constexpr const char* CMD_TELEPORT_RECALL_MARKER   = "teleport_recall_marker";
constexpr const char* CMD_TELEPORT_TO_CURSOR       = "teleport_to_cursor";
constexpr const char* CMD_TELEPORT_GET_MARKERS     = "teleport_get_markers";
constexpr const char* CMD_TELEPORT_CLEAR_MARKER    = "teleport_clear_marker";
constexpr const char* CMD_TELEPORT_RECALL_LAST     = "teleport_recall_last";
constexpr const char* CMD_TELEPORT_GET_POV         = "teleport_get_pov";
constexpr const char* CMD_TELEPORT_RELATIVE        = "teleport_relative";
constexpr const char* CMD_SET_MOUSE_CURSOR         = "set_mouse_cursor";
constexpr const char* CMD_GET_MOUSE_CURSOR         = "get_mouse_cursor";

// GodMode (Solitar) — force AActor::bCanBeDamaged (docs/godmode-spec.md §6.1)
constexpr const char* CMD_SET_GOD_MODE             = "set_god_mode";
constexpr const char* CMD_GET_GOD_MODE             = "get_god_mode";
constexpr const char* CMD_GET_PROTECT_STATE        = "get_protect_state";

// Foreground lock (Grausam) — hook GetForegroundWindow so the game always thinks
// it is the foreground app, defeating t.IdleWhenNotForeground / focus-loss pause.
constexpr const char* CMD_SET_FOREGROUND_LOCK      = "set_foreground_lock";
constexpr const char* CMD_GET_FOREGROUND_LOCK      = "get_foreground_lock";

// Movement tuning (Laufen) — per-pawn CMC float knobs held against per-tick
// overwrites: walk_speed (P1) / gravity / jump (P2/P3). "knob" string selects
// which: "walk_speed" | "gravity" | "jump".
constexpr const char* CMD_GET_MOVEMENT_PARAMS      = "get_movement_params";
constexpr const char* CMD_SET_MOVEMENT_MULTIPLIER  = "set_movement_multiplier";
constexpr const char* CMD_RESET_MOVEMENT           = "reset_movement";
// Gravity DIRECTION vector (UE5.4+ GravityDirection); x/y/z normalized DLL-side.
constexpr const char* CMD_SET_GRAVITY_DIRECTION    = "set_gravity_direction";
constexpr const char* CMD_RESET_GRAVITY_DIRECTION  = "reset_gravity_direction";

// Fly (Dunste) — no-gravity keyboard-driven 3D flight. fly_set applies whichever
// of {enable, speed, preset} are present and returns the live status; fly_get_state
// polls it. Input is read DLL-side (GetAsyncKeyState); the UI only toggles/config.
constexpr const char* CMD_FLY_SET                  = "fly_set";
constexpr const char* CMD_FLY_GET_STATE            = "fly_get_state";

// Standalone CE-Lua trainer bake (no-DLL trainer export). One-shot: returns the
// decomposed *GWorld->Pawn offset chain + RootComponent/RelativeLocation,
// CharacterMovement + knob offsets, and the protection bits — everything a
// GWorld-anchored standalone .CT needs to re-walk and write without the DLL.
constexpr const char* CMD_GET_TRAINER_OFFSETS      = "get_trainer_offsets";

// Snapshot capture (experimental — Phase A). Stateless cursor pagination
// like get_object_list: begin_snapshot returns the total object count for
// progress; snapshot_chunk streams [offset, offset+limit) objects with their
// numeric UPROPERTY values. No end command — there is no server-side session.
constexpr const char* CMD_BEGIN_SNAPSHOT           = "begin_snapshot";
constexpr const char* CMD_SNAPSHOT_CHUNK           = "snapshot_chunk";

// Event types
constexpr const char* EVT_WATCH            = "watch";

// Address to hex string "0x..."
inline std::string AddrToStr(uintptr_t addr) {
    std::ostringstream oss;
    oss << "0x" << std::uppercase << std::hex << addr;
    return oss.str();
}

// Strict hex parse: optional "0x"/"0X" prefix, allows trailing whitespace, rejects
// any other trailing garbage (e.g. unsubstituted CE placeholders like "0x[ply_base]").
// Returns true and writes outAddr on success; returns false on failure (outAddr untouched).
inline bool TryStrToAddr(const std::string& str, uintptr_t& outAddr) noexcept {
    if (str.empty()) return false;
    // Reject leading sign — std::stoull would silently accept "-1" and 2's-complement
    // wrap to 0xFFFFFFFFFFFFFFFF, which is never a meaningful address from the UI side.
    size_t front = 0;
    while (front < str.size() && std::isspace(static_cast<unsigned char>(str[front]))) ++front;
    if (front < str.size() && (str[front] == '-' || str[front] == '+')) return false;
    try {
        size_t pos = 0;
        uintptr_t v = std::stoull(str, &pos, 16);
        while (pos < str.size() && std::isspace(static_cast<unsigned char>(str[pos]))) ++pos;
        if (pos != str.size()) return false;
        outAddr = v;
        return true;
    } catch (...) {
        return false;
    }
}

// Hex string "0x..." to address. Noexcept: returns 0 on malformed input.
// Prefer TryStrToAddr at boundaries where you want to surface a clear error to the caller.
inline uintptr_t StrToAddr(const std::string& str) noexcept {
    uintptr_t v = 0;
    TryStrToAddr(str, v);
    return v;
}

// Bytes to hex string (no 0x prefix)
inline std::string BytesToHex(const uint8_t* data, size_t len) {
    std::ostringstream oss;
    for (size_t i = 0; i < len; ++i) {
        oss << std::hex << std::setfill('0') << std::setw(2) << std::uppercase
            << static_cast<int>(data[i]);
    }
    return oss.str();
}

// Hex string to bytes
inline std::vector<uint8_t> HexToBytes(const std::string& hex) {
    std::vector<uint8_t> bytes;
    for (size_t i = 0; i + 1 < hex.size(); i += 2) {
        uint8_t byte = static_cast<uint8_t>(strtoul(hex.substr(i, 2).c_str(), nullptr, 16));
        bytes.push_back(byte);
    }
    return bytes;
}

// Build a success response
inline nlohmann::json MakeResponse(int id, const nlohmann::json& data = {}) {
    nlohmann::json res;
    res["id"] = id;
    res["ok"] = true;
    // Cross-cutting game-thread liveness hint riding the shared success
    // envelope: a paused / suspended game stops ticking ProcessEvent, so every
    // live-camera / function-invoke feature times out. Carrying it on EVERY
    // response lets the UI raise a non-blocking "game paused" banner from any
    // command it happens to send (no dedicated heartbeat command or timer), and
    // clear it on the next response once the thread ticks again. Cost: one
    // atomic read + a steady-clock diff. Placed before merge_patch so a handler
    // that (unusually) sets its own "game_thread_stalled" still wins.
    res["game_thread_stalled"] = !Stark::IsGameThreadResponsive();
    if (!data.is_null() && !data.empty()) {
        res.merge_patch(data);
    }
    return res;
}

// Build an error response
inline nlohmann::json MakeError(int id, const std::string& errorMsg) {
    return {
        {"id", id},
        {"ok", false},
        {"error", errorMsg}
    };
}

// Build a push event (no id)
inline nlohmann::json MakeEvent(const std::string& eventType, const nlohmann::json& data = {}) {
    nlohmann::json evt;
    evt["event"] = eventType;
    if (!data.is_null() && !data.empty()) {
        evt.merge_patch(data);
    }
    return evt;
}

} // namespace Renge
