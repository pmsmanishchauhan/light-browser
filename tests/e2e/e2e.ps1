# End-to-end checks against the real app, on a throwaway data folder (LIGHTBROWSER_HOME), so your own
# profiles are never touched. A tiny local web server plays the "OTA site": /login?acct=..&p=.. sets a
# persistent session cookie and redirects to /whoami, and every request is logged with its Cookie header.
# That log is the evidence for session isolation, persistence, resource management and crash recovery.
#
#   powershell -File tests\e2e\e2e.ps1 [-Exe path\to\LightBrowser.exe]
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\bin\Release\net8.0-windows\LightBrowser.exe'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$Exe = (Resolve-Path $Exe).Path
$home1 = Join-Path $env:TEMP ('lb-e2e-' + [guid]::NewGuid().ToString('N'))
New-Item $home1 -ItemType Directory | Out-Null
$env:LIGHTBROWSER_HOME = $home1
$log = Join-Path $home1 'requests.log'
$port = Get-Random -Minimum 20000 -Maximum 40000
$origin = "http://localhost:$port"
$script:fail = 0
$script:app = $null

$server = Start-Job -ArgumentList $port, $log -ScriptBlock {
    param($port, $log)
    $l = [System.Net.HttpListener]::new(); $l.Prefixes.Add("http://localhost:$port/"); $l.Start()
    while ($l.IsListening) {
        $ctx = $l.GetContext(); $req = $ctx.Request; $res = $ctx.Response
        Add-Content $log ('{0}|{1}' -f $req.Url.PathAndQuery, $req.Headers['Cookie'])
        if ($req.Url.AbsolutePath -eq '/login') {
            $res.StatusCode = 302
            $res.Headers.Add('Location', '/whoami?p=' + $req.QueryString['p'])
            $res.Headers.Add('Set-Cookie', 'session=' + $req.QueryString['acct'] + '; Max-Age=3600; Path=/')
        } else {
            $b = [Text.Encoding]::UTF8.GetBytes('<html><head><title>whoami ' + $req.QueryString['p'] + '</title></head><body>ok</body></html>')
            $res.ContentType = 'text/html'; $res.OutputStream.Write($b, 0, $b.Length)
        }
        $res.Close()
    }
}

function Check($name, $ok, $detail = '') {
    if ($ok) { "  PASS  $name" } else { "  FAIL  $name  $detail"; $script:fail++ }
}
function Requests($pattern) {
    if (-not (Test-Path $log)) { return @() }
    @(Get-Content $log | Where-Object { $_ -like $pattern })
}
function Start-App {
    $script:app = Start-Process $Exe -PassThru
    Start-Sleep -Seconds 9
}
function Stop-App {
    if ($script:app -and -not $script:app.HasExited) {
        $script:app.CloseMainWindow() | Out-Null; Start-Sleep 4
        if (-not $script:app.HasExited) { $script:app.Kill() }
    }
    Start-Sleep 2
}
function Select-Profile($index) {
    $win = [System.Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle)
    $list = $win.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ProfileList')))
    $items = $list.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)
    $items[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 8
}
function Browser-Processes($type) {
    Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object {
        $_.CommandLine -like "*$home1*" -and (($type -eq 'renderer') -eq ($_.CommandLine -like '*--type=renderer*')) -and
        ($type -eq 'renderer' -or $_.CommandLine -notlike '*--type=*')
    }
}

