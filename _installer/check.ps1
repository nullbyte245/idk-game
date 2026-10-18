$Unity = 'C:\Unity\6000.0.64f1\Editor\Unity.exe'
$Project = 'C:\Users\Dmitry\.source\game'
$Log = 'C:\Users\Dmitry\.source\_installer\check.log'
if (Test-Path $Log) { Remove-Item $Log -Force }
$p = Start-Process -FilePath $Unity -ArgumentList @(
    '-batchmode', '-nographics', '-projectPath', $Project, '-logFile', $Log,
    '-executeMethod', 'Game.Editor.LobbyClientCheck.VerifyMappings', '-quit'
) -Wait -PassThru
Write-Output "=== check exit=$($p.ExitCode)"
Select-String -Path $Log -Pattern '\[LobbyClientCheck\]', 'error CS' | ForEach-Object { Write-Output ('   ' + $_.Line.Trim()) }
