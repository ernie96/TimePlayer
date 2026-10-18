<#
.SYNOPSIS
    Builds the TimePlayer release: portable zip, NVDA add-on file, checksums and (when
    Inno Setup is installed) the installer. Output goes to release\.

.PARAMETER ToolsFrom
    Folder holding the mpv and yt-dlp to bundle (bin\mpv\mpv.exe and bin\yt-dlp.exe layout).
    Defaults to the copies TimePlayer downloaded into %LOCALAPPDATA%\TimePlayer\bin.

.EXAMPLE
    pwsh build\package.ps1
#>
param(
    [string]$ToolsFrom = (Join-Path $env:LOCALAPPDATA 'TimePlayer\bin')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$release = Join-Path $root 'release'
$staging = Join-Path $release 'staging\TimePlayer'

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
Write-Host "Packaging TimePlayer $version"

# Fresh staging folder; never the developer's working copy in dist\ (it holds personal settings and library).
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

# 1. Tests must pass.
dotnet test (Join-Path $root 'TimePlayer.sln') --filter 'FullyQualifiedName!~LiveDirectoryIntegration' --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not packaging.' }

# 2. Single-file, self-contained app.
dotnet publish (Join-Path $root 'src\TimePlayer.Wpf\TimePlayer.Wpf.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Get-ChildItem $staging -Filter *.pdb | Remove-Item

# 3. Bundled tools: mpv (video engine) and yt-dlp (stream extraction). Nothing personal is copied.
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
# The mpv\mpv sub-folder only needs its font config; it also holds stray yt-dlp copies.
Get-ChildItem (Join-Path $binTarget 'mpv\mpv') -Exclude 'fonts.conf' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
Copy-Item $ytDlpSource $binTarget
Set-Content (Join-Path $binTarget 'yt-dlp.conf') '--extractor-args "youtube:player_client=mweb,default"'

# 4. Documents and the portable marker.
Copy-Item (Join-Path $PSScriptRoot 'README.txt') $staging
Copy-Item (Join-Path $root 'docs\THIRD_PARTY_NOTICES.md') $staging
Set-Content (Join-Path $staging 'portable.txt') 'This file keeps TimePlayer portable: settings, library and logs stay in this folder. Delete it to use %LOCALAPPDATA%\TimePlayer instead.'

# 5. NVDA add-on as a standalone .nvda-addon (a zip of the add-on folder).
$addonSource = Join-Path $root 'src\TimePlayer.Core\Accessibility\NvdaAddon'
$addonVersion = (Select-String -Path (Join-Path $addonSource 'manifest.ini') -Pattern '^version\s*=\s*(.+)$').Matches[0].Groups[1].Value.Trim()
$addonFile = Join-Path $staging 'TimePlayerBraille.nvda-addon'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($addonFile, 'Create')
try {
    foreach ($file in Get-ChildItem $addonSource -Recurse -File | Where-Object { $_.FullName -notmatch '__pycache__' }) {
        $entry = $file.FullName.Substring($addonSource.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry) | Out-Null
    }
} finally { $zip.Dispose() }
Write-Host "NVDA add-on $addonVersion packaged."

# 6. Portable zip.
$zipPath = Join-Path $release "TimePlayer-$version-win-x64-portable.zip"
Compress-Archive -Path $staging -DestinationPath $zipPath -CompressionLevel Optimal

# 7. Installer, when Inno Setup's compiler is available.
$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$staging" "/DOutputDir=$release" (Join-Path $PSScriptRoot 'TimePlayer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
} else {
    Write-Warning 'Inno Setup 6 (ISCC.exe) not found: skipped the installer. Install it with: winget install JRSoftware.InnoSetup'
}

# 8. Checksums for everything published.
$sums = Get-ChildItem $release -File | Where-Object { $_.Extension -in '.zip', '.exe' } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content (Join-Path $release 'SHA256SUMS.txt') $sums
Copy-Item $addonFile $release

Write-Host ''
Get-ChildItem $release -File | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
