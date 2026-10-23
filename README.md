# Idle Spiral OpenSpeedy runtime patch

Version 2 runtime patch with Steam autoload for Idle Spiral 3.0.82 build 117 (Unity/Mono x64), tested with OpenSpeedy 3.3.8. It replaces elapsed admission and the simulation loop inside `GameSystem.Update` with Harmony 2.4.3. Original game binaries remain unchanged.

## Behavior

When OpenSpeedy is enabled above 1×, the normal four-step frame budget becomes `ceil(4 * speed)` (80 at 20×), capped at 128. The loop checks independent real time between simulation steps and yields once eight milliseconds have elapsed. It drains each 0.02-second step immediately before executing it, so unexecuted steps remain queued without consuming Offline Boost. Production, battles and boost admission still execute through the existing game methods; admitted-delta properties reflect executed steps.

Queued simulation time is capped at `min(40, 2 * speed)` seconds. Sustained excess is returned to the existing offline-gap path, preventing unlimited catch-up debt. Under overload this deliberately sacrifices the requested effective multiplier in favor of responsiveness. Eight milliseconds is a cooperative boundary: a single expensive step, GC pause, rendering or other game code can exceed it and cannot be preempted by this patch.

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

`patch-status.txt` records version, state, factor, step budget, admitted seconds, backlog, real simulation milliseconds, yielded frames and overflow routed offline. Its file timestamp is from the game's accelerated process and should not be treated as independent real time. `patch-error.txt`, if created, contains loader/managed errors.

A full 20× still requires enough CPU time to execute 1000 simulation steps per real second. The patch preserves time, but cannot guarantee every future scene will sustain that throughput. Network/server-controlled systems and independent UI schedules are outside this patch.

## Verification

An isolated process embeds the same Mono runtime shipped with the game. Its tests verify full step accounting, fractional carry, short/long gap handling, disabled/paused behavior, 128-step cap, timed yield/resume, bounded admission, 10,000-frame overload conservation, the independent timer, and Harmony install/remove. Remote invocation is exercised on that process before use on the game.

The live test measures changes in automatic save files using an independent external Stopwatch. Version 1 reached about 20× but lacked a real-time frame limit. Version 2 measurements and external responsiveness observations are supplied separately. Extreme multipliers are tested in the isolated scheduler, not imposed on the user's live game. This does not reproduce or prove elimination of every historical freeze.

Backups of the original core assemblies and pre-application saves are retained separately. The running game continues to autosave legitimate gameplay progress; no saves were restored or edited during testing.

## Build

```powershell
dotnet build .\Runtime\Runtime.csproj -c Release
dotnet build .\Injector\Injector.csproj -c Release
dotnet build .\MonoHost\MonoHost.csproj -c Release
dotnet build .\Bootstrap\Bootstrap.csproj -c Release
```

Override the GameManaged MSBuild property if the game lives elsewhere. The game assemblies are compile-time references and are not distributed with this patch.

The x64 loader uses Mono's embedding API. The small vendored memory/PE helpers originate from SharpMonoInjector commit 73566c1be1e8e1bb25ab60683557958368b2cd47 (MIT); memory disposal is corrected to MEM_RELEASE. The loader filters required exports, uses 64-bit exception pointers, attaches/detaches each remote thread, restricts target process names, and retains remote allocations if a thread times out instead of freeing executing code. Removal unpatches this owner only and does not unload a live managed assembly.

Sources: [Mono embedding](https://www.mono-project.com/docs/advanced/embedding/), [Harmony transpilers](https://harmony.pardeike.net/v2/articles/patching-transpiler.html), [Windows independent interrupt timer](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryunbiasedinterrupttimeprecise), [SharpMonoInjector](https://github.com/warbler/SharpMonoInjector).
