#include "DumperTestSubsystem.h"

#include "DumperTestActor.h"
#include "Engine/World.h"

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
