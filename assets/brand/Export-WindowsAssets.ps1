[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'windows')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [Parameter(Mandatory)][Drawing.RectangleF]$Rectangle,
        [Parameter(Mandatory)][single]$Radius
    )
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = 2 * $Radius
    $path.AddArc($Rectangle.Left, $Rectangle.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.Left, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-TermLinkBitmap {
    param([Parameter(Mandatory)][ValidateRange(16, 1024)][int]$Size)

    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $scale = [single]($Size / 1024.0)

        $ink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#10151D'))
        $white = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#F4F7FB'))
        $link = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#62E6C5'))
        $presence = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#FFCF5A'))
        try {
            $background = New-RoundedRectanglePath `
                -Rectangle ([Drawing.RectangleF]::new(0, 0, $Size, $Size)) `
                -Radius ([single](224 * $scale))
            try { $graphics.FillPath($ink, $background) } finally { $background.Dispose() }

            $outer = New-RoundedRectanglePath `
                -Rectangle ([Drawing.RectangleF]::new(252 * $scale, 292 * $scale, 520 * $scale, 440 * $scale)) `
                -Radius ([single](110 * $scale))
            $outerPen = [Drawing.Pen]::new($white, [single](40 * $scale))
            try { $graphics.DrawPath($outerPen, $outer) } finally { $outerPen.Dispose(); $outer.Dispose() }

            $promptPen = [Drawing.Pen]::new($link, [single](35 * $scale))
            $promptPen.StartCap = [Drawing.Drawing2D.LineCap]::Round
            $promptPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
            $promptPen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
            try {
                $graphics.DrawLines($promptPen, [Drawing.PointF[]]@(
                    [Drawing.PointF]::new(302 * $scale, 472 * $scale),
                    [Drawing.PointF]::new(352 * $scale, 522 * $scale),
                    [Drawing.PointF]::new(302 * $scale, 572 * $scale)))
            }
            finally { $promptPen.Dispose() }

            $whitePen = [Drawing.Pen]::new($white, [single](70 * $scale))
            $whitePen.StartCap = [Drawing.Drawing2D.LineCap]::Round
            $whitePen.EndCap = [Drawing.Drawing2D.LineCap]::Round
            try {
                $graphics.DrawLine($whitePen, 352 * $scale, 392 * $scale, 572 * $scale, 392 * $scale)
                $graphics.DrawLine($whitePen, 462 * $scale, 392 * $scale, 462 * $scale, 642 * $scale)
            }
            finally { $whitePen.Dispose() }

            $linkPen = [Drawing.Pen]::new($link, [single](70 * $scale))
            $linkPen.StartCap = [Drawing.Drawing2D.LineCap]::Round
            $linkPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
            $linkPen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
            try {
                $graphics.DrawLines($linkPen, [Drawing.PointF[]]@(
                    [Drawing.PointF]::new(462 * $scale, 642 * $scale),
                    [Drawing.PointF]::new(662 * $scale, 642 * $scale),
                    [Drawing.PointF]::new(662 * $scale, 502 * $scale)))
            }
            finally { $linkPen.Dispose() }

            $graphics.FillEllipse($presence, 627 * $scale, 357 * $scale, 90 * $scale, 90 * $scale)
        }
        finally {
            $ink.Dispose()
            $white.Dispose()
            $link.Dispose()
            $presence.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }
    return $bitmap
}

function Get-TermLinkPngBytes {
    param([Parameter(Mandatory)][int]$Size)
    $bitmap = New-TermLinkBitmap -Size $Size
    $stream = [IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

$packageAssets = [ordered]@{
    'Square44x44Logo.png' = 44
    'Square150x150Logo.png' = 150
    'StoreLogo.png' = 50
    'TermLink-preview-256.png' = 256
}
foreach ($entry in $packageAssets.GetEnumerator()) {
    [IO.File]::WriteAllBytes((Join-Path $resolvedOutput $entry.Key), (Get-TermLinkPngBytes -Size $entry.Value))
}

$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$iconImages = @($iconSizes | ForEach-Object { Get-TermLinkPngBytes -Size $_ })
$iconPath = Join-Path $resolvedOutput 'TermLink.ico'
$iconStream = [IO.File]::Open($iconPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
$writer = [IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$iconSizes.Count)
    $offset = 6 + (16 * $iconSizes.Count)
    for ($index = 0; $index -lt $iconSizes.Count; $index++) {
        $size = $iconSizes[$index]
        $image = $iconImages[$index]
        $encodedSize = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$encodedSize)
        $writer.Write([byte]$encodedSize)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Length
    }
    foreach ($image in $iconImages) { $writer.Write($image) }
}
finally {
    $writer.Dispose()
    $iconStream.Dispose()
}

Get-ChildItem -LiteralPath $resolvedOutput -File | Sort-Object Name | ForEach-Object {
    [pscustomobject]@{
        Name = $_.Name
        Bytes = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
