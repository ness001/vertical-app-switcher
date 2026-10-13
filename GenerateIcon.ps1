param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath {
    param(
        [System.Drawing.RectangleF]$Bounds,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($Bounds.Left, $Bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Top, $diameter, $diameter, 270, 90)
    $path.AddArc(
        $Bounds.Right - $diameter,
        $Bounds.Bottom - $diameter,
        $diameter,
        $diameter,
        0,
        90)
    $path.AddArc($Bounds.Left, $Bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$bitmap = New-Object System.Drawing.Bitmap(256, 256)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$pngStream = New-Object System.IO.MemoryStream

try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $panelBounds = New-Object System.Drawing.RectangleF(8, 8, 240, 240)
    $panelPath = New-RoundedPath -Bounds $panelBounds -Radius 44
    $panelBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $panelBounds,
        [System.Drawing.Color]::FromArgb(39, 47, 62),
        [System.Drawing.Color]::FromArgb(13, 17, 25),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $borderPen = New-Object System.Drawing.Pen(
        [System.Drawing.Color]::FromArgb(94, 114, 143),
        5)

    try {
        $graphics.FillPath($panelBrush, $panelPath)
        $graphics.DrawPath($borderPen, $panelPath)
    }
    finally {
        $borderPen.Dispose()
        $panelBrush.Dispose()
        $panelPath.Dispose()
    }

    $rows = @(
        @{ Y = 41; Selected = $false },
        @{ Y = 103; Selected = $true },
        @{ Y = 165; Selected = $false }
    )

    foreach ($row in $rows) {
        $rowBounds = New-Object System.Drawing.RectangleF(38, $row.Y, 181, 48)
        $rowPath = New-RoundedPath -Bounds $rowBounds -Radius 12
        $rowColor = if ($row.Selected) {
            [System.Drawing.Color]::FromArgb(62, 85, 115)
        }
        else {
            [System.Drawing.Color]::FromArgb(35, 43, 57)
        }
        $rowBrush = New-Object System.Drawing.SolidBrush($rowColor)

        try {
            $graphics.FillPath($rowBrush, $rowPath)
        }
        finally {
            $rowBrush.Dispose()
            $rowPath.Dispose()
        }

        $iconColor = if ($row.Selected) {
            [System.Drawing.Color]::FromArgb(100, 218, 255)
        }
        else {
            [System.Drawing.Color]::FromArgb(166, 179, 199)
        }
        $iconBrush = New-Object System.Drawing.SolidBrush($iconColor)
        $textBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(224, 232, 243))

        try {
            $graphics.FillEllipse($iconBrush, 50, $row.Y + 12, 24, 24)
            $graphics.FillRectangle($textBrush, 88, $row.Y + 12, 105, 8)
            $graphics.FillRectangle($textBrush, 88, $row.Y + 28, 72, 6)
        }
        finally {
            $textBrush.Dispose()
            $iconBrush.Dispose()
        }

        if ($row.Selected) {
            $accentBrush = New-Object System.Drawing.SolidBrush(
                [System.Drawing.Color]::FromArgb(81, 211, 255))
            try {
                $graphics.FillRectangle($accentBrush, 38, $row.Y + 8, 6, 32)
            }
            finally {
                $accentBrush.Dispose()
            }
        }
    }

    $bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes = $pngStream.ToArray()
    $iconStream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($iconStream)

    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]1)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$pngBytes.Length)
        $writer.Write([uint32]22)
        $writer.Write($pngBytes)
        $writer.Flush()

        [System.IO.File]::WriteAllBytes($OutputPath, $iconStream.ToArray())
    }
    finally {
        $writer.Dispose()
        $iconStream.Dispose()
    }
}
finally {
    $pngStream.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
