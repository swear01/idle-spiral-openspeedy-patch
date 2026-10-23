param(
    [ValidateSet('Install', 'Upgrade', 'Remove', 'Status')][string]$Action = 'Status',
    [string]$GameDirectory = 'E:\SteamLibrary\steamapps\common\Idle Spiral',
    [string]$OpenSpeedyDll = 'C:\Program Files\OpenSpeedy\speedpatch64.dll'
)
$ErrorActionPreference = 'Stop'
$GameDirectory = (Resolve-Path -LiteralPath $GameDirectory).Path
$payload = Join-Path $GameDirectory 'IdleSpiralSpeedPatch'
$manifestPath = Join-Path $payload 'install-manifest.json'
$proxy = Join-Path $GameDirectory 'winhttp.dll'
$config = Join-Path $GameDirectory 'doorstop_config.ini'
$isolatedNativeHash = 'c72ba4d9e35c5c00efc113e541da91a02dbc0c85c6fb2effff5cee63898bc70c'
$boundedRuntimeHash = 'dbfeebdc66091f27f63320971cd3f369b3fd66928fc6a66af336425e44df635f'
$expected = @{
    'IdleSpiralDomain.dll' = 'caec145b335c11fdd4cb86f5ce72f057ccec83297507ad0d1ed838aceb29f37b'
    'Assembly-CSharp.dll' = 'a67ee1d53fc2247d738ff2166f1a5e217bcb3fdebc3dfebf0a21393b8e5fbfbb'
    'IdleLibrary.dll' = '9e665da9e2b44503d98db50557dfefb5cd43c892ab47b8a7b8995535b3784a3e'
    'IdleSpiral.CloudSave.dll' = '1b4c9c790e662ed09a6255c56c01a841865c2569882622ed9142db6b40a0970f'
    'IdleSystem.Auth.dll' = 'c1e2a0f1c8352428ab8f4d9bb2bb02d380d48781c0499c02ef76baf0f6d05923'
    'IdleSystem.CloudSave.dll' = '1daee61b66847527ccb60415183c1c167fb4f416047f1591ffb1b94fadd8ca74'
    'IdleSystem.Analytics.dll' = '1e1c233c358ffa6475269d6d86b0c8c460ac0dc651bd0a765599242bad684029'
    'com.rlabrecque.steamworks.net.dll' = '0b7d5a5fc171bd89b663291dc83002a5f69e7124882d2cb1f075295057030058'
}

function Assert-OwnedInstallation($manifest) {
    if ($manifest.owner -ne 'IdleSpiral-SpeedPatch-SteamAutoload-v1') { throw 'Unknown installation owner.' }
    foreach ($file in $manifest.files) {
        if ($file.path -ne 'winhttp.dll' -and $file.path -ne 'doorstop_config.ini' -and
            $file.path -notmatch '^IdleSpiralSpeedPatch\\[^\\/]+$') { throw 'Unexpected owned installation path.' }
        $path = [IO.Path]::GetFullPath((Join-Path $GameDirectory $file.path))
        if (-not $path.StartsWith($GameDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid installation path.' }
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "Installed file changed or missing: $path. Preserve it and inspect before reinstalling/removing."
        }
    }
}

if ($Action -eq 'Status') {
    if (-not (Test-Path -LiteralPath $manifestPath)) { Write-Output 'Autoload is not installed.'; return }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    Assert-OwnedInstallation $manifest
    Write-Output 'Autoload installed; all owned file hashes match.'
    foreach ($name in 'autoload-status.txt', 'autoload-error.txt', 'patch-status.txt', 'patch-error.txt', 'clock-status.txt', 'clock-error.txt', 'auth-status.txt', 'auth-error.txt', 'adaptive-status.txt', 'adaptive-error.txt', 'render-status.txt', 'render-error.txt', 'theory-status.txt', 'theory-error.txt', 'housekeeping-status.txt', 'housekeeping-error.txt', 'designcount-status.txt', 'designcount-error.txt', 'isolation-status.txt', 'isolation-error.txt', 'macrostep-status.txt', 'macrostep-error.txt', 'automation-status.txt', 'automation-error.txt') {
        $path = Join-Path $payload $name
        if (Test-Path -LiteralPath $path) { Write-Output "${name}:"; Get-Content -LiteralPath $path }
    }
    return
}

if ($Action -in 'Install', 'Upgrade') {
    $nativePath = $OpenSpeedyDll
    if ($Action -eq 'Upgrade') {
        $loaderSettings = Get-Content -LiteralPath (Join-Path $payload 'openspeedy-loader.txt')
        if ($loaderSettings.Count -ne 2 -or $loaderSettings[1] -ne $isolatedNativeHash) {
            throw 'Installed OpenSpeedy configuration does not support clock isolation.'
        }
        $nativePath = $loaderSettings[0]
    }
    if ((Get-FileHash -LiteralPath $nativePath).Hash -ne $isolatedNativeHash) {
        throw 'Unsupported OpenSpeedy native binary for clock isolation; nothing was changed.'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'payload\IdleSpiral.SpeedPatch.SteamGuard.dll')).Hash -ne $boundedRuntimeHash) {
        throw 'Unsupported packaged simulation runtime; preserve the bundled version.'
    }
}

if ($Action -eq 'Upgrade') {
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Install autoload before upgrading.' }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    Assert-OwnedInstallation $manifest
    if ((Get-FileHash -LiteralPath (Join-Path $payload 'IdleSpiral.SpeedPatch.SteamGuard.dll')).Hash -ne $boundedRuntimeHash) {
        throw 'Installed simulation runtime is not supported by the adaptive guard.'
    }
    $changes = @('IdleSpiral.ClockIsolation.dll', 'IdleSpiral.MacroStepGuard.dll', 'IdleSpiral.AutomationGuard.dll', 'IdleSpiral.DesignCountGuard.dll', 'IdleSpiral.HousekeepingGuard2.dll', 'IdleSpiral.RenderGuard.dll', 'IdleSpiral.TheoryGuard.dll', 'IdleSpiral.AdaptiveGuard.dll', 'IdleSpiral.AuthGuard.dll', 'IdleSpiral.CalendarGuard.dll', 'IdleSpiral.Autoload.dll')
    $backups = @{}
    $written = [Collections.Generic.List[string]]::new()
    foreach ($name in $changes) {
        $sourcePath = Join-Path $PSScriptRoot "payload\$name"
        if (-not (Test-Path -LiteralPath $sourcePath)) { throw "Missing upgrade payload: $name" }
        $relativePath = "IdleSpiralSpeedPatch\$name"
        $target = Join-Path $GameDirectory $relativePath
        if (Test-Path -LiteralPath $target) {
            if (-not @($manifest.files | Where-Object path -eq $relativePath).Count) { throw "Unowned upgrade target: $target" }
            $backups[$name] = [IO.File]::ReadAllBytes($target)
        } else { $backups[$name] = $null }
    }
    foreach ($name in 'IdleSpiral.ClockIsolation.dll', 'IdleSpiral.MacroStepGuard.dll', 'IdleSpiral.AutomationGuard.dll', 'IdleSpiral.DesignCountGuard.dll', 'IdleSpiral.HousekeepingGuard2.dll', 'IdleSpiral.CalendarGuard.dll', 'IdleSpiral.AuthGuard.dll', 'IdleSpiral.AdaptiveGuard.dll', 'IdleSpiral.RenderGuard.dll', 'IdleSpiral.TheoryGuard.dll') {
        $guardTarget = Join-Path $payload $name
        if ((Test-Path -LiteralPath $guardTarget) -and
            (Get-FileHash -LiteralPath $guardTarget).Hash -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "payload\$name")).Hash -and
            @(Get-Process -Name IdleSpiral -ErrorAction SilentlyContinue | Where-Object Path -EQ (Join-Path $GameDirectory 'IdleSpiral.exe')).Count -gt 0) {
            throw "Close the game before replacing an existing guard version: $name"
        }
    }
    foreach ($name in $expected.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $GameDirectory "IdleSpiral_Data\Managed\$name")).Hash -ne $expected[$name]) {
            throw "Game assembly changed: $name"
        }
    }
    $oldManifest = [IO.File]::ReadAllBytes($manifestPath)
    $manifest.gameAssemblies = [pscustomobject]$expected
    try {
        foreach ($name in $changes) {
            $relativePath = "IdleSpiralSpeedPatch\$name"
            $sourcePath = Join-Path $PSScriptRoot "payload\$name"
            $target = Join-Path $GameDirectory $relativePath
            $hash = (Get-FileHash -LiteralPath $sourcePath).Hash
            if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne $hash) {
                $temporary = $target + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
                try {
                    [IO.File]::WriteAllBytes($temporary, [IO.File]::ReadAllBytes($sourcePath))
                    if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary, $target, [NullString]::Value) }
                    else { [IO.File]::Move($temporary, $target) }
                }
                finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
                $written.Add($name)
                if ((Get-FileHash -LiteralPath $target).Hash -ne $hash) { throw "Upgrade copy failed: $name" }
            }
            $manifest.files = @($manifest.files | Where-Object path -ne $relativePath) + @([pscustomobject]@{path=$relativePath; sha256=$hash})
        }
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 -LiteralPath $manifestPath
        Assert-OwnedInstallation $manifest
    }
    catch {
        foreach ($name in $written) {
            $target = Join-Path $payload $name
            if ($null -eq $backups[$name]) { Remove-Item -LiteralPath $target }
            else { [IO.File]::WriteAllBytes($target, $backups[$name]) }
        }
        [IO.File]::WriteAllBytes($manifestPath, $oldManifest)
        throw
    }
    Write-Output 'Clock isolation, macro-step, automation, design-count, background, rendering, theory, simulation, authentication, calendar and autosave guards installed for the next launch. Speed settings were preserved.'
    return
}

