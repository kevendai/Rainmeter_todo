param(
    [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\ui\Rainmeter.Desktop\Assets')
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$imageAssemblies = @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Color].Assembly.Location)
Add-Type -ReferencedAssemblies $imageAssemblies -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;

public static class BrandIconAlpha
{
    public static Bitmap RemoveExteriorWhite(Bitmap source)
    {
        int width = source.Width, height = source.Height;
        Bitmap output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        bool[] seen = new bool[width * height];
        int[] queue = new int[width * height];
        int head = 0, tail = 0;
        Action<int> visit = index => {
            if (index < 0 || index >= seen.Length || seen[index]) return;
            int x = index % width, y = index / width;
            Color color = source.GetPixel(x, y);
            int low = Math.Min(color.R, Math.Min(color.G, color.B));
            int high = Math.Max(color.R, Math.Max(color.G, color.B));
            if (low < 210 || high - low > 48) return;
            seen[index] = true;
            queue[tail++] = index;
        };
        for (int x = 0; x < width; x++) { visit(x); visit((height - 1) * width + x); }
        for (int y = 0; y < height; y++) { visit(y * width); visit(y * width + width - 1); }
        while (head < tail)
        {
            int index = queue[head++], x = index % width, y = index / width;
            if (x > 0) visit(index - 1);
            if (x + 1 < width) visit(index + 1);
            if (y > 0) visit(index - width);
            if (y + 1 < height) visit(index + width);
        }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                output.SetPixel(x, y, seen[y * width + x] ? Color.Transparent : source.GetPixel(x, y));
        return output;
    }
}
'@

$pngPath = Join-Path $AssetDirectory 'brand-mark.png'
$icoPath = Join-Path $AssetDirectory 'brand-mark.ico'
$source = [System.Drawing.Bitmap]::new($pngPath)
try { $transparent = [BrandIconAlpha]::RemoveExteriorWhite($source) }
finally { $source.Dispose() }
try {
    $pngTemp = $pngPath + '.tmp'
    $transparent.Save($pngTemp, [System.Drawing.Imaging.ImageFormat]::Png)
    $iconSize = 256
    $iconBitmap = [System.Drawing.Bitmap]::new($iconSize, $iconSize)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($iconBitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($transparent, 0, 0, $iconSize, $iconSize)
        } finally { $graphics.Dispose() }
        $stream = [System.IO.MemoryStream]::new()
        try {
            $iconBitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $bytes = $stream.ToArray()
            $icoTemp = $icoPath + '.tmp'
            $writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create($icoTemp))
            try {
                $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
                $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
                $writer.Write([uint16]1); $writer.Write([uint16]32)
                $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]22)
                $writer.Write($bytes)
            } finally { $writer.Dispose() }
            Move-Item -LiteralPath $icoTemp -Destination $icoPath -Force
            Move-Item -LiteralPath $pngTemp -Destination $pngPath -Force
        } finally { $stream.Dispose() }
    } finally { $iconBitmap.Dispose() }
} finally { $transparent.Dispose() }

$verify = [System.Drawing.Bitmap]::new($pngPath)
try {
    if ($verify.GetPixel(0, 0).A -ne 0) { throw 'Icon corner is not transparent.' }
    Write-Output "Transparent icon ready: $pngPath ($($verify.Width)x$($verify.Height))"
} finally { $verify.Dispose() }
