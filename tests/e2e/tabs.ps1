# Checks the "new tab on click" behaviour and the tab shortcut keys with real mouse and keyboard input against a local
# test page. Ctrl-click and middle-click open a background tab, a plain click does not, double-click on the empty tab bar
# and Alt+Enter in the address bar open a new tab; Ctrl+Tab, Ctrl+1..9 and Ctrl+Shift+T work from the address bar and
# from inside a page. Needs the desktop (it moves the mouse), so leave the machine alone while it runs.
#
#   powershell -File tests\e2e\tabs.ps1 [-Exe path\to\LightBrowser.exe]
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\bin\Debug\net8.0-windows\LightBrowser.exe'))
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$script:fail = 0
function Check($name, $ok, $detail = '') { if ($ok) { "  PASS  $name" } else { "  FAIL  $name  $detail"; $script:fail++ } }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public class Mouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public static void Move(int x, int y) { SetCursorPos(x, y); }
  public static void Click(bool middle) { uint down = middle ? 0x20u : 0x2u, up = middle ? 0x40u : 0x4u; mouse_event(down, 0, 0, 0, UIntPtr.Zero); mouse_event(up, 0, 0, 0, UIntPtr.Zero); }
  public static void Ctrl(bool d) { keybd_event(0x11, 0, d ? 0u : 2u, UIntPtr.Zero); }
}
"@

$h = Join-Path $env:TEMP ('lb-tabs-' + [guid]::NewGuid().ToString('N')); New-Item $h -ItemType Directory | Out-Null
$log = Join-Path $h 'server.log'; New-Item $log -ItemType File | Out-Null
$port = Get-Random -Minimum 20000 -Maximum 40000
$server = Start-Job -ArgumentList $port, $log -ScriptBlock {
  param($port, $log)
  $l = [System.Net.HttpListener]::new(); $l.Prefixes.Add("http://localhost:$port/"); $l.Start()
  while ($l.IsListening) {
    $ctx = $l.GetContext(); $req = $ctx.Request; $res = $ctx.Response; $path = $req.Url.AbsolutePath
    if ($path -ne '/favicon.ico') { Add-Content $log $path }
    if ($path -eq '/') { $html = '<html><head><title>page one</title></head><body style="margin:0"><a href="/p2" style="display:block;width:700px;height:120px;background:#fc0;font-size:30px">go to page two</a></body></html>' }
    else { $html = "<html><head><title>page $path</title></head><body>$path</body></html>" }
    $b = [Text.Encoding]::UTF8.GetBytes($html); $res.ContentType = 'text/html'; $res.OutputStream.Write($b, 0, $b.Length); $res.Close()
  }
}
Start-Sleep 1
$env:LIGHTBROWSER_HOME = Join-Path $h 'home'; New-Item $env:LIGHTBROWSER_HOME -ItemType Directory | Out-Null
ConvertTo-Json -InputObject @(@{ Id='tab000000001'; Name='T'; Color='#4F8EF7'; Group=''; Notes=''; HomeUrls=@("http://localhost:$port/"); OpenUrls=@(); IsDeleted=$false }) -Depth 5 | Set-Content (Join-Path $env:LIGHTBROWSER_HOME 'profiles.json') -Encoding utf8
'{"SidebarCollapsed": false, "SidebarSetByUser": true, "DesktopShortcutOffered": true, "Mode": "Balanced"}' | Set-Content (Join-Path $env:LIGHTBROWSER_HOME 'settings.json')

