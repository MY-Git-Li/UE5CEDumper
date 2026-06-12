namespace UE5DumpUI.Models;

/// <summary>
/// Pawn pose returned by <c>teleport_get_pose</c> / <c>teleport_save_marker</c>.
/// X/Y/Z = world location, Pitch/Yaw/Roll = control rotation. The DLL widens
/// UE4 float fields to double at the boundary, so these are always doubles.
/// </summary>
public sealed class TeleportPose
{
    /// <summary>Wirbel result code (0 = OK, negatives per
    /// docs/teleport-spec.md §8). <see cref="TeleportCodes"/> maps these.</summary>
    public int Code { get; init; }

    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double Pitch { get; init; }
    public double Yaw { get; init; }
    public double Roll { get; init; }

    /// <summary>UWorld object name the pose was read on.</summary>
    public string Map { get; init; } = "";

    /// <summary>"raw" (direct property read) or "invoke"
    /// (K2_GetActorLocation, used for attached/vehicle pawns).</summary>
    public string Source { get; init; } = "raw";
}

/// <summary>Result of a teleport action (recall / cursor).</summary>
public sealed class TeleportResult
{
    /// <summary>Wirbel result code (0 = OK).</summary>
    public int Code { get; init; }

    /// <summary>1 = engine invoke path (clean), 2 = raw-write fallback
    /// (the game may snap the pawn back).</summary>
    public int Tier { get; init; }

    // --- Map mismatch detail (Code == -7) ---
    public string? CurrentMap { get; init; }
    public string? MarkerMap { get; init; }

    // --- Cursor teleport detail (Code == 0) ---
    public bool UsedCenter { get; init; }
    public double HitX { get; init; }
    public double HitY { get; init; }
    public double HitZ { get; init; }
}

/// <summary>One marker slot as reported by <c>teleport_get_markers</c>.</summary>
public sealed class TeleportMarker
{
    public int Slot { get; init; }
    public bool Valid { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double Pitch { get; init; }
    public double Yaw { get; init; }
    public double Roll { get; init; }
    public string Map { get; init; } = "";
}

/// <summary>
/// Maps Wirbel result codes (docs/teleport-spec.md §8) to user-facing hint
/// strings. Kept here (not in the DLL) so the UI owns its own phrasing.
/// </summary>
public static class TeleportCodes
{
    public const int Ok            = 0;
    public const int NotInit       = -1;
    public const int NoController  = -2;
    public const int NoPawn        = -3;
    public const int Reflection    = -4;
    public const int Invoke        = -5;
    public const int EmptyMarker   = -6;
    public const int MapMismatch   = -7;
    public const int NoHit         = -8;
    public const int NoCursor      = -9;
    public const int WriteFailed   = -10;

    public static string Describe(int code) => code switch
    {
        Ok           => "OK",
        NotInit      => "DLL not initialized — Connect & scan first (GWorld not found).",
        NoController => "No local player controller (are you in the main menu?).",
        NoPawn       => "Not possessing a pawn (menu / cutscene / spectator?).",
        Reflection   => "Engine layout unrecognized — please report the game + UE version.",
        Invoke       => "Game thread idle (menu / loading?) — try again during gameplay.",
        EmptyMarker  => "Marker slot is empty.",
        MapMismatch  => "Marker was saved on a different map — use Force to override.",
        NoHit        => "Nothing under the cursor / center within range.",
        NoCursor     => "No cursor available — enable the screen-center fallback.",
        WriteFailed  => "Raw write failed (protected memory?).",
        _            => $"Teleport failed (code {code}).",
    };
}
