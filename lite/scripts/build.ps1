[CmdletBinding()]
param(
    [switch]$Test,
    [string]$OutputDirectory = '',
    [switch]$Package,
    [string]$PackagePath = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $projectRoot
$buildHome = Join-Path $projectRoot '.build'
$env:DOTNET_CLI_HOME = Join-Path $buildHome 'dotnet-home'
$env:APPDATA = Join-Path $buildHome 'appdata'
$sharedPackages = Join-Path $repoRoot '.build/packages'
$env:NUGET_PACKAGES = if (Test-Path -LiteralPath $sharedPackages -PathType Container) { $sharedPackages } else { Join-Path $buildHome 'packages' }
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:DOTNET_CLI_HOME | Out-Null

$projectFile = Join-Path $projectRoot 'src/ScreenNoteLite/ScreenNoteLite.csproj'
$assetsSource = Join-Path $projectRoot 'src/ScreenNoteLite/assets'
foreach ($assetName in 'app.ico', 'app.png') {
    if (-not (Test-Path -LiteralPath (Join-Path $assetsSource $assetName) -PathType Leaf)) { throw ('Missing application icon: ' + $assetName) }
}

dotnet restore $projectFile --configfile (Join-Path $projectRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
dotnet build $projectFile -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$artifactRoot = if (Test-Path -LiteralPath (Join-Path $repoRoot 'src/ScreenNote/ScreenNote.csproj') -PathType Leaf) { Join-Path $repoRoot 'output/lite' } else { Join-Path $projectRoot 'output' }
$publishRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $artifactRoot 'ScreenNoteLite' }
dotnet publish $projectFile -c Release --no-restore --self-contained false -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'create-desktop-shortcut.ps1') -Destination $publishRoot -Force
$assetsOutput = Join-Path $publishRoot 'assets'
New-Item -ItemType Directory -Force -Path $assetsOutput | Out-Null
foreach ($assetName in 'app.ico', 'app.png', 'app.svg') {
    $assetPath = Join-Path $assetsSource $assetName
    if (Test-Path -LiteralPath $assetPath -PathType Leaf) { Copy-Item -LiteralPath $assetPath -Destination $assetsOutput -Force }
}
$docsOutput = Join-Path $publishRoot 'docs'
New-Item -ItemType Directory -Force -Path $docsOutput | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -File -Filter '*.md' | Copy-Item -Destination $docsOutput -Force

if ($Test) {
    $testData = Join-Path $projectRoot 'output/self-test'
    $process = Start-Process -FilePath (Join-Path $publishRoot 'ScreenNoteLite.exe') -ArgumentList @('--self-test', '--data-dir', ('"' + $testData + '"')) -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw 'Desktop self-test failed; see output/self-test/failure.txt' }
    Get-Content -LiteralPath (Join-Path $testData 'self-test.json')
}

Write-Output ('Ready: ' + (Join-Path $publishRoot 'ScreenNoteLite.exe'))

if ($Package -or $PackagePath) {
    [xml]$projectXml = Get-Content -LiteralPath $projectFile -Raw
    $packageVersion = [string]@($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    $archivePath = if ($PackagePath) { [IO.Path]::GetFullPath($PackagePath) } else { Join-Path $artifactRoot ('ScreenNoteLite-' + $packageVersion + '-lite-x64.zip') }
    $archiveDirectory = Split-Path -Parent $archivePath
    $publishFullPath = [IO.Path]::GetFullPath($publishRoot).TrimEnd([char[]]'\/')
    if ($archivePath.StartsWith($publishFullPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'PackagePath must be outside the published application folder.'
    }
    New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
    $temporaryArchive = $archivePath + '.partial-' + [Guid]::NewGuid().ToString('N') + '.zip'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        $programFiles = @('ScreenNoteLite.exe', 'ScreenNoteLite.dll', 'ScreenNoteLite.deps.json', 'ScreenNoteLite.runtimeconfig.json', 'README.md', 'create-desktop-shortcut.ps1')
        $includedFiles = @($programFiles | ForEach-Object { Join-Path $publishRoot $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
        foreach ($assetName in 'app.ico', 'app.png', 'app.svg') {
            $assetPath = Join-Path $assetsOutput $assetName
            if (Test-Path -LiteralPath $assetPath -PathType Leaf) { $includedFiles += $assetPath }
        }
        $includedFiles += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -File -Filter '*.md' | ForEach-Object { Join-Path $docsOutput $_.Name })
        $archive = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $includedFiles) {
                $relative = $file.Substring($publishFullPath.Length).TrimStart([char[]]'\/').Replace('\', '/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $archive.Dispose() }
        Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
    } finally {
        if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath $temporaryArchive -Force }
    }
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    [IO.File]::WriteAllText($archivePath + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($archivePath) + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output ('Package: ' + $archivePath)
    Write-Output ('  size   ' + (Get-Item -LiteralPath $archivePath).Length + ' bytes')
    Write-Output ('  sha256 ' + $hash)
}