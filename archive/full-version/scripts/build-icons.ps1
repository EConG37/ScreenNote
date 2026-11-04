param([string]$SourcePath = '', [string]$DestinationDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SourcePath) { $SourcePath = Join-Path $projectRoot 'src\ScreenNote\assets\app.svg' }
if (-not $DestinationDirectory) { $DestinationDirectory = Join-Path $projectRoot 'src\ScreenNote\assets' }
$SourcePath = [IO.Path]::GetFullPath($SourcePath)
$DestinationDirectory = [IO.Path]::GetFullPath($DestinationDirectory)
[IO.Directory]::CreateDirectory($DestinationDirectory) | Out-Null
$vector = [IO.Path]::GetExtension($SourcePath) -eq '.svg'
if ($vector) {
 [xml]$svg = [IO.File]::ReadAllText($SourcePath)
 $drawingGroup = [System.Windows.Media.DrawingGroup]::new()
 $vectorContext = $drawingGroup.Open()
 foreach($element in $svg.DocumentElement.ChildNodes) {
  if ($element.LocalName -notin @('rect','path')) { continue }
  $fill = if ($element.fill -and $element.fill -ne 'none') { [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($element.fill) } else { $null }
  $pen = if ($element.stroke -and $element.stroke -ne 'none') { [Windows.Media.Pen]::new([Windows.Media.BrushConverter]::new().ConvertFromInvariantString($element.stroke),[double]$element.'stroke-width') } else { $null }
  if ($element.LocalName -eq 'rect') {
   $radius = if ($element.rx) { [double]$element.rx } else { 0 }
   $vectorContext.DrawRoundedRectangle($fill,$pen,[Windows.Rect]::new([double]$element.x,[double]$element.y,[double]$element.width,[double]$element.height),$radius,$radius)
  } else { $vectorContext.DrawGeometry($fill,$pen,[Windows.Media.Geometry]::Parse($element.d)) }
 }
 $vectorContext.Close()
 $drawingGroup.Freeze()
} else {
 $source = [System.Windows.Media.Imaging.BitmapImage]::new()
 $source.BeginInit()
 $source.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
 $source.UriSource = [Uri]::new($SourcePath)
 $source.EndInit()
 $source.Freeze()
}
function Get-PngFrame([int]$FrameSize) {
 $visual = [System.Windows.Media.DrawingVisual]::new()
 [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual,[System.Windows.Media.BitmapScalingMode]::HighQuality)
 $drawing = $visual.RenderOpen()
 if ($vector) {
  $drawing.PushTransform([Windows.Media.ScaleTransform]::new($FrameSize/256.0,$FrameSize/256.0))
  $drawing.DrawDrawing($drawingGroup)
  $drawing.Pop()
 } else { $drawing.DrawImage($source,[Windows.Rect]::new(0,0,$FrameSize,$FrameSize)) }
 $drawing.Close()
 $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($FrameSize,$FrameSize,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($visual)
 $bitmap.Freeze()
 $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
 $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream = [IO.MemoryStream]::new()
 try { $encoder.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = foreach($size in $sizes) { Get-PngFrame $size }
[IO.File]::WriteAllBytes((Join-Path $DestinationDirectory 'app.png'),(Get-PngFrame 256))
if ($vector) { [IO.File]::WriteAllBytes((Join-Path $DestinationDirectory 'icon-master.png'),(Get-PngFrame 1280)) }
$icoFile = [IO.File]::Create((Join-Path $DestinationDirectory 'app.ico'))
$writer = [IO.BinaryWriter]::new($icoFile)
try {
 $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
 $offset = 6 + 16 * $sizes.Count
 for($index=0;$index -lt $sizes.Count;$index++) {
  $dimension = if($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
  $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
  $writer.Write([byte]0); $writer.Write([byte]0)
  $writer.Write([uint16]1); $writer.Write([uint16]32)
  $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
  $offset += $frames[$index].Length
 }
 foreach($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
$extensionIcons = Join-Path $projectRoot 'browser-extension\icons'
[IO.Directory]::CreateDirectory($extensionIcons) | Out-Null
foreach($size in @(16,32,48,128)) { [IO.File]::WriteAllBytes((Join-Path $extensionIcons ('icon-' + $size + '.png')),(Get-PngFrame $size)) }
$finalPng = [Windows.Media.Imaging.BitmapFrame]::Create([Uri]::new((Join-Path $DestinationDirectory 'app.png')))
$pixel = [byte[]]::new(4)
$finalPng.CopyPixels([Windows.Int32Rect]::new(0,0,1,1),$pixel,4,0)
[pscustomobject]@{Frames=$sizes;TransparentOuterPixel=$pixel[3] -eq 0;IcoLength=(Get-Item -LiteralPath (Join-Path $DestinationDirectory 'app.ico')).Length;ExtensionFrames=@(16,32,48,128)} | ConvertTo-Json
