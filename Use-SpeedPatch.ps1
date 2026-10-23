param([ValidateSet('Apply', 'Remove')][string]$Action = 'Apply')
$ErrorActionPreference = 'Stop'
$targets = @(Get-Process -Name IdleSpiral -ErrorAction SilentlyContinue)
if ($targets.Count -ne 1) { throw 'Exactly one running IdleSpiral game is required.' }
$target = $targets[0]
$gameDirectory = Split-Path -Parent $target.Path
$domain = Join-Path $gameDirectory 'IdleSpiral_Data\Managed\IdleSpiralDomain.dll'
$expected = 'caec145b335c11fdd4cb86f5ce72f057ccec83297507ad0d1ed838aceb29f37b'
if ((Get-FileHash -LiteralPath $domain -Algorithm SHA256).Hash -ne $expected) {
    throw 'This patch supports Idle Spiral 3.0.82 build 117 only.'
}
$loader = Join-Path $PSScriptRoot 'IdleSpiral.PatchLoader.exe'
$assembly = Join-Path $PSScriptRoot 'IdleSpiral.SpeedPatch.SteamGuard.dll'
& $loader $Action.ToLowerInvariant() $target.Id $assembly
if ($LASTEXITCODE -ne 0) { throw 'Patch loader failed. Check patch-error.txt and do not retry blindly.' }
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'patch-status.txt')
