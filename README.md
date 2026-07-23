# Expeditions Macro

Expeditions Macro is a Windows desktop utility for repeatable Anime Expeditions, Challenge, Story, and Raid runs in Roblox. It combines prioritized task plans, camera yaw alignment, editable unit placement, saved-team selection, UI-state detection, recovery, checkpoint extraction, and optional Discord reporting in one native app.

![Expeditions Macro in dark mode](docs/images/app-dark.png)

It uses screen capture and ordinary Windows input. It does not inject into Roblox, read process memory, or bypass anti-cheat systems.

> This is an independent, noncommercial community project. It is not affiliated with Roblox Corporation, Anime Expeditions, or the game's developers. Automation may be restricted by a game or platform's rules; you are responsible for how you use it.

## What it does

- Starts and stops with one configurable global hotkey; **F6** is the default, with letters, digits, punctuation, numpad keys, and supported function keys available.
- Can begin from the Roblox lobby and navigate to the configured Expeditions map and difficulty.
- Runs any enabled Trait, Stat, and Sprite Challenges on the global half-hour reset, recognizes five rotating maps, and loads the matching camera and placement models.
- Supports separate prestart and delayed in-match Challenge placements and configurable defeat retries; while Challenges are unavailable, the Macro scheduler selects the next eligible task by priority.
- Navigates Story Acts 1-5, Infinite, and Mastery across five maps, plus Spirit City Raid Acts 1-3.
- Saves prioritized Macro plans that can rotate Challenge, Expedition, Story, and Raid presets while preserving progress between launches.
- Optionally opens Units and loads Team 1-8 before a configured run.
- Fully zooms out with Roblox's `O` key (with mouse-wheel input retained as a fallback), toggles shift lock with the configured physical key, sets a top-down pitch, and aligns yaw against a learned full-turn camera model.
- Records, edits, saves, and tests Roblox-relative unit placements in one tool.
- Detects start, checkpoint, continue, confirmation, reward, victory, defeat, lobby, disconnect, and AFK Chamber screens.
- Captures the Roblox window directly instead of reading overlapping desktop windows, and normalizes HDR/Auto HDR pixels to SDR before detection.
- Detects reward cards from the stable reward overlay and available Select Upgrade controls, including layouts where a card is still collapsed or moving and regardless of rarity color.
- Extracts at the first real checkpoint or after a configured number of boss nodes. The spawn is not counted because it has no Extract action.
- Handles an early defeat even when extraction was planned later.
- Rejoins after a Roblox disconnect, an unexpected lobby teleport, or an inactivity teleport to the AFK Chamber. From the AFK Chamber it chooses **Return to Lobby**, then navigates back to the configured map and difficulty.
- Sends a safe `O` pulse every eight minutes during long-running gameplay and Challenge waits so normal inactivity does not reach the AFK Chamber.
- Detects the rare blue-void stage load before camera movement or placement, returns through Play, and retries the same task.
- Can optionally close only the verified Roblox player process and reopen a saved private server when bounded in-client recovery cannot restore the run.
- Confirms recovery screens across consecutive captures before rejoining, so one animation frame cannot reset an active run or its checkpoint-extraction progress.
- Optionally sends Discord Components V2 reports with a Roblox screenshot when each mode starts, per-match and total runtime on results, victory/defeat totals, recovery notices, semantic state accents, and localized Discord timestamps. A configured Discord user ID receives five restricted mentions when a macro stops unexpectedly.
- Records an unlimited timed Roblox screenshot sequence from Settings and packages the frames plus a manifest into one diagnostic ZIP. Automatic failure diagnostics keep the latest 10 action-state frames and add 10 frames at half-second intervals after an unexpected macro error.
- Stores webhook secrets with Windows DPAPI and emits no telemetry.

## Install

