# 从界面的同一份矢量 Logo 导出 PNG 和多尺寸 ICO，避免两处图案出现偏差。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assetPath = Join-Path $PSScriptRoot '..\src\GitVault.App\Assets'
$reader = [System.Xml.XmlReader]::Create((Join-Path $assetPath 'Icons.xaml'))
try { $resources = [System.Windows.Markup.XamlReader]::Load($reader) }
finally { $reader.Dispose() }
$logo = $resources['AppLogo']
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    # 按目标像素直接渲染矢量，所有尺寸均保留透明圆角。
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawImage($logo, [System.Windows.Rect]::new(0, 0, $size, $size))
    $context.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    try { $encoder.Save($stream); $frames += ,$stream.ToArray() }
    finally { $stream.Dispose() }
}
# ICO 目录指向各尺寸的 PNG 数据；256 像素按文件格式约定记为 0。
$output = [System.IO.File]::Create((Join-Path $assetPath 'GitVault.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = [byte]($sizes[$index] % 256)
        $writer.Write($dimension); $writer.Write($dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally { $writer.Dispose() }
[System.IO.File]::WriteAllBytes((Join-Path $assetPath 'GitVault.png'), $frames[-1])
