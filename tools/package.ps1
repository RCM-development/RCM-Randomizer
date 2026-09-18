# Build a release zip: clean Release builds of the randomizer, mix&match and the mod manager,
# laid out so the zip extracts straight into the game folder.
#
#   powershell -File tools\package.ps1 [-GameDir "E:\Games\Rogue Command"]
#
# Expects the RCM repositories as siblings (see INSTALL.md). Output: <dev folder>\dist\.
param(
    [string]$GameDir = "",
    [string]$Dotnet = ""
)
$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot -Parent          # RCM-Randomizer
$root = Split-Path $repo -Parent                  # dev folder with the sibling repos
if (-not $Dotnet) {
    $local = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    $Dotnet = if (Test-Path $local) { $local } else { "dotnet" }
}

$version = ([xml](Get-Content (Join-Path $repo "RCM_Randomizer.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in RCM_Randomizer.csproj" }
$codeVersion = Select-String -Path (Join-Path $repo "Randomizer.cs") -Pattern 'public const string Version = "([^"]+)"'
if (-not $codeVersion -or $codeVersion.Matches[0].Groups[1].Value -ne $version) {
    throw "Version mismatch: csproj says $version, Randomizer.cs says $($codeVersion.Matches[0].Groups[1].Value)"
}

$projects = @(
    (Join-Path $root "RCM-UnitsMixNMatch\RCM_UnitsMixNMatch.csproj"),
    (Join-Path $repo "RCM_Randomizer.csproj")   # also builds RCM-Manager (TestMod.dll) as a reference
)
$buildArgs = @("-c", "Release", "--no-incremental", "-v", "q", "-nologo")
if ($GameDir) { $buildArgs += "-p:GameDir=$GameDir" }
foreach ($p in $projects) {
    if (-not (Test-Path $p)) { throw "Missing $p - clone the RCM repositories as siblings" }
    & $Dotnet build $p @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $p" }
}

$name = "RCM-Randomizer-$version"
$stage = Join-Path $root "dist\$name"
$plugins = Join-Path $stage "BepInEx\plugins"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $plugins | Out-Null

$files = @(
    (Join-Path $root "RCM-Manager\bin\Release\TestMod.dll"),
    (Join-Path $root "RCM-Manager\res\rcmoverlay"),
    (Join-Path $root "RCM-UnitsMixNMatch\bin\Release\RCM_UnitsMixNMatch.dll"),
    (Join-Path $root "RCM-UnitsMixNMatch\res\MixNMatchUnits.txt"),
    (Join-Path $repo "bin\Release\RCM_Randomizer.dll")
)
foreach ($f in $files) {
    if (-not (Test-Path $f)) { throw "Build output missing: $f" }
    Copy-Item $f $plugins
}
Copy-Item (Join-Path $repo "CHANGELOG.md") $stage
Copy-Item (Join-Path $repo "INSTALL.md") $stage

$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Written entry by entry rather than with Compress-Archive: Windows PowerShell 5.1's version
# stores "BepInEx\plugins\x.dll" with backslashes, against the zip spec, and some extractors and
# mod managers then create a single file of that literal name in the game folder.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }

Write-Host "Packaged $zip"
Get-ChildItem $plugins | Select-Object Name, Length | Format-Table -AutoSize
Get-FileHash $zip -Algorithm SHA256 | Select-Object Hash
