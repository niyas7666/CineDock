$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $PSScriptRoot 'cinedock.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
  $bitmap = [System.Drawing.Bitmap]::new($size, $size)
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $graphics.Clear([System.Drawing.Color]::FromArgb(29, 33, 41))
  $margin = [Math]::Max(1, [int]($size * 0.07))
  $radius = [Math]::Max(2, [int]($size * 0.21))
  $rect = [System.Drawing.Rectangle]::new($margin, $margin, $size - (2 * $margin), $size - (2 * $margin))
  $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
  $d = $radius * 2
  $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90); $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
  $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90); $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90); $path.CloseFigure()
  $graphics.FillPath([System.Drawing.Brushes]::SlateGray, $path)
  $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(76, 85, 99), [Math]::Max(1, $size / 42))
  $graphics.DrawPath($pen, $path)
  $points = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new($size * .40, $size * .28), [System.Drawing.PointF]::new($size * .40, $size * .72), [System.Drawing.PointF]::new($size * .75, $size * .50)
  )
  $graphics.FillPolygon([System.Drawing.Brushes]::Orange, $points)
  $stream = [System.IO.MemoryStream]::new(); $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
  $pen.Dispose(); $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
  ,$stream.ToArray()
}

$stream = [System.IO.File]::Open($output, [System.IO.FileMode]::Create)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
  $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$images.Count)
  $offset = 6 + (16 * $images.Count)
  for ($index = 0; $index -lt $images.Count; $index++) {
    $size = $sizes[$index]; $bytes = $images[$index]
    $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size }))); $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size })))
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([UInt16]1); $writer.Write([UInt16]32); $writer.Write([UInt32]$bytes.Length); $writer.Write([UInt32]$offset)
    $offset += $bytes.Length
  }
  foreach ($bytes in $images) { $writer.Write($bytes) }
} finally { $writer.Dispose() }
