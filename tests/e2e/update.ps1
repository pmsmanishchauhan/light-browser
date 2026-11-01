# End-to-end check of the Update button, against a local stand-in for GitHub (nothing real is contacted).
# Publishes the app twice (this version, and a fake 9.9.9), serves a fake "latest release" that points at the
# 9.9.9 exe, then drives the real app: check -> update available -> install -> restart as 9.9.9.
# Also checks that a download whose checksum does not match is refused and leaves the app untouched.
#
#   powershell -File tests\e2e\update.ps1
param([string]$Project = (Join-Path $PSScriptRoot '..\..\LightBrowser.csproj'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$Project = (Resolve-Path $Project).Path
$tmp = Join-Path $env:TEMP ('lb-upd-' + [guid]::NewGuid().ToString('N'))
New-Item $tmp -ItemType Directory | Out-Null
$script:fail = 0
$script:proc = $null

function Check($name, $ok, $detail = '') { if ($ok) { "  PASS  $name" } else { "  FAIL  $name  $detail"; $script:fail++ } }
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class WinFind2 {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    public static void Click(IntPtr h) { PostMessage(h, 0xF5, IntPtr.Zero, IntPtr.Zero); }   // BM_CLICK
    public static IntPtr Find(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { var s = new StringBuilder(256); GetWindowText(h, s, 256); if (s.ToString() == title) { found = h; return false; } } return true; }, IntPtr.Zero);
        return found;
    }
}
"@
function Prop($n) { [System.Windows.Automation.AutomationElement]::$n }
function By-Name($root, $name) { $root.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition((Prop 'NameProperty'), $name))) }
function Main-Win { [System.Windows.Automation.AutomationElement]::FromHandle($script:proc.MainWindowHandle) }
# Buttons here open modal dialogs, so the click must not block the script.
function Invoke-El($el) {
    $ps = [powershell]::Create()
    [void]$ps.AddScript({ param($pattern) $pattern.Invoke() }).AddArgument($el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern))
    $null = $ps.BeginInvoke()
}
function Update-Text {
    $b = By-Name (Main-Win) 'Update'
    (@($b.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition((Prop 'ControlTypeProperty'), [System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name }) -join ' ')
}
function Wait-Text($pattern, $sec = 30) {
    $t = Get-Date
    while (((Get-Date) - $t).TotalSeconds -lt $sec) { if ((Update-Text) -like $pattern) { return $true }; Start-Sleep -Milliseconds 400 }
    $false
}
function Wait-Dialog($title, $sec = 15) {
    $t = Get-Date
    while (((Get-Date) - $t).TotalSeconds -lt $sec) {
        $h = [WinFind2]::Find([uint32]$script:proc.Id, $title)
        if ($h -ne [IntPtr]::Zero) { return [System.Windows.Automation.AutomationElement]::FromHandle($h) }
        Start-Sleep -Milliseconds 300
    }
    $null
}
# A Win32 message box: its text is in "Static" panes and its buttons are plain "Button" windows (no Invoke pattern).
function Dialog-Text($dlg) {
    (@($dlg.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition((Prop 'ClassNameProperty'), 'Static'))) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' ')
}
function Press($dlg, $name) {
    $b = By-Name $dlg $name
    [WinFind2]::Click([IntPtr]$b.Current.NativeWindowHandle)
}
function Start-App($exe) { $script:proc = Start-Process $exe -PassThru; Start-Sleep -Seconds 9 }
function Stop-App {
    Get-Process LightBrowser -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$tmp*" } | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep 4
    Get-Process LightBrowser -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$tmp*" } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep 1
}
function Version-Of($exe) { ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0] }   # drop the "+commit" suffix

