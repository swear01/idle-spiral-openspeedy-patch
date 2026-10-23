# Idle Spiral OpenSpeedy runtime patch

Version 2 bounded runtime plus an adaptive budget guard and Steam autoload for Idle Spiral 3.0.82 build 117 (Unity/Mono x64), tested with OpenSpeedy 3.3.8. It replaces elapsed admission and the simulation loop inside `GameSystem.Update` with Harmony 2.4.3. Original game binaries remain unchanged.

RenderGuard now suppresses the central main spiral through the game's existing `CreateSpirals.SetRenderingSuppressed` ownership API, on the Unity main thread. It preserves graph history, theta updates, achievement notifications, cameras and UI. Removing the guard releases only its own suppression owner on the next game update; suppression requested by menus remains intact. This hides the main spiral, not every battle/equation preview or cosmetic object. Rendering suppression alone did not show a measurable FPS improvement in the observed scene.

TheoryGuard shares the two common study-progress parameter reads only during one `TheoryContainer.UpdateTheories` call. The original per-theory EXP update order and arithmetic remain intact. Cache state is per thread, cleared after each call, including exceptions; nested calls use the original reads. Two known compiler delegates are patched, rather than all parameter getters. In isolated tests, 100,000 EXP values over 10,000 changing steps match the original mantissa bits and exponents. Live sampled Theory cost decreased from 56.60 to 10.98 microseconds per simulation step; total sampled step cost decreased from 212.37 to 173.17 microseconds. These are instrumented observations, not a guaranteed speedup in every scene.

The current multiplier was preserved at approximately 41.69x. Stable 200–500x and absence of high-speed freezes remain unproven. The existing 128-step and 8–16 ms cooperative limits remain in place. The latest complete Steam restart remains pending. `Formula/` contains an experimental pure-formula cache: isolated tests passed, but live cache-eligible hit/miss counters remained zero, so there was no verified benefit. Its hooks were removed and its DLL is excluded from the package and autoload. Cheap branches bypass those counters; zero does not mean the functions were never called.

Later in the same session, frames stalled for about seven real seconds. The stalls persisted with both new guards removed, and a separate 20-second temporary OpenSpeedy disable did not restore updates; its enabled state was then restored without changing the factor. Two local minidumps placed the main thread in Steam client code or Steam-related native waits. Stack pointer scans are not a fully unwound stack and do not establish a root cause. These stalls remain unresolved. Removing the engine FPS cap also failed to restore normal throughput; that diagnostic restored the original frame-rate target. All temporary observers were removed. No Steam API, achievement callback or original game assembly was changed to bypass the wait.

## Behavior

When OpenSpeedy is enabled above 1×, the normal four-step frame budget becomes `ceil(4 * speed)` (80 at 20×), capped at 128. The loop checks independent real time between simulation steps. Its baseline budget is eight milliseconds; AdaptiveGuard adjusts this within 8–16 milliseconds according to queued work and observed time outside simulation. It drains each 0.02-second step immediately before executing it, so unexecuted steps remain queued without consuming Offline Boost. Production, battles and boost admission still execute through the existing game methods; admitted-delta properties reflect executed steps.

Queued simulation time is capped at `min(40, 2 * speed)` seconds. Sustained excess is returned to the existing offline-gap path, preventing unlimited catch-up debt. Under overload this deliberately sacrifices the requested effective multiplier in favor of responsiveness. The selected budget is a cooperative boundary: a single expensive step, GC pause, rendering or other game code can exceed it and cannot be preempted by this patch.

The timer uses `QueryUnbiasedInterruptTimePrecise` from KernelBase.dll, whose export was verified locally. OpenSpeedy 3.3.8 hooks QPC/tick/system clocks but does not hook this API. A deterministic 10,000-frame overload test verifies bounded backlog and conservation of simulated, offline and queued time.

The game's original offline policy uses a two-second elapsed-time threshold. The replacement accounts for OpenSpeedy's faster clock: gaps up to two real seconds are admitted in chunks that the original clock accepts, and longer gaps retain the game's offline-memory behavior. When OpenSpeedy is disabled or at 1×, the original clock calls are used. Time.timeScale=0 remains paused.

The patch validates both known game assembly hashes and the simulation-loop IL structure, including external branch entries and exception blocks. Unsupported versions fail instead of applying a speculative patch. Autoload adds official UnityDoorstop 4.5.0's x64 winhttp.dll, its configuration, and an owned payload directory; no original game binary, Steam API library, save, achievement flag or Steam launch option is edited.

## Use

Requires Windows 10 or newer and the installed .NET 8 runtime. Keep the package files together and start the game normally. Remove version 1 using its own script before applying version 2; the installer rejects a still-active older owner rather than silently leaving it running. The delivered game session has already been upgraded.

