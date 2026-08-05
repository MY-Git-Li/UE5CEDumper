// ============================================================
// DumperTestActor — populate the property zoo with KNOWN values.
//
// The numbers here are the acceptance criteria. tools/ue-sample/README.md holds
// the same table; if you change a literal, change both.
//
// Encoding: every CJK string is a \uXXXX escape. See the header for why — a
// source file re-interpreted through the system code page would corrupt exactly
// the strings B28 is about, and the test would silently be measuring MSVC.
// ============================================================

#include "DumperTestActor.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "TimerManager.h"

#define LOCTEXT_NAMESPACE "DumperTest"

// The five CJK literals, defined once so the FText and FString mirrors are
// GUARANTEED to be the same bytes. Two copies of the same escape sequence would
// be a place for them to drift, and "the FString one is right and the FText one
// is wrong" is the entire measurement.
namespace DumperTestStrings
{
	// 統一 — U+7D71 U+4E00. 2 chars (even), one low-byte-00 char.
	static const TCHAR* Even2_OneNull = TEXT("\u7D71\u4E00");

	// 一言 — U+4E00 U+8A00. 2 chars (even), BOTH low-byte-00.
	// UTF-16LE bytes: 00 4E 00 8A — a NUL at offset 0 AND at offset 2.
	static const TCHAR* Even2_TwoNull = TEXT("\u4E00\u8A00");

	// 統一言語 — U+7D71 U+4E00 U+8A00 U+8A9E. 4 chars (even), two low-byte-00.
	static const TCHAR* Even4_TwoNull = TEXT("\u7D71\u4E00\u8A00\u8A9E");

	// 走一步 — U+8D70 U+4E00 U+6B65. 3 chars (ODD), exactly ONE low-byte-00.
	// 退 (U+9000) was here first and made the constant's NAME lie: it is also
	// low-byte-00, so the string held two. check_ue_sample_values invariant 4 caught
	// it on its first run - which is the whole argument for that gate existing.
	static const TCHAR* Odd3_OneNull = TEXT("\u8D70\u4E00\u6B65");

	// 日本語テスト — U+65E5 U+672C U+8A9E U+30C6 U+30B9 U+30C8.
	// 6 chars (even), NOT ONE low byte is 00 (E5 2C 9E C6 B9 C8).
	static const TCHAR* Even6_NoNull = TEXT("\u65E5\u672C\u8A9E\u30C6\u30B9\u30C8");
}

void UDumperTestPayload::Populate()
{
	PayloadText   = FText::FromString(DumperTestStrings::Even4_TwoNull);
	PayloadString = FString(DumperTestStrings::Even4_TwoNull);
	PayloadValue  = 909090;
}

