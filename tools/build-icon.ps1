$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$assetDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'
[System.IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
$images = @()
foreach ($size in @(16,20,24,32,48,64,128,256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size,$size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform($size / 256.0,$size / 256.0)
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc(8,8,72,72,180,90); $shape.AddArc(176,8,72,72,270,90)
    $shape.AddArc(176,176,72,72,0,90); $shape.AddArc(8,176,72,72,90,90); $shape.CloseFigure()
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#142C35'))
    $mint = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#7DE2CA'))
    $pen = [System.Drawing.Pen]::new($mint,16)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.FillPath($background,$shape)
    $graphics.DrawRectangle($pen,54,60,148,136)
    $graphics.DrawLine($pen,56,98,200,98)
    $graphics.FillEllipse($mint,136,124,38,38)
    $graphics.FillPolygon($mint,[System.Drawing.Point[]]@([System.Drawing.Point]::new(65,185),[System.Drawing.Point]::new(110,132),[System.Drawing.Point]::new(159,185)))
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $images += @{Size=$size;Bytes=$stream.ToArray()}
    $stream.Dispose();$pen.Dispose();$mint.Dispose();$background.Dispose();$shape.Dispose();$graphics.Dispose();$bitmap.Dispose()
}
$output = [System.IO.File]::Create((Join-Path $assetDirectory 'app.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($entry in $images) {
        $dimension = if ($entry.Size -eq 256) {0} else {$entry.Size}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$entry.Bytes.Length);$writer.Write([uint32]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $images) {$writer.Write([byte[]]$entry.Bytes)}
} finally {$writer.Dispose()}