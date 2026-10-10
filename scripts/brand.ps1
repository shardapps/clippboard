# Rebuild the original paper-and-peg icon at Windows icon sizes.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\brand'
function New-RoundedPath([float]$x,[float]$y,[float]$width,[float]$height,[float]$radius) {
  $path = [Drawing.Drawing2D.GraphicsPath]::new()
  $diameter = $radius * 2
  $path.AddArc($x,$y,$diameter,$diameter,180,90)
  $path.AddArc($x+$width-$diameter,$y,$diameter,$diameter,270,90)
  $path.AddArc($x+$width-$diameter,$y+$height-$diameter,$diameter,$diameter,0,90)
  $path.AddArc($x,$y+$height-$diameter,$diameter,$diameter,90,90)
  $path.CloseFigure()
  return $path
}
$frames = foreach ($size in @(16,32,48,256)) {
  $bitmap = [Drawing.Bitmap]::new($size,$size)
  $graphics = [Drawing.Graphics]::FromImage($bitmap)
  $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $graphics.ScaleTransform($size/24.0,$size/24.0)
  $blue = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#526DCC'))
  $white = [Drawing.Pen]::new([Drawing.Color]::White,1.4)
  $background = New-RoundedPath 0 0 24 24 6
  $paper = New-RoundedPath 5 5 14 16 2
  $graphics.FillPath($blue,$background)
  $graphics.DrawPath($white,$paper)
  $graphics.DrawLine($white,8,13,16,13)
  $graphics.DrawLine($white,8,17,14,17)
  $graphics.FillRectangle($blue,10,2,4,7)
  $graphics.DrawRectangle($white,10,2,4,7)
  $memory = [IO.MemoryStream]::new()
  $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
  if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assetRoot 'clippboard.png'),$memory.ToArray()) }
  [PSCustomObject]@{ Size=$size; Bytes=$memory.ToArray() }
  $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $background.Dispose(); $paper.Dispose(); $blue.Dispose(); $white.Dispose()
}
$stream = [IO.File]::Create((Join-Path $assetRoot 'clippboard.ico'))
$writer = [IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
  $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
  $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
  $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
  $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
$writer.Dispose(); $stream.Dispose()