ADumperTestActor::ADumperTestActor()
{
	// Tick is ON only to drive the on-screen readout. The VALUES are still driven by
	// the 1 Hz timer -- keeping the two on separate clocks is what makes "timer dead"
	// distinguishable from "nothing is drawing at all".
	PrimaryActorTick.bCanEverTick = true;

	// --- B28: FText ---
	Text_Even2_OneNull  = FText::FromString(DumperTestStrings::Even2_OneNull);
	Text_Even2_TwoNull  = FText::FromString(DumperTestStrings::Even2_TwoNull);
	Text_Even4_TwoNull  = FText::FromString(DumperTestStrings::Even4_TwoNull);
	Text_Odd3_OneNull   = FText::FromString(DumperTestStrings::Odd3_OneNull);
	Text_Even6_NoNull   = FText::FromString(DumperTestStrings::Even6_NoNull);
	Text_Ascii          = FText::FromString(TEXT("DumperTest FText ASCII"));
	// Same glyphs as Text_Even4_TwoNull but a DIFFERENT FTextHistory. If these
	// two disagree the fault is history traversal, not decoding.
	Text_Localized      = LOCTEXT("Loc_Cjk", "\u7D71\u4E00\u8A00\u8A9E");   // 統一言語
	Text_Empty          = FText::GetEmpty();

	// --- FString controls (never had B28) ---
	Str_Even2_OneNull = DumperTestStrings::Even2_OneNull;
	Str_Even4_TwoNull = DumperTestStrings::Even4_TwoNull;
	Str_Odd3_OneNull  = DumperTestStrings::Odd3_OneNull;
	Str_Even6_NoNull  = DumperTestStrings::Even6_NoNull;

	Name_Cjk = FName(DumperTestStrings::Even2_OneNull);

	// --- containers (V1a) ---
	Set_Int.Add(1337);
	Set_Int.Add(4242);
	Set_Int.Add(8888);

	Map_NameToInt.Add(FName(TEXT("Alpha")), 111);
	Map_NameToInt.Add(FName(TEXT("Beta")),  222);
	Map_NameToInt.Add(FName(TEXT("Gamma")), 333);

	Map_IntToFloat.Add(1, 1.5f);
	Map_IntToFloat.Add(2, 2.5f);
	Map_IntToFloat.Add(3, 3.5f);

	Arr_Int = { 10, 20, 30, 40, 50 };

	// Struct-element container: an FText one level deep inside a TArray, so a
	// B28 regression that only shows up under the deep descent still has a home.
	{
		FDumperTestStat A;
		A.StatName = FName(TEXT("Attack"));
		A.Value    = 7777;
		A.Label    = FText::FromString(DumperTestStrings::Even2_OneNull);
		Arr_Struct.Add(A);

		FDumperTestStat B;
		B.StatName = FName(TEXT("Defence"));
		B.Value    = 6666;
		B.Label    = FText::FromString(DumperTestStrings::Even2_TwoNull);
		Arr_Struct.Add(B);
	}

	// --- TOptional (V1c) ---
	Opt_Int_Set   = 24680;
	Opt_Float_Set = 99.5f;
	Opt_Str_Set   = FString(TEXT("OptionalPresent"));
	// Opt_Int_Unset is LEFT ALONE on purpose. A scan for 0 must not find it.

	// --- numerics ---
	I8_Neg   = -5;      // Int8 yes / UInt8 no — the unit-tested boundary
	U8_Small = 1;
	U8_Max   = 255;
	I16      = -12345;
	U16      = 54321;
	I32      = 1234567;
	I64      = 8899001122334455LL;
	F32      = 513.36f;             // Round 513.36 / Trunc 513.3 / Ceil 513.4
	F64      = 2718.281828;

	// --- raw non-UPROPERTY holes ---
	RawInt    = 0x5A5A5A5A;   // 1515870810
	RawFloat  = 777.75f;
	RawDouble = 31415.926535;

	bFlagA     = 1;
	bFlagB     = 0;
	bFlagC     = 1;
	bPlainBool = true;

	Grade = EDumperTestGrade::Elite;

	for (int32 i = 0; i < 8; ++i)
	{
		FixedArr[i] = (i + 1) * 100;   // 100..800
	}

	Health.BaseValue    = 100.f;
	Health.CurrentValue = 100.f;

	TickCount = 0;
	FrozenInt = 424242;   // never written again — the Unchanged control
}

void ADumperTestActor::BeginPlay()
{
	Super::BeginPlay();

	// Created at runtime rather than as a default subobject so it is a genuine
	// heap UObject in GObjects, reachable only through the pointer — which is
	// what makes it a real "Locate in GWorld" target rather than a level actor.
	Payload = NewObject<UDumperTestPayload>(this, TEXT("DumperTestPayload"));
	if (Payload)
	{
		Payload->Populate();
	}

	if (UWorld* W = GetWorld())
	{
		W->GetTimerManager().SetTimer(TickHandle, this, &ADumperTestActor::OnSecondTick, 1.0f, /*loop*/ true);
	}

	// Warning level so it survives a Shipping build's default log verbosity \u2014
	// this line is how you confirm the actor exists WITHOUT attaching the dumper,
	// which matters the first time you run the package and nothing shows up.
	UE_LOG(LogTemp, Warning,
	       TEXT("[DumperTest] ADumperTestActor ready at %p, Payload=%p. ")
	       TEXT("Find it via Instances -> 'DumperTestActor'."),
	       this, Payload.Get());
}

void ADumperTestActor::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	++FrameCount;
	DrawHeartbeat();
}

void ADumperTestActor::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
	if (UWorld* W = GetWorld())
	{
		W->GetTimerManager().ClearTimer(TickHandle);
	}
	Super::EndPlay(EndPlayReason);
}

