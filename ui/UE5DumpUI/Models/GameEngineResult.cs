namespace UE5DumpUI.Models;

/// <summary>
/// Result of resolving the live GEngine object (<c>resolve_game_engine</c>) for
/// the Live Walker "Start from GameEngine" root. The DLL finds the engine by a
/// reflected member (GameViewport), NOT by class name, so this works across UE
/// versions and game-specific UGameEngine subclasses. The two *Ok flags report
/// whether the standard pointer members are present and non-null — i.e. this is
/// the active engine, not a CDO or an early-boot stub.
/// </summary>
public sealed class GameEngineResult
{
    /// <summary>True when a live engine object was found (<see cref="Address"/> is valid).</summary>
    public bool Found { get; init; }

    public string Address { get; init; } = "";

    /// <summary>Class FName, e.g. "GameEngine" or a game subclass.</summary>
    public string ClassName { get; init; } = "";

    /// <summary>The engine's GameViewport member is present and non-null.</summary>
    public bool GameViewportOk { get; init; }

    /// <summary>The engine's GameInstance member is present and non-null.</summary>
    public bool GameInstanceOk { get; init; }
}
