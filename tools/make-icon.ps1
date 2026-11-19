# Draws the Byterake icon (mint tile with a rake) and writes Assets\byterake.ico and icon-256.png.
# Run with Windows PowerShell: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "..\src\Byterake.App\Assets"
New-Item -ItemType Directory -Force $out | Out-Null

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Rounded mint tile
    $r = 56 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $size - 1
    $path.AddArc(0, 0, $r, $r, 180, 90)
    $path.AddArc($w - $r, 0, $r, $r, 270, 90)
    $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90)
    $path.AddArc(0, $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x4B, 0xE3, 0xA4))), $path)

    # Rake: handle, head, tines
    $ink = [System.Drawing.Color]::FromArgb(255, 0x08, 0x13, 0x0E)
    $pen = New-Object System.Drawing.Pen $ink, ([Math]::Max(2, 18 * $s))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLine($pen, 128 * $s, 44 * $s, 128 * $s, 150 * $s)      # handle
    $g.DrawLine($pen, 64 * $s, 150 * $s, 192 * $s, 150 * $s)      # head
    foreach ($x in 64, 96, 128, 160, 192) {
        $g.DrawLine($pen, $x * $s, 150 * $s, $x * $s, 206 * $s)   # tines
    }
    $g.Dispose()
    return $bmp
}

$sizes = 256, 64, 48, 32, 24, 16
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = Draw-Icon $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($sz -eq 256) { $bmp.Save((Join-Path $out "icon-256.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    $pngs += , $ms.ToArray()
}

# ICO file with PNG-compressed images
$fs = [System.IO.File]::Create((Join-Path $out "byterake.ico"))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Output "Wrote $out\byterake.ico"