try {
    # Two client profiles on the SAME website, different accounts.
    $profiles = @(
        @{ Id = 'e2eaaaaaaaaa'; Name = 'Client A (alice)'; Color = '#4F8EF7'; HomeUrls = @("$origin/login?acct=alice&p=A"); OpenUrls = @(); Group = ''; Notes = ''; IsDeleted = $false },
        @{ Id = 'e2ebbbbbbbbb'; Name = 'Client B (bob)';   Color = '#3DBE7A'; HomeUrls = @("$origin/login?acct=bob&p=B");   OpenUrls = @(); Group = ''; Notes = ''; IsDeleted = $false }
    )
    ConvertTo-Json -InputObject $profiles -Depth 5 | Set-Content (Join-Path $home1 'profiles.json') -Encoding utf8

    '1. Two profiles, same site, different accounts, at the same time'
    Start-App                      # profile A opens first and runs its home page (login, then redirect)
    Select-Profile 1               # then profile B
    $a = @(Requests '/whoami?p=A|*'); $b = @(Requests '/whoami?p=B|*')
    Check 'A logged in as alice'                    ($a.Count -ge 1 -and ($a[0] -eq '/whoami?p=A|session=alice')) ($a -join ';')
    Check 'B logged in as bob'                      ($b.Count -ge 1 -and ($b[0] -eq '/whoami?p=B|session=bob')) ($b -join ';')
    $loginB = @(Requests '/login?acct=bob*')
    Check "B's login request carried no cookie from A (isolated sessions)" ($loginB.Count -ge 1 -and ($loginB[0] -eq '/login?acct=bob&p=B|')) ($loginB -join ';')
    Check 'only the site origin was saved as a suggestion' ((Get-Content (Join-Path $home1 'suggestions.json') -Raw) -match [regex]::Escape($origin) -and (Get-Content (Join-Path $home1 'suggestions.json') -Raw) -notmatch 'whoami')
    Stop-App
    $saved = Get-Content (Join-Path $home1 'profiles.json') -Raw | ConvertFrom-Json
    Check 'open tabs were saved for both profiles' (($saved | Where-Object { $_.OpenUrls.Count -ge 1 }).Count -eq 2)
    Check 'last-used time was recorded' (@($saved | Where-Object { $_.LastUsed }).Count -eq 2)

    '2. After restarting the app: logins and tabs restored'
    Remove-Item $log -ErrorAction SilentlyContinue
    Start-App
    $a = @(Requests '/whoami?p=A|*')
    Check 'A restored its tab and is still logged in as alice' ($a.Count -ge 1 -and ($a[0] -eq '/whoami?p=A|session=alice')) ($a -join ';')
    Select-Profile 1
    $b = @(Requests '/whoami?p=B|*')
    Check 'B restored its tab and is still logged in as bob' ($b.Count -ge 1 -and ($b[0] -eq '/whoami?p=B|session=bob')) ($b -join ';')
    Check 'no new login was needed' ((Requests '/login*').Count -eq 0)
    Stop-App

    '3. Resource limit (1 loaded tab) does not log anyone out'
    '{ "MaxLoadedTabs": 1, "IdleMinutes": 60, "GraceSeconds": 0 }' | Set-Content (Join-Path $home1 'settings.json') -Encoding utf8
    Remove-Item $log -ErrorAction SilentlyContinue
    Start-App                      # A loaded
    Select-Profile 1               # B loaded, A unloaded (cap)
    Select-Profile 0               # A clicked again: reloads
    $a = @(Requests '/whoami?p=A|*')
    Check 'A tab was unloaded and reloaded still as alice' ($a.Count -ge 2 -and ($a | Where-Object { $_ -ne '/whoami?p=A|session=alice' }).Count -eq 0) ($a -join ';')
    Check 'no re-login happened' ((Requests '/login*').Count -eq 0)

    '4. Crash recovery'
    $before = (Requests '/whoami?p=A|*').Count
    Browser-Processes 'renderer' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 8
    $script:app.Refresh()
    Check 'app survived a renderer crash' (-not $script:app.HasExited -and $script:app.Responding)
    Check 'crashed tab reloaded itself, still logged in' ((Requests '/whoami?p=A|*').Count -gt $before -and (Requests '/whoami?p=A|*')[-1] -eq '/whoami?p=A|session=alice')
    $before = (Requests '/whoami?p=A|*').Count
    $browser = @(Browser-Processes 'browser')
    $browser | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 14
    $script:app.Refresh()
    Check "killed the browser process ($($browser.Count) found)" ($browser.Count -ge 1)
    Check 'app survived a browser-process crash' (-not $script:app.HasExited -and $script:app.Responding)
    Check 'tabs were rebuilt and the page reloaded, still logged in' ((Requests '/whoami?p=A|*').Count -gt $before -and (Requests '/whoami?p=A|*')[-1] -eq '/whoami?p=A|session=alice')
}
finally {
    Stop-App
    Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like "*$home1*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep 2
    Remove-Item $home1 -Recurse -Force -ErrorAction SilentlyContinue
    $env:LIGHTBROWSER_HOME = $null
}
if ($script:fail -eq 0) { 'ALL E2E CHECKS PASSED' } else { "$($script:fail) E2E CHECK(S) FAILED"; exit 1 }