```powershell
.\Use-SpeedPatch.ps1 -Action Apply
.\Use-SpeedPatch.ps1 -Action Remove
```

The Steam autoload package uses Install-SteamAutoload.ps1 to install once. Doorstop's minimal mscorlib-only entrypoint launches a hidden .NET 8 helper and immediately returns. The helper waits at least two real seconds and for the current game's main-loop initialization marker in Player.log, checks the target process, then calls Loader.AutoInstall through the existing Mono embedding injector. It exits after completion or a 90-second readiness timeout. AutoInstall installs the bounded loop before loading the user's existing hash-checked OpenSpeedy DLL; it never writes the shared speed or settings. This avoids loading game/Harmony assemblies during Unity's early bootstrap. The simulation patch source is unchanged; Loader adds this autoload entrypoint.

Restarting clears the in-memory patch, then autoload reapplies it. Keep OpenSpeedy running to retain its shared speed. The native DLL defaults to 1× if no controller retains the factor and enables itself when first loaded. Use the autoload installer's Remove action after closing the game to restore normal startup. A manifest verifies ownership and hashes before removal; unknown files and diagnostic logs are retained. Existing proxy/config/payload paths are never overwritten.

`patch-status.txt` records version, state, factor, step budget, admitted seconds, backlog, real simulation milliseconds, yielded frames and overflow routed offline. Measure rates with an external monotonic observer. `patch-error.txt`, if created, contains loader/managed errors.

AdaptiveGuard preserves the distributed v2 runtime byte for byte and checks its SHA-256 before patching one 80000-tick constant in RunFrame. A separate Harmony owner observes RunSimulationFrame and selects the budget. Under queue pressure the requested budget rises by 0.25 ms per frame to 16 ms; low pressure reduces it by 0.125 ms toward 8 ms. An exponential average of outside work limits the selection against a soft 40 ms frame target, retaining the original 8 ms baseline when outside work is already heavy. This is not a frame-rate guarantee. Removing AdaptiveGuard restores the original fixed budget without removing authentication, calendar or simulation fixes.

`adaptive-status.txt` is authoritative for the selected budget and adds independent real time, simulation/game seconds, process ID and outside-work timing. The unchanged v2 `patch-status.txt` still prints its baseline `frameBudgetMilliseconds=8`; its actual duration and backlog counters remain valid. AutoInstall enters TheoryGuard → RenderGuard → AdaptiveGuard → AuthGuard → CalendarGuard → the original simulation/OpenSpeedy loader, installing the guards after their dependencies. No speed setter, persistent frame-rate setting, battle mode or reward logic is changed. Status files are snapshots and can be stale during a stall. A new full Steam restart remains pending; isolated autoload and native bootstrap components are tested.

A full 20× still requires enough CPU time to execute 1000 simulation steps per real second. The patch preserves time, but cannot guarantee every future scene will sustain that throughput. Network/server-controlled systems and independent UI schedules are outside this patch.

## Verification

The calendar guard rewrites the 53 methods in the four hash-checked game assemblies that directly call DateTime.Now/UtcNow and DateTimeOffset.Now/UtcNow, including generated iterator/lambda methods. It reads documented GetSystemTime (UTC) and converts to local time where required. OpenSpeedy 3.3.8 does not hook that entry point; an isolated hooked-process test verifies one real second against QueryUnbiasedInterruptTimePrecise while DateTime itself advances about 55 seconds. Global .NET/Unity clock methods remain untouched. Previously compiled DailySpiral clock delegates are covered. Existing future daily reset dates and reward counts are preserved; this prevents further clock drift but does not reset already claimed days or unblock a future saved daily date early.