void ADumperTestActor::OnSecondTick()
{
	++TickCount;

	// The canonical group-scan case, running once a second:
	//   CurrentValue  DECREASED   (and INCREASED on the wrap)
	//   BaseValue     UNCHANGED   <- the slot a group match needs
	// A scan for "something went down while something else held still" has a
	// guaranteed hit here, which no commercial game can promise on demand.
	Health.CurrentValue -= 1.f;
	if (Health.CurrentValue <= 1.f)
	{
		Health.CurrentValue = Health.BaseValue;
	}

	// FrozenInt and BaseValue are deliberately NOT touched.

}

/// Put the two ticking values ON SCREEN.
///
/// WHY. This actor is invisible by design -- no mesh, no HUD, it exists only to be read
/// by the dumper -- so "is the timer actually running?" was unanswerable without
/// attaching the dumper and walking the object. That question came up three times in one
/// session and cost several rounds of log forensics, because a group scan finding nothing
/// CHANGED and a game that is not ticking look identical from the outside.
///
/// Now it is a glance. If the numbers on screen are moving, the sample is alive and any
/// "0 results" belongs to the scan; if they are frozen, the sample is the problem.
/// That is the whole diagnostic, available before the dumper is even injected.
///
/// -DumperTestNoHud turns it off for a clean screenshot.
void ADumperTestActor::DrawHeartbeat() const
{
	if (!GEngine || FParse::Param(FCommandLine::Get(), TEXT("DumperTestNoHud")))
	{
		return;
	}

	// ⚠ THIS READOUT DOES NOT WORK IN A SHIPPING BUILD, AND CANNOT BE MADE TO.
	// Verified 2026-08-05 against the installed 5.4 source, after the packaged
	// Shipping exe stayed silent while Development printed fine:
	//
	//     Engine/Source/Runtime/Engine/Private/UnrealEngine.cpp:11397
	//     void UEngine::AddOnScreenDebugMessage(uint64 Key, ...)
	//     {
	//     #if !(UE_BUILD_SHIPPING || UE_BUILD_TEST)
	//
	// The whole BODY is compiled out, so the message never enters the list and no
	// amount of flag-setting can bring it back. The three stores below are kept
	// because they are correct and free for Development/Test (a console command or
	// a screenshot request can flip GAreScreenMessagesEnabled underneath us), but
	// they are NOT what makes Shipping quiet.
	//
	// This comment previously claimed the opposite -- "not compiled out of Shipping,
	// the display call site is gated `#if !(UE_BUILD_TEST)`". That read the DISPLAY
	// gate and missed the ADD gate, and the flag-setting fix built on it could never
	// have worked. Second wrong assertion about this same function; the rule the repo
	// already has applies -- read the gate, do not infer it from a sibling.
	//
	// To get a Shipping heartbeat, draw it yourself: a tiny AHUD subclass overriding
	// DrawHUD() + DrawText(), set as the GameMode's HUDClass. AHUD::DrawHUD runs in
	// Shipping. NOT DONE -- it needs a re-cook to verify and nothing may claim to work
	// here without one.
	GEngine->bEnableOnScreenDebugMessages        = true;
	GEngine->bEnableOnScreenDebugMessagesDisplay = true;
	GAreScreenMessagesEnabled                    = true;

	// A fixed key so each line REPLACES its previous self instead of scrolling a wall of
	// text off the screen -- these fire once a second for the life of the process.
	GEngine->AddOnScreenDebugMessage(/*Key*/ 1001, /*Time*/ 1.5f, FColor::Green,
		FString::Printf(TEXT("[DumperTest] frames=%d   TickCount=%d  "
		                    "(frames must ALWAYS climb; TickCount climbs only if the 1 Hz timer runs)"),
		                    FrameCount, TickCount));
	GEngine->AddOnScreenDebugMessage(1002, 1.5f, FColor::Yellow,
		FString::Printf(TEXT("[DumperTest] Health.CurrentValue=%.0f  (must fall, wraps to %.0f)"),
		                Health.CurrentValue, Health.BaseValue));
	GEngine->AddOnScreenDebugMessage(1003, 1.5f, FColor::Silver,
		FString::Printf(TEXT("[DumperTest] Health.BaseValue=%.0f  FrozenInt=%d  (both must NOT move)"),
		                Health.BaseValue, FrozenInt));
}

#undef LOCTEXT_NAMESPACE
