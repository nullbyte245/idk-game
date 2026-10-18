Add-Type -AssemblyName System.Drawing
Add-Type -Path "$PSScriptRoot\SvgRaster.cs" -ReferencedAssemblies System.Drawing, System.Core
$black = [System.Drawing.Color]::FromArgb(255,0,0,0)
$map = @{ 'play'='fill\play-fill.svg'; 'settings'='fill\gear-fill.svg'; 'quit'='fill\power-fill.svg' }
foreach ($k in $map.Keys) { [SvgRaster]::Render((Join-Path 'C:\Users\Dmitry\Desktop\assets' $map[$k]), "$PSScriptRoot\_preview_$k.png", 256, $black, 14) }
"ok"
