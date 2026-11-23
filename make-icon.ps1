# Generates RemoteHub.ico (multi-size PNG-compressed ICO).
# Pure ASCII on purpose: no BOM / codepage issues on Windows PowerShell 5.1.
[void][System.Reflection.Assembly]::LoadWithPartialName('System.Drawing')

$out = Join-Path $PSScriptRoot 'RemoteHub.ico'
$sizes = @(16, 32, 48, 64, 128, 256)
$images = New-Object 'System.Collections.Generic.List[byte[]]'

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($s * 0.04))
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(26, 68, 112))
    $g.FillEllipse($bg, $pad, $pad, $s - 2 * $pad, $s - 2 * $pad)

    $white = [System.Drawing.Brushes]::White
    $line = [Math]::Max(1, [int]($s * 0.045))
    $mx = [int]($s * 0.21)
    $my = [int]($s * 0.24)
    $mw = [int]($s * 0.58)
    $mh = [int]($s * 0.38)
    $g.FillRectangle($white, $mx, $my, $mw, $mh)
    $screen = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(104, 178, 235))
    $g.FillRectangle($screen, $mx + $line, $my + $line, $mw - 2 * $line, $mh - 2 * $line)
    $g.FillRectangle($white, [int]($s * 0.45), $my + $mh, [int]($s * 0.10), [int]($s * 0.08))
    $g.FillRectangle($white, [int]($s * 0.33), $my + $mh + [int]($s * 0.08), [int]($s * 0.34), [int]($s * 0.055))

    $dot = [int]($s * 0.20)
    $g.FillEllipse([System.Drawing.Brushes]::Orange, $s - $dot - [int]($s * 0.08), $s - $dot - [int]($s * 0.08), $dot, $dot)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images.Add($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
    $bg.Dispose()
    $screen.Dispose()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w = 0
    if ($s -lt 256) { $w = $s }
    $bw.Write([byte]$w)
    $bw.Write([byte]$w)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
$fs.Close()

Write-Output ("Icon written: " + $out + " (" + (Get-Item $out).Length + " bytes, " + $sizes.Count + " sizes)")
