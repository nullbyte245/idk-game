param([string]$Source = 'C:\Users\Dmitry\Desktop\assets',
       [string]$Out = 'C:\Users\Dmitry\.source\game\Assets\Art\UI',
       [int]$Size = 256,
       [int]$Padding = 14)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -Path "$PSScriptRoot\SvgRaster.cs" -ReferencedAssemblies System.Drawing, System.Core

$icons = @(
    @{ source = 'fill\play-fill.svg';  file = 'IconPlay.png' },
    @{ source = 'fill\gear-fill.svg';  file = 'IconSettings.png' },
    @{ source = 'fill\power-fill.svg'; file = 'IconQuit.png' }
)

foreach ($icon in $icons) {
    $svg = Join-Path $Source $icon.source
    if (-not (Test-Path $svg)) { throw "missing icon source: $svg" }

    $png = Join-Path $Out $icon.file
    [SvgRaster]::Render($svg, $png, $Size, [System.Drawing.Color]::White, $Padding)
    Write-Output ("rendered " + $icon.file + " <- " + $icon.source)
}
