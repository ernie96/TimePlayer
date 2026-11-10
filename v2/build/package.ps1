<#
.SYNOPSIS
    Builds the TimePlayer 2 release for Windows: portable zip, installer, NVDA add-on, browser extension zip,
    signatures and checksums, with the same asset names as 1.x so 1.x's updater installs 2.0. Output goes to
    v2\release\. Run from the v2 folder after `. .\env.ps1`.

.PARAMETER ToolsFrom
    Folder holding the mpv and yt-dlp to bundle (mpv\mpv.exe and yt-dlp.exe). Defaults to the copies
    TimePlayer downloaded into %LOCALAPPDATA%\TimePlayer\bin.

    Signing uses the UPDATE_SIGNING_KEY environment variable, or the key file in UPDATE_SIGNING_KEY_FILE, or
    %APPDATA%\TimePlayer-release\update-signing.key, through 1.x's signer (build\Signer at the repository root).
#>
param(
    [string]$ToolsFrom = (Join-Path $env:LOCALAPPDATA 'TimePlayer\bin'),
    [string]$TargetDir = $(if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'target' })
)

$ErrorActionPreference = 'Stop'
$v2 = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $v2
$release = Join-Path $v2 'release'
$staging = Join-Path $release 'staging\TimePlayer'

$version = (Select-String -Path (Join-Path $v2 'Cargo.toml') -Pattern '^version\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
Write-Host "Packaging TimePlayer $version"
if (Test-Path $release) { Get-ChildItem $release -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

# 1. Tests, then the release build.
cargo test -p timeplayer-core -p timeplayer --quiet
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not packaging.' }
cargo build -p timeplayer --release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item (Join-Path $TargetDir 'release\TimePlayer.exe') $staging

# 2. The Visual C++ runtime, beside the exe (Microsoft's app-local deployment), for PCs that don't have it.
$vsRoots = @('C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio')
$crt = Get-ChildItem $vsRoots -Recurse -Directory -Filter 'Microsoft.VC14*.CRT' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\Redist\\MSVC\\[^\\]+\\x64\\' -and $_.FullName -notmatch 'onecore|debug' } |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $crt) { throw 'The Visual C++ runtime (Redist\MSVC\...\x64\Microsoft.VC143.CRT) was not found.' }
foreach ($dll in 'msvcp140.dll', 'msvcp140_atomic_wait.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') {
    Copy-Item (Join-Path $crt.FullName $dll) $staging
}

# 3. Bundled tools: mpv and yt-dlp. Nothing personal is copied.
$mpvSource = Join-Path $ToolsFrom 'mpv'
$ytDlpSource = Join-Path $ToolsFrom 'yt-dlp.exe'
if (-not (Test-Path (Join-Path $mpvSource 'mpv.exe'))) { throw "mpv not found in $mpvSource" }
if (-not (Test-Path $ytDlpSource)) { throw "yt-dlp not found at $ytDlpSource" }
$binTarget = Join-Path $staging 'bin'
New-Item -ItemType Directory -Force (Join-Path $binTarget 'mpv') | Out-Null
foreach ($item in 'mpv.exe', 'd3dcompiler_43.dll', 'mpv') {
    $src = Join-Path $mpvSource $item
    if (Test-Path $src) { Copy-Item $src (Join-Path $binTarget 'mpv') -Recurse -Force }
}
Get-ChildItem (Join-Path $binTarget 'mpv\mpv') -Exclude 'fonts.conf' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
Copy-Item $ytDlpSource $binTarget
Set-Content (Join-Path $binTarget 'yt-dlp.conf') '--extractor-args "youtube:player_client=mweb,default"'

# 4. Documents and the portable marker.
Copy-Item (Join-Path $repo 'build\README.txt') $staging
Copy-Item (Join-Path $repo 'docs\THIRD_PARTY_NOTICES.md') $staging
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $staging 'LICENSE.txt')
Set-Content (Join-Path $staging 'portable.txt') 'This file keeps TimePlayer portable: settings, library and logs stay in this folder. Delete it to use %LOCALAPPDATA%\TimePlayer instead.'

# 5. NVDA add-on, from the copy TimePlayer 2 installs.
$addonSource = Join-Path $v2 'crates\timeplayer-core\nvda-addon'
$addonFile = Join-Path $staging 'TimePlayerBraille.nvda-addon'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($addonFile, 'Create')
try {
    foreach ($file in Get-ChildItem $addonSource -Recurse -File | Where-Object { $_.FullName -notmatch '__pycache__' }) {
        $entry = $file.FullName.Substring($addonSource.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry) | Out-Null
    }
} finally { $zip.Dispose() }

# 6. Browser extension: in the app folder (Settings opens it) and as its own zip.
$extensionSource = Join-Path $repo 'browser-extension'
Copy-Item $extensionSource (Join-Path $staging 'browser-extension') -Recurse -Force
Compress-Archive -Path (Join-Path $extensionSource '*') -DestinationPath (Join-Path $release 'TimePlayer-browser-extension.zip') -CompressionLevel Optimal -Force

# 7. Portable zip and installer, with 1.x's names (the updater looks for exactly these).
Compress-Archive -Path $staging -DestinationPath (Join-Path $release 'TimePlayer.zip') -CompressionLevel Optimal
$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found.' }
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$staging" "/DOutputDir=$release" (Join-Path $repo 'build\TimePlayer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

# 8. Sign the update downloads with 1.x's signer, and check them against the key built into 2.0.
$keyFile = if ($env:UPDATE_SIGNING_KEY_FILE) { $env:UPDATE_SIGNING_KEY_FILE } else { Join-Path $env:APPDATA 'TimePlayer-release\update-signing.key' }
if (-not $env:UPDATE_SIGNING_KEY -and (Test-Path $keyFile)) { $env:UPDATE_SIGNING_KEY_FILE = $keyFile }
if (-not $env:UPDATE_SIGNING_KEY -and -not $env:UPDATE_SIGNING_KEY_FILE) { throw 'No signing key found; the updater would refuse an unsigned release.' }
$updateFiles = @('TimePlayer.zip', 'TimePlayer_setup.exe' | ForEach-Object { Join-Path $release $_ })
$signerOut = Join-Path $release 'signer'
dotnet build (Join-Path $repo 'build\Signer\TimePlayer.Signer.csproj') -c Release -o $signerOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Building the signer failed.' }
$signer = Join-Path $signerOut 'TimePlayer.Signer.exe'
& $signer sign @updateFiles
if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
$publicKey = (Select-String -Path (Join-Path $v2 'crates\timeplayer-core\src\updates.rs') -Pattern 'PUBLIC_KEY[^"]*"([^"]+)"').Matches[0].Groups[1].Value
if (-not $publicKey) { throw 'The public key was not found in updates.rs.' }
foreach ($file in $updateFiles) {
    & $signer verify $publicKey $file
    if ($LASTEXITCODE -ne 0) { throw "$file does not verify against 2.0's public key." }
}
Remove-Item $signerOut -Recurse -Force

# 9. Checksums.
Copy-Item $addonFile $release
$sums = Get-ChildItem $release -File | Where-Object { $_.Extension -in '.zip', '.exe', '.nvda-addon' } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content (Join-Path $release 'SHA256SUMS.txt') $sums
Get-ChildItem $release -File | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
