# Opens a list of addresses in LightBrowser's own engine and reports which ones work.
#   powershell -File tools\check-sites.ps1                      # the built-in OTA portals and search engines
#   powershell -File tools\check-sites.ps1 -List sites.json     # your own list [ { "Name": "...", "Url": "https://..." } ]
# Add -Out results.json to keep the raw results. Runs on a throwaway data folder, so your profiles are untouched.
# "login page" means a visible password field was found. This loads pages; it cannot sign in.
param([string]$List = 'catalog', [string]$Out = '', [string]$Exe = '', [int]$TimeoutMinutes = 15)

$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\bin\Release\net8.0-windows\LightBrowser.exe' }
if ($List -ne 'catalog') { $List = (Resolve-Path $List).Path }
$Exe = (Resolve-Path $Exe).Path
$home1 = Join-Path $env:TEMP ('lb-check-' + [guid]::NewGuid().ToString('N'))
New-Item $home1 -ItemType Directory | Out-Null
$result = if ($Out) { $Out } else { Join-Path $home1 'results.json' }
$env:LIGHTBROWSER_HOME = $home1
try {
    $p = Start-Process $Exe -ArgumentList '--check-sites', "`"$List`"", "`"$result`"" -PassThru
    $t = Get-Date
    while (-not $p.HasExited -and ((Get-Date) - $t).TotalMinutes -lt $TimeoutMinutes) { Start-Sleep 2 }
    if (-not $p.HasExited) { $p.Kill(); throw "timed out after $TimeoutMinutes minutes" }
    if (Test-Path "$result.error") { throw (Get-Content "$result.error" -Raw) }
    $rows = Get-Content $result -Raw | ConvertFrom-Json
    foreach ($r in $rows) {
        $verdict =
            if (-not $r.Loaded) { 'FAILED ' + $r.Error }
            elseif ($r.Flag) { 'LOADED, ' + $r.Flag }
            elseif ($r.PasswordField) { 'OK, login page' }
            elseif ($r.LoginField) { 'OK, login field' }
            else { 'LOADED, no login form seen' }
        '{0,-34} {1,3} {2,5} ms  {3}  -> {4}' -f $r.Name, $r.Status, $r.Ms, $verdict, $r.FinalUrl
    }
} finally {
    Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like "*$home1*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep 2
    if (-not $Out) { Remove-Item $home1 -Recurse -Force -ErrorAction SilentlyContinue }
    $env:LIGHTBROWSER_HOME = $null
}