1. Open the [latest GitHub Release](https://github.com/Bluffy420/Expedition-Macro/releases/latest).
2. Download `ExpeditionsMacro-<version>-win-x64-setup.exe`, or the portable ZIP.
3. Verify the file against `SHA256SUMS.txt` if desired.

Windows 10 or Windows 11 x64 is required. Release builds are self-contained; a separate .NET installation is not required.

Follow the [Expeditions Macro setup guide](https://docs.google.com/document/d/10NeDNa3BNEwPEpZj0oVQiR98_7GN67dmKS-OZwaxALM/edit?usp=sharing) for a visual walkthrough. Join the public [Expeditions Macro Discord](https://discord.gg/wE6XSVyXsN) for setup help, bug reports, model sharing, and release announcements. Both links are available from the app sidebar.

## First-time setup

The instructions below refer to the **macro hotkey**. It defaults to **F6** and can be changed under **Settings > Controls** by clicking the key button and pressing a letter, number, punctuation key, numpad key, or supported function key.

Before starting a macro, assign Anime Expeditions' **Toggle Play Menu** action to one letter from A through Z. Under **Settings > Controls**, click **Play menu key** and press that same letter. This setting intentionally starts empty, so the app shows an immediate setup popup and refuses to start until it is configured. Keep it different from the macro start/stop hotkey. The same key opens Play from the lobby as well as from Victory, Defeat, or an unstarted match, avoiding the small on-screen Play button. If three verified presses do not open Play, the macro stops with a setup popup instead of continuing through an unreliable click path.

If a preset should load a saved Team, also assign the game's **Toggle Units** action to a letter and record it as **Unit menu key** under Settings. Leave a preset's Team setting at **Don't change** when the active team should remain untouched.

The **Shift Lock key** defaults to **Left Ctrl**. If Anime Expeditions uses a different Shift Lock binding, click that key under **Settings > Controls** and press the matching physical key. Left and right Shift/Ctrl are stored separately; letters, numbers, symbols, numpad keys, function keys, and common control keys are also supported. Keep it different from the macro hotkey, Play menu key, and Unit menu key.

### 1. Create a camera model

1. Open **Camera Models** and choose **New model**.
2. Put Roblox at the repeatable world position and goal yaw. Leave shift lock off.
3. Choose **Setup model**. The app arms the workflow without stealing focus.
4. Focus Roblox and press the macro hotkey.

Leave shift lock off before pressing the macro hotkey. Setup uses the standard 808 by 611 client size, zooms fully out, enables shift lock with the key configured under Settings, sets a top-down pitch, takes several full-client goal captures, and automatically chooses four stable, detailed map regions outside the usual HUD zones. The regions span the left, center, and right of the map so one animated structure or lighting effect cannot dominate the comparison. Setup first sweeps a small mouse-controlled neighborhood around the goal, then learns one full yaw turn with Right-arrow pulses. The neighborhood lets setup recognize a wrap that lands slightly beside the exact starting angle; a final fine sweep returns to the strongest goal view. The signed neighborhood atlas is saved with the model and applied before the live micro-search during later alignments. If the coarse scan only finds a degraded wraparound view, setup verifies the following yaw view and still requires a strong refined match before accepting it. The resulting atlases support large sensitivity-independent arrow corrections when far from the goal and learned one-pixel mouse correction near it. After two macro runs confirm the same load-in view, the app can try one locally learned mouse drag and verify the goal before falling back to normal atlas alignment; manual **Auto align** does not train or use this shortcut. Lighting normalization, temporal median capture, edge/gradient comparison, and multiple independent regions reduce sensitivity to lighting changes and moving units.

Camera regions are saved relative to the Roblox client and shown as colored outlines in the goal preview. When using **Auto align** by itself, the app also manages shift lock automatically and applies the recorded client size. If the fast yaw estimate misses its confidence target, alignment scans one complete arrow-based turn and refines the strongest match. The Expeditions workflow does not place units unless the final result meets the model target. Use **Show 30% overlay** to visually confirm the result.

### 2. Create a placement model

1. Open **Placement Models** and choose **New model**.
2. Enter a name and choose recorded delays or a default interval.
3. Choose **Record placements**, focus Roblox, then press the macro hotkey.
4. For each unit, press its top-row number and click the placement location.
5. Press the macro hotkey again to finish and save.

Recording uses the same 808 by 611 Roblox client size as the detector pack. Every row can be edited afterward: unit key, client-relative X/Y, and delay. **Test playback** replays the model through the same input path used during an Expedition run.

Saving the same name replaces the previous model.

### 3. Configure Expeditions

1. Open **Expeditions**.
2. Choose map, difficulty, camera model, and placement model.
3. Enable checkpoint extraction and set **Boss nodes before extract**:
   - `0`: extract at the first real in-run checkpoint.
   - `1`: extract at the first checkpoint after one boss node.
   - A high value, or disabling extraction: continue until defeat/victory.
4. Leave automatic lobby/disconnect/AFK recovery enabled unless you intend to supervise navigation.
5. Optionally paste a standard, Canary, or PTB Discord webhook and use **Test webhook** to verify it. Add a numeric Discord user ID if unexpected errors should send five mention alerts.
6. Save the preset and press the macro hotkey.

The app waits for the difficulty carousel animation to settle and verifies the active difficulty before continuing.

### 4. Configure Challenges

1. Open **Challenges** and enable any combination of Trait, Stat, and Sprite Challenges.
2. For each of the five maps, choose a camera model, a before-start placement model, an optional after-start placement model, and its elapsed-time delay.
3. Set how many times a Challenge may retry after defeat. The default is zero; a failed entry becomes eligible again at the next global reset.
4. Optionally enter a Discord webhook and numeric user ID for five error alerts, save the Challenge preset, and press the macro hotkey.

The selector order is fixed by Challenge type. The macro recognizes the current map, skips entries without **Select Stage**, and resets its per-window attempts at `xx:00` and `xx:30`. In a Macro plan, an unavailable Challenge task returns to the shared game-mode selector so the scheduler can run the next highest-priority eligible task. If all three entries remain unavailable across a complete global reset, it treats the daily limit as reached.

Before-start coordinates that fall beneath the centered Start Game dialog cannot reach the map. The Challenge runner automatically places unobstructed rows first, clicks Start deliberately, then immediately plays only the covered rows. A placement point therefore cannot accidentally start the match.

### 5. Configure Story or Raid

1. Open **Story** or **Raid** and create a named preset.
2. For Story, choose one of the five maps and an Act, Infinite, or Mastery route. Act routes can use Normal or Hard difficulty.
3. For Raid, choose Spirit City Act 1, 2, or 3.
4. Choose a camera model and at least one before-start or after-start placement model.
5. Optionally choose Team 1-8, set defeat retries, and save the preset.

Story and Raid pages edit presets. Add the saved preset to a Macro plan to run it. A before-start model plays in the countdown; an after-start model runs after its configured delay. Victory completes one scheduled attempt, while the preset's retry limit controls immediate retries after defeat.

### 6. Build a Macro plan

1. Open **Macro**, create a plan, and add saved Challenge, Expedition, Story, or Raid presets.
2. Order tasks with **Up** and **Down**. The first enabled, eligible row always runs next.
3. Set a victory target for finite tasks. Challenge tasks recur after their global reset instead of completing permanently.
4. For an Infinite Story preset, the target can be runtime: it completes after the configured runtime has elapsed and that run ends in defeat.
5. Save the plan and press the macro hotkey.

The scheduler never interrupts a live match. It updates saved task progress only after the current runner returns control, then reevaluates priority. **Reset progress** clears victories, defeats, runtime, completion, and Challenge eligibility for the plan.

### Optional Roblox restart recovery

Under **Macro > Roblox reconnect**, paste either a modern Roblox private-server share link or a legacy `privateServerLinkCode` link, then enable restart recovery. The link is global to the app rather than part of an individual plan.

Normal lobby, disconnect, AFK, party, and blue-void recovery remains the first path. If that bounded recovery errors or times out, the app:

1. verifies the visible Roblox window belongs to a supported Roblox player process;
2. closes only that process;
3. launches the private server through Windows' registered `roblox://` protocol;
4. waits for a new verified Roblox PID; and
5. reloads the saved plan and retries the same incomplete task.

This launch does not use a browser, stored account credentials, cookies, or process injection. The current Windows user must already be signed into the installed Roblox client. To prevent a restart loop, automatic process recovery stops after three restarts within ten minutes. The private-server link grants access to that server, so it is protected with Windows DPAPI and excluded from logs and diagnostics.

## Runtime behavior

The Expeditions loop prepares the camera, places units, starts the node, and watches for:

- the next Start button;
- checkpoint, Continue, or confirmation actions;
- reward-card selection;
- unplaced hotbar units that need retrying;
- extraction when the boss target is met;
- victory or defeat, followed by retry;
- lobby, disconnect, or AFK Chamber recovery.

The Challenges loop navigates the fixed three-entry selector, recognizes the rotating map, runs its map-specific camera and two placement phases, handles Victory or Defeat, opens Play with the configured in-game key, and returns through **Change Gamemode**. When no selected Challenge is eligible, it returns through the verified game-mode selector and lets the Macro scheduler choose the next eligible task without depending on the small Play icon or hotbar layout.

Story and Raid runners navigate from Play to their configured route, optionally load a saved Team, align the camera, run the two placement phases, select reward cards, and return to Play after Victory or the final Defeat. The Macro scheduler consumes one result at a time and then selects the highest-priority eligible task.

Leave shift lock off before starting a camera workflow. Camera preparation centers the pointer, uses the configured Shift Lock key before any pitch or fine-yaw mouse drag, and uses that same key during cleanup after success, cancellation, or failure.

Before yaw alignment, the app waits for stable rendered geometry in the saved camera regions. A prestart UI over a textureless blue world is treated as an incomplete stage load: no camera input or placement is sent, and automatic recovery returns through Play to retry the same route. Fine-yaw calibration uses the same atomic one-step right-drag gestures in both directions and verifies that the real zero pose survives a round trip before saving a model. A failed saved-neighborhood shortcut restores its pose and continues the current full-turn scan instead of recursively starting another rotation.

Stopping is cooperative. The app releases right mouse and shift-lock state where applicable, cancels pending work, and leaves Roblox at the standardized client size used for detection.

Roblox discovery verifies the owning player process instead of trusting a window title alone, so unrelated windows such as a Notepad document containing “Roblox” are ignored. If Roblox recreates its window during a teleport, the app refreshes the verified handle and retries focus. Standard sizing first keeps the normal window frame; when Windows or Roblox clamps that frame above 808 by 611, the app temporarily uses a verified borderless window so the exact client geometry can still be applied. The original frame style is restored when the app exits or an explicit bounds restore is requested.

### Diagnostic screenshot capture

Open **Settings**, enter a capture name and interval under **Debug capture**, then choose **Arm capture**. Focus Roblox and press the macro hotkey to start; press it again to stop. The app uses the standard 808 by 611 client size and writes a same-name ZIP under `diagnostics/`. A completed same-name capture replaces the previous ZIP. Enable the log option when a bug report needs both screenshots and the current run log.

Automatic failure capture is enabled by default. It retains the latest 10 action-state frames from the active macro, then captures 10 more Roblox-client frames at 0.5-second intervals after an unexpected Expeditions or Challenge error. These captures use timestamped ZIP names and do not run after a normal completion or manual Stop. The app keeps the 10 newest automatic error ZIPs and removes older ones; manual diagnostic ZIPs are not affected.

**Deep debug logging** is a separate, disabled-by-default option in Settings. Enabling it requires confirming a red storage warning because every operation can produce a multi-gigabyte ZIP. While enabled, every detector capture, detector score/state, high-level action, generated key/mouse event, and placement-recording input is written in sequence. The archive also contains sanitized app settings, the selected plan and presets, the active detector pack, the referenced camera/placement models, and a sanitized run log. A ZIP is finalized after success, cancellation, or failure and is never removed automatically. Discord webhook values, protected webhook material, Discord user IDs, and the active Windows username/profile path are excluded.

Developers can replay these archives with the source-only [Deep Debug Viewer](tools/ExpeditionsMacro.DeepDebugViewer/README.md), which synchronizes captured frames with nearby detector, workflow, and input events. The viewer is not included in release artifacts.

## Local files and privacy

Application data is stored under `%LocalAppData%\ExpeditionsMacro`:

- `camera-models/`
- `placement-models/`
- `presets/`
- `challenge-presets/`
- `story-presets/`
- `raid-presets/`
- `macro-plans/`
- `detector-packs/`
- `diagnostics/`
- `logs/`
- `settings.json`

See [PRIVACY.md](PRIVACY.md) for the exact network and screenshot behavior. Do not publish logs, models, or screenshots without reviewing them for account names, chat, notifications, or other private information.

## Build from source

Requirements:

- Windows 10/11 x64
- .NET SDK 10.0.302 or a compatible later 10.0 patch
- Git
- Inno Setup 6 only when creating the installer

```powershell
dotnet restore ExpeditionsMacro.slnx
dotnet build ExpeditionsMacro.slnx -c Debug
dotnet test tests/ExpeditionsMacro.Tests/ExpeditionsMacro.Tests.csproj -c Debug
```

The repository includes the detector image dataset, so the standard test command runs both unit tests and the complete golden-image regression suite. See [datasets/README.md](datasets/README.md) for its structure and capture requirements.

Build release artifacts:

```powershell
.\scripts\Generate-Icon.ps1
.\scripts\Build-Release.ps1 -Version 1.2.0
```

The release script publishes the self-contained app, creates the portable ZIP, creates the detector-pack ZIP, optionally invokes Inno Setup, and writes SHA-256 checksums plus a dependency inventory.

Pushing a stable `vX.Y.Z` tag runs the normal release workflow. After GitHub publishes the verified assets, the workflow normally sends a Components V2 announcement to the public Discord `#releases` channel using the encrypted `DISCORD_RELEASE_WEBHOOK_URL` repository secret. Maintainers can include `[skip discord]` in the tagged commit message to suppress that announcement. Prerelease tags such as `vX.Y.Z-beta.N`, `vX.Y.Z-alpha.N`, and `vX.Y.Z-rc.N` instead use the silent prerelease workflow, are marked as GitHub prereleases, do not become the latest stable release, and never send a Discord announcement.

CI runs fast tests, six golden-image shards, and dark/light UI snapshots as independent parallel jobs. Silent prerelease packaging also runs independently, so a beta can become downloadable before validation finishes. Any failing validation remains visible on the tagged commit and must be fixed before promoting the build to stable.

## Project layout

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for layer boundaries, [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) and [docs/TESTING.md](docs/TESTING.md) for contributor policy, [docs/GAME-BEHAVIOR.md](docs/GAME-BEHAVIOR.md) for field-confirmed navigation, [docs/CHALLENGE-MODE.md](docs/CHALLENGE-MODE.md) for Challenge behavior, and [docs/DETECTOR-PACKS.md](docs/DETECTOR-PACKS.md) for the update format.

## License

Source code is available under the [PolyForm Noncommercial License 1.0.0](LICENSE.md). Commercial use is not granted. Third-party game content and marks remain owned by their respective owners; see [NOTICE.md](NOTICE.md).