The four calendar assemblies are Assembly-CSharp, IdleSpiralDomain, IdleLibrary and IdleSpiral.CloudSave. The latter two cover backup/envelope timestamps and additional DateTimeOffset season deadlines. GetSystemTime returns UTC according to [Microsoft's API documentation](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getsystemtime).

BeginAutomaticSave is separately gated to at least 15 real seconds per save controller, matching the game's intended automatic-save interval. Its original save preparation, atomic write, completion and failure handling remain intact. Manual, pause, focus-loss and quit saves bypass this additional gate. The original accelerated coroutine can still poll the gate. Use Install-SteamAutoload.ps1 -Action Upgrade for an existing owned installation; ownership and hashes are checked before atomically replacing changed payload files. Unsupported or modified installations fail without overwriting unrelated files.

AuthGuard covers four methods in two additional hash-checked assemblies, IdleSystem.Auth and IdleSystem.CloudSave. Five UTC clock reads use real UTC. Two token-expiry reads use the earlier of the SDK's cached expiry and the ID token's JWT exp value. A live failure had an already expired JWT but a cached expiry in the future, so the original SDK skipped refresh and cloud inspection received 403. This guard only decides when to request the original SDK refresh; decoding exp does not verify identity or signature, and server validation remains unchanged. Missing or malformed expiry requests refresh. The original five-minute margin remains intact. Firebase documents [ID token expiry](https://firebase.google.com/docs/auth/admin/verify-id-tokens) and [session refresh](https://firebase.google.com/docs/auth/admin/manage-sessions).

In the live session, the original SDK refreshed successfully with the same account. The game's original read-only cloud coordinator returned LocalOnly; the user then pressed RETRY and confirmed the normal no-cloud-backup display. No backup was uploaded, downloaded into the active save, merged or deleted. Isolated tests cover expired/future-cached tokens, valid and mutated tokens, earlier cached expiry, refresh margin and malformed expiry. The new AuthGuard startup chain has component verification; a subsequent real Steam restart remains pending.

An isolated process embeds the same Mono runtime shipped with the game. Its tests verify full step accounting, fractional carry, short/long gap handling, disabled/paused behavior, 128-step cap, timed yield/resume, bounded admission, 10,000-frame overload conservation, the independent timer, and Harmony install/remove. Remote invocation is exercised on that process before use on the game.

The live test measures changes in automatic save files using an independent external Stopwatch. Version 1 reached about 20× but lacked a real-time frame limit. Version 2 measurements and external responsiveness observations are supplied separately. Extreme multipliers are tested in the isolated scheduler, not imposed on the user's live game. This does not reproduce or prove elimination of every historical freeze.

The observer excludes the save that already existed before sampling; otherwise a 15-second-old initial save can materially bias the rate. Its default sampling window is 40 real seconds. With CalendarGuard, saved timestamps run at real time even though OpenSpeedy's QPC/tick clocks remain accelerated. A subsequent actual Steam launch was observed with the original executable and Steam parent, successful automatic loading, factor 10 and approximately 10.03× game progress with no offline-memory overflow. New achievement unlock/upload remains untested. An earlier roughly 55× session generated a Windows AppHangB1/1002 event and was closed; no dump from that hang exists, so its root cause remains unresolved. Do not call the high-speed freeze fixed.

The user confirmed restarting the game, but did not change the multiplier. OpenSpeedy's settings file currently saves 10× and was last written before the old game closed; its hash differs from the pre-fix record. The patch only reads SP_GetSpeed and never calls SP_SetSpeed or writes OpenSpeedy settings. OpenSpeedy 3.3.8's useSpeed hook restores the saved speed when mounted, but there is no writer audit for this particular change. The origin of the saved 10× remains unresolved; do not attribute it to the user or force a higher speed to reproduce the hang.

Backups of the original core assemblies and pre-application saves are retained separately. The running game continues to autosave legitimate gameplay progress; no saves were restored or edited during testing.

## Build

```powershell
dotnet build .\Runtime\Runtime.csproj -c Release
dotnet build .\Injector\Injector.csproj -c Release
dotnet build .\MonoHost\MonoHost.csproj -c Release
dotnet build .\Bootstrap\Bootstrap.csproj -c Release
dotnet build .\Clock\Clock.csproj -c Release
dotnet build .\Auth\Auth.csproj -c Release
dotnet build .\Adaptive\Adaptive.csproj -c Release
dotnet build .\Render\Render.csproj -c Release
dotnet build .\Theory\Theory.csproj -c Release
```

Override the GameManaged MSBuild property if the game lives elsewhere. The game assemblies are compile-time references and are not distributed with this patch.

AdaptiveGuard intentionally requires the bundled v2 runtime hash. A rebuilt runtime with different bytes needs an explicit compatibility review and hash update. Bootstrap enters TheoryGuard, whose autoload chain installs RenderGuard, AdaptiveGuard and the existing authentication/calendar/runtime chain before installing the new guards. FrameProbe and SimulationProbe are temporary 15-real-second observers used for verification; neither is part of autoload or the delivered payload. SimulationProbe samples every 127 steps to avoid aliasing the game's four-step currency cadence. Its per-stage times are inclusive and nested stages must not be added together.

The x64 loader uses Mono's embedding API. The small vendored memory/PE helpers originate from SharpMonoInjector commit 73566c1be1e8e1bb25ab60683557958368b2cd47 (MIT); memory disposal is corrected to MEM_RELEASE. The loader filters required exports, uses 64-bit exception pointers, attaches/detaches each remote thread, restricts target process names, and retains remote allocations if a thread times out instead of freeing executing code. Removal unpatches this owner only and does not unload a live managed assembly.

Sources: [Mono embedding](https://www.mono-project.com/docs/advanced/embedding/), [Harmony transpilers](https://harmony.pardeike.net/v2/articles/patching-transpiler.html), [Windows independent interrupt timer](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryunbiasedinterrupttimeprecise), [SharpMonoInjector](https://github.com/warbler/SharpMonoInjector).
