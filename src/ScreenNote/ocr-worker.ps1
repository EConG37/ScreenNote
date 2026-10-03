param(
    [Parameter(Mandatory = $true)]
    [string] $ImagePath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$stream = $null
$bitmap = $null

try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
    $null = [Windows.Storage.FileAccessMode, Windows.Storage, ContentType = WindowsRuntime]
    $null = [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
    $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
    $null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
    $null = [Windows.Graphics.Imaging.BitmapTransform, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
    $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
    $null = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType = WindowsRuntime]

    $script:asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and
        $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1

    function Wait-WinRt([object] $Operation, [type] $ResultType) {
        $task = $script:asTask.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
        return $task.GetAwaiter().GetResult()
    }

    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    $languages = @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages)
    # The Chinese recognizer handles Latin letters too. Prefer it for mixed Chinese/English screenshots.
    $chinese = $languages | Where-Object { $_.LanguageTag -like 'zh-Hans*' } | Select-Object -First 1
    if ($null -eq $chinese) {
        $chinese = $languages | Where-Object { $_.LanguageTag -like 'zh-*' } | Select-Object -First 1
    }
    if ($null -ne $chinese) {
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($chinese)
    }
    if ($null -eq $engine) {
        $fallback = $languages | Where-Object { $_.LanguageTag -like 'en-*' } | Select-Object -First 1
        if ($null -eq $fallback) { $fallback = $languages | Select-Object -First 1 }
        if ($null -ne $fallback) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($fallback) }
    }
    if ($null -eq $engine) {
        throw 'Windows has no installed OCR language. Add Chinese or English OCR in Windows Settings > Time & language > Language options.'
    }

    $file = Wait-WinRt ([Windows.Storage.StorageFile]::GetFileFromPathAsync([IO.Path]::GetFullPath($ImagePath))) ([Windows.Storage.StorageFile])
    $stream = Wait-WinRt ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Wait-WinRt ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $transform = New-Object Windows.Graphics.Imaging.BitmapTransform
    $limit = [Windows.Media.Ocr.OcrEngine]::MaxImageDimension
    $longestSide = [Math]::Max($decoder.PixelWidth, $decoder.PixelHeight)
    if ($longestSide -gt $limit) {
        $scale = [double] $limit / $longestSide
        $transform.ScaledWidth = [uint32] [Math]::Max(1, [Math]::Floor($decoder.PixelWidth * $scale))
        $transform.ScaledHeight = [uint32] [Math]::Max(1, [Math]::Floor($decoder.PixelHeight * $scale))
        $transform.InterpolationMode = [Windows.Graphics.Imaging.BitmapInterpolationMode]::Fant
    }
    $bitmap = Wait-WinRt ($decoder.GetSoftwareBitmapAsync(
        [Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,
        [Windows.Graphics.Imaging.BitmapAlphaMode]::Ignore,
        $transform,
        [Windows.Graphics.Imaging.ExifOrientationMode]::IgnoreExifOrientation,
        [Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage
    )) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $recognized = Wait-WinRt ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    # Use Lines instead of Text so the editor retains useful physical line breaks.
    $text = (@($recognized.Lines | ForEach-Object {
        # Windows inserts spaces between individual Chinese OCR words; these are not word boundaries.
        [regex]::Replace($_.Text, '(?<=[\u2E80-\u9FFF\uF900-\uFAFF\uFF01-\uFF60]) +(?=[\u2E80-\u9FFF\uF900-\uFAFF\uFF01-\uFF60])', '')
    }) -join "`n")
    $result = [ordered] @{ Text = [string] $text; Language = [string] $engine.RecognizerLanguage.LanguageTag; Error = $null }
}
catch {
    $result = [ordered] @{ Text = ''; Language = ''; Error = ('Local Windows OCR failed: ' + $_.Exception.Message) }
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if ($null -ne $stream) { $stream.Dispose() }
}

[Console]::Write(($result | ConvertTo-Json -Compress -Depth 4))
