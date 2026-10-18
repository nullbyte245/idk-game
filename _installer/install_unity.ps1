$ErrorActionPreference = 'Continue'
$exe = 'C:\Users\Dmitry\.source\_installer\UnitySetup64-6000.0.64f1.exe'
$dest = 'C:\Unity\6000.0.64f1'

Write-Output ("installer=" + (Test-Path $exe))
Write-Output ("dest_before=" + (Test-Path $dest))

$args = @('/S', ('/D=' + $dest))
Write-Output ("arguments=" + ($args -join ' '))

$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $p = Start-Process -FilePath $exe -ArgumentList $args -Wait -PassThru
    Write-Output ("exit=" + $p.ExitCode)
} catch {
    Write-Output ("error=" + $_.Exception.Message)
}
$sw.Stop()
Write-Output ("seconds=" + [int]$sw.Elapsed.TotalSeconds)

$unityExe = Join-Path $dest 'Editor\Unity.exe'
Write-Output ("unity_exe=" + (Test-Path $unityExe))
if (Test-Path $dest) {
    $size = (Get-ChildItem $dest -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    Write-Output ("installed_mb=" + [int]($size / 1MB))
    Write-Output '--- top level ---'
    Get-ChildItem $dest | ForEach-Object { Write-Output $_.Name }
    if (Test-Path (Join-Path $dest 'Editor')) {
        Write-Output '--- editor ---'
        Get-ChildItem (Join-Path $dest 'Editor') | ForEach-Object { Write-Output $_.Name }
    }
}
