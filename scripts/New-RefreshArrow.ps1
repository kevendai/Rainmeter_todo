param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function New-StaticIcon([string]$Name,[scriptblock]$Draw) {
    $bitmap=[Drawing.Bitmap]::new(48,48,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $pen=$null
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $pen=[Drawing.Pen]::new([Drawing.Color]::White,3.4)
        $pen.StartCap=[Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap=[Drawing.Drawing2D.LineCap]::Round
        & $Draw $graphics $pen
        $bitmap.Save((Join-Path $OutputDirectory $Name),[Drawing.Imaging.ImageFormat]::Png)
    } finally {
        if($pen){$pen.Dispose()};$graphics.Dispose();$bitmap.Dispose()
    }
}

New-StaticIcon 'Menu.png' {
    param($g,$p)
    foreach($y in 12,24,36){$g.DrawLine($p,11,$y,37,$y)}
}
New-StaticIcon 'Plus.png' {
    param($g,$p)
    $g.DrawLine($p,24,11,24,37);$g.DrawLine($p,11,24,37,24)
}

for ($frame = 0; $frame -lt 36; $frame++) {
    $bitmap = [Drawing.Bitmap]::new(48,48,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $brush = $null
    $pen = $null
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TranslateTransform(24,24)
        $graphics.RotateTransform($frame * 10)
        $graphics.TranslateTransform(-24,-24)
        $brush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
        $pen = [Drawing.Pen]::new([Drawing.Color]::White,3.0)
        $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawArc($pen,[Drawing.RectangleF]::new(11,11,26,26),-45,270)
        $arrowHead = [Drawing.PointF[]]@(
            [Drawing.PointF]::new(14.8,14.8),
            [Drawing.PointF]::new(14.5,21.5),
            [Drawing.PointF]::new(8.5,16.0)
        )
        $graphics.FillPolygon($brush,$arrowHead)
        $path = Join-Path $OutputDirectory ("RefreshArrow-$frame.png")
        $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
    } finally {
        if ($pen) { $pen.Dispose() }
        if ($brush) { $brush.Dispose() }
        $graphics.Dispose(); $bitmap.Dispose()
    }
}
