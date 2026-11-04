$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root      = Split-Path -Parent $PSScriptRoot
$project   = Join-Path $root 'src\ScreenNote\ScreenNote.csproj'
$xml       = [xml](Get-Content -Raw $project)
$version   = [string]@($xml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $version) { throw "Cannot read Version from $project" }

$buildRoot = Join-Path $root ".build\installer\$version"
$payload   = Join-Path $buildRoot 'payload'
$outputDir = Join-Path $root 'output'
$zipPath   = Join-Path $outputDir "ScreenNote-$version-portable-x64.zip"
$rootName  = "ScreenNote-$version-portable"
$readmeSrc = Join-Path $root 'installer\portable-readme.txt'
# Chinese file name built from code points so this script stays pure ASCII (Windows PowerShell 5.1 reads ANSI).
$cn        = [string]([char]0x4F7F) + [char]0x7528 + [char]0x8BF4 + [char]0x660E

if (-not (Test-Path $payload)) {
    throw "Payload not found: $payload`nRun scripts\build-installer.ps1 first; it publishes and trims the payload."
}
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

$files = Get-ChildItem $payload -Recurse -File | Sort-Object FullName
$rawBytes = ($files | Measure-Object Length -Sum).Sum
Write-Output ("Payload: " + $files.Count + " files, " + [math]::Round($rawBytes / 1MB, 2) + " MB")

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
$n = 0
foreach ($f in $files) {
    $relative = $f.FullName.Substring($payload.Length + 1).Replace('\', '/')
    $entry = $zip.CreateEntry($rootName + '/' + $relative, [System.IO.Compression.CompressionLevel]::Optimal)
    $destination = $entry.Open()
    $source = [System.IO.File]::OpenRead($f.FullName)
    $source.CopyTo($destination)
    $source.Dispose()
    $destination.Dispose()
    $n++
    if ($n % 250 -eq 0) {
        Write-Output ("  packed " + $n + "/" + $files.Count + "  t=" + [int]$sw.Elapsed.TotalSeconds + "s")
    }
}

if (Test-Path $readmeSrc) {
    $readmeBytes = [System.IO.File]::ReadAllBytes($readmeSrc)
    $entry = $zip.CreateEntry($rootName + '/' + $cn + '.txt', [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    $stream.Write($readmeBytes, 0, $readmeBytes.Length)
    $stream.Dispose()
    Write-Output "  added portable readme"
} else {
    Write-Warning "installer\portable-readme.txt not found, skipping readme"
}
$zip.Dispose()

# Verify every entry against the payload by SHA-256.
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
$map = @{}
foreach ($e in $archive.Entries) { $map[$e.FullName] = $e }
$bad = 0
foreach ($f in $files) {
    $relative = $f.FullName.Substring($payload.Length + 1).Replace('\', '/')
    $key = $rootName + '/' + $relative
    if (-not $map.ContainsKey($key)) { $bad++; Write-Output ("MISSING: " + $relative); continue }
    $entry = $map[$key]
    $memory = New-Object System.IO.MemoryStream
    $stream = $entry.Open(); $stream.CopyTo($memory); $stream.Dispose()
    $actual = ([System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($memory.ToArray()))).Replace('-', '')
    if ($actual -ne (Get-FileHash $f.FullName -Algorithm SHA256).Hash) { $bad++; Write-Output ("HASH MISMATCH: " + $relative) }
}
$archive.Dispose()
if ($bad -gt 0) { throw "Portable archive verification failed: $bad problem(s)." }

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
$name = Split-Path $zipPath -Leaf
[System.IO.File]::WriteAllText(($zipPath + '.sha256'), ($hash.ToUpper() + '  ' + $name + [char]13 + [char]10), (New-Object System.Text.UTF8Encoding($false)))

Write-Output ("Portable: " + $name)
Write-Output ("  size  " + (Get-Item $zipPath).Length + " bytes (" + [math]::Round((Get-Item $zipPath).Length / 1MB, 2) + " MB)")
Write-Output ("  entries " + ($files.Count + $(if (Test-Path $readmeSrc) { 1 } else { 0 })) + " verified")
Write-Output ("  sha256 " + $hash)
Write-Output ("  time  " + [int]$sw.Elapsed.TotalSeconds + "s")