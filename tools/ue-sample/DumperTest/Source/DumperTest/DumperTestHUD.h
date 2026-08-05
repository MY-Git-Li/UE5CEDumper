// DumperTestHUD -- the heartbeat readout, drawn through a path that SURVIVES SHIPPING.
//
// WHY THIS CLASS EXISTS. The readout used to go through
// `GEngine->AddOnScreenDebugMessage`, which is a no-op in a Shipping package:
//
//     Engine/Source/Runtime/Engine/Private/UnrealEngine.cpp:11397
//     void UEngine::AddOnScreenDebugMessage(uint64 Key, ...)
//     {
//     #if !(UE_BUILD_SHIPPING || UE_BUILD_TEST)
//
// The whole BODY is compiled out, so the message never enters the list and no flag
// can bring it back. That silence was read twice as "the sample is broken" and once
// as "config"; it was neither.
//
// AHUD is the draw path that is NOT gated. Verified against the installed 5.4 source
// before writing a line of this (the rule this file exists to honour -- read the gate,
// never infer it from a sibling):
//   - AHUD::PostRender      HUD.cpp:149  -- no UE_BUILD_* gate, needs a Canvas
//   - AHUD::DrawHUD         HUD.cpp:638  -- no gate
//   - AHUD::DrawText        HUD.cpp:929  -- no gate
//   - AHUD ctor sets bShowHUD = true     HUD.cpp:75
//
// DEVELOPMENT USES THE SAME PATH ON PURPOSE. The old code drew through a mechanism
// that worked in one configuration and not the other, so "it works in Development"
// hid the Shipping failure for two rounds. One path, one behaviour, both configs.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/HUD.h"
#include "DumperTestHUD.generated.h"

class ADumperTestActor;

UCLASS()
class ADumperTestHUD : public AHUD
{
	GENERATED_BODY()

public:
	virtual void DrawHUD() override;

private:
	/// The actor whose values are drawn. Weak: the actor is spawned by
	/// UDumperTestSubsystem and can outlive or predecease this HUD across a travel.
	/// Re-found whenever it goes stale, so a level change cannot silently blank the
	/// readout -- a blank screen must mean "the sample is not running", nothing else.
	TWeakObjectPtr<ADumperTestActor> Sample;

	/// One-shot complaint when there is no usable font, so "nothing on screen" can be
	/// told apart from "drew nothing because it had nothing to draw with".
	bool bWarnedNoFont = false;
};