try {
    'Publishing two builds (this version and a fake 9.9.9)...'
    foreach ($v in @(@('pub1', ''), @('pub2', '-p:Version=9.9.9'))) {
        $args2 = @('publish', $Project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true',
                   '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', (Join-Path $tmp $v[0]), '--nologo', '-v', 'q')
        if ($v[1]) { $args2 += $v[1] }
        & dotnet @args2 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "publish failed ($($v[0]))" }
    }
    $v1 = Version-Of (Join-Path $tmp 'pub1\LightBrowser.exe'); $v2 = Version-Of (Join-Path $tmp 'pub2\LightBrowser.exe')
    "  built $v1 and $v2"

    # A stand-in for GitHub: /latest is the release JSON, /dl the new exe, /badlatest a release whose checksum is wrong.
    $newExe = Join-Path $tmp 'pub2\LightBrowser.exe'
    $sha = (Get-FileHash $newExe -Algorithm SHA256).Hash.ToLower()
    $size = (Get-Item $newExe).Length
    $port = Get-Random -Minimum 20000 -Maximum 40000
    $server = Start-Job -ArgumentList $port, $newExe, $sha, $size -ScriptBlock {
        param($port, $newExe, $sha, $size)
        $l = [System.Net.HttpListener]::new(); $l.Prefixes.Add("http://localhost:$port/"); $l.Start()
        function Json($digest) { '{"tag_name":"v9.9.9","html_url":"https://github.com/x/y/releases/tag/v9.9.9","prerelease":false,"draft":false,"assets":[{"name":"LightBrowser-small.exe","size":' + $size + ',"browser_download_url":"http://localhost:' + $port + '/dl","digest":"sha256:' + $digest + '"}]}' }
        while ($l.IsListening) {
            $ctx = $l.GetContext(); $res = $ctx.Response; $path = $ctx.Request.Url.AbsolutePath
            if ($path -eq '/dl') { $b = [IO.File]::ReadAllBytes($newExe); $res.ContentType = 'application/octet-stream' }
            elseif ($path -eq '/badlatest') { $b = [Text.Encoding]::UTF8.GetBytes((Json ('0' * 64))); $res.ContentType = 'application/json' }
            else { $b = [Text.Encoding]::UTF8.GetBytes((Json $sha)); $res.ContentType = 'application/json' }
            $res.ContentLength64 = $b.Length; $res.OutputStream.Write($b, 0, $b.Length); $res.Close()
        }
    }
    Start-Sleep 1

    $appDir = Join-Path $tmp 'app'; New-Item $appDir -ItemType Directory | Out-Null
    $exe = Join-Path $appDir 'LightBrowser.exe'
    $env:LIGHTBROWSER_HOME = Join-Path $tmp 'home'
    New-Item $env:LIGHTBROWSER_HOME -ItemType Directory | Out-Null

    '1. A download that does not match the published checksum is refused'
    Copy-Item (Join-Path $tmp 'pub1\LightBrowser.exe') $exe
    $env:LIGHTBROWSER_UPDATE_API = "http://localhost:$port/badlatest"
    Start-App $exe
    Check 'the button starts as a version label (nothing contacted yet)' ((Update-Text) -like "v$v1 - check for updates*") (Update-Text)
    Invoke-El (By-Name (Main-Win) 'Update')
    Check 'checking finds the newer release' (Wait-Text 'Update to v9.9.9') (Update-Text)
    Invoke-El (By-Name (Main-Win) 'Update')
    $ask = Wait-Dialog 'Update'
    Check 'it asks before installing' ($null -ne $ask -and (Dialog-Text $ask) -like '*v9.9.9*') ("dialog=" + $(if ($ask) { Dialog-Text $ask } else { 'none; button now says: ' + (Update-Text) }))
    if (-not $ask) { throw 'no confirmation dialog' }
    Press $ask 'Yes'
    $warn = Wait-Dialog 'Update'
    Check 'the bad download is refused with a warning' ($null -ne $warn -and (Dialog-Text $warn) -like '*checksum*') (Dialog-Text $warn)
    if ($warn) { Press $warn 'OK' }
    Start-Sleep 1
    Check 'the app is still running and still the old version' ((-not $script:proc.HasExited) -and ((Version-Of $exe) -eq $v1)) (Version-Of $exe)
    Check 'no half-downloaded file was left behind' (-not (Test-Path "$exe.new"))
    Stop-App

    '2. A matching download installs and restarts as the new version'
    $env:LIGHTBROWSER_UPDATE_API = "http://localhost:$port/latest"
    Start-App $exe
    Invoke-El (By-Name (Main-Win) 'Update')
    Check 'checking finds the newer release' (Wait-Text 'Update to v9.9.9') (Update-Text)
    Invoke-El (By-Name (Main-Win) 'Update')
    $ask = Wait-Dialog 'Update'
    $oldPid = $script:proc.Id
    Press $ask 'Yes'
    $t = Get-Date; $newProc = $null
    while (((Get-Date) - $t).TotalSeconds -lt 40 -and -not $newProc) {
        Start-Sleep -Milliseconds 500
        $newProc = Get-Process LightBrowser -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe -and $_.Id -ne $oldPid -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    }
    Check 'the old process exited' ($null -eq (Get-Process -Id $oldPid -ErrorAction SilentlyContinue))
    Check 'a new process started from the same exe' ($null -ne $newProc)
    Check "the exe on disk is now $v2" ((Version-Of $exe) -eq $v2) (Version-Of $exe)
    if ($newProc) {
        $script:proc = $newProc; Start-Sleep -Seconds 8
        Check 'the restarted app shows the new version' ((Update-Text) -like "v$v2 - check for updates*") (Update-Text)
    }
    Check 'the previous version was cleaned up' (-not (Test-Path "$exe.old"))
    Stop-App
}
finally {
    Stop-App
    if ($server) { Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like "*$tmp*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep 2
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    $env:LIGHTBROWSER_HOME = $null; $env:LIGHTBROWSER_UPDATE_API = $null
}
if ($script:fail -eq 0) { 'ALL UPDATE CHECKS PASSED' } else { "$($script:fail) UPDATE CHECK(S) FAILED"; exit 1 }
