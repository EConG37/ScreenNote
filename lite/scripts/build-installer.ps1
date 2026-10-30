[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [string]$CompilerPath = '',
    [string]$RuntimeVersion = '10.0.14',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $projectRoot
$projectFile = Join-Path $projectRoot 'src/ScreenNoteLite/ScreenNoteLite.csproj'
[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$version = [string]@($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
$buildRoot = Join-Path $projectRoot ('.build/installer/' + $version)
$payload = Join-Path $buildRoot 'payload'
$repoToolRoot = Join-Path $repoRoot '.build/installer-tools'
$toolRoot = if (Test-Path -LiteralPath (Join-Path $repoToolRoot 'InnoSetup/ISCC.exe') -PathType Leaf) { $repoToolRoot } else { Join-Path $projectRoot '.build/installer-tools' }
$artifactRoot = if (Test-Path -LiteralPath (Join-Path $repoRoot 'src/ScreenNote/ScreenNote.csproj') -PathType Leaf) { Join-Path $repoRoot 'output/lite' } else { Join-Path $projectRoot 'output' }
$outputRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { $artifactRoot }
if ($outputRoot.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $outputRoot -eq $payload) {
    throw 'The installer output must be outside its payload directory.'
}
New-Item -ItemType Directory -Force -Path $buildRoot, $toolRoot, $outputRoot | Out-Null

function Get-VerifiedBuildTool([string]$Url, [string]$Destination, [string]$Sha256) {
    if ((Test-Path -LiteralPath $Destination -PathType Leaf) -and (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Sha256) { return }
    $download = $Destination + '.download'
    & curl.exe --ssl-revoke-best-effort --http1.1 --fail --location --retry 2 --connect-timeout 20 --max-time 600 --header 'Accept: application/octet-stream' $Url --output $download
    if ($LASTEXITCODE -ne 0) { throw ('Build tool download failed: ' + $Url) }
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $Sha256) { throw ('Build tool checksum mismatch: ' + $Url) }
    Move-Item -LiteralPath $download -Destination $Destination -Force
}

$chineseMessages = Join-Path $toolRoot 'ChineseSimplified.isl'
if (-not (Test-Path -LiteralPath $chineseMessages -PathType Leaf)) {
    Get-VerifiedBuildTool 'https://raw.githubusercontent.com/jrsoftware/issrc/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl' $chineseMessages '7D544B9BB1D142CFA11F2E5D3CC8ABE2E55F8E066C5124E3772675AA236E1278'
}
if (-not $CompilerPath) {
    $compilerHome = Join-Path $toolRoot 'InnoSetup'
    $CompilerPath = Join-Path $compilerHome 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        $bootstrap = Join-Path $toolRoot 'innosetup-6.7.3.exe'
        Get-VerifiedBuildTool 'https://api.github.com/repos/jrsoftware/issrc/releases/assets/430321419' $bootstrap '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/PORTABLE=1', '/NOICONS', ('/DIR="' + $compilerHome + '"'), ('/LOG="' + (Join-Path $toolRoot 'compiler-bootstrap.log') + '"'))
        $bootstrapProcess = Start-Process -FilePath $bootstrap -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
        if ($bootstrapProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $CompilerPath)) { throw 'Portable Inno Setup compiler preparation failed.' }
    }
}
$CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Inno Setup compiler not found.' }

$sharedPackages = Join-Path $repoRoot '.build/packages'
$env:NUGET_PACKAGES = if (Test-Path -LiteralPath $sharedPackages -PathType Container) { $sharedPackages } else { Join-Path $projectRoot '.build/packages' }

if (-not $SkipPublish) {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build/dotnet-home'
    $env:APPDATA = Join-Path $projectRoot '.build/appdata'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
    & dotnet publish $projectFile -c Release -r win-x64 --self-contained true --source https://api.nuget.org/v3/index.json ('-p:RuntimeFrameworkVersion=' + $RuntimeVersion) -p:DebugType=None -p:DebugSymbols=false -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained Windows publish failed.' }
}

