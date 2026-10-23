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
$expected = @{
    'IdleSpiralDomain.dll' = 'caec145b335c11fdd4cb86f5ce72f057ccec83297507ad0d1ed838aceb29f37b'
    'Assembly-CSharp.dll' = 'a67ee1d53fc2247d738ff2166f1a5e217bcb3fdebc3dfebf0a21393b8e5fbfbb'
    'IdleLibrary.dll' = '9e665da9e2b44503d98db50557dfefb5cd43c892ab47b8a7b8995535b3784a3e'
    'IdleSpiral.CloudSave.dll' = '1b4c9c790e662ed09a6255c56c01a841865c2569882622ed9142db6b40a0970f'
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
    foreach ($name in 'autoload-status.txt', 'autoload-error.txt', 'patch-status.txt', 'patch-error.txt', 'clock-status.txt', 'clock-error.txt') {
        $path = Join-Path $payload $name
        if (Test-Path -LiteralPath $path) { Write-Output "${name}:"; Get-Content -LiteralPath $path }
    }
    return
}

if ($Action -eq 'Upgrade') {
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Install autoload before upgrading.' }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    Assert-OwnedInstallation $manifest
    $changes = @('IdleSpiral.CalendarGuard.dll', 'IdleSpiral.Autoload.dll')
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
    $clockTarget = Join-Path $payload 'IdleSpiral.CalendarGuard.dll'
    if ((Test-Path -LiteralPath $clockTarget) -and
        (Get-FileHash -LiteralPath $clockTarget).Hash -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'payload\IdleSpiral.CalendarGuard.dll')).Hash -and
        @(Get-Process -Name IdleSpiral -ErrorAction SilentlyContinue | Where-Object Path -EQ (Join-Path $GameDirectory 'IdleSpiral.exe')).Count -gt 0) {
        throw 'Close the game before replacing an existing CalendarGuard version.'
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
    Write-Output 'Calendar and real-time autosave guard installed for the next launch. Existing simulation patch and speed settings were preserved.'
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
