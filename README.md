# LightBrowser

A small Windows browser for people who log in to the same websites with many different accounts, for example one OTA extranet login per hotel client. Each profile has its own cookies, logins and site data, so you can be signed in to the same site as several clients at once.

It is a thin shell (a few MB) around the Microsoft Edge WebView2 engine that ships with Windows 10/11, so it does not bundle a browser and stays light on memory. All profiles share one WebView2 environment; a profile only costs memory while it has open tabs.

## Features

- Unlimited profiles, each with isolated cookies, sessions, local storage and saved passwords.
- Logins persist across restarts.
- Tabs per profile. Restored tabs load lazily: only the first one opens, the rest load when clicked.
- Profile list with search (Ctrl+K), groups, colours, notes (shown on hover) and an "open" dot.
- Suggested sites: every site that loads successfully (HTTP 2xx) in any tab is remembered locally (origin only, e.g. `https://example.com`) and offered to all groups, as one-click chips in the profile dialog and as a dropdown in the address bar. Stored in `suggestions.json`, never uploaded.
- Close profile (right-click) to free its memory without losing logins; Restart profile; Duplicate (copies name, group, colour, notes and home pages, never logins); Clear website data (signs one profile out of every site, others untouched).
- Launch site in profiles: open one website in several profiles at once, or close several profiles, from one dialog. Profiles that are not on screen get an unloaded tab, so launching in 50 profiles does not load 50 pages.
- Group suggestions: the profile dialog shows two separate rows, "Suggested sites" (common, shared by all groups) and "Used by this group's other profiles".
- Last-used time per profile (hover over it), plus a diagnostics line in the sidebar: tabs loaded vs the limit, and memory in use.
- Crash recovery: a crashed page reloads itself (up to 3 times in a row); if the browser process dies, all tabs are rebuilt from their last address. Profiles and tab lists are saved every 30 seconds, written crash-safe (temp file, then swap, previous version kept as `.bak`); an unreadable file is moved aside, never overwritten.
- Trash with restore; "Delete forever" wipes the profile's data.
- Colour bar and window title show which client profile you are in.
- Popups (OAuth, login windows) open as tabs in the same profile and keep `window.opener`.
- Shortcuts: Ctrl+T new tab, Ctrl+W close tab, Ctrl+L address bar, Ctrl+K profile search, middle-click closes a tab.

## Memory

RAM stays bounded however many profiles and tabs exist. Only `MaxLoadedTabs` tabs (default 5, across all profiles) are ever loaded; the oldest hidden tab is unloaded when you go over, and a hidden tab nobody looked at for `IdleMinutes` (default 10) is unloaded too. An unloaded tab keeps its title and address, costs almost nothing, and reloads when clicked (still logged in; cookies are on disk). Hidden tabs are also put in WebView2's low-memory mode. The visible tab, tabs playing audio, loading tabs, and tabs used in the last `GraceSeconds` (default 120, protects login popups) are never unloaded.

Change the limits in `%LocalAppData%\LightBrowser\settings.json` and restart:

```json
{ "MaxLoadedTabs": 5, "IdleMinutes": 10, "GraceSeconds": 120 }
```

An unloaded tab loses unsaved page state (text typed into a form, scroll position), so finish forms before leaving a tab for a long time.

## Modes

Three buttons at the bottom of the sidebar switch how much memory the browser may use, live, with no restart:

| Mode | Tabs loaded at once | Unused tab unloaded after | Hidden tabs |
|---|---|---|---|
| Low RAM | 2 | 3 min | trimmed |
| Balanced (default) | 5 | 10 min | trimmed |
| Fast | 15 | 60 min | not trimmed |

Your choice is remembered. Advanced: set `"Mode": "Custom"` in `settings.json` to use your own `MaxLoadedTabs`, `IdleMinutes` and `GraceSeconds`. `BrowserArguments` passes extra Chromium switches to the engine; in a test with six real sites, `--disable-gpu` cut total memory by about 25-30% (roughly 300 MB to 225 MB) at the cost of a little more CPU (about 1% to 3% of a core when idle), so it can suit a weak PC. It is off by default.

## History, passwords and names

- **History** (Ctrl+H or the clock button): each profile has its own local history with search. Stored per profile in `history\`, web pages only, no fragments or credentials in addresses, newest 2000 kept, never uploaded.
- **Passwords** (button in the toolbar, or right-click a profile): each profile has its own password list with add, edit, delete, copy (clipboard cleared after 30 seconds), a generator, and "Fill page" for the login form on screen. Encrypted with Windows DPAPI for your Windows account and bound to its profile, stored in `vault\`. The engine's own "Save password?" prompt still works and is also per profile, but it cannot be listed, which is why this list exists. Fill works on forms in the main page, not inside frames.
- **Rename**: F2, double-click a profile, or the pencil button on hover. The trash button on hover moves a profile to Trash (restore from Trash).
- **Suggested names** in the new-profile dialog come from the group, the first home page and the names already used.

## Run

Download an exe from [Releases](../../releases):

| File | Size | Needs |
|---|---|---|
| `LightBrowser-small.exe` | about 1 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| `LightBrowser-standalone.exe` | about 69 MB | nothing extra |

Both need the WebView2 Runtime, which is already part of Windows 11 and current Windows 10.

The exes are not code-signed, so Windows SmartScreen may warn on first run.

## Build

Requires the .NET 8 SDK.

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\small
```

## Where data is stored

`%LocalAppData%\LightBrowser`: `profiles.json` (names, groups, notes, saved tab list) and `WebView2Data` (cookies, logins, saved passwords, encrypted by Windows for your user account). Back up that folder to keep your logins.

## How a website is "verified" as a suggestion

A site is saved to the common suggestions only when its top-level page finishes loading with a final HTTP status of 200-299 (after redirects). Network, TLS or timeout errors, aborted loads, and 3xx/4xx/5xx responses (including 401/403 "login required") are not verification. Only the origin (`https://host`) is stored, never the path or query string. Duplicates (any capitalisation) are ignored; the list holds the newest 200.

## Tests

```powershell
dotnet test tests\LightBrowser.Tests                  # unit tests (storage, recovery, suggestions, groups)
powershell -File tests\e2e\e2e.ps1                    # end-to-end against the real app
```

The end-to-end script runs the app on a throwaway data folder (`LIGHTBROWSER_HOME`) against a local test server and checks session isolation between two profiles on the same site, login persistence across restarts, that the tab limit does not log anyone out, and renderer/browser crash recovery. Set `LIGHTBROWSER_HOME` yourself to run LightBrowser from a different data folder (for example a USB stick).

## Limits

- This is not an anti-detect browser. All profiles share your IP address and device fingerprint, so it will not get around a site that blocks several accounts from one machine.
- Passwords are only as safe as your Windows login.
- No history, bookmarks, download manager or per-profile proxy.
