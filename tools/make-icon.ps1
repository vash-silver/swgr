# Writes swgr-tools.ico (16-256 px, PNG entries) with the same teal ring + gold dot as the tray icon
# (SwgrTools.MakeIcon in swgr-tools.cs). Run from tools/: powershell -ExecutionPolicy Bypass -File make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot 'swgr-tools.ico'))
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($n in $sizes) {
  $s = $n / 32.0
  $bmp = New-Object System.Drawing.Bitmap $n, $n
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(80, 210, 200)), (4 * $s)
  $g.DrawEllipse($pen, 4 * $s, 4 * $s, 24 * $s, 24 * $s)
  $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(233, 214, 90))
  $g.FillEllipse($brush, 12 * $s, 12 * $s, 8 * $s, 8 * $s)
  $pen.Dispose(); $brush.Dispose(); $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  , $ms.ToArray()
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $n = $sizes[$i]; $dim = if ($n -ge 256) { 0 } else { $n }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Output "wrote $Out"
