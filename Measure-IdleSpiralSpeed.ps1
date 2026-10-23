param(
    [string]$SavePath = (Join-Path $env:USERPROFILE 'AppData\LocalLow\IdleSystem\IdleSpiral\IdleSpiralSaveV2\shared-v1\primary.idlespiral-save'),
    [ValidateRange(3, 120)][int]$SampleCount = 21,
    [ValidateRange(1, 30)][int]$IntervalSeconds = 2
)

# Read automatic saves only; no UI input, game calls, or writes to game files.
$ErrorActionPreference = 'Stop'
$samples = [Collections.Generic.List[object]]::new()
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
for ($index = 0; $index -lt $SampleCount; $index++) {
    $envelope = $null
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        try {
            $envelope = Get-Content -Raw -LiteralPath $SavePath | ConvertFrom-Json
            $dto = ($envelope.entries | Where-Object key -eq 'dto').json | ConvertFrom-Json
            if ($null -eq $dto.totalTime -or $null -eq $dto.offlineBoostAmount) {
                throw 'Save does not contain the expected Idle Spiral 3.0.82 fields.'
            }
            break
        } catch {
            if ($attempt -eq 1) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
    $samples.Add([pscustomobject]@{
        ObserverElapsedSeconds = $stopwatch.Elapsed.TotalSeconds
        ProcessSavedAtUtc = ([datetime]$envelope.savedAtUtc).ToUniversalTime().ToString('O')
        GameSeconds = [long]$dto.totalTime
        OfflineMemory = [double]$dto.offlineBoostAmount
        InGameBoostEnabled = [bool]$dto.isOfflineBoost
        DefeatedEnemies = @($dto.enemy_defeated)
        SelectedEquationCount = @($dto.spiralEquipment_isSelected | Where-Object { $_ }).Count
    })
    if ($index -lt $SampleCount - 1) { Start-Sleep -Seconds $IntervalSeconds }
}

$changed = @($samples | Group-Object ProcessSavedAtUtc | ForEach-Object { $_.Group[0] } |
    Sort-Object ObserverElapsedSeconds | Select-Object -Skip 1)
if ($changed.Count -lt 2) {
    throw 'Fewer than two automatic save changes were observed after sampling began. Use a longer sample window; the initial save may predate the observer.'
}
$first = $changed[0]
$last = $changed[-1]
$realSeconds = $last.ObserverElapsedSeconds - $first.ObserverElapsedSeconds
$processSeconds = ([datetime]$last.ProcessSavedAtUtc - [datetime]$first.ProcessSavedAtUtc).TotalSeconds
$gameSeconds = $last.GameSeconds - $first.GameSeconds
if ($gameSeconds -lt 0 -or $processSeconds -le 0) {
    throw 'Save reset or clock discontinuity detected; repeat without restarting/loading the game.'
}
$boostObserved = @($samples | Where-Object InGameBoostEnabled).Count -gt 0
[pscustomobject]@{
    Note = 'Rates exclude the pre-existing save. Save cadence adds sampling error; ProcessClockMultiplier measures save timestamps, which use real time with CalendarGuard.'
    RealSeconds = [Math]::Round($realSeconds, 4)
    ProcessClockSeconds = [Math]::Round($processSeconds, 4)
    ProcessClockMultiplier = [Math]::Round($processSeconds / $realSeconds, 4)
    GameSeconds = $gameSeconds
    GameProgressMultiplier = [Math]::Round($gameSeconds / $realSeconds, 4)
    OfflineMemoryGain = [Math]::Round($last.OfflineMemory - $first.OfflineMemory, 4)
    InGameBoostObserved = $boostObserved
    FirstSelectedEquationCount = $first.SelectedEquationCount
    LastSelectedEquationCount = $last.SelectedEquationCount
    Samples = $samples
} | ConvertTo-Json -Depth 5
