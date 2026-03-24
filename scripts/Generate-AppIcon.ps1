param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

Add-Type -AssemblyName System.Drawing

$iconPath = Join-Path $ProjectRoot "src\OptiScalerInstaller.App\app.ico"
$previewPath = Join-Path $ProjectRoot "assets\app-icon-preview.png"
$size = 256

$bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$graphics.Clear([System.Drawing.Color]::Transparent)

$outerRect = [System.Drawing.Rectangle]::new(10, 10, 236, 236)
$outerPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$outerRadius = 50
$outerDiameter = $outerRadius * 2
$outerPath.AddArc($outerRect.X, $outerRect.Y, $outerDiameter, $outerDiameter, 180, 90)
$outerPath.AddArc($outerRect.Right - $outerDiameter, $outerRect.Y, $outerDiameter, $outerDiameter, 270, 90)
$outerPath.AddArc($outerRect.Right - $outerDiameter, $outerRect.Bottom - $outerDiameter, $outerDiameter, $outerDiameter, 0, 90)
$outerPath.AddArc($outerRect.X, $outerRect.Bottom - $outerDiameter, $outerDiameter, $outerDiameter, 90, 90)
$outerPath.CloseFigure()

$backgroundBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    $outerRect,
    [System.Drawing.Color]::FromArgb(255, 13, 17, 20),
    [System.Drawing.Color]::FromArgb(255, 32, 40, 48),
    45)

$borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 255, 111, 0), 10)

$graphics.FillPath($backgroundBrush, $outerPath)
$graphics.DrawPath($borderPen, $outerPath)

$innerRect = [System.Drawing.Rectangle]::new(38, 38, 180, 180)
$innerPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$innerRadius = 34
$innerDiameter = $innerRadius * 2
$innerPath.AddArc($innerRect.X, $innerRect.Y, $innerDiameter, $innerDiameter, 180, 90)
$innerPath.AddArc($innerRect.Right - $innerDiameter, $innerRect.Y, $innerDiameter, $innerDiameter, 270, 90)
$innerPath.AddArc($innerRect.Right - $innerDiameter, $innerRect.Bottom - $innerDiameter, $innerDiameter, $innerDiameter, 0, 90)
$innerPath.AddArc($innerRect.X, $innerRect.Bottom - $innerDiameter, $innerDiameter, $innerDiameter, 90, 90)
$innerPath.CloseFigure()

$innerBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 22, 29, 34))
$graphics.FillPath($innerBrush, $innerPath)

$accentPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 255, 111, 0), 18)
$accentPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$accentPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawArc($accentPen, 68, 60, 120, 126, 26, 308)
$graphics.DrawLine($accentPen, 164, 148, 202, 148)
$graphics.DrawLine($accentPen, 152, 172, 192, 172)

$font = [System.Drawing.Font]::new("Segoe UI Semibold", 46, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$fontBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 243, 234))
$stringFormat = [System.Drawing.StringFormat]::new()
$stringFormat.Alignment = [System.Drawing.StringAlignment]::Center
$stringFormat.LineAlignment = [System.Drawing.StringAlignment]::Center
$textRect = [System.Drawing.RectangleF]::new(0, 150, 256, 62)
$graphics.DrawString("OS", $font, $fontBrush, $textRect, $stringFormat)

New-Item -ItemType Directory -Force -Path (Split-Path $previewPath -Parent) | Out-Null
$bitmap.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)

$memoryStream = New-Object System.IO.MemoryStream
$bitmap.Save($memoryStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $memoryStream.ToArray()
$memoryStream.Dispose()

$fileStream = [System.IO.File]::Open($iconPath, [System.IO.FileMode]::Create)
$writer = New-Object System.IO.BinaryWriter($fileStream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]1)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]32)
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)
$writer.Write($pngBytes)
$writer.Flush()
$writer.Dispose()
$fileStream.Dispose()

$graphics.Dispose()
$bitmap.Dispose()
$font.Dispose()
$fontBrush.Dispose()
$stringFormat.Dispose()
$accentPen.Dispose()
$innerBrush.Dispose()
$borderPen.Dispose()
$backgroundBrush.Dispose()
$outerPath.Dispose()
$innerPath.Dispose()

Get-Item $iconPath, $previewPath | Select-Object FullName, Length
