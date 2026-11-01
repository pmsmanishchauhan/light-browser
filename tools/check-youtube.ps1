# Plays a YouTube video in LightBrowser's own engine (sound off) and reports whether ads got through.
#   powershell -File tools\check-youtube.ps1 [-Url https://www.youtube.com/watch?v=...] [-Seconds 45] [-NoBlock]
# Run it once normally and once with -NoBlock: if the -NoBlock run shows no ad either, YouTube is not serving ads to
# this machine right now and the blocked run proves nothing. Runs on a throwaway data folder.
#   powershell -File tools\check-youtube.ps1 -SelfTest         # no network: runs the script against made-up YouTube data and a made-up player
param([string]$Url = 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', [int]$Seconds = 45, [switch]$NoBlock, [switch]$SelfTest, [string]$Exe = '', [string]$Out = '')

$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\bin\Release\net8.0-windows\LightBrowser.exe' }
$Exe = (Resolve-Path $Exe).Path
if ($SelfTest) {
    $home1 = Join-Path $env:TEMP ('lb-yts-' + [guid]::NewGuid().ToString('N'))
    New-Item $home1 -ItemType Directory | Out-Null
    $result = Join-Path $home1 'selftest.json'
    $env:LIGHTBROWSER_HOME = $home1
    try {
        $p = Start-Process $Exe -ArgumentList '--check-youtube-selftest', "`"$result`"" -PassThru
        $t = Get-Date
        while (-not $p.HasExited -and ((Get-Date) - $t).TotalSeconds -lt 90) { Start-Sleep 1 }
        if (-not $p.HasExited) { $p.Kill(); throw 'timed out' }
        if (Test-Path "$result.error") { throw (Get-Content "$result.error" -Raw) }
        $rows = Get-Content $result -Raw | ConvertFrom-Json | ForEach-Object { $_ }
        foreach ($r in $rows) { if ($r.Ok) { '  PASS  ' + $r.Name } else { '  FAIL  ' + $r.Name + '  ' + $r.Detail } }
        $bad = @($rows | Where-Object { -not $_.Ok }).Count
        if ($bad -eq 0) { "ALL $(@($rows).Count) SELF-TEST CHECKS PASSED" } else { "$bad SELF-TEST CHECK(S) FAILED"; exit 1 }
    } finally {
        Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like "*$home1*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep 2
        Remove-Item $home1 -Recurse -Force -ErrorAction SilentlyContinue
        $env:LIGHTBROWSER_HOME = $null
    }
    return
}
$home1 = Join-Path $env:TEMP ('lb-yt-' + [guid]::NewGuid().ToString('N'))
New-Item $home1 -ItemType Directory | Out-Null
$result = if ($Out) { $Out } else { Join-Path $home1 'result.json' }
$env:LIGHTBROWSER_HOME = $home1
try {
    $argList = @('--check-youtube', "`"$Url`"", "`"$result`"", '--seconds', $Seconds)
    if ($NoBlock) { $argList += '--no-block' }
    $p = Start-Process $Exe -ArgumentList $argList -PassThru
    $t = Get-Date
    while (-not $p.HasExited -and ((Get-Date) - $t).TotalSeconds -lt ($Seconds + 90)) { Start-Sleep 2 }
    if (-not $p.HasExited) { $p.Kill(); throw 'timed out' }
    if (Test-Path "$result.error") { throw (Get-Content "$result.error" -Raw) }
    $r = Get-Content $result -Raw | ConvertFrom-Json
    "{0}  blocker={1}  title='{2}'" -f $r.Url, $r.Block, $r.Title
    "  ad shown: {0} ({1} of {2} samples, first at {3} ms), ad showing at the end: {4}" -f $r.AdSeen, $r.AdSamples, $r.Samples, $r.FirstAdMs, $r.AdSeenAtEnd
    "  video reached {0} s of its own content, enforcement pop-up: {1}, requests blocked: {2}" -f $r.ContentSecondsReached, $r.EnforcementPopup, $r.BlockedRequests
    "  script counters: $($r.FinalStats)"
} finally {
    Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like "*$home1*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep 2
    if (-not $Out) { Remove-Item $home1 -Recurse -Force -ErrorAction SilentlyContinue }
    $env:LIGHTBROWSER_HOME = $null
}
