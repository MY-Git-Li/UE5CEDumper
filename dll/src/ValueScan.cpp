// ============================================================
// ValueScan — SessionManager implementation + type/predicate helpers
// ============================================================

#include "ValueScan.h"

#include <algorithm>
#include <cstring>

namespace ValueScan {

// --- DataType / ScanType helpers ---

size_t SizeOf(DataType dt) {
    switch (dt) {
        case DataType::Int8:   case DataType::UInt8:  case DataType::Bool:   return 1;
        case DataType::Int16:  case DataType::UInt16:                        return 2;
        case DataType::Int32:  case DataType::UInt32: case DataType::Float:  return 4;
        case DataType::Int64:  case DataType::UInt64: case DataType::Double: return 8;
        // Vector primitives: 3 floats packed = 12 bytes. FTransform's
        // candidate-stored bytes are the Translation FVector only.
        case DataType::FVector: case DataType::FRotator: case DataType::FTransform: return 12;
        // String types use the candidate's prevStr field instead of
        // the byte buffer — return 0 so callers can branch on it.
        case DataType::FString: case DataType::FName: case DataType::FText: return 0;
    }
    return 0;
}

const char* NameOf(DataType dt) {
    switch (dt) {
        case DataType::Int8:   return "Int8";
        case DataType::Int16:  return "Int16";
        case DataType::Int32:  return "Int32";
        case DataType::Int64:  return "Int64";
        case DataType::UInt8:  return "UInt8";
        case DataType::UInt16: return "UInt16";
        case DataType::UInt32: return "UInt32";
        case DataType::UInt64: return "UInt64";
        case DataType::Float:  return "Float";
        case DataType::Double: return "Double";
        case DataType::Bool:   return "Bool";
        case DataType::FString:    return "FString";
        case DataType::FName:      return "FName";
        case DataType::FText:      return "FText";
        case DataType::FVector:    return "FVector";
        case DataType::FRotator:   return "FRotator";
        case DataType::FTransform: return "FTransform";
    }
    return "?";
}

bool TryParseDataType(const std::string& s, DataType& out) {
    if (s == "Int8")   { out = DataType::Int8;   return true; }
    if (s == "Int16")  { out = DataType::Int16;  return true; }
    if (s == "Int32")  { out = DataType::Int32;  return true; }
    if (s == "Int64")  { out = DataType::Int64;  return true; }
    if (s == "UInt8")  { out = DataType::UInt8;  return true; }
    if (s == "UInt16") { out = DataType::UInt16; return true; }
    if (s == "UInt32") { out = DataType::UInt32; return true; }
    if (s == "UInt64") { out = DataType::UInt64; return true; }
    if (s == "Float")  { out = DataType::Float;  return true; }
    if (s == "Double") { out = DataType::Double; return true; }
    if (s == "Bool")   { out = DataType::Bool;   return true; }
    if (s == "FString")    { out = DataType::FString;    return true; }
    if (s == "FName")      { out = DataType::FName;      return true; }
    if (s == "FText")      { out = DataType::FText;      return true; }
    if (s == "FVector")    { out = DataType::FVector;    return true; }
    if (s == "FRotator")   { out = DataType::FRotator;   return true; }
    if (s == "FTransform") { out = DataType::FTransform; return true; }
    return false;
}

bool TryParseScanType(const std::string& s, ScanType& out) {
    if (s == "Exact")      { out = ScanType::Exact;      return true; }
    if (s == "Bigger")     { out = ScanType::Bigger;     return true; }
    if (s == "Smaller")    { out = ScanType::Smaller;    return true; }
    if (s == "Between")    { out = ScanType::Between;    return true; }
    if (s == "Changed")    { out = ScanType::Changed;    return true; }
    if (s == "Unchanged")  { out = ScanType::Unchanged;  return true; }
    if (s == "Increased")  { out = ScanType::Increased;  return true; }
    if (s == "Decreased")  { out = ScanType::Decreased;  return true; }
    if (s == "Contains")   { out = ScanType::Contains;   return true; }
    if (s == "StartsWith") { out = ScanType::StartsWith; return true; }
    if (s == "EndsWith")   { out = ScanType::EndsWith;   return true; }
    return false;
}

bool IsPrevValueScanType(ScanType st) {
    switch (st) {
        case ScanType::Changed:
        case ScanType::Unchanged:
        case ScanType::Increased:
        case ScanType::Decreased:
            return true;
        default:
            return false;
    }
}

bool IsFirstScanType(ScanType st) {
    switch (st) {
        case ScanType::Exact:
        case ScanType::Bigger:
        case ScanType::Smaller:
        case ScanType::Between:
        case ScanType::Contains:
        case ScanType::StartsWith:
        case ScanType::EndsWith:
            return true;
        default:
            return false;
    }
}

bool IsStringDataType(DataType dt) {
    return dt == DataType::FString || dt == DataType::FName || dt == DataType::FText;
}

bool IsVectorDataType(DataType dt) {
    return dt == DataType::FVector || dt == DataType::FRotator || dt == DataType::FTransform;
}

bool IsSubstringScanType(ScanType st) {
    return st == ScanType::Contains || st == ScanType::StartsWith || st == ScanType::EndsWith;
}

bool IsScanTypeValidFor(DataType dt, ScanType st) {
    if (IsStringDataType(dt)) {
        // Strings: Exact, Contains, StartsWith, EndsWith, Changed, Unchanged.
        // No ordering -> reject Bigger/Smaller/Between/Increased/Decreased.
        switch (st) {
            case ScanType::Exact:
            case ScanType::Contains:
            case ScanType::StartsWith:
            case ScanType::EndsWith:
            case ScanType::Changed:
            case ScanType::Unchanged:
                return true;
            default:
                return false;
        }
    }
    if (IsVectorDataType(dt)) {
        // Vectors: component-wise ordering applies; substring predicates reject.
        switch (st) {
            case ScanType::Exact:
            case ScanType::Bigger:
            case ScanType::Smaller:
            case ScanType::Between:
            case ScanType::Changed:
            case ScanType::Unchanged:
            case ScanType::Increased:
            case ScanType::Decreased:
                return true;
            default:
                return false;
        }
    }
    // Numeric primitives: substring predicates reject, everything else valid.
    return !IsSubstringScanType(st);
}

const std::vector<std::string>& PropertyTypeNames(DataType dt) {
    // Per-DataType static tables, populated on first call. Returning by
    // const-ref keeps the hot loop allocation-free.
    static const std::vector<std::string> kInt8   = { "Int8Property" };
    static const std::vector<std::string> kInt16  = { "Int16Property" };
    static const std::vector<std::string> kInt32  = { "IntProperty" };
    static const std::vector<std::string> kInt64  = { "Int64Property" };
    static const std::vector<std::string> kUInt8  = { "ByteProperty" };
    static const std::vector<std::string> kUInt16 = { "UInt16Property" };
    static const std::vector<std::string> kUInt32 = { "UInt32Property" };
    static const std::vector<std::string> kUInt64 = { "UInt64Property" };
    static const std::vector<std::string> kFloat  = { "FloatProperty" };
    static const std::vector<std::string> kDouble = { "DoubleProperty" };
    static const std::vector<std::string> kBool   = { "BoolProperty" };
    static const std::vector<std::string> kFString = { "StrProperty" };
    static const std::vector<std::string> kFName   = { "NameProperty" };
    static const std::vector<std::string> kFText   = { "TextProperty" };
    // Vector / Rotator / Transform are represented as StructProperty in
    // ClassInfo.Fields. The scan-side caller must also match the inner
    // UScriptStruct name (see VectorStructNames) before emitting.
    static const std::vector<std::string> kStruct = { "StructProperty" };
    static const std::vector<std::string> kEmpty;

    switch (dt) {
        case DataType::Int8:   return kInt8;
        case DataType::Int16:  return kInt16;
        case DataType::Int32:  return kInt32;
        case DataType::Int64:  return kInt64;
        case DataType::UInt8:  return kUInt8;
        case DataType::UInt16: return kUInt16;
        case DataType::UInt32: return kUInt32;
        case DataType::UInt64: return kUInt64;
        case DataType::Float:  return kFloat;
        case DataType::Double: return kDouble;
        case DataType::Bool:   return kBool;
        case DataType::FString: return kFString;
        case DataType::FName:   return kFName;
        case DataType::FText:   return kFText;
        case DataType::FVector:
        case DataType::FRotator:
        case DataType::FTransform: return kStruct;
    }
    return kEmpty;
}

const std::vector<std::string>& VectorStructNames(DataType dt) {
    // UE5 introduced Large World Coordinate variants — FVector became
    // double-precision (kept as "Vector"), with explicit float variants
    // FVector3f / FVector4f. Cooked-game StructProperty inner names
    // vary by version + variant. Accept the common shapes for each
    // logical type; the scan engine reads only 12 bytes regardless so
    // it'll grab the first 3 floats (UE5 double-vector reads as the
    // first 3 doubles' first 4 bytes each = junk; we accept Vector3f
    // as the safe primary match and add "Vector" for UE4 + cases where
    // the struct still names itself Vector despite being float-backed).
    static const std::vector<std::string> kVec  = { "Vector", "Vector3f", "Vector_NetQuantize", "Vector_NetQuantizeNormal" };
    static const std::vector<std::string> kRot  = { "Rotator", "Rotator3f" };
    // FTransform deferred: layout starts with FQuat Rotation (16B UE4 /
    // 32B UE5 LWC), so reading 12 bytes from struct start would grab
    // the Rotation quat's first three floats, not Translation. Proper
    // support needs per-version offset detection (Translation lives at
    // +16 non-LWC, +32 LWC). Leaving FTransform DataType in the enum
    // so the C# wire enum can stay stable, but no struct names map to
    // it -- scan returns 0 candidates until the per-version offset
    // table lands.
    static const std::vector<std::string> kEmpty;
    switch (dt) {
        case DataType::FVector:    return kVec;
        case DataType::FRotator:   return kRot;
        case DataType::FTransform: return kEmpty;
        default:                   return kEmpty;
    }
}

// --- Compare predicate ---

namespace {

// Read a value of the given DataType from `bytes`. We return the
// "natural" wide type (int64_t for signed, uint64_t for unsigned,
// double for floats) so the predicate logic only needs three branches.
template <typename Out>
inline Out LoadTyped(DataType dt, const uint8_t* bytes);

template <>
inline int64_t LoadTyped<int64_t>(DataType dt, const uint8_t* bytes) {
    int64_t v = 0;
    switch (dt) {
        case DataType::Int8:  v = static_cast<int64_t>(*reinterpret_cast<const int8_t*>(bytes));  break;
        case DataType::Int16: v = static_cast<int64_t>(*reinterpret_cast<const int16_t*>(bytes)); break;
        case DataType::Int32: v = static_cast<int64_t>(*reinterpret_cast<const int32_t*>(bytes)); break;
        case DataType::Int64: v = *reinterpret_cast<const int64_t*>(bytes); break;
        default: break;
    }
    return v;
}

template <>
inline uint64_t LoadTyped<uint64_t>(DataType dt, const uint8_t* bytes) {
    uint64_t v = 0;
    switch (dt) {
        case DataType::UInt8:  v = static_cast<uint64_t>(*bytes); break;
        case DataType::UInt16: v = static_cast<uint64_t>(*reinterpret_cast<const uint16_t*>(bytes)); break;
        case DataType::UInt32: v = static_cast<uint64_t>(*reinterpret_cast<const uint32_t*>(bytes)); break;
        case DataType::UInt64: v = *reinterpret_cast<const uint64_t*>(bytes); break;
        case DataType::Bool:   v = (*bytes != 0) ? 1u : 0u; break;
        default: break;
    }
    return v;
}

template <>
inline double LoadTyped<double>(DataType dt, const uint8_t* bytes) {
    double v = 0.0;
    switch (dt) {
        case DataType::Float:  v = static_cast<double>(*reinterpret_cast<const float*>(bytes)); break;
        case DataType::Double: v = *reinterpret_cast<const double*>(bytes); break;
        default: break;
    }
    return v;
}

bool IsFloatType(DataType dt) { return dt == DataType::Float || dt == DataType::Double; }
bool IsSignedIntType(DataType dt) {
    return dt == DataType::Int8 || dt == DataType::Int16
        || dt == DataType::Int32 || dt == DataType::Int64;
}
bool IsUnsignedIntType(DataType dt) {
    return dt == DataType::UInt8 || dt == DataType::UInt16
        || dt == DataType::UInt32 || dt == DataType::UInt64
        || dt == DataType::Bool;
}

template <typename T>
bool ApplyOrdered(ScanType st, T cur, T a, T b) {
    switch (st) {
        case ScanType::Exact:     return cur == a;
        case ScanType::Bigger:    return cur >  a;
        case ScanType::Smaller:   return cur <  a;
        case ScanType::Between:   return cur >= a && cur <= b;
        case ScanType::Changed:   return cur != a;
        case ScanType::Unchanged: return cur == a;
        case ScanType::Increased: return cur >  a;
        case ScanType::Decreased: return cur <  a;
    }
    return false;
}

// Tolerance-aware double predicate. tol applies as a +- band around
// the reference value(s); per-scan-type semantics are documented on
// ComparePredicate in ValueScan.h. Negative tolerance is clamped to 0
// (a malformed UI input shouldn't widen the band on the wrong side).
inline double Absd(double x) { return x < 0.0 ? -x : x; }

bool ApplyOrderedTol(ScanType st, double cur, double a, double b, double tol) {
    if (tol < 0.0) tol = 0.0;
    switch (st) {
        case ScanType::Exact:     return Absd(cur - a) <= tol;
        case ScanType::Bigger:    return cur > a + tol;
        case ScanType::Smaller:   return cur < a - tol;
        case ScanType::Between:   return cur >= a - tol && cur <= b + tol;
        case ScanType::Changed:   return Absd(cur - a) > tol;
        case ScanType::Unchanged: return Absd(cur - a) <= tol;
        case ScanType::Increased: return cur > a + tol;
        case ScanType::Decreased: return cur < a - tol;
    }
    return false;
}

}  // namespace

bool ComparePredicate(DataType dt, ScanType st,
                      const uint8_t* rawBytes,
                      const uint8_t* targetBytes,
                      const uint8_t* target2Bytes,
                      double         tolerance) {
    if (!rawBytes || !targetBytes) return false;
    if (st == ScanType::Between && !target2Bytes) return false;
    // Substring predicates have no meaning for numeric types.
    if (IsSubstringScanType(st)) return false;

    if (IsFloatType(dt)) {
        double cur = LoadTyped<double>(dt, rawBytes);
        double a   = LoadTyped<double>(dt, targetBytes);
        double b   = target2Bytes ? LoadTyped<double>(dt, target2Bytes) : 0.0;
        // Tolerance is only meaningful for Float/Double: integral types
        // get exact comparison regardless of the supplied tol value.
        return ApplyOrderedTol(st, cur, a, b, tolerance);
    }
    if (IsSignedIntType(dt)) {
        int64_t cur = LoadTyped<int64_t>(dt, rawBytes);
        int64_t a   = LoadTyped<int64_t>(dt, targetBytes);
        int64_t b   = target2Bytes ? LoadTyped<int64_t>(dt, target2Bytes) : 0;
        return ApplyOrdered<int64_t>(st, cur, a, b);
    }
    if (IsUnsignedIntType(dt)) {
        uint64_t cur = LoadTyped<uint64_t>(dt, rawBytes);
        uint64_t a   = LoadTyped<uint64_t>(dt, targetBytes);
        uint64_t b   = target2Bytes ? LoadTyped<uint64_t>(dt, target2Bytes) : 0;
        return ApplyOrdered<uint64_t>(st, cur, a, b);
    }
    return false;
}

// --- String predicate ---

namespace {

// ASCII-aware case folder. We don't pull in <locale> / ICU; for the
// cheat use cases that hit string scans (English item names, save
// keys, dialogue tags) a byte-level tolower over [A-Z] is sufficient.
// Non-ASCII bytes (UTF-8 multibyte sequences) compare bitwise — two
// strings that differ only in non-ASCII case (e.g. "Ä" vs "ä") will
// not match in case-insensitive mode. Documented in ValueScan.h.
inline char FoldAscii(char c) {
    return (c >= 'A' && c <= 'Z') ? static_cast<char>(c + ('a' - 'A')) : c;
}

bool EqualsFolded(const std::string& a, const std::string& b, bool caseSensitive) {
    if (a.size() != b.size()) return false;
    if (caseSensitive) return a == b;
    for (size_t i = 0; i < a.size(); ++i) {
        if (FoldAscii(a[i]) != FoldAscii(b[i])) return false;
    }
    return true;
}

bool StartsWithFolded(const std::string& s, const std::string& prefix, bool caseSensitive) {
    if (prefix.size() > s.size()) return false;
    if (caseSensitive) return std::memcmp(s.data(), prefix.data(), prefix.size()) == 0;
    for (size_t i = 0; i < prefix.size(); ++i) {
        if (FoldAscii(s[i]) != FoldAscii(prefix[i])) return false;
    }
    return true;
}

bool EndsWithFolded(const std::string& s, const std::string& suffix, bool caseSensitive) {
    if (suffix.size() > s.size()) return false;
    size_t off = s.size() - suffix.size();
    if (caseSensitive) return std::memcmp(s.data() + off, suffix.data(), suffix.size()) == 0;
    for (size_t i = 0; i < suffix.size(); ++i) {
        if (FoldAscii(s[off + i]) != FoldAscii(suffix[i])) return false;
    }
    return true;
}

bool ContainsFolded(const std::string& s, const std::string& needle, bool caseSensitive) {
    if (needle.empty()) return true;
    if (needle.size() > s.size()) return false;
    if (caseSensitive) return s.find(needle) != std::string::npos;
    // Inline ASCII-fold substring search — O(N*M) but bounded by short
    // user-entered needles + ~50K candidates. No allocations.
    const size_t lastStart = s.size() - needle.size();
    for (size_t i = 0; i <= lastStart; ++i) {
        bool match = true;
        for (size_t j = 0; j < needle.size(); ++j) {
            if (FoldAscii(s[i + j]) != FoldAscii(needle[j])) { match = false; break; }
        }
        if (match) return true;
    }
    return false;
}

}  // namespace

bool CompareStringPredicate(ScanType           st,
                            const std::string& cur,
                            const std::string& target,
                            bool               caseSensitive) {
    switch (st) {
        case ScanType::Exact:      return EqualsFolded(cur, target, caseSensitive);
        case ScanType::Contains:   return ContainsFolded(cur, target, caseSensitive);
        case ScanType::StartsWith: return StartsWithFolded(cur, target, caseSensitive);
        case ScanType::EndsWith:   return EndsWithFolded(cur, target, caseSensitive);
        case ScanType::Changed:    return !EqualsFolded(cur, target, caseSensitive);
        case ScanType::Unchanged:  return  EqualsFolded(cur, target, caseSensitive);
        // Numeric-ordering predicates are nonsensical for strings.
        case ScanType::Bigger:
        case ScanType::Smaller:
        case ScanType::Between:
        case ScanType::Increased:
        case ScanType::Decreased:
            return false;
    }
    return false;
}

// --- Vector predicate ---

bool CompareVectorPredicate(ScanType       st,
                            const uint8_t* rawBytes,
                            const uint8_t* targetBytes,
                            const uint8_t* target2Bytes,
                            double         tolerance) {
    if (!rawBytes || !targetBytes) return false;
    if (st == ScanType::Between && !target2Bytes) return false;
    if (IsSubstringScanType(st)) return false;
    if (tolerance < 0.0) tolerance = 0.0;

    float c[3], a[3], b[3] = {0.0f, 0.0f, 0.0f};
    std::memcpy(c, rawBytes,     sizeof(c));
    std::memcpy(a, targetBytes,  sizeof(a));
    if (target2Bytes) std::memcpy(b, target2Bytes, sizeof(b));

    auto absf = [](float x) -> float { return x < 0.0f ? -x : x; };
    const float tol = static_cast<float>(tolerance);

    switch (st) {
        case ScanType::Exact:
            for (int i = 0; i < 3; ++i)
                if (absf(c[i] - a[i]) > tol) return false;
            return true;
        case ScanType::Bigger:
            for (int i = 0; i < 3; ++i)
                if (!(c[i] > a[i] + tol)) return false;
            return true;
        case ScanType::Smaller:
            for (int i = 0; i < 3; ++i)
                if (!(c[i] < a[i] - tol)) return false;
            return true;
        case ScanType::Between:
            for (int i = 0; i < 3; ++i)
                if (!(c[i] >= a[i] - tol && c[i] <= b[i] + tol)) return false;
            return true;
        case ScanType::Changed:
            for (int i = 0; i < 3; ++i)
                if (absf(c[i] - a[i]) > tol) return true;
            return false;
        case ScanType::Unchanged:
            for (int i = 0; i < 3; ++i)
                if (absf(c[i] - a[i]) > tol) return false;
            return true;
        case ScanType::Increased:
            for (int i = 0; i < 3; ++i)
                if (c[i] > a[i] + tol) return true;
            return false;
        case ScanType::Decreased:
            for (int i = 0; i < 3; ++i)
                if (c[i] < a[i] - tol) return true;
            return false;
        case ScanType::Contains:
        case ScanType::StartsWith:
        case ScanType::EndsWith:
            return false;
    }
    return false;
}

// --- SessionManager ---

SessionManager& SessionManager::Instance() {
    // Heap-allocated, intentionally leaked singleton. The DLL may hold
    // tens of thousands of candidates per session × multiple sessions
    // × ~200 bytes of string metadata each — easily multi-MB of small
    // allocations. Running the destructor chain over those during
    // process exit can cause perceptible hangs. The Windows process
    // heap is reclaimed wholesale at termination, so the leak is
    // harmless — same reasoning as discrete's ValueScanSession.
    static SessionManager* inst = new SessionManager();
    return *inst;
}

uint64_t SessionManager::Begin(DataType dt, std::vector<Candidate> candidates) {
    ExpireOldSessions();

    std::lock_guard<std::mutex> lk(mu_);
    uint64_t id = nextId_++;
    auto sess = std::make_unique<Session>();
    sess->id         = id;
    sess->dt         = dt;
    sess->candidates = std::move(candidates);
    sess->lastUse    = std::chrono::steady_clock::now();
    sessions_.emplace(id, std::move(sess));
    return id;
}

bool SessionManager::End(uint64_t sessionId) {
    std::lock_guard<std::mutex> lk(mu_);
    return sessions_.erase(sessionId) > 0;
}

void SessionManager::ExpireOldSessions() {
    auto now = std::chrono::steady_clock::now();
    std::lock_guard<std::mutex> lk(mu_);
    for (auto it = sessions_.begin(); it != sessions_.end(); ) {
        if (now - it->second->lastUse > kExpirySeconds) {
            it = sessions_.erase(it);
        } else {
            ++it;
        }
    }
}

void SessionManager::DropAll() {
    std::lock_guard<std::mutex> lk(mu_);
    sessions_.clear();
}

SessionManager::Stats SessionManager::GetStats() {
    Stats s;
    std::lock_guard<std::mutex> lk(mu_);
    s.sessionCount = sessions_.size();
    s.nextId       = nextId_;
    for (const auto& kv : sessions_) {
        s.totalCandidates += kv.second->candidates.size();
    }
    return s;
}

}  // namespace ValueScan
