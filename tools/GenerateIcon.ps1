$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$output = Join-Path $PSScriptRoot '..\src\MagicViewWallpaper.App\Assets\AppIcon.ico'
$directory = Split-Path $output
[IO.Directory]::CreateDirectory($directory) | Out-Null

$bitmap = [Drawing.Bitmap]::new(256, 256, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)

$outer = [Drawing.RectangleF]::new(8, 8, 240, 240)
$path = [Drawing.Drawing2D.GraphicsPath]::new()
$radius = 48.0
$diameter = $radius * 2
$path.AddArc($outer.X, $outer.Y, $diameter, $diameter, 180, 90)
$path.AddArc($outer.Right - $diameter, $outer.Y, $diameter, $diameter, 270, 90)
$path.AddArc($outer.Right - $diameter, $outer.Bottom - $diameter, $diameter, $diameter, 0, 90)
$path.AddArc($outer.X, $outer.Bottom - $diameter, $diameter, $diameter, 90, 90)
$path.CloseFigure()

$background = [Drawing.Drawing2D.LinearGradientBrush]::new($outer, [Drawing.Color]::FromArgb(255, 20, 39, 45), [Drawing.Color]::FromArgb(255, 7, 16, 20), 45)
$graphics.FillPath($background, $path)

$accent = [Drawing.Color]::FromArgb(255, 230, 255, 89)
$mountain = [Drawing.Drawing2D.GraphicsPath]::new()
$mountain.AddPolygon([Drawing.PointF[]]@(
    [Drawing.PointF]::new(38, 178), [Drawing.PointF]::new(92, 105),
    [Drawing.PointF]::new(126, 145), [Drawing.PointF]::new(164, 82),
    [Drawing.PointF]::new(222, 178)
))
$graphics.FillPath([Drawing.SolidBrush]::new($accent), $mountain)

$cut = [Drawing.Drawing2D.GraphicsPath]::new()
$cut.AddPolygon([Drawing.PointF[]]@(
    [Drawing.PointF]::new(76, 178), [Drawing.PointF]::new(108, 134),
    [Drawing.PointF]::new(130, 158), [Drawing.PointF]::new(164, 111),
    [Drawing.PointF]::new(199, 178)
))
$graphics.FillPath([Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 12, 28, 34)), $cut)
$graphics.FillEllipse([Drawing.SolidBrush]::new($accent), 181, 43, 28, 28)

$handle = $bitmap.GetHicon()
$icon = [Drawing.Icon]::FromHandle($handle)
$stream = [IO.File]::Create($output)
$icon.Save($stream)
$stream.Dispose()
$icon.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

Write-Host "Generated $output"
