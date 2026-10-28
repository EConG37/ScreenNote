param([switch]$Test, [string]$OutputDirectory = '', [switch]$Package, [string]$PackagePath = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildHome = Join-Path $projectRoot '.build'
$env:DOTNET_CLI_HOME = Join-Path $buildHome 'dotnet-home'
$env:APPDATA = Join-Path $buildHome 'appdata'
$env:NUGET_PACKAGES = Join-Path $buildHome 'packages'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force -Path $env:APPDATA,$env:DOTNET_CLI_HOME | Out-Null
$projectFile = Join-Path $projectRoot 'src\ScreenNote\ScreenNote.csproj'
$assetsSource = Join-Path $projectRoot 'src\ScreenNote\assets'
foreach ($assetName in 'app.ico','app.png') {
    if (-not (Test-Path -LiteralPath (Join-Path $assetsSource $assetName) -PathType Leaf)) { throw ('Missing application icon: ' + $assetName) }
}
dotnet restore $projectFile --configfile (Join-Path $projectRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
dotnet build $projectFile -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$publishRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'output\ScreenNote' }
dotnet publish $projectFile -c Release --no-restore --self-contained false -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'create-desktop-shortcut.ps1') -Destination $publishRoot -Force
$assetsOutput = Join-Path $publishRoot 'assets'
New-Item -ItemType Directory -Force -Path $assetsOutput | Out-Null
foreach ($assetName in 'app.ico','app.png') { Copy-Item -LiteralPath (Join-Path $assetsSource $assetName) -Destination $assetsOutput -Force }
if (Test-Path -LiteralPath (Join-Path $assetsSource 'app.svg') -PathType Leaf) { Copy-Item -LiteralPath (Join-Path $assetsSource 'app.svg') -Destination $assetsOutput -Force }
$docsOutput = Join-Path $publishRoot 'docs'
New-Item -ItemType Directory -Force -Path $docsOutput | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -File -Filter '*.md' | Copy-Item -Destination $docsOutput -Force
$previewSource = Join-Path $projectRoot 'docs\previews'
if (Test-Path -LiteralPath $previewSource -PathType Container) {
    $previewOutput = Join-Path $docsOutput 'previews'
    New-Item -ItemType Directory -Force -Path $previewOutput | Out-Null
    Get-ChildItem -LiteralPath $previewSource -File -Filter '*-0??.png' | Copy-Item -Destination $previewOutput -Force
}
if ($Test) {
    $testData = Join-Path $projectRoot 'output\self-test'
    $process = Start-Process -FilePath (Join-Path $publishRoot 'ScreenNote.exe') -ArgumentList @('--self-test','--data-dir',('"' + $testData + '"')) -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw 'Desktop self-test failed; see output/self-test/failure.txt' }
    Get-Content -LiteralPath (Join-Path $testData 'self-test.json')
}
Write-Output ('Ready: ' + (Join-Path $publishRoot 'ScreenNote.exe'))
if ($Package -or $PackagePath) {
    [xml]$projectXml = Get-Content -LiteralPath $projectFile -Raw
    $packageVersion = [string]@($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    $archivePath = if ($PackagePath) { [IO.Path]::GetFullPath($PackagePath) } else { Join-Path $projectRoot ('output\ScreenNote-' + $packageVersion + '-windows.zip') }
    $archiveDirectory = Split-Path -Parent $archivePath
    $publishFullPath = [IO.Path]::GetFullPath($publishRoot).TrimEnd([char[]]'\/')
    if ($archivePath.StartsWith($publishFullPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'PackagePath must be outside the published application folder.'
    }
    New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
    $readme = @"
拾屏 $packageVersion · Windows 离线分发包

1. 解压整个文件夹后运行 ScreenNote.exe。
2. 本包沿用已有 .NET 10 Desktop Runtime，适用于 Windows 10/11。未安装运行时的电脑需先准备运行时安装包。
3. 创建桌面图标：在本文件夹运行 create-desktop-shortcut.ps1。图标指向当前位置，请先把文件夹放在准备长期保留的位置。
4. 默认 Alt+A 截图。在设置中点击快捷键框直接按键录入；默认优先使用拾屏，也支持 PrintScreen。可以选择截图资料文件夹。
5. 资料默认保存在软件文件夹内 data\history。卸载或删除软件文件夹会导致资料丢失，重要资料建议在设置中另选文件夹。
6. 发送给 AI 使用原生复制粘贴，无需浏览器扩展或配对。若网页需要登录，请登录后点击聊天框按 Ctrl+V。检查附件后自行发送。
7. 使用说明见 README.md，网页连接和兼容性限制见 docs/ai-bridge.md。

本包包含程序、中文增强 OCR、中英离线翻译模型、图标资源和说明。翻译直接在软件内显示。软件不要求模型 API，不需要另装 Python。
"@
    [IO.File]::WriteAllText((Join-Path $publishRoot '离线包说明.txt'), $readme, [Text.UTF8Encoding]::new($true))
    $temporaryArchive = $archivePath + '.partial-' + [Guid]::NewGuid().ToString('N') + '.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        # Screenshot data can live beside the executable. Package program files only.
        $programFiles = @('ScreenNote.exe','ScreenNote.dll','ScreenNote.pdb','ScreenNote.deps.json','ScreenNote.runtimeconfig.json','ocr-worker.ps1','rapid-ocr-worker.py','translation-worker.py','README.md','create-desktop-shortcut.ps1','离线包说明.txt')
        $includedFiles = @($programFiles | ForEach-Object { Join-Path $publishRoot $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
        foreach ($assetName in 'app.ico','app.png','app.svg') {
            $assetPath = Join-Path $assetsOutput $assetName
            if (Test-Path -LiteralPath $assetPath -PathType Leaf) { $includedFiles += $assetPath }
        }
        $includedFiles += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -File -Filter '*.md' | ForEach-Object { Join-Path $docsOutput $_.Name })
        if (Test-Path -LiteralPath $previewSource -PathType Container) {
            $includedFiles += @(Get-ChildItem -LiteralPath $previewSource -File -Filter '*-0??.png' | ForEach-Object { Join-Path $previewOutput $_.Name })
        }
        $ocrRuntime = Join-Path $publishRoot 'ocr-runtime'
        if (-not (Test-Path -LiteralPath (Join-Path $ocrRuntime 'python.exe') -PathType Leaf)) { throw 'Bundled offline OCR runtime is missing.' }
        $includedFiles += @(Get-ChildItem -LiteralPath $ocrRuntime -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/]__pycache__[\\/]' })
        $translationModels = Join-Path $publishRoot 'translation-models'
        foreach ($direction in 'en_zh','zh_en') {
            if (-not (Test-Path -LiteralPath (Join-Path $translationModels ($direction + '\model\model.bin')) -PathType Leaf)) { throw ('Bundled translation model is missing: ' + $direction) }
        }
        $includedFiles += @(Get-ChildItem -LiteralPath $translationModels -Recurse -File)
        $archive = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $includedFiles) {
                $relativePath = [IO.Path]::GetRelativePath($publishFullPath, $file).Replace('\','/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $relativePath, [IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally { $archive.Dispose() }
        Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
    }
    finally { if (Test-Path -LiteralPath $temporaryArchive -PathType Leaf) { Remove-Item -LiteralPath $temporaryArchive -Force } }
    Write-Output ('Package: ' + $archivePath)
}
