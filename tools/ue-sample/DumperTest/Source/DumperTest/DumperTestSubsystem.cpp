#include "DumperTestSubsystem.h"

#include "DumperTestActor.h"
#include "Engine/World.h"
#include "HAL/IConsoleManager.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"

namespace
{

/// Turn on the engine's idle-when-backgrounded mode, from CODE and only on request.
///
/// WHY NOT THE INI, which is where this obviously belongs. `t.IdleWhenNotForeground`
/// is registered `ECVF_Cheat` (Core/Private/HAL/ConsoleManager.cpp), and
/// ConfigUtilities.cpp refuses a cheat cvar from any ini but consolevariables.ini with
/// an `ensureMsgf(false, ...)`. In the editor that is a yellow message; **in a cook it
/// is 22 errors and `Cook failed / ExitCode=25`**. Measured 2026-08-05: putting it in
/// DefaultEngine.ini's [SystemSettings] made the package unbuildable, and it was the
/// only distinct error in a 198 KB log. The project cannot use ConsoleVariables.ini
/// either -- ConfigCacheIni.cpp reads that from FPaths::EngineDir(), so it is a shared
/// engine-wide developer file that is never packaged.
///
/// Setting it from C++ is a different path and is NOT blocked: the Shipping restriction
/// (DISABLE_CHEAT_CVARS, Build.h) hides cheat cvars from the CONSOLE -- IConsoleManager.h
/// says "hidden in the console and cannot be changed by the user" -- while
/// ProcessUserConsoleInput is the only place that refuses them.
///
/// WHY OPT-IN rather than always on. While you work in the dumper's UI the game IS the
/// background app, so an idling game thread makes every game-thread dispatch (Teleport,
/// invoke, POV) time out. Idle mode is wanted for exactly two checks -- B8's deferred
/// collision restore and Grausam's foreground lock -- and is actively harmful to the
/// rest of the sample. Hence a switch, defaulting to off.
void ApplyIdleWhenNotForeground()
{
	if (!FParse::Param(FCommandLine::Get(), TEXT("DumperTestIdle")))
	{
		UE_LOG(LogTemp, Warning,
		       TEXT("[DumperTest] idle-when-backgrounded is OFF (pass -DumperTestIdle to enable). "
		            "Game-thread dispatches keep working while you are in the dumper UI."));
		return;
	}

	IConsoleVariable* CVar = IConsoleManager::Get().FindConsoleVariable(TEXT("t.IdleWhenNotForeground"));
	if (!CVar)
	{
		UE_LOG(LogTemp, Error,
		       TEXT("[DumperTest] -DumperTestIdle requested but t.IdleWhenNotForeground was not "
		            "found -- the engine renamed or removed it; B8/Grausam cannot be staged."));
		return;
	}

	CVar->Set(TEXT("1"), ECVF_SetByCode);
	UE_LOG(LogTemp, Warning,
	       TEXT("[DumperTest] idle-when-backgrounded is ON (t.IdleWhenNotForeground=%d). "
	            "Alt-tab now STALLS the game thread -- expect game-thread commands to time out."),
	       CVar->GetInt());
}

} // namespace

bool UDumperTestSubsystem::ShouldCreateSubsystem(UObject* Outer) const
{
	if (!Super::ShouldCreateSubsystem(Outer))
	{
		return false;
	}

	// Game and PIE only. The editor opens preview/inactive worlds constantly
	// (thumbnail rendering, asset previews, the blueprint viewport) and spawning
	// into those would litter the editor with actors that have nothing to do
	// with the test — and would make the Instances count depend on which asset
	// you last clicked.
	if (const UWorld* World = Cast<UWorld>(Outer))
	{
		return World->WorldType == EWorldType::Game
		    || World->WorldType == EWorldType::PIE;
	}
	return false;
}

void UDumperTestSubsystem::OnWorldBeginPlay(UWorld& InWorld)
{
	Super::OnWorldBeginPlay(InWorld);

	if (SpawnedActor)
	{
		return;   // already spawned for this world
	}

	ApplyIdleWhenNotForeground();

	FActorSpawnParameters Params;
	Params.Name = TEXT("DumperTestActor_0");
	// The dumper is often pointed at this actor by NAME, so a collision must not
	// silently produce "DumperTestActor_1" and send someone looking at the wrong
	// object. Requesting the name and accepting a rename only if it is genuinely
	// taken keeps the common case stable.
	Params.NameMode = FActorSpawnParameters::ESpawnActorNameMode::Requested;
	Params.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;

	SpawnedActor = InWorld.SpawnActor<ADumperTestActor>(
		ADumperTestActor::StaticClass(), FVector::ZeroVector, FRotator::ZeroRotator, Params);

	UE_LOG(LogTemp, Warning, TEXT("[DumperTest] subsystem spawned actor=%p in world '%s'"),
	       SpawnedActor.Get(), *InWorld.GetName());
}
