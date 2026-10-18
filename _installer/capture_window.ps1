param([string]$Exe = 'C:\Users\Dmitry\.source\game\Build\Windows\NeonRelay.exe',
       [string]$Out = 'C:\Users\Dmitry\.source\_installer\shot.png',
       [int]$WaitSeconds = 14,
       [int]$ClickX = -1,
       [int]$ClickY = -1,
       [string]$HoldKey = '',
       [int]$HoldSeconds = 3,
       [string]$SecondOut = '')

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$signature = @'
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class Win {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    public const uint Down = 0x0002, Up = 0x0004;
}
'@
Add-Type -TypeDefinition $signature

$process = Start-Process -FilePath $Exe -ArgumentList @('-logFile', 'C:\Users\Dmitry\.source\_installer\player_shot.log') -PassThru
$id = $process.Id
Start-Sleep -Seconds $WaitSeconds

$process.Refresh()
$handle = $process.MainWindowHandle
if ($handle -eq 0) { $handle = [System.Diagnostics.Process]::GetProcessById($id).MainWindowHandle }
if ($handle -eq 0) { throw "no main window for pid $id" }

$rect = New-Object RECT
[void][Win]::GetWindowRect($handle, [ref]$rect)

function Capture([string]$path) {
    $r = New-Object RECT
    [void][Win]::GetWindowRect($handle, [ref]$r)
    $width = $r.Right - $r.Left
    $height = $r.Bottom - $r.Top
    if ($width -le 0 -or $height -le 0) { throw "empty window rect $width x $height" }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($r.Left, $r.Top, 0, 0, $bitmap.Size)
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Output "captured $width x $height -> $path"
}

if ($ClickX -ge 0) {
    [void][Win]::SetCursorPos($rect.Left + $ClickX, $rect.Top + $ClickY)
    Start-Sleep -Milliseconds 250
    [Win]::mouse_event([Win]::Down, 0, 0, 0, [IntPtr]::Zero)
    [Win]::mouse_event([Win]::Up, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds 2
}

if ($HoldKey -ne '') {
    $vk = [byte][char]$HoldKey.ToUpper()[0]
    [Win]::keybd_event($vk, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds $HoldSeconds
    Capture $Out
    Start-Sleep -Seconds 1
    if ($SecondOut -ne '') { Capture $SecondOut }
    # KEYEVENTF_KEYUP is 0x2 for keybd_event, unlike the mouse_event constants above.
    [Win]::keybd_event($vk, 0, 2, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 200
} else {
    Capture $Out
}

Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
