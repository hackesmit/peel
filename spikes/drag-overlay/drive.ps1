$ErrorActionPreference = 'Stop'
$dir = 'C:\peel-dev\spike'
$test = "$dir\testdir"
Remove-Item "$dir\log.txt","$dir\shot.png","$dir\drive.log" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $test | Out-Null
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
function L($s) { Add-Content "$dir\drive.log" ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $s) }

$bmp = New-Object System.Drawing.Bitmap 64,64
[System.Drawing.Graphics]::FromImage($bmp).Clear([System.Drawing.Color]::Orange)
$bmp.Save("$test\sample.png", [System.Drawing.Imaging.ImageFormat]::Png)

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class In {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  public static void Move(int x, int y) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    int ax = (int)(((x - vx) * 65535.0) / (vw - 1)), ay = (int)(((y - vy) * 65535.0) / (vh - 1));
    mouse_event(0x0001 | 0x8000 | 0x4000, ax, ay, 0, UIntPtr.Zero);
  }
}
'@
[In]::SetProcessDPIAware() | Out-Null

(New-Object -ComObject Shell.Application).Windows() | Where-Object { $_.LocationURL -like '*peel-dev/spike/testdir*' } | ForEach-Object { $_.Quit() }
$spike = Start-Process "$dir\bin\Release\net8.0-windows\Spike.exe" -PassThru
Start-Sleep 1
$exp = Start-Process explorer.exe $test -PassThru
Start-Sleep 3
$root = [System.Windows.Automation.AutomationElement]::RootElement
$c1 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'sample.png')
$c2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'sample')
$c3 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$cond = New-Object System.Windows.Automation.AndCondition($c3, (New-Object System.Windows.Automation.OrCondition($c1, $c2)))
$item = $null
for ($i = 0; $i -lt 20 -and -not $item; $i++) {
  $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond); Start-Sleep -Milliseconds 300 }
if (-not $item) { L 'sample.png not found in Explorer'; $spike | Stop-Process; (New-Object -ComObject Shell.Application).Windows() | Where-Object { $_.LocationURL -like '*peel-dev/spike/testdir*' } | ForEach-Object { $_.Quit() }; exit 1 }
$r = $item.Current.BoundingRectangle
$x = [int]($r.X + 24); $y = [int]($r.Y + $r.Height / 2)
L "item at $x,$y"
$orig = [System.Windows.Forms.Cursor]::Position

[In]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 200
[In]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # left down
for ($i = 1; $i -le 15; $i++) { [In]::Move($x + $i * 12, $y + $i * 6); Start-Sleep -Milliseconds 30 }
$cx = $x + 180; $cy = $y + 90
L 'tap shift'
[In]::keybd_event(0xA0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
[In]::keybd_event(0xA0, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 400
for ($i = 1; $i -le 10; $i++) { [In]::Move($cx + $i * 11, $cy); Start-Sleep -Milliseconds 40 }
Start-Sleep -Milliseconds 400
$s = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$shot = New-Object System.Drawing.Bitmap 700,500
$g = [System.Drawing.Graphics]::FromImage($shot)
$g.CopyFromScreen($cx - 300, $cy - 250, 0, 0, $shot.Size)
$shot.Save("$dir\shot.png")
L 'release on WEBP segment'
[In]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # left up
Start-Sleep 1
[In]::SetCursorPos($orig.X, $orig.Y)
$spike | Stop-Process -ErrorAction SilentlyContinue
(New-Object -ComObject Shell.Application).Windows() | Where-Object { $_.LocationURL -like '*peel-dev/spike/testdir*' } | ForEach-Object { $_.Quit() }
L 'done'
