param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assetsRoot = Join-Path $repositoryRoot 'src\Gomail.App\Assets'
$iconSource = Join-Path $assetsRoot 'InboxwellIcon.png'

function Resize-Png([string]$Source, [string]$Destination, [int]$Width, [int]$Height) {
    $image = [System.Drawing.Image]::FromFile($Source)
    try {
        $bitmap = New-Object System.Drawing.Bitmap $Width, $Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($image, 0, 0, $Width, $Height)
            }
            finally {
                $graphics.Dispose()
            }
            $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $image.Dispose()
    }
}

foreach ($size in @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)) {
    foreach ($suffix in @('', '_altform-unplated', '_altform-lightunplated')) {
        Resize-Png $iconSource (Join-Path $assetsRoot "Square44x44Logo.targetsize-$size$suffix.png") $size $size
    }
}

Resize-Png $iconSource (Join-Path $assetsRoot 'Square44x44Logo.png') 44 44
Resize-Png $iconSource (Join-Path $assetsRoot 'Square150x150Logo.png') 150 150
Resize-Png (Join-Path $assetsRoot 'LockScreenLogo.scale-200.png') (Join-Path $assetsRoot 'LockScreenLogo.png') 24 24
Resize-Png (Join-Path $assetsRoot 'SplashScreen.scale-200.png') (Join-Path $assetsRoot 'SplashScreen.png') 620 300
Resize-Png (Join-Path $assetsRoot 'Wide310x150Logo.scale-200.png') (Join-Path $assetsRoot 'Wide310x150Logo.png') 310 150

Write-Host 'Generated complete Windows icon assets for Inboxwell.' -ForegroundColor Green
