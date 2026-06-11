# Tips & Recipes

User-facing how-to recipes for common tasks with UE5CEDumper. Each recipe maps
a goal to the panels / buttons that get you there. Add new recipes as separate
`##` sections.

-----

## Forcing camera rotation in a fixed-view (2.5D / 45°) game

UE4 and UE5 share the camera pipeline, so the same handful of entry points work
across versions. A "locked" 45° / top-down camera usually just means the game
**never wired input to the camera** — the underlying UE camera chain is still
complete and writable.

### The shared camera chain (bottom → top)

```
APlayerController.ControlRotation
        ↓ (bUsePawnControlRotation / bInheritYaw ...)
USpringArmComponent.RelativeRotation   ← 2.5D games most often hard-code the angle here
        ↓
UCameraComponent
        ↓
APlayerCameraManager.CameraCachePrivate.POV.Rotation  ← final output, recomputed every tick
```

Typical fixed-view setup: the SpringArm's `RelativeRotation.Pitch` is set to
-45/-60 with `bUsePawnControlRotation=false` and `bInheritYaw=false`, so mouse
input never reaches the camera.

### Approaches (easy → hard)

**1. Debug Camera (easiest, UE4/5 both).**
`UCheatManager::ToggleDebugCamera` spawns an `ADebugCameraController` — a free-fly
camera (WASD + mouse), zero memory edits. In UE5CEDumper: **Console → load exec
commands → the Debug Camera control row** (visible when `ToggleDebugCamera` is
found). Use **Force On / Force Off** (robust — handles Shipping builds whose
`DisableDebugCamera` is stripped by switching the player's controller back), or
**Copy CE Script** for a self-contained CE checkbox (tick = on, untick = off).
Caveat: this is a *separate* camera; game logic keeps running underneath.

**2. Rotate the SpringArm / SceneComponent (most common real fix).**
Instances → find `SpringArmComponent` → open it in **Live Walker** → look at
`RelativeRotation` (FRotator = Pitch/Yaw/Roll; note UE5 LWC makes these `double`,
UE4 `float`). Edit **Yaw** to turn the view. If the value snaps back, a Blueprint
is re-setting it every tick — either **Freeze** the field, or invoke the function
instead of a raw write:

- `USceneComponent::K2_SetRelativeRotation` — BlueprintCallable, exists in UE4 &
  UE5, callable through the invoke helper via ProcessEvent (cleaner than a raw
  write — it runs `UpdateComponentToWorld`).
- While you're there, check the `bInheritYaw` / `bUsePawnControlRotation`
  bitfields — flipping `bUsePawnControlRotation` to `true` often restores mouse
  control of the view outright.

**3. PlayerController.ControlRotation.**
`APlayerController::SetControlRotation` is a cross-version native function (the K2
`SetControlRotation` is BlueprintCallable). In fixed-view games it's usually cut
off by the SpringArm's inherit flags, so combine it with the bitfield flips from
approach 2 for it to have any effect.

**4. CameraManager POV (last resort).**
`APlayerCameraManager.CameraCachePrivate.POV.Rotation` is the final value, but
`UpdateCamera` overwrites it every tick — a raw write does nothing. You'd have to
hook (UE5CEDumper already has the Stark/MinHook ProcessEvent base) or NOP the
write. Rarely worth it; the first three paths usually suffice.

### Caveats

- Some games don't use a SpringArm — they `SetViewTargetWithBlend` to a fixed
  `CameraActor` placed in the level. Then rotate **that actor's** RootComponent
  instead (`K2_SetActorRotation` / `K2_SetWorldRotation`).
- 2.5D side effects: after rotating, billboard sprites, occlusion culling, and
  "front-only" art can break — that's a limitation of the game's art, not a tech
  problem.
- FOV: `APlayerCameraManager.DefaultFOV` (or the `fov` console command) is also
  cross-version; you'll often want to widen it when pulling the view back.

### Fastest workflow in UE5CEDumper

Instance Finder → search `SpringArmComponent` → Live Walker → inspect
`RelativeRotation` → edit **Yaw** and watch the screen turn → if it gets
overwritten, flip `bUsePawnControlRotation` / `bInheritYaw`, or invoke
`K2_SetRelativeRotation` instead of writing raw.
