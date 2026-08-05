// ============================================================
// DumperTestSubsystem — spawns ADumperTestActor into every game world.
//
// WHY A SUBSYSTEM RATHER THAN PLACING THE ACTOR IN THE LEVEL. A level is a
// BINARY asset: it cannot be written or reviewed as text, cannot be diffed, and
// a checklist step that says "remember to drag the actor into the map" is a step
// that will eventually be forgotten — and its failure mode is silent (the
// dumper simply finds no DumperTestActor, which looks like a dumper bug).
//
// A UWorldSubsystem needs no asset edit, is created automatically for every
// world, and therefore works in PIE, in the packaged build, and in whatever map
// happens to load first. The whole project stays reviewable as source.
// ============================================================
#pragma once

#include "CoreMinimal.h"
#include "Subsystems/WorldSubsystem.h"
#include "DumperTestSubsystem.generated.h"

class ADumperTestActor;

UCLASS()
class DUMPERTEST_API UDumperTestSubsystem : public UWorldSubsystem
{
	GENERATED_BODY()

public:
	virtual bool ShouldCreateSubsystem(UObject* Outer) const override;
	virtual void OnWorldBeginPlay(UWorld& InWorld) override;

private:
	/// Held in a UPROPERTY so the spawned actor is GC-rooted through the
	/// subsystem for the world's whole lifetime. Without this the actor is
	/// still owned by the level, but the extra reference makes the ownership
	/// edge explicit — and gives Related Objects a second inbound edge to find.
	UPROPERTY()
	TObjectPtr<ADumperTestActor> SpawnedActor;
};
