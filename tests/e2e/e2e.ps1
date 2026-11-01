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
    $form = @'
<html><head><title>login form</title></head><body>
<input id="search" type="text" placeholder="search">
<form><input id="hid" type="text" style="display:none"><input id="user" type="email"><input id="pw" type="password"></form>
<script>
for (const id of ['user', 'pw', 'search', 'hid']) {
  const el = document.getElementById(id);
  ['input', 'change'].forEach(t => el.addEventListener(t, () => fetch('/fill?field=' + id + '&type=' + t + '&v=' + encodeURIComponent(el.value))));
}
</script></body></html>
'@
    while ($l.IsListening) {
        $ctx = $l.GetContext(); $req = $ctx.Request; $res = $ctx.Response
        Add-Content $log ('{0}|{1}' -f $req.Url.PathAndQuery, $req.Headers['Cookie'])
        if ($req.Url.AbsolutePath -eq '/login') {
            $res.StatusCode = 302
            $res.Headers.Add('Location', '/whoami?p=' + $req.QueryString['p'])
            $res.Headers.Add('Set-Cookie', 'session=' + $req.QueryString['acct'] + '; Max-Age=3600; Path=/')
        } elseif ($req.Url.AbsolutePath -eq '/form') {
            $b = [Text.Encoding]::UTF8.GetBytes($form)
            $res.ContentType = 'text/html'; $res.OutputStream.Write($b, 0, $b.Length)
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
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class WinFind {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    public static IntPtr Find(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { var s = new StringBuilder(256); GetWindowText(h, s, 256); if (s.ToString() == title) { found = h; return false; } } return true; }, IntPtr.Zero);
        return found;
    }
}
"@
function Wait-Window($title, $sec = 12) {
    $t = Get-Date
    while (((Get-Date) - $t).TotalSeconds -lt $sec) {
        $h = [WinFind]::Find([uint32]$script:app.Id, $title)
        if ($h -ne [IntPtr]::Zero) { return [System.Windows.Automation.AutomationElement]::FromHandle($h) }
        Start-Sleep -Milliseconds 200
    }
    $null
}
function Find-ByName($root, $name) {
    $root.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
}
# Clicks a button that opens a modal dialog without blocking the script.
function Invoke-Async($element) {
    $ps = [powershell]::Create()
    [void]$ps.AddScript({ param($pattern) $pattern.Invoke() }).AddArgument($element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern))
    $null = $ps.BeginInvoke()
}
function Get-Edits($root) {
    $root.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
}
function Get-Stats {
    $win = [System.Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle)
    $e = $win.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'StatsText')))
    $e.Current.Name
}
function Click-Button($name) {
    $win = [System.Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle)
    $b = $win.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
}
function Settings-Mode { (Get-Content (Join-Path $home1 'settings.json') -Raw | ConvertFrom-Json).Mode }
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
    # Client C has four home pages, used by the modes check.
    $profiles += @{ Id = 'e2eccccccccc'; Name = 'Client C (four tabs)'; Color = '#F5A442'; HomeUrls = @(1..4 | ForEach-Object { "$origin/whoami?p=C$_" }); OpenUrls = @(); Group = ''; Notes = ''; IsDeleted = $false }
    $profiles += @{ Id = 'e2eddddddddd'; Name = 'Client D (login form)'; Color = '#A06CF0'; HomeUrls = @("$origin/form"); OpenUrls = @(); Group = ''; Notes = ''; IsDeleted = $false }
    # Client E goes through a proxy that wants a login (checked in phase 8); client F connects directly.
    # The proxy password is stored the way the app stores it: DPAPI, bound to the profile id.
    Add-Type -AssemblyName System.Security
    $pport = Get-Random -Minimum 40001 -Maximum 50000
    $plog = Join-Path $home1 'proxy.log'
    $entropy = [Text.Encoding]::UTF8.GetBytes('LightBrowser.proxy.v1.e2eeeeeeeeee')
    $proxyPassword = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('px-pass-1'), $entropy, 'CurrentUser'))
    $profiles += @{ Id = 'e2eeeeeeeeee'; Name = 'Client E (proxy)'; Color = '#E5575B'; HomeUrls = @('http://ota.test/whoami?p=E'); OpenUrls = @(); Group = ''; Notes = ''; IsDeleted = $false;
                    Proxy = "http://127.0.0.1:$pport"; ProxyUser = 'pxuser'; ProxyPasswordProtected = $proxyPassword }
    $profiles += @{ Id = 'e2effffffffff'; Name = 'Client F (direct)'; Color = '#2BB5B5'; HomeUrls = @("$origin/whoami?p=F"); OpenUrls = @(); Group = ''; Notes = ''; IsDeleted = $false }
    ConvertTo-Json -InputObject $profiles -Depth 5 | Set-Content (Join-Path $home1 'profiles.json') -Encoding utf8
    "# test list`r`ntracker.test" | Set-Content (Join-Path $home1 'blocklist.txt') -Encoding ascii   # blocked in phase 8

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

    '5. Modes (Low RAM / Balanced / Fast)'
    Stop-App
    '{ "Mode": "Balanced" }' | Set-Content (Join-Path $home1 'settings.json') -Encoding utf8
    Remove-Item $log -ErrorAction SilentlyContinue
    Start-App                       # profile A: 1 tab
    Select-Profile 2                # profile C: 4 home pages, all loaded
    $stats = Get-Stats
    Check 'Balanced mode shows 5 of 5 tabs loaded' ($stats -like 'Balanced | 5/5 tabs*') $stats
    Click-Button 'Low RAM'
    Check 'choosing Low RAM is saved to settings.json at once' ((Settings-Mode) -eq 'LowMemory') (Settings-Mode)
    Start-Sleep -Seconds 4          # an explicit mode change trims at once (no waiting for the idle timer)
    $stats = Get-Stats
    Check 'Low RAM unloaded tabs down to its limit of 2, live, without a restart' ($stats -like 'Low RAM | 2/2 tabs*') $stats
    Check 'unloading did not trigger any login or reload' ((Requests '/login*').Count -eq 0)
    Click-Button 'Fast'
    Check 'choosing Fast is saved' ((Settings-Mode) -eq 'Performance') (Settings-Mode)
    Check 'Fast shows its higher limit' ((Get-Stats) -like 'Fast | */15 tabs*') (Get-Stats)
    Stop-App
    Start-App
    Check 'the chosen mode is remembered after a restart' ((Get-Stats) -like 'Fast |*') (Get-Stats)

    '6. Password vault: add a login, fill the page, per-profile isolation'
    Remove-Item $log -ErrorAction SilentlyContinue
    Select-Profile 3                # profile D: a page with a decoy search box, a hidden field and a login form
    $win = [System.Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle)
    Invoke-Async (Find-ByName $win 'Passwords')
    $vault = Wait-Window 'Passwords - Client D (login form)'
    Check 'the Passwords dialog opens for this profile' ($null -ne $vault)
    Invoke-Async (Find-ByName $vault 'Add...')
    $entry = Wait-Window 'Add password'
    Check 'the Add password dialog opens' ($null -ne $entry)
    $edits = Get-Edits $entry
    $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('localhost')
    $edits[1].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('client.d@example.com')
    (Find-ByName $entry 'Generate').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    $edits = Get-Edits $entry
    $generated = $edits[2].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    Check 'a strong password was generated' ($generated.Length -eq 18) $generated
    (Find-ByName $entry 'Save').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 2
    $file = Join-Path $home1 'vault\e2eddddddddd.vault'
    Check 'the vault file exists and is encrypted' ((Test-Path $file) -and ((Get-Content $file -Raw) -notmatch 'client\.d|localhost'))
    Check 'only this profile has a vault' ((Get-ChildItem (Join-Path $home1 'vault') -Filter '*.vault').Count -eq 1)
    $vault = [System.Windows.Automation.AutomationElement]::FromHandle([WinFind]::Find([uint32]$script:app.Id, 'Passwords - Client D (login form)'))
    (Find-ByName $vault 'Fill page').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 3
    $fills = @(Requests '/fill?*')
    $user = $fills | Where-Object { $_ -like '/fill?field=user&type=input*' } | Select-Object -Last 1
    $pw = $fills | Where-Object { $_ -like '/fill?field=pw&type=input*' } | Select-Object -Last 1
    Check 'the login field received the username' ($user -and [uri]::UnescapeDataString(($user -split '&v=')[1].Split('|')[0]) -eq 'client.d@example.com') $user
    Check 'the password field received the saved password' ($pw -and [uri]::UnescapeDataString(($pw -split '&v=')[1].Split('|')[0]) -eq $generated) $pw
    Check 'the decoy search box and hidden field were left alone' (@($fills | Where-Object { $_ -like '/fill?field=search*' -or $_ -like '/fill?field=hid*' }).Count -eq 0)
    Check 'the page was told about the change (input and change events)' (@($fills | Where-Object { $_ -like '/fill?field=pw&type=change*' }).Count -ge 1)
    Select-Profile 0                # another profile has its own, empty list
    Invoke-Async (Find-ByName ([System.Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle)) 'Passwords')
    $other = Wait-Window 'Passwords - Client A (alice)'
    Check "another profile's password list is empty" ($null -ne $other -and (Find-ByName $other 'No passwords yet. Click Add...'))
    (Find-ByName $other 'Close').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 1

    '7. Hide and show the profile panel'
    function Main-Win { [System.Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle) }
    function Find-ById($root, $id) { $root.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))) }
    function Sidebar-Collapsed { (Get-Content (Join-Path $home1 'settings.json') -Raw | ConvertFrom-Json).SidebarCollapsed }
    Check 'the profile panel is visible at first' ($null -ne (Find-ById (Main-Win) 'SearchBox'))
    (Find-ByName (Main-Win) 'Toggle profile panel').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 2
    Check 'hiding it removes the panel and is saved' (($null -eq (Find-ById (Main-Win) 'SearchBox')) -and ((Sidebar-Collapsed) -eq $true))
    $chip = Find-ByName (Main-Win) 'Switch profile'
    $chipText = @($chip.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name })
    Check 'a toolbar chip shows which profile is active' ($null -ne $chip -and ($chipText -join '|') -like '*Client A (alice)*') ($chipText -join '|')
    Stop-App
    Start-App
    Check 'the hidden panel is remembered after a restart' (($null -eq (Find-ById (Main-Win) 'SearchBox')) -and ($null -ne (Find-ByName (Main-Win) 'Switch profile')))
    (Find-ByName (Main-Win) 'Toggle profile panel').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 2
    Check 'showing it again restores the panel' (($null -ne (Find-ById (Main-Win) 'SearchBox')) -and ((Sidebar-Collapsed) -eq $false))

    '8. Per-profile proxy: one profile through an authenticating proxy, another direct'
    # A small proxy that demands a login: 407 until the right Proxy-Authorization arrives, then a page.
    $proxyJob = Start-Job -ArgumentList $pport, $plog -ScriptBlock {
        param($pport, $plog)
        $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $pport); $l.Start()
        while ($true) {
            $c = $l.AcceptTcpClient()
            try {
                $s = $c.GetStream(); $s.ReadTimeout = 3000
                $buf = New-Object byte[] 8192; $head = ''
                while ($head -notmatch "`r`n`r`n") {
                    $n = $s.Read($buf, 0, $buf.Length); if ($n -le 0) { break }
                    $head += [Text.Encoding]::ASCII.GetString($buf, 0, $n)
                }
                if ($head -ne '') {
                    $first = ($head -split "`r`n")[0]
                    $auth = ''; if ($head -match '(?im)^Proxy-Authorization: (.+)$') { $auth = $Matches[1].Trim() }
                    Add-Content $plog "$first|$auth"
                    if ($first -like 'CONNECT*') { $r = "HTTP/1.1 502 Bad Gateway`r`nContent-Length: 0`r`nConnection: close`r`n`r`n" }
                    elseif ($auth -eq '') { $r = "HTTP/1.1 407 Proxy Authentication Required`r`nProxy-Authenticate: Basic realm=`"e2e`"`r`nContent-Length: 0`r`nConnection: close`r`n`r`n" }
                    else {
                        $body = '<html><head><title>via proxy</title></head><body>ok<img src="http://tracker.test/px.gif"><img src="http://allowed.test/px.gif"><img src="http://pagead.doubleclick.net/px.gif"></body></html>'
                        $r = "HTTP/1.1 200 OK`r`nContent-Type: text/html`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n$body"
                    }
                    $out = [Text.Encoding]::ASCII.GetBytes($r); $s.Write($out, 0, $out.Length)
                }
            } catch { } finally { $c.Close() }
        }
    }
    Start-Sleep 1
    Remove-Item $log -ErrorAction SilentlyContinue
    Select-Profile 4                # Client E
    # The page's images load after the page; wait for the last one that is allowed through (slow machines need a while).
    $t = Get-Date
    while (((Get-Date) - $t).TotalSeconds -lt 30 -and @(Get-Content $plog -ErrorAction SilentlyContinue | Where-Object { $_ -like '*allowed.test*' }).Count -eq 0) { Start-Sleep 1 }
    Start-Sleep 3                   # so a request that should have been blocked would have shown up by now
    $expected = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('pxuser:px-pass-1'))
    $viaProxy = @(Get-Content $plog -ErrorAction SilentlyContinue | Where-Object { $_ -like 'GET http://ota.test/whoami?p=E *' })
    Check "E's page request went to the proxy" ($viaProxy.Count -ge 1) ($viaProxy -join ';')
    Check "the proxy was given E's login (after its 407 challenge)" (@($viaProxy | Where-Object { $_.EndsWith('|' + $expected) }).Count -ge 1) ($viaProxy -join ';')
    Check "E's traffic never reached the site directly" ((Requests '*p=E*').Count -eq 0)
    $engine = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like '*WebView2Data-px-*' -and $_.CommandLine -like '*--proxy-server=*' -and $_.CommandLine -notlike '*--type=*' })
    Check 'the proxied profile runs in its own engine, started with the proxy' ($engine.Count -ge 1)
    Check 'the proxy password is not stored as plain text' ((Get-Content (Join-Path $home1 'profiles.json') -Raw) -notmatch 'px-pass-1')
    $seen = @(Get-Content $plog | Where-Object { $_ -like '*://*' })
    Check 'a normal third-party image was requested' (@($seen | Where-Object { $_ -like '*allowed.test*' }).Count -ge 1) ($seen -join ';')
    Check 'a domain from blocklist.txt was blocked before it left the browser' (@($seen | Where-Object { $_ -like '*tracker.test*' }).Count -eq 0)
    Check 'a built-in ad domain was blocked' (@($seen | Where-Object { $_ -like '*doubleclick.net*' }).Count -eq 0)
    function Block-Setting { (Get-Content (Join-Path $home1 'settings.json') -Raw | ConvertFrom-Json).BlockTrackers }
    $blockBtn = Find-ByName (Main-Win) 'Blocking'
    Check 'the Blocking button is in the toolbar' ($null -ne $blockBtn)
    $blockBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1
    Check 'turning blocking off is saved' ((Block-Setting) -eq $false) (Block-Setting)
    $blockBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1
    Check 'turning it back on is saved' ((Block-Setting) -eq $true) (Block-Setting)
    Select-Profile 5                # Client F
    Check "F connected directly to the site" ((Requests '/whoami?p=F|*').Count -ge 1)
    Check "F's traffic did not go through E's proxy" (@(Get-Content $plog | Where-Object { $_ -like '*p=F*' }).Count -eq 0)
    Check 'the proxy never saw the site that F visited' (@(Get-Content $plog | Where-Object { $_ -like "*localhost:$port*" }).Count -eq 0)
}
finally {
    Stop-App
    if ($proxyJob) { Stop-Job $proxyJob -ErrorAction SilentlyContinue; Remove-Job $proxyJob -Force -ErrorAction SilentlyContinue }
    Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like "*$home1*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep 2
    Remove-Item $home1 -Recurse -Force -ErrorAction SilentlyContinue
    $env:LIGHTBROWSER_HOME = $null
}
if ($script:fail -eq 0) { 'ALL E2E CHECKS PASSED' } else { "$($script:fail) E2E CHECK(S) FAILED"; exit 1 }