function By-Id($p, $id) { [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle).FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))) }
function Tabs($p) { $list = By-Id $p 'TabList'; @($list.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)) | ForEach-Object { $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } }
function Count($p) { @(Tabs $p).Count }
function Selected($p) { $i = 0; foreach ($s in (Tabs $p)) { if ($s) { return $i }; $i++ }; -1 }
function Keys($k) { [Mouse]::SetForegroundWindow($p.MainWindowHandle) | Out-Null; [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 900 }

try {
  $p = Start-Process $Exe -PassThru; Start-Sleep 10
  (By-Id $p 'ProfileList').FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep 9
  [Mouse]::SetForegroundWindow($p.MainWindowHandle) | Out-Null; Start-Sleep 1
  $addr = (By-Id $p 'Address').Current.BoundingRectangle
  $x = [int]($addr.X + 150); $y = [int]($addr.Y + $addr.Height + 70)   # inside the big link at the top of the page

  Check 'one tab at the start' ((Count $p) -eq 1) (Count $p)
  'Ctrl+click on a link'
  [Mouse]::Move($x, $y); Start-Sleep -Milliseconds 400; [Mouse]::Ctrl($true); [Mouse]::Click($false); [Mouse]::Ctrl($false); Start-Sleep 5
  Check 'it opened a second tab' ((Count $p) -eq 2) (Count $p)
  Check 'the first tab is still the one shown (background tab)' ((Selected $p) -eq 0) (Selected $p)
  Check 'the new tab loaded the link' ((Get-Content $log) -contains '/p2')
  'Middle-click on a link'
  [Mouse]::Move($x, $y); Start-Sleep -Milliseconds 300; [Mouse]::Click($true); Start-Sleep 5
  Check 'it opened a third tab, again in the background' (((Count $p) -eq 3) -and ((Selected $p) -eq 0)) "$(Count $p) tabs, shown $(Selected $p)"
  'Plain click on a link'
  [Mouse]::Move($x, $y); Start-Sleep -Milliseconds 300; [Mouse]::Click($false); Start-Sleep 4
  Check 'no new tab (the page navigates in place)' ((Count $p) -eq 3) (Count $p)
  'Double-click on the empty tab bar'
  $list = (By-Id $p 'TabList').Current.BoundingRectangle
  [Mouse]::Move([int]($list.X + $list.Width - 40), [int]($list.Y + $list.Height / 2)); Start-Sleep -Milliseconds 300; [Mouse]::Click($false); Start-Sleep -Milliseconds 80; [Mouse]::Click($false); Start-Sleep 4
  Check 'a new tab opened and is shown' (((Count $p) -eq 4) -and ((Selected $p) -eq 3)) "$(Count $p) tabs, shown $(Selected $p)"
  'Alt+Enter in the address bar'
  (By-Id $p 'Address').SetFocus(); Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait('^a'); [System.Windows.Forms.SendKeys]::SendWait("localhost:$port/alt"); Start-Sleep -Milliseconds 500
  [System.Windows.Forms.SendKeys]::SendWait('%{ENTER}'); Start-Sleep 5
  Check 'the address opened in a new tab' (((Count $p) -eq 5) -and ((Get-Content $log) -contains '/alt')) "$(Count $p) tabs"

  'Shortcut keys, typed into the address bar'
  (By-Id $p 'Address').SetFocus(); Start-Sleep -Milliseconds 400
  Keys '^+{TAB}'; Check 'Ctrl+Shift+Tab goes to the previous tab' ((Selected $p) -eq 3) (Selected $p)
  Keys '^{TAB}';  Check 'Ctrl+Tab goes to the next tab' ((Selected $p) -eq 4) (Selected $p)
  Keys '^{TAB}';  Check 'Ctrl+Tab wraps around to the first tab' ((Selected $p) -eq 0) (Selected $p)
  Keys '^3';      Check 'Ctrl+3 shows the third tab' ((Selected $p) -eq 2) (Selected $p)
  Keys '^9';      Check 'Ctrl+9 shows the last tab' ((Selected $p) -eq 4) (Selected $p)
  Keys '^w';      Check 'Ctrl+W closes the tab' ((Count $p) -eq 4) (Count $p)
  $before = @(Get-Content $log | Where-Object { $_ -eq '/alt' }).Count
  Keys '^+t'; Start-Sleep 3
  Check 'Ctrl+Shift+T reopens it, at the same address' (((Count $p) -eq 5) -and (@(Get-Content $log | Where-Object { $_ -eq '/alt' }).Count -gt $before)) "$(Count $p) tabs"

  'Shortcut keys, typed while a page has the focus'
  [Mouse]::Move($x, [int]($addr.Y + $addr.Height + 400)); Start-Sleep -Milliseconds 300; [Mouse]::Click($false); Start-Sleep -Milliseconds 600
  $now = Selected $p
  Keys '^{TAB}'; Check 'Ctrl+Tab works from inside a page' ((Selected $p) -ne $now) "still $(Selected $p)"
  Keys '^1';      Check 'Ctrl+1 works from inside a page' ((Selected $p) -eq 0) (Selected $p)
}
finally {
  if ($p) { try { $p.Kill() } catch {} }
  Start-Sleep 2
  Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue
  Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like "*$h*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep 2; Remove-Item $h -Recurse -Force -ErrorAction SilentlyContinue; $env:LIGHTBROWSER_HOME = $null
}
if ($script:fail -eq 0) { 'ALL TAB CHECKS PASSED' } else { "$($script:fail) TAB CHECK(S) FAILED"; exit 1 }
