# Rebuild typebridge.ico with uncompressed BMP frames for small sizes (16/24/32/48)
# and keep PNG frames for large sizes (64/128/256). PNG-only icons fail to render
# in the Win10 tray via legacy .NET Icon decoding.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File build\rebuild_ico.ps1
param(
    [string]$IcoPath = "$PSScriptRoot\..\src\typebridge.ico"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$IcoPath = (Resolve-Path $IcoPath).Path
$src = [System.IO.File]::ReadAllBytes($IcoPath)

# ---- parse original ICO directory ----
$count = [BitConverter]::ToUInt16($src, 4)
$entries = @()
for ($i = 0; $i -lt $count; $i++) {
    $off = 6 + $i * 16
    $w = $src[$off]; if ($w -eq 0) { $w = 256 }
    $size = [BitConverter]::ToUInt32($src, $off + 8)
    $dataOff = [BitConverter]::ToUInt32($src, $off + 12)
    $entries += @{ W = $w; Size = $size; Off = $dataOff }
}

function Get-FrameBytes([int]$w) {
    # decode frame via GDI+ and return uncompressed BMP-format frame bytes (bottom-up BGRA + zero AND mask)
    $ms = New-Object System.IO.MemoryStream(,$src)
    $ms.Position = 0
    $icon = New-Object System.Drawing.Icon($ms, (New-Object System.Drawing.Size($w, $w)))
    $bmp = $icon.ToBitmap()
    try {
        $rect = New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)
        $bd = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $stride = $bd.Stride
            $px = New-Object byte[] ($stride * $bmp.Height)
            [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $px, 0, $px.Length)
        } finally { $bmp.UnlockBits($bd) }
        $w2 = $bmp.Width; $h = $bmp.Height
        $xorLen = $w2 * 4 * $h
        $andStride = [int][Math]::Ceiling($w2 / 32.0) * 4
        $andLen = $andStride * $h
        $header = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($header)
        $bw.Write([UInt32]40); $bw.Write([Int32]$w2); $bw.Write([Int32]($h * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $bw.Write([UInt32]($xorLen + $andLen))
        $bw.Write([Int32]0); $bw.Write([Int32]0)
        $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        $bw.Flush()
        $out = New-Object System.IO.MemoryStream
        $out.Write($header.ToArray(), 0, 40)
        # rows bottom-up
        for ($y = $h - 1; $y -ge 0; $y--) {
            $row = New-Object byte[] ($w2 * 4)
            [Array]::Copy($px, $y * $stride, $row, 0, $w2 * 4)
            $out.Write($row, 0, $row.Length)
        }
        $zero = New-Object byte[] $andLen
        $out.Write($zero, 0, $andLen)
        return ,$out.ToArray()
    } finally { $bmp.Dispose(); $icon.Dispose(); $ms.Dispose() }
}

# ---- build new frames: BMP for <=48, copy original PNG for >=64 ----
$newFrames = @()
foreach ($e in $entries) {
    if ($e.W -le 48) {
        $bytes = Get-FrameBytes $e.W
        $newFrames += @{ W = $e.W; Bytes = $bytes }
    } else {
        $bytes = New-Object byte[] $e.Size
        [Array]::Copy($src, $e.Off, $bytes, 0, $e.Size)
        $newFrames += @{ W = $e.W; Bytes = $bytes }
    }
}

# ---- write new ICO ----
$outMs = New-Object System.IO.MemoryStream
$bw2 = New-Object System.IO.BinaryWriter($outMs)
$bw2.Write([UInt16]0); $bw2.Write([UInt16]1); $bw2.Write([UInt16]$newFrames.Count)
$dataBase = 6 + 16 * $newFrames.Count
$offset = $dataBase
foreach ($f in $newFrames) {
    $wByte = if ($f.W -ge 256) { 0 } else { $f.W }
    $bw2.Write([byte]$wByte); $bw2.Write([byte]$wByte)
    $bw2.Write([byte]0); $bw2.Write([byte]0)
    $bw2.Write([UInt16]1); $bw2.Write([UInt16]32)
    $bw2.Write([UInt32]$f.Bytes.Length); $bw2.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
$bw2.Flush()
foreach ($f in $newFrames) { $outMs.Write($f.Bytes, 0, $f.Bytes.Length) }
[System.IO.File]::WriteAllBytes($IcoPath, $outMs.ToArray())
Write-Host ("rebuilt: {0} ({1} bytes)" -f $IcoPath, $outMs.Length)
