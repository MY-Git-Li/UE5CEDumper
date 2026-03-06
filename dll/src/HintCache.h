#pragma once

// ============================================================
// HintCache.h — Scan hint cache for AOB pattern acceleration
//
// Reads/writes a JSON file with previously-winning pattern IDs
// per game (keyed by PE hash). On second scan of the same game
// version, the cached pattern is tried first for a speedup.
//
// File: %LOCALAPPDATA%\UE5CEDumper\UE5CEDumper.{COMPUTERNAME}.json
// Format: same as the C# AobUsageService writes — DLL and UI
//         share the file (last writer wins, both formats compatible).
// ============================================================

#include <string>

// Forward declare to avoid pulling OffsetFinder.h
namespace OffsetFinder { struct EnginePointers; }

namespace HintCache {

/// Per-target hint: pattern ID to try first (empty = no hint).
struct ScanHints {
    std::string gobjectsPatternId;
    std::string gnamesPatternId;
    std::string gworldPatternId;
};

/// Load hints for a given PE hash from the cache file.
/// Returns empty strings if the file doesn't exist, is corrupt,
/// or the PE hash is not found.  Never throws.
ScanHints LoadHints(const char* peHash);

/// Save scan results to the cache file.  Reads the existing file,
/// updates/inserts the record for peHash, writes atomically.
/// Never throws — errors are logged and silently ignored.
void SaveResults(const char* peHash, const OffsetFinder::EnginePointers& ptrs,
                 const char* processName);

} // namespace HintCache
