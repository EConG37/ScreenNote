param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\.build\ocr-fixtures')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$worker = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\ScreenNote\ocr-worker.ps1'))

function New-Fixture([string] $Name, [string[]] $Lines, [int] $Width = 1500, [int] $Height = 300, [int] $FontSize = 38) {
    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $font = New-Object System.Drawing.Font('Microsoft YaHei', $FontSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        for ($index = 0; $index -lt $Lines.Count; $index++) {
            $graphics.DrawString($Lines[$index], $font, [System.Drawing.Brushes]::Black, 35, (35 + $index * ($FontSize + 20)))
        }
        $path = Join-Path $OutputDirectory ($Name + '.png')
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        return $path
    }
    finally {
        $font.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$fixtures = @(
    @{ Name = 'english'; Path = (New-Fixture 'english' @('ScreenNote OCR test', 'Edit text after screenshot 123')); Required = @('ScreenNote', '123') },
    @{ Name = 'chinese'; Path = (New-Fixture 'chinese' @('截图之后直接修改文字', '本地识别，不需要配置 API')); Required = @('截图', '文字', 'API') },
    @{ Name = 'blank'; Path = (New-Fixture 'blank' @()); Required = @() },
    @{ Name = 'oversized'; Path = (New-Fixture 'oversized' @('Large image OCR 2026') 10500 300 38); Required = @('2026') }
)
$results = @()
foreach ($fixture in $fixtures) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = & powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $worker -ImagePath $fixture.Path
    $watch.Stop()
    if ($LASTEXITCODE -ne 0) { throw "Worker failed: $($fixture.Name)" }
    $result = $output | ConvertFrom-Json
    if ($null -ne $result.Error) { throw "$($fixture.Name): $($result.Error)" }
    foreach ($required in $fixture.Required) {
        if (!$result.Text.Contains($required)) { throw "$($fixture.Name) missing '$required': $($result.Text)" }
    }
    if ($fixture.Name -eq 'blank' -and $result.Text -ne '') { throw 'Blank image must be successful empty text.' }
    $results += [pscustomobject] @{ Fixture = $fixture.Name; Language = $result.Language; Text = $result.Text; Milliseconds = $watch.ElapsedMilliseconds }
}
$invalid = & powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $worker -ImagePath (Join-Path $OutputDirectory 'missing.png') | ConvertFrom-Json
if ([string]::IsNullOrEmpty($invalid.Error)) { throw 'Missing image must return a structured error.' }

$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding UTF8
$results | Format-Table -AutoSize -Wrap
Write-Host 'PASS: English, Chinese, empty image, oversized image, and structured failure.'
