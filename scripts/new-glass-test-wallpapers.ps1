param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$palettes = @(
    @{ Name='light'; From='#EAF2F8'; To='#FFF2DA'; Colors=@('#9DBCE5','#B1D6BA','#E3B6B0') },
    @{ Name='dark'; From='#101A2A'; To='#302339'; Colors=@('#284B70','#315B50','#6C3C4F') },
    @{ Name='saturated'; From='#164DC0'; To='#CC1762'; Colors=@('#00C8B1','#FFA526','#9226CE') }
)
foreach ($palette in $palettes) {
    $path = Join-Path $output ($palette.Name + '.png')
    if (Test-Path -LiteralPath $path) { throw "不会覆盖既有文件：$path" }
    $bitmap = [Drawing.Bitmap]::new(1920, 1080)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Rectangle]::new(0,0,1920,1080),
        [Drawing.ColorTranslator]::FromHtml($palette.From), [Drawing.ColorTranslator]::FromHtml($palette.To), [single]25)
    try {
        $graphics.FillRectangle($gradient,0,0,1920,1080)
        for ($i=0; $i -lt 3; $i++) {
            $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($palette.Colors[$i]))
            try { $graphics.FillRectangle($brush,120+$i*600,200,480,640) } finally { $brush.Dispose() }
        }
        $fine = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(160,255,255,255))
        try {
            for ($x=0; $x -lt 1920; $x+=12) { $graphics.FillRectangle($fine,$x,420,3,140) }
            for ($x=0; $x -lt 1920; $x+=64) { $graphics.FillRectangle($fine,$x,900,20,80) }
        } finally { $fine.Dispose() }
        $font = [Drawing.Font]::new('Segoe UI',24)
        $textBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(230,255,255,255))
        try { $graphics.DrawString("GLASSBOGO MATERIAL TEST / $($palette.Name.ToUpperInvariant()) / COLOR + DETAIL",$font,$textBrush,40,40) }
        finally { $textBrush.Dispose(); $font.Dispose() }
        $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
    } finally { $gradient.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    Write-Output $path
}
# Generate files only. Deliberately no SystemParametersInfo or registry wallpaper changes.
