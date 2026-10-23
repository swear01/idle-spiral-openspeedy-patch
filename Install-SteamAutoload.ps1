param(
    [ValidateSet('Install', 'Remove', 'Status')][string]$Action = 'Status',
    [string]$GameDirectory = 'E:\SteamLibrary\steamapps\common\Idle Spiral',
    [string]$OpenSpeedyDll = 'C:\Program Files\OpenSpeedy\speedpatch64.dll'
)
$ErrorActionPreference = 'Stop'
$GameDirectory = (Resolve-Path -LiteralPath $GameDirectory).Path
$payload = Join-Path $GameDirectory 'IdleSpiralSpeedPatch'
$manifestPath = Join-Path $payload 'install-manifest.json'
$proxy = Join-Path $GameDirectory 'winhttp.dll'
$config = Join-Path $GameDirectory 'doorstop_config.ini'

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
    foreach ($name in 'autoload-status.txt', 'autoload-error.txt', 'patch-status.txt', 'patch-error.txt') {
        $path = Join-Path $payload $name
        if (Test-Path -LiteralPath $path) { Write-Output "${name}:"; Get-Content -LiteralPath $path }
    }
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
$expected = @{
    'IdleSpiralDomain.dll' = 'caec145b335c11fdd4cb86f5ce72f057ccec83297507ad0d1ed838aceb29f37b'
    'Assembly-CSharp.dll' = 'a67ee1d53fc2247d738ff2166f1a5e217bcb3fdebc3dfebf0a21393b8e5fbfbb'
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
