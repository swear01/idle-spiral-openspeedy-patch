param([Parameter(Mandatory)][string]$OldPackage,[Parameter(Mandatory)][string]$NewPackage,[Parameter(Mandatory)][string]$WorkDirectory)
$ErrorActionPreference='Stop'
$fixture=Join-Path $WorkDirectory ('hundred-installer-'+[guid]::NewGuid().ToString('N'))
$managed=Join-Path $fixture 'IdleSpiral_Data\Managed'
New-Item -ItemType Directory -Path $managed -Force | Out-Null
foreach($name in 'Assembly-CSharp.dll','IdleSpiralDomain.dll','IdleLibrary.dll','IdleSpiral.CloudSave.dll','IdleSystem.Auth.dll','IdleSystem.CloudSave.dll','IdleSystem.Analytics.dll','com.rlabrecque.steamworks.net.dll') {
    Copy-Item -LiteralPath (Join-Path 'E:\SteamLibrary\steamapps\common\Idle Spiral\IdleSpiral_Data\Managed' $name) -Destination $managed
}
& (Join-Path $OldPackage 'Install-SteamAutoload.ps1') -Action Install -GameDirectory $fixture
$manifest=Join-Path $fixture 'IdleSpiralSpeedPatch\install-manifest.json'
$bootstrap=Join-Path $fixture 'IdleSpiralSpeedPatch\IdleSpiral.Autoload.dll'
$oldManifest=[IO.File]::ReadAllBytes($manifest)
$oldBootstrap=(Get-FileHash -LiteralPath $bootstrap).Hash
$lock=[IO.File]::Open($bootstrap,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
$rejected=$false
try{& (Join-Path $NewPackage 'Install-SteamAutoload.ps1') -Action Upgrade -GameDirectory $fixture}catch{$rejected=$true}finally{$lock.Dispose()}
if(-not $rejected -or (Get-FileHash -LiteralPath $bootstrap).Hash -ne $oldBootstrap -or
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($manifest)) -ne [Convert]::ToBase64String($oldManifest) -or
    (Test-Path -LiteralPath (Join-Path $fixture 'IdleSpiralSpeedPatch\IdleSpiral.DesignCountGuard.dll')) -or
    (Test-Path -LiteralPath (Join-Path $fixture 'IdleSpiralSpeedPatch\IdleSpiral.HousekeepingGuard2.dll')) -or
    (Test-Path -LiteralPath (Join-Path $fixture 'IdleSpiralSpeedPatch\IdleSpiral.ClockIsolation.dll'))) {throw 'Upgrade rollback failed.'}
'PASS: locked replacement rolls back all three new guards, bootstrap and manifest.'
& (Join-Path $NewPackage 'Install-SteamAutoload.ps1') -Action Upgrade -GameDirectory $fixture
& (Join-Path $NewPackage 'Install-SteamAutoload.ps1') -Action Upgrade -GameDirectory $fixture
$data=Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
if(@($data.gameAssemblies.PSObject.Properties).Count -ne 8){throw 'Eight assembly preflight missing.'}
foreach($file in $data.files){if((Get-FileHash -LiteralPath (Join-Path $fixture $file.path)).Hash -ne $file.sha256){throw 'Owned hash mismatch.'}}
$guard=Join-Path $fixture 'IdleSpiralSpeedPatch\IdleSpiral.DesignCountGuard.dll'
$guardBytes=[IO.File]::ReadAllBytes($guard);[IO.File]::AppendAllText($guard,'tamper')
$rejected=$false
try{& (Join-Path $NewPackage 'Install-SteamAutoload.ps1') -Action Remove -GameDirectory $fixture}catch{$rejected=$true}
if(-not $rejected -or -not(Test-Path -LiteralPath $bootstrap)){throw 'Tampered ownership accepted.'}
[IO.File]::WriteAllBytes($guard,$guardBytes)
$unknown=Join-Path $fixture 'IdleSpiralSpeedPatch\unowned-note.txt';Set-Content -LiteralPath $unknown 'keep'
& (Join-Path $NewPackage 'Install-SteamAutoload.ps1') -Action Remove -GameDirectory $fixture
if(-not(Test-Path -LiteralPath $unknown)){throw 'Unknown file removed.'}
'PASS: idempotent upgrade, eight assembly hashes, tamper rejection and unowned-file preservation.'
$fresh=Join-Path $fixture 'fresh';New-Item -ItemType Directory -Path "$fresh\IdleSpiral_Data\Managed" -Force | Out-Null
Copy-Item -Path "$managed\*" -Destination "$fresh\IdleSpiral_Data\Managed"
& (Join-Path $NewPackage 'Install-SteamAutoload.ps1') -Action Install -GameDirectory $fresh
& (Join-Path $NewPackage 'Install-SteamAutoload.ps1') -Action Remove -GameDirectory $fresh
[IO.File]::AppendAllText((Join-Path $fresh 'IdleSpiral_Data\Managed\IdleSystem.Analytics.dll'),'tamper')
$rejected=$false
try{& (Join-Path $NewPackage 'Install-SteamAutoload.ps1') -Action Install -GameDirectory $fresh}catch{$rejected=$true}
if(-not $rejected -or (Test-Path -LiteralPath (Join-Path $fresh 'winhttp.dll'))){throw 'Modified analytics SDK not rejected before writes.'}
'PASS: fresh install/remove and modified analytics SDK rejection before writes.'

$nativeFixture=Join-Path $fixture 'unsupported-speedpatch64.dll'
[IO.File]::WriteAllText($nativeFixture,'unsupported-native-fixture')
$rejected=$false
try{& (Join-Path $NewPackage 'Install-SteamAutoload.ps1') -Action Install -GameDirectory $fresh -OpenSpeedyDll $nativeFixture}catch{$rejected=$true}
if(-not $rejected -or (Test-Path -LiteralPath (Join-Path $fresh 'winhttp.dll'))){throw 'Unsupported native binary was accepted.'}
'PASS: unsupported native binary rejected before writes.'