if ($Action -eq 'Remove') {
    $gameExecutable = Join-Path $GameDirectory 'IdleSpiral.exe'
    if (@(Get-Process -Name IdleSpiral -ErrorAction SilentlyContinue | Where-Object Path -EQ $gameExecutable).Count -gt 0) { throw 'Close Idle Spiral before removing autoload.' }
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Owned installation manifest not found; nothing was removed.' }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    Assert-OwnedInstallation $manifest
    # Disable the next launch before removing its payload.
    Remove-Item -LiteralPath $config
    Remove-Item -LiteralPath $proxy
    foreach ($file in $manifest.files) {
        if ($file.path -like 'IdleSpiralSpeedPatch\*') { Remove-Item -LiteralPath (Join-Path $GameDirectory $file.path) }
    }
    Remove-Item -LiteralPath $manifestPath
    # Keep diagnostic logs and any unrecognized files for review.
    if (@(Get-ChildItem -LiteralPath $payload -Force).Count -eq 0) { Remove-Item -LiteralPath $payload }
    Write-Output 'Autoload removed. Original game files and saves were not changed.'
    return
}

if (Test-Path -LiteralPath $manifestPath) {
    Assert-OwnedInstallation (Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json)
    Write-Output 'Autoload is already installed. Use Status to inspect it.'
    return
}
foreach ($path in $proxy, $config, $payload) {
    if (Test-Path -LiteralPath $path) { throw "Existing file/directory will not be overwritten: $path" }
}
foreach ($name in $expected.Keys) {
    $path = Join-Path $GameDirectory "IdleSpiral_Data\Managed\$name"
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected[$name]) { throw "Unsupported game assembly: $name" }
}
$OpenSpeedyDll = (Resolve-Path -LiteralPath $OpenSpeedyDll).Path
$speedHash = (Get-FileHash -LiteralPath $OpenSpeedyDll -Algorithm SHA256).Hash
$copied = [Collections.Generic.List[string]]::new()
$manifestFiles = [Collections.Generic.List[object]]::new()
try {
    New-Item -ItemType Directory -Path $payload | Out-Null
    $source = Join-Path $PSScriptRoot 'payload'
    foreach ($file in Get-ChildItem -LiteralPath $source -File) {
        $target = Join-Path $payload $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $target
        $copied.Add($target)
    }
    $settings = Join-Path $payload 'openspeedy-loader.txt'
    [IO.File]::WriteAllLines($settings, [string[]]@($OpenSpeedyDll, $speedHash))
    $copied.Add($settings)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'winhttp.dll') -Destination $proxy
    $copied.Add($proxy)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'doorstop_config.ini') -Destination $config
    $copied.Add($config)
    foreach ($path in $copied) {
        $manifestFiles.Add(@{ path = $path.Substring($GameDirectory.Length + 1); sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash })
    }
    @{ owner = 'IdleSpiral-SpeedPatch-SteamAutoload-v1'; files = $manifestFiles; gameAssemblies = $expected } |
        ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 -LiteralPath $manifestPath
    Write-Output "Installed Steam autoload in $GameDirectory. Effective on the next normal game launch."
}
catch {
    foreach ($path in $copied) { Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath }
    if ((Test-Path -LiteralPath $payload) -and @(Get-ChildItem -LiteralPath $payload -Force).Count -eq 0) { Remove-Item -LiteralPath $payload }
    throw
}
