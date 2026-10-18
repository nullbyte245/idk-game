# Runs the headless pipeline stages after the first project open: compile, art import, scene, player.
# -Only 'step2-compile','step3-scene' limits the run; two Unity processes must never overlap.
param([string[]]$Only = @('step2-compile', 'step2b-art', 'step3-scene', 'step4-player'))

$Unity = 'C:\Unity\6000.0.64f1\Editor\Unity.exe'
$Project = 'C:\Users\Dmitry\.source\game'
$Logs = 'C:\Users\Dmitry\.source\_installer'

function Step([string]$label, [array]$extra) {
    if ($Only -notcontains $label) { return }
    $log = Join-Path $Logs "$label.log"
    if (Test-Path $log) { Remove-Item $log -Force }
    Write-Output "=== $label start $(Get-Date -Format HH:mm:ss)"
    $arguments = @('-batchmode', '-nographics', '-projectPath', $Project, '-logFile', $log) + $extra
    $p = Start-Process -FilePath $Unity -ArgumentList $arguments -Wait -PassThru
    Write-Output "=== $label exit=$($p.ExitCode)"
    Select-String -Path $log -Pattern 'error CS', 'Exception', 'FAILED', 'ArtImport', 'SceneBuilder', 'UiSpriteFactory' |
        Select-Object -First 40 | ForEach-Object { Write-Output ('   ' + $_.Line.Trim()) }
}

Step 'step2-compile' @('-quit')
Step 'step2c-mapping' @('-executeMethod', 'Game.Editor.LobbyClientCheck.VerifyMappings', '-quit')
Step 'step2b-art' @('-executeMethod', 'Game.Editor.ArtImporter.Configure', '-quit')
Step 'step3-scene' @('-executeMethod', 'Game.Editor.SceneBuilder.BuildAll', '-quit')
if ($Only -contains 'step4-player') {
    Remove-Item -Recurse -Force (Join-Path $Project 'Build') -ErrorAction SilentlyContinue
    Step 'step4-player' @('-executeMethod', 'Game.Editor.SceneBuilder.BuildWindowsPlayer', '-quit')
}
Write-Output "=== artifacts scene=$(Test-Path (Join-Path $Project 'Assets\Scenes\Main.unity')) exe=$(Test-Path (Join-Path $Project 'Build\Windows\NeonRelay.exe'))"
