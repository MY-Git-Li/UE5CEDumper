#include "DumperTestHUD.h"

#include "DumperTestActor.h"
#include "Engine/Engine.h"
#include "Engine/Font.h"
#include "EngineUtils.h"   // TActorIterator

void ADumperTestHUD::DrawHUD()
{
	Super::DrawHUD();

	// Re-find on staleness rather than caching once at BeginPlay: the actor is spawned
	// by a UWorldSubsystem and a seamless travel replaces it while this HUD lives on.
	if (!Sample.IsValid())
	{
		if (UWorld* W = GetWorld())
		{
			for (TActorIterator<ADumperTestActor> It(W); It; ++It)
			{
				Sample = *It;
				break;
			}
		}
	}
	const ADumperTestActor* Actor = Sample.Get();
	if (!Actor)
	{
		return;   // sample not running -- the one thing a blank screen SHOULD mean
	}

	// UEngine's font getters are static ENGINE_API (Engine.h:2893-2914). MediumFont is the
	// one AHUD::DrawText itself falls back to.
	//
	// The engine resolves these from EngineFonts config at init for every build that can
	// render, so a null here is not a case anyone has produced -- the guard is defensive,
	// NOT a documented failure mode, and its silence must not be read as evidence that
	// fonts were checked. Kept only because DrawText would hand a null straight to
	// FCanvasTextItem, and a crash in a test asset is worse than a missing line.
	UFont* Font = UEngine::GetMediumFont();
	if (!Font)
	{
		Font = UEngine::GetLargeFont();
	}
	if (!Font)
	{
		if (!bWarnedNoFont)
		{
			bWarnedNoFont = true;
			// NOTE: compiled out in Shipping -- Build.h:328 sets NO_LOGGING for that config
			// and LogMacros.h:146-158 keeps only Fatal. So in the build that matters this
			// path is silent, and a missing line looks the same as a dead sample. That is
			// acceptable ONLY because the branch is unreachable in practice; if it ever
			// fires for real, draw the complaint on screen instead of logging it.
			UE_LOG(LogTemp, Warning,
			       TEXT("[DumperTest] HUD has no usable font (GetMediumFont and GetLargeFont "
			            "both null) -- the heartbeat cannot draw. The sample itself is fine; "
			            "read TickCount at +0x518 in Live Walker instead."));
		}
		return;
	}

	// Same three lines, same meaning as the old on-screen-debug version, and the same
	// deliberate split of clocks: `frames` is driven by Tick, everything else by the
	// 1 Hz timer, so a dead timer shows as a frozen TickCount against a climbing
	// frames -- not as a blank screen. A diagnostic must not be driven by the thing it
	// is diagnosing.
	const float X = 16.f;
	float       Y = 16.f;
	const float LineHeight = 18.f;

	DrawText(FString::Printf(
		         TEXT("[DumperTest] frames=%d   TickCount=%d  "
		              "(frames must ALWAYS climb; TickCount climbs only if the 1 Hz timer runs)"),
		         Actor->GetFrameCount(), Actor->TickCount),
	         FLinearColor::Green, X, Y, Font);
	Y += LineHeight;

	DrawText(FString::Printf(TEXT("[DumperTest] Health.CurrentValue=%.0f  (must fall, wraps to 100)"),
	                         Actor->Health.CurrentValue),
	         FLinearColor::Yellow, X, Y, Font);
	Y += LineHeight;

	DrawText(FString::Printf(TEXT("[DumperTest] Health.BaseValue=%.0f  FrozenInt=%d  (both must NOT move)"),
	                         Actor->Health.BaseValue, Actor->FrozenInt),
	         FLinearColor::White, X, Y, Font);
}
