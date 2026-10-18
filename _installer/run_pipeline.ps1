# Headless pipeline: generate manifest -> add packages -> compile -> build Main scene from code.
# Requires an activated Unity license (done via Unity Hub sign-in).
$ErrorActionPreference = 'Continue'

$Unity    = 'C:\Unity\6000.0.64f1\Editor\Unity.exe'
$Project  = 'C:\Users\Dmitry\.source\game'
$Logs     = 'C:\Users\Dmitry\.source\_installer'
$Manifest = Join-Path $Project 'Packages\manifest.json'
$Settings = Join-Path $Project 'ProjectSettings\ProjectSettings.asset'
$Transcript = Join-Path $Logs 'pipeline.log'

function Log([string]$message) {
    $line = (Get-Date -Format 'HH:mm:ss') + ' | ' + $message
    Write-Output $line
    Add-Content -Path $Transcript -Value $line
}

function Invoke-Unity([string]$label, [array]$extra) {
    Log "--- $label : start"
    $log = Join-Path $Logs "$label.log"
    if (Test-Path $log) { Remove-Item $log -Force }

    $arguments = @('-batchmode', '-nographics', '-projectPath', $Project, '-logFile', $log) + $extra
    $process = Start-Process -FilePath $Unity -ArgumentList $arguments -Wait -PassThru
    Log "--- $label : exit=$($process.ExitCode)"

    $hits = Select-String -Path $log -Pattern 'error CS', 'No valid Unity Editor license',
        'SceneBuilder', 'Scripts have compiler errors', 'Exception', 'Failed to' -SimpleMatch
    if ($hits) {
        $hits | Select-Object -First 30 | ForEach-Object { Log ('   ' + $_.Line.Trim()) }
    } else {
        Log '   (no error lines)'
    }
}

Log '==================== pipeline run ===================='
if (-not (Test-Path $Unity)) { Log 'FATAL: Unity.exe missing'; exit 1 }

# 1. First import: lets the Editor write Packages/manifest.json and build Library.
Invoke-Unity 'step1-open' @('-quit')

if (-not (Test-Path $Manifest)) {
    Log 'FATAL: manifest.json still missing after first import.'
    exit 1
}

# 2. Pin packages. ugui 2.0.0 ships bundled with this Editor; inputsystem 1.20.1 is the
#    registry release whose "unity" requirement is 6000.0.
$pins = [ordered]@{
    'com.unity.ugui'        = '2.0.0'
    'com.unity.inputsystem' = '1.20.1'
}
$manifestText = Get-Content $Manifest -Raw
$changed = $false
foreach ($package in $pins.Keys) {
    if ($manifestText -match ('"' + [regex]::Escape($package) + '"')) {
        Log "package present: $package"
        continue
    }
    $entry = '        "' + $package + '": "' + $pins[$package] + '",'
    $manifestText = [regex]::Replace($manifestText, '("dependencies"\s*:\s*\{)', ('$1' + "`n" + $entry))
    $changed = $true
    Log "package added: $package $($pins[$package])"
}
if ($changed) { Set-Content -Path $Manifest -Value $manifestText -Encoding ascii -NoNewline }

# 3. Switch the active input handler to the Input System package (0 = old Input Manager).
$settingsText = Get-Content $Settings -Raw
$handlerMatch = [regex]::Match($settingsText, 'activeInputHandler: \d+')
Log ("before: " + $handlerMatch.Value)
if ($handlerMatch.Success -and $handlerMatch.Value -ne 'activeInputHandler: 1') {
    $settingsText = $settingsText.Replace($handlerMatch.Value, 'activeInputHandler: 1')
    Set-Content -Path $Settings -Value $settingsText -Encoding ascii -NoNewline
    Log 'after: activeInputHandler: 1'
}

# 4. Compile against the resolved package set.
Invoke-Unity 'step2-compile' @('-quit')

# 4b. Reimport the CC0 character FBX files with the rig/material settings the scene builder relies on.
Invoke-Unity 'step2b-art' @('-executeMethod', 'Game.Editor.ArtImporter.Configure', '-quit')

# 5. Build the scene and the whole UI hierarchy from code.
Invoke-Unity 'step3-scene' @('-executeMethod', 'Game.Editor.SceneBuilder.BuildAll', '-quit')

# 6. Compile the standalone Windows player. A clean output folder rules out stale/partial data files.
Remove-Item -Recurse -Force (Join-Path $Project 'Build') -ErrorAction SilentlyContinue
Invoke-Unity 'step4-player' @('-executeMethod', 'Game.Editor.SceneBuilder.BuildWindowsPlayer', '-quit')

Log '==================== artifacts ===================='
Log ("scene      = " + (Test-Path (Join-Path $Project 'Assets\Scenes\Main.unity')))
Log ("config     = " + (Test-Path (Join-Path $Project 'Assets\Data\GameConfig.asset')))
Log ("player exe = " + (Test-Path (Join-Path $Project 'Build\Windows\NeonRelay.exe')))
Get-ChildItem (Join-Path $Project 'Assets\Materials') -Filter *.mat -ErrorAction SilentlyContinue |
    ForEach-Object { Log ("material   = " + $_.Name) }
Log ("packages   = " + ((Get-Content $Manifest -Raw) -match 'inputsystem'))
