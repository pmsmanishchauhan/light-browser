# Checks that a sign-in made with a session cookie (no expiry, like most extranets) survives closing every page of a
# profile and restarting the app. A local server stands in for the site.
#
#   powershell -File tests\e2e\signin.ps1 [-Exe path\to\LightBrowser.exe]
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\bin\Debug\net8.0-windows\LightBrowser.exe'))
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public class WinFocus { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
"@
$script:fail = 0
function Check($name, $ok, $detail = '') { if ($ok) { "  PASS  $name" } else { "  FAIL  $name  $detail"; $script:fail++ } }

$tmp = Join-Path $env:TEMP ('lb-signin-' + [guid]::NewGuid().ToString('N')); New-Item $tmp -ItemType Directory | Out-Null
$log = Join-Path $tmp 'server.log'; New-Item $log -ItemType File | Out-Null
$port = Get-Random -Minimum 20000 -Maximum 40000
$server = Start-Job -ArgumentList $port, $log -ScriptBlock {
  param($port, $log)
  $l = [System.Net.HttpListener]::new(); $l.Prefixes.Add("http://localhost:$port/"); $l.Start()
  while ($l.IsListening) {
    $ctx = $l.GetContext(); $req = $ctx.Request; $res = $ctx.Response
    if ($req.Url.AbsolutePath -ne '/favicon.ico') { Add-Content $log ('{0}|{1}' -f $req.Url.AbsolutePath, $req.Headers['Cookie']) }
    if ($req.Url.AbsolutePath -eq '/login') { $res.Headers.Add('Set-Cookie', 'session=alice; Path=/'); $res.StatusCode = 302; $res.Headers.Add('Location', '/whoami') }
    else { $b = [Text.Encoding]::UTF8.GetBytes('<html><title>whoami</title><body>hello</body></html>'); $res.ContentType = 'text/html'; $res.OutputStream.Write($b, 0, $b.Length) }
    $res.Close()
  }
}
Start-Sleep 1
$env:LIGHTBROWSER_HOME = Join-Path $tmp 'home'; New-Item $env:LIGHTBROWSER_HOME -ItemType Directory | Out-Null
ConvertTo-Json -InputObject @(@{ Id='ses000000001'; Name='Expedia A'; Color='#4F8EF7'; Group=''; Notes=''; HomeUrls=@("http://localhost:$port/login"); OpenUrls=@(); IsDeleted=$false }) -Depth 5 | Set-Content (Join-Path $env:LIGHTBROWSER_HOME 'profiles.json') -Encoding utf8
'{"SidebarCollapsed": false, "SidebarSetByUser": true, "DesktopShortcutOffered": true}' | Set-Content (Join-Path $env:LIGHTBROWSER_HOME 'settings.json')

function By-Id($p, $id) { [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle).FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))) }
function Start-And-Pick {
  $p = Start-Process $Exe -PassThru
  $t = Get-Date; while (((Get-Date) - $t).TotalSeconds -lt 60 -and -not ($p.MainWindowHandle -ne 0 -and (By-Id $p 'ProfileList'))) { Start-Sleep -Milliseconds 500; $p.Refresh() }
  Start-Sleep 2
  (By-Id $p 'ProfileList').FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep 9
  $p
}
function Last-Whoami { (Get-Content $log | Where-Object { $_ -like '/whoami|*' } | Select-Object -Last 1) }
function Stop-Gracefully($p) { $p.CloseMainWindow() | Out-Null; Start-Sleep 5; if (-not $p.HasExited) { $p.Kill() }; Start-Sleep 2 }
try {
  'Sign in with a session cookie'
  $p = Start-And-Pick
  Check 'the site set the cookie and the next request carried it' ((Get-Content $log) -contains '/whoami|session=alice') ((Get-Content $log) -join '  ')

  'Close the profile''s only page, then open another'
  [WinFocus]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
  [System.Windows.Forms.SendKeys]::SendWait('^w'); Start-Sleep 3
  [System.Windows.Forms.SendKeys]::SendWait('^t'); Start-Sleep 3
  [System.Windows.Forms.SendKeys]::SendWait("http://localhost:$port/whoami{ENTER}"); Start-Sleep 6
  Check 'still signed in after every page of the profile was closed' ((Last-Whoami) -eq '/whoami|session=alice') (Last-Whoami)

  'Restart the app'
  Stop-Gracefully $p
  $p = Start-And-Pick
  Check 'still signed in after a restart' ((Last-Whoami) -eq '/whoami|session=alice') (Last-Whoami)
  Stop-Gracefully $p
}
finally {
  Get-Process LightBrowser -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force -ErrorAction SilentlyContinue
  Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue
  Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like "*$tmp*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep 2; Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue; $env:LIGHTBROWSER_HOME = $null
}
if ($script:fail -eq 0) { 'ALL SIGN-IN CHECKS PASSED' } else { "$($script:fail) SIGN-IN CHECK(S) FAILED"; exit 1 }
