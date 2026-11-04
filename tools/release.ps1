# Builds Together Separate Economy locally and prepares release artifacts in dist\.
# Windows PowerShell 5.1 compatible. Does NOT publish anything.
#   usage:  powershell -ExecutionPolicy Bypass -File tools\release.ps1
# The build uses GameDir.props.user (or -p:GameDir via the GAMEDIR environment variable if you set it).

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host 'Building (Release)...'
if ($env:GAMEDIR) { & dotnet build -c Release "-p:GameDir=$($env:GAMEDIR)" } else { & dotnet build -c Release }
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

$dll = Join-Path $root 'bin\Release\net472\TogetherSeparateEconomy.dll'
if (-not (Test-Path $dll)) { throw "Built DLL not found: $dll" }

# Full version (e.g. 1.0.0+20261104123456) comes from AssemblyInformationalVersion.
$full = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
if (-not $full) { throw 'Could not read the version from the DLL' }
$base = $full.Split('+')[0]
Write-Host "Version: $full"

$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

$zipName = "TogetherSeparateEconomy-$full.zip"
$zipPath = Join-Path $dist $zipName

# Zip entries are written with forward slashes (Compress-Archive in 5.1 uses backslashes).
$entries = @(
    @{ Src = $dll;                                   Name = 'Mods/TogetherSeparateEconomy.dll' },
    @{ Src = (Join-Path $root 'tools\StartCMS21.bat'); Name = 'tools/StartCMS21.bat' },
    @{ Src = (Join-Path $root 'README.md');          Name = 'README.md' },
    @{ Src = (Join-Path $root 'LICENSE');            Name = 'LICENSE' },
    @{ Src = (Join-Path $root 'CHANGELOG.md');       Name = 'CHANGELOG.md' }
)
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    foreach ($e in $entries) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $e.Src, $e.Name, 'Optimal')
    }
} finally { $zip.Dispose() }

# Raw DLL as a separate release asset (the release guard workflow expects it by name).
$dllOut = Join-Path $dist 'TogetherSeparateEconomy.dll'
Copy-Item $dll $dllOut

# SHA256SUMS.txt
$lines = @()
foreach ($f in @($zipPath, $dllOut)) {
    $h = (Get-FileHash -Algorithm SHA256 -Path $f).Hash.ToLowerInvariant()
    $lines += ($h + '  ' + (Split-Path -Leaf $f))
}
$sumsPath = Join-Path $dist 'SHA256SUMS.txt'
[System.IO.File]::WriteAllText($sumsPath, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

# Release notes: the matching section of CHANGELOG.md.
$cl = Get-Content (Join-Path $root 'CHANGELOG.md') -Encoding UTF8
$notes = @()
$in = $false
foreach ($l in $cl) {
    if ($l -match '^##\s+(.+)$') {
        if ($in) { break }
        if ($Matches[1].Trim() -eq $base) { $in = $true; continue }
    } elseif ($in) { $notes += $l }
}
if ($notes.Count -eq 0) { $notes = @("Release $base") }
$notesPath = Join-Path $dist 'RELEASE_NOTES.md'
[System.IO.File]::WriteAllText($notesPath, (($notes -join "`n").Trim() + "`n"), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host "Artifacts in $dist :"
Get-ChildItem $dist | ForEach-Object { Write-Host ('  ' + $_.Name) }
Write-Host ''
Write-Host 'Review the artifacts, then publish a DRAFT release yourself with (NOT run by this script):'
Write-Host ''
Write-Host ('gh release create v{0} --draft --title "Together Separate Economy {0}" --notes-file "dist\RELEASE_NOTES.md" "dist\{1}" "dist\TogetherSeparateEconomy.dll" "dist\SHA256SUMS.txt"' -f $base, $zipName)
