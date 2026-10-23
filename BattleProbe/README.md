# Battle Spiral observer

Temporary observation for the installed Idle Spiral 3.0.82 build 117. This is separate from Steam autoload. It counts actual main-loop admission, BattleCtrl.UpdateBattle time, ally/enemy update time, logical attack accumulation, direct attacks, enemy attacks and enemy creation. It records gameplay snapshots without writing game state or changing speed.

Build with `dotnet build BattleProbe/BattleProbe.csproj -c Release`. Game references are compile-time only; override GameManaged when needed. Place the output DLL beside the existing Harmony DLL and invoke Loader.Install through the existing PatchLoader. The eight observer hooks use a separate Harmony owner. The next GameSystem.Update starts a 30-real-second sample using QueryUnbiasedInterruptTimePrecise; completion removes only these observer hooks. Loader.Remove also removes them explicitly. Do not run multiple observations in one loaded assembly identity; use a new session/assembly for another sample.

Verification: build passed with zero warnings/errors; install/remove succeeded in the game's isolated Mono runtime before live use. The live sample completed and its hooks were explicitly removed again. Combat time matched admitted main-loop time, and generated attack progress plus starting fraction equalled direct attacks plus ending fraction. No defeat occurred during this sample, so reward settlement was not exercised. Nuke 10's existing PerformanceBattleProgress modifier slows attacks by reward-slot-count times Nuke cycles; this observer preserves that behavior.

Local base: d90dec7, branch battle-acceleration-audit. No remote repository or PR was created.