# Drop localized framework resources other than Chinese; the lite edition ships no Python runtime.
$trimmed = New-Object System.Collections.Generic.List[string]
$trimmedBytes = [long]0
function Remove-PayloadItem([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    $bytes = [long]0
    if ($item.PSIsContainer) {
        $sum = (@(Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction SilentlyContinue) | Measure-Object -Property Length -Sum).Sum
        if ($sum) { $bytes = [long]$sum }
    } else {
        $bytes = [long]$item.Length
    }
    $relative = $item.FullName.Substring($payload.Length + 1).Replace('\', '/')
    $script:trimmed.Add(('{0}  {1}' -f $relative, $bytes))
    $script:trimmedBytes += $bytes
    Remove-Item -LiteralPath $Path -Recurse -Force
}

$keptCultures = @('zh-Hans', 'zh-Hant')
foreach ($cultureDir in @(Get-ChildItem -LiteralPath $payload -Directory -Force | Where-Object { $_.Name -match '^[a-z]{2,3}(-[A-Za-z]{2,4})?$' -and $keptCultures -notcontains $_.Name })) {
    if (@(Get-ChildItem -LiteralPath $cultureDir.FullName -File -Force | Where-Object { $_.Name -notlike '*.resources.dll' }).Count -ne 0) { continue }
    Remove-PayloadItem $cultureDir.FullName
}
if ($trimmed.Count -gt 0) {
    $trimmed | Set-Content -LiteralPath (Join-Path $buildRoot 'payload-trim.txt') -Encoding UTF8
}

foreach ($file in @('ScreenNoteLite.exe', 'ScreenNoteLite.dll', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) { throw ('Missing installer payload: ' + $file) }
}
$runtime = Get-Content -LiteralPath (Join-Path $payload 'ScreenNoteLite.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.frameworks -or $runtime.runtimeOptions.framework -or $runtime.runtimeOptions.includedFrameworks.Count -ne 2 -or @($runtime.runtimeOptions.includedFrameworks | Where-Object version -ne $RuntimeVersion).Count -ne 0) {
    throw 'The payload does not contain the requested desktop runtime.'
}
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'ScreenNoteLite.exe')).ProductVersion
if ($productVersion -ne $version -and -not $productVersion.StartsWith($version + '+', [StringComparison]::Ordinal)) { throw 'Installer and app versions differ.' }

$assetOutput = Join-Path $payload 'assets'
$docsOutput = Join-Path $payload 'docs'
New-Item -ItemType Directory -Force -Path $assetOutput, $docsOutput | Out-Null
foreach ($name in 'app.ico', 'app.png', 'app.svg') {
    $assetPath = Join-Path $projectRoot ('src/ScreenNoteLite/assets/' + $name)
    if (Test-Path -LiteralPath $assetPath -PathType Leaf) { Copy-Item -LiteralPath $assetPath -Destination $assetOutput -Force }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $payload -Force
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -File -Filter '*.md' | Copy-Item -Destination $docsOutput -Force
$licenseOutput = Join-Path $docsOutput 'runtime-licenses'
foreach ($pack in 'microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64') {
    $packRoot = Join-Path $env:NUGET_PACKAGES ($pack + '/' + $RuntimeVersion)
    $packLicenseOutput = Join-Path $licenseOutput $pack
    New-Item -ItemType Directory -Force -Path $packLicenseOutput | Out-Null
    foreach ($name in 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') {
        $file = Join-Path $packRoot $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { Copy-Item -LiteralPath $file -Destination $packLicenseOutput -Force }
    }
}

$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Where-Object { $_.Extension -ne '.pdb' })
$manifest = @($files | ForEach-Object {
    $relative = $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
    if ($relative -match '^(data|history|recordings)/|(^|/)(settings\.json|browser-pairing\.json|\.env(\..*)?)$') { throw ('Private content must not be packaged: ' + $relative) }
    [ordered]@{ path = $relative; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $buildRoot 'payload-manifest.json') -Encoding UTF8

$compileLog = Join-Path $buildRoot 'compiler.log'
& $CompilerPath ('/DAppVersion=' + $version) ('/DPayloadDir=' + $payload) ('/DChineseMessages=' + $chineseMessages) ('/O' + $outputRoot) (Join-Path $projectRoot 'installer/ScreenNoteLite.iss') *> $compileLog
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $compileLog -Tail 25; throw 'Windows installer compilation failed.' }
$installer = Join-Path $outputRoot ('ScreenNoteLite-' + $version + '-Setup-x64.exe')
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'The installer file was not produced.' }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
[IO.File]::WriteAllText($installer + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($installer) + "`n", [Text.UTF8Encoding]::new($false))
$report = [ordered]@{ status = 'built'; version = $version; architecture = 'win-x64'; runtime = $RuntimeVersion; selfContained = $true; file = $installer; bytes = (Get-Item -LiteralPath $installer).Length; sha256 = $hash; payloadFiles = $files.Count; payloadBytes = ($files | Measure-Object Length -Sum).Sum; compiler = [Diagnostics.FileVersionInfo]::GetVersionInfo($CompilerPath).ProductVersion }
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $buildRoot 'build-report.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 4