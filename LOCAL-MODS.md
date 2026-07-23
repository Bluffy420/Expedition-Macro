# Local Modification Log — Expeditions Macro

**Date:** 2026-07-23
**Base:** upstream `main` @ `3c5d65a` (Merge PR #15)
**Status:** All changes are UNCOMMITTED in the working tree. Kept private/local — do not push.
**Test state:** Full suite green — 462 tests passing.

This file records what was changed and why, so future feature work can build on it.
Keep it updated when you add more.

---

## 1. Camera alignment confidence fix (the original problem)

### Symptom
Camera alignment failed after 3 attempts. Required 72%, got 40–46%.
Neighborhood evidence ~64%, final confidence ~42%.

### Root cause (confirmed by live probing, not guessed)
The stored camera model was **calibrated on a sharper render** than current windowed mode
produces. Its auto-learned `baseline_score` was 0.9995 → `success_threshold` 0.716.
The live windowed view only matched that stale reference at ~0.42–0.61, so it could never pass.
The macro was working correctly — the reference was stale.

**Primary fix = recalibrate in current windowed conditions.** New model "School Windowed"
learned threshold 0.671; live goal view scores 0.90–0.95 against it. Passes.

**Recalibrate whenever render conditions change** (windowed↔fullscreen, graphics quality,
sharpness). The model is tied to render quality.

### Secondary fix = blur-robust scorer (code change)
The confidence scorers were ~70% edge/gradient-derived, which collapses under defocus.
Rebalanced toward blur-tolerant terms (gradient direction + normalized luminance), keeping
the blur-fragile terms (gradient magnitude ratio, chamfer) as weak cues. Wrong-yaw rejection
preserved (verified by tests).

- `src/ExpeditionsMacro.Vision/Camera/CameraRegisteredScorer.cs`
  `ManagedStructuralSimilarity`: `0.50·grad + 0.30·lum + 0.20·mag` → `0.55·grad + 0.38·lum + 0.07·mag`
- `src/ExpeditionsMacro.Vision/VisionScorer.cs`
  `RobustSimilarity`: `0.45·grad + 0.35·chamfer + 0.20·lum` → `0.50·grad + 0.22·chamfer + 0.28·lum`

Measured improvement grows with blur severity; goal-vs-wrong separation widened slightly.

### Tests added
- `tests/.../CameraAlignmentTests.cs`: `RegisteredCameraScore_ToleratesWindowedDefocusWithoutAcceptingWrongYaw`
  (fails on old weights 82.2%, passes on new 87.0% — genuinely guards the fix)
- `tests/.../VisionScorerTests.cs`: `RobustSimilarity_ToleratesWindowedDefocusWithoutAcceptingWrongYaw`
  + `Blur(...)` helper (Gaussian blur via OpenCvSharp)

---

## 2. Feature: Camera pre-alignment before placement recording

Placements are now recorded from the same aligned viewpoint the macro uses at runtime.

- `src/ExpeditionsMacro.Automation/Placement/PlacementService.cs`
  `RecordAsync` gained optional params `camera`, `cameraModel`, `progress`. If a camera model
  is supplied, it aligns (via `CameraAlignmentEngine.AlignAsync`) after client resize and
  before capture. On alignment failure it throws `InvalidOperationException("Camera alignment
  failed before placement recording: ...")` and records nothing (user chose abort-on-failure).
- `src/ExpeditionsMacro.App/Pages/PlacementModelsPage.xaml(.cs)`
  New "Align camera before recording (optional)" ComboBox (camera models + "(none)").
  Loads the chosen model, passes `_services.Camera` + progress into `RecordAsync`.

### Tests
- `tests/.../PlacementServiceTests.cs`:
  `Record_WhenCameraAlignmentFails_AbortsBeforeCapturing`,
  `Record_WithoutCameraModel_SkipsAlignment` (+ `CreateCameraModel` helper, `NullCameraRepository`)

---

## 3. Feature: Cycle expedition presets until stopped

Run each enabled Expedition preset once, then rotate to the next, looping until stopped
(different maps → different loot).

- `src/ExpeditionsMacro.Core/Models/MacroPlan.cs`
  `MacroTaskDefinition.Cycle` (default false). `IsRecurring` now also true for
  `Expedition && Cycle`. `Validate()` rejects `Cycle` on non-Expedition kinds.
  Additive/schema-compatible (old plans load with Cycle=false).
- `src/ExpeditionsMacro.Automation/Scheduling/MacroScheduler.cs`
  - `Advance`: cycling Expedition never sets `Completed` (stays eligible forever).
  - `SelectNext`: rewritten. Cycling Expedition tasks round-robin among themselves by
    least-recently-run (`LastAttemptAt`, null=first), so they alternate A→B→C→A regardless of
    row priority. The cycling group is anchored at its best priority so it still interleaves
    with non-cycling tasks. Non-cycling behavior unchanged.
- `src/ExpeditionsMacro.App/Pages/MacroPage.xaml(.cs)`
  - `ExecuteExpeditionAsync`: `maximumRuns: task.Cycle ? 1 : null` (one run then rotate).
  - Task editor: "Cycle" CheckBox, visible only for Expedition kind; wired into add/edit/reset;
    included in `SameWork` so toggling it resets task progress.

### Tests
- `tests/.../MacroSchedulerTests.cs`:
  `CyclingExpedition_IsRecurringAndNeverCompletes`,
  `Cycle_IsRejectedForNonExpeditionTasks`,
  `CyclingExpeditions_RoundRobinByLeastRecentlyRun`,
  `CyclingExpeditions_StayEligibleAcrossManyRuns`

---

## Diagnostic tooling (untracked, not part of the build)

- `tools/ScoreProbe/` — small console probe (references Vision + Windows projects).
  Modes it has had: live goal-evidence scorer, atlas self-consistency, stability/baseline
  predictor, live rotating-score monitor. `run-monitor.bat` launches it with the SDK on PATH.
  Useful for diagnosing future calibration issues.
- `debug-view/` — captured PNGs (goal vs live side-by-side, region crops). Safe to delete.

---

## How to rebuild the runnable app

.NET SDK 10.0.302 is installed at `C:\Users\Shreshth Bhardwaj\dotnet-sdk` (user profile).

```powershell
$env:PATH = "$HOME\dotnet-sdk;$env:PATH"
cd C:\Users\Shreshth Bhardwaj\Desktop\expeditions-macro-src
dotnet restore ExpeditionsMacro.slnx --locked-mode
dotnet build ExpeditionsMacro.slnx -c Release --no-restore
dotnet test tests\ExpeditionsMacro.Tests\ExpeditionsMacro.Tests.csproj -c Release --no-build
```

Runnable self-contained app (shares `%LocalAppData%\ExpeditionsMacro` with the installed app):
```
src\ExpeditionsMacro.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\ExpeditionsMacro.exe
```
Close any running instance before rebuilding (it locks the output DLLs).

## Files changed (working tree)
- Vision: `CameraRegisteredScorer.cs`, `VisionScorer.cs`
- Automation: `Placement/PlacementService.cs`, `Scheduling/MacroScheduler.cs`
- Core: `Models/MacroPlan.cs`
- App: `Pages/MacroPage.xaml(.cs)`, `Pages/PlacementModelsPage.xaml(.cs)`
- Tests: `CameraAlignmentTests.cs`, `VisionScorerTests.cs`, `PlacementServiceTests.cs`, `MacroSchedulerTests.cs`

## Open follow-ups / ideas for next time
- Cycling currently does 1 run per preset before rotating; could add "N runs per preset".
- Placement align currently aborts on failure; could add a warn-and-continue option.
- Region selector could avoid the bottom hotbar strip if a weak region is ever picked there.
- Consider committing to a local branch (e.g. `local/mods`) so changes are versioned without
  being pushed anywhere.
