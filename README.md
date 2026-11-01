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
- OTA portals: a built-in list of extranet and channel-manager sign-in pages, one click to add as home pages (see below).
- Search engines: DuckDuckGo (the default), Google, Bing, Brave Search, Yahoo, Ecosia, Startpage and Qwant. Each profile can have its own engine, chosen in the profile dialog or with the magnifier button next to the address bar (which also sets the app-wide default); a profile without one uses the app default. Start a search with a keyword to use another engine once (`ddg hotel rates`, `b ...`, `br ...`, `g ...`, `y ...`, `e ...`, `sp ...`, `q ...`).
- Ad and tracker blocking, per-profile proxy: see the sections below.
- Update button (bottom of the profile panel): checks GitHub only when you click it. See "Updates".
- App icon: drawn by `tools\make-icon.ps1`, which writes `assets\icon.ico` and `assets\logo.png`.
- One copy per data folder: starting the app a second time brings the first window forward instead of opening a second copy that could overwrite your profiles.
- Errors do not close the app: an unexpected error is written to `crash.log` in the data folder, your open tabs are saved, and the app keeps running (it closes, after saving, only if errors keep coming).
- The profile panel starts hidden, to give pages the whole window. Use the profile chip in the toolbar (or Ctrl+K) to switch profiles, and the menu button (or Ctrl+B) to show the panel; once you choose, it stays that way.
- Dialogs fit small screens: the profile dialog scrolls (Save and Cancel stay in view) and the other dialogs never grow taller than the screen.

## OTA portals

**OTA portals...** in the profile dialog (and in Launch site...) lists the sign-in pages below. Typing a portal's name in the address bar also suggests it. They were checked on 1 Nov 2026 by opening each in this browser's own engine (`tools\check-sites.ps1`): every one below loaded with HTTP 200 and showed a sign-in page. **That only proves the page opens here; I could not sign in, so your own login is untested.**

| Group | Portals |
|---|---|
| OTA extranets | Booking.com Extranet, Expedia Partner Central (also Hotels.com, Vrbo), Agoda YCS, Airbnb Hosting, Trip.com eBooking, Hostelworld Inbox, Google Hotel Center |
| India OTAs | Goibibo / MakeMyTrip Extranet (Ingo-MMT), MakeMyTrip Partner |
| Channel managers and hotel software | AxisRooms (Hotelogix), Aiosell, Simplotel, Djubo, SiteMinder, Little Hotelier, Cloudbeds, HotelRunner, Beds24, WuBook |

Caveats from the check: Agoda YCS loaded the Agoda Partner Portal sign-in but the form was not detected by the probe; HotelRunner's sign-in page loaded but its text mentions a bot check; Google Hotel Center goes to Google sign-in, which can refuse sign-in inside embedded browsers (only that first page was checked).

**Not in the list** because no working address was found (the guesses did not resolve, returned 404, timed out, or only reached a marketing page): Cleartrip, Yatra, EaseMyTrip, ixigo, OYO (its partner pages are sign-up pages, not an extranet login), Treebo, FabHotels, Traveloka, Tiket.com, Vrbo, eZee, Hotelogix's own login, RezNext (the domain is for sale), ResAvenue and Trivago Hotel Manager (timed out), STAAH Max (blank page). Use the address from your onboarding email and add it as a home page; a verified address can be added to `OtaCatalog.cs`. Re-run the check any time:

```powershell
powershell -File tools\check-sites.ps1                       # the built-in portals and search engines
powershell -File tools\check-sites.ps1 -List mine.json       # your own [ { "Name": "...", "Url": "https://..." } ] list
```

## Updates

The **check for updates** button (it shows the version you run, for example "v0.4.1 - check for updates") at the bottom of the profile panel asks GitHub for the latest release when you press it (nothing is contacted before that). If a newer version exists it offers to update. It downloads the matching exe (`small` or `standalone`, chosen by the size of the one you run) only from GitHub over HTTPS, checks it against the SHA-256 that GitHub publishes for that file, swaps it in for the running exe (the old one is kept as `LightBrowser.exe.old` until the next start) and restarts. Profiles, logins and settings are separate files and are not touched. A development build, a read-only folder, or a release without a published checksum opens the release page instead, for a manual download. The exes are not code-signed.

## Memory

RAM stays bounded however many profiles and tabs exist. Only `MaxLoadedTabs` tabs (default 5, across all profiles) are ever loaded; the oldest hidden tab is unloaded when you go over, and a hidden tab nobody looked at for `IdleMinutes` (default 10) is unloaded too. An unloaded tab keeps its title and address, costs almost nothing, and reloads when clicked (still logged in; cookies are on disk). Hidden tabs are also put in WebView2's low-memory mode. The visible tab, tabs playing audio, loading tabs, and tabs used in the last `GraceSeconds` (default 120, protects login popups) are never unloaded.

Change the limits in `%LocalAppData%\LightBrowser\settings.json` and restart:

```json
{ "MaxLoadedTabs": 5, "IdleMinutes": 10, "GraceSeconds": 120 }
```

An unloaded tab loses unsaved page state (text typed into a form, scroll position), so finish forms before leaving a tab for a long time.

## Proxy per profile

Each profile can send all its traffic through its own proxy, so each client account comes from its own IP address. Set it in the profile's New/Edit dialog: `host:port` (HTTP), or `https://`, `socks4://`, `socks5://` in front. HTTP and HTTPS proxies can have a user name and password; the password is stored encrypted for your Windows account and never as plain text. SOCKS proxies that need a login are not supported.

- If the proxy is down, pages do not load. They never fall back to your own connection.
- A proxied engine is started with Chromium's switch that stops WebRTC sending UDP outside the proxy (set, but not yet checked against a leak-test site).
- The proxy is an engine-wide setting, so every distinct proxy runs its own browser engine and data folder (`WebView2Data-px-...`). Profiles without a proxy share the normal engine. Each extra proxy costs one more set of browser processes (memory not measured yet), so many profiles on the same proxy share one, while many different proxies add up. A proxy's engine only runs while one of its tabs is loaded: when the last one is closed or unloaded the engine's processes exit, and they start again when a tab on that proxy loads (checked in the end-to-end test).
- A profile's logins stay with the proxy they were made through. Changing a profile's proxy asks first, then signs it out of every site (saved passwords in the password list are kept).
- A duplicated profile does not copy the proxy, so two accounts do not end up on the same IP by accident.
- Addresses on your own machine (localhost) bypass the proxy, as in Chrome.

## Ad and tracker blocking

The **Blocking** button in the toolbar turns blocking on or off (on by default; the number is requests blocked since the app started). A request is blocked, before it leaves the browser, when its host or a parent domain is on the list: a short built-in set of ad networks and trackers, plus your own `blocklist.txt` in the data folder (created on first run; one domain per line, `0.0.0.0 domain` hosts lines and `||domain^` lines also work; restart after editing). The built-in list is deliberately small and leaves out sites that also serve real content (Google, Facebook), so pages keep working.

**YouTube ads** are handled separately (domain blocking cannot touch them: they come from the same servers as the video). On youtube.com the same Blocking switch also runs a script, the same idea Brave and uBlock Origin use, that strips the ad data out of the player's responses, hides ad slots, and as a fallback skips or fast-forwards an ad that still starts. Checked with `tools\check-youtube.ps1`, which plays a video with sound off and samples the player: on three popular videos an ad played in every run without the blocker (about 15-20 s each) and in none with it, and the video played normally, with no "ad blockers are not allowed" pop-up. The real app showed the same on one of them: a "Sponsored" ad with Blocking off, the video itself with it on. The script also removes ad entries from the home page, search results and the Shorts queue (on live search results it removed 4 ad entries in one test), covers YouTube Kids, silences the "abnormality" callback YouTube uses to notice tampering (it fires on every watch page; Brave's list does the same), reloads a video once or twice if the player refuses to play after a block, and puts sound and speed back after it fast-forwards an ad. Twenty offline checks of the script's logic run against made-up YouTube data and a made-up player (`tools\check-youtube.ps1 -SelfTest`, and as phase 13 of the end-to-end test); they prove the logic, not that YouTube still accepts it. **This is best-effort.** YouTube changes its player often and works against ad blockers, so it can stop working without warning; run the checker to see whether it still does. Blocking ads is against YouTube's terms of service. It does not remove ads inside the video itself (sponsor reads). Not tried against the real site: Shorts playback, live streams, mid-roll ads in long videos, YouTube Music, YouTube Kids, and the reload-after-refusal recovery (the made-up player checks its logic, but YouTube has not refused this script yet).

This is a content blocker inside the browser, not a system firewall: it does not filter other apps, has no malware feed, and does no page cosmetic filtering. Every request passes through a quick list lookup while blocking is on.

## Modes

Three buttons at the bottom of the sidebar switch how much memory the browser may use, live, with no restart:

| Mode | Tabs loaded at once | Unused tab unloaded after | Hidden tabs |
|---|---|---|---|
| Low RAM | 2 | 3 min | trimmed |
| Balanced (default) | 5 | 10 min | trimmed |
| Fast | 15 | 60 min | not trimmed |

Low RAM is a strict cap: opening a third tab unloads the oldest hidden one straight away (the tab you are looking at, a loading tab and one playing audio are never unloaded, so the counter can briefly read 3/2 while a page loads). Low RAM also starts the engine without GPU acceleration, which takes effect the next time the app starts. On the first run, a PC with 4 GB of RAM or less starts in Low RAM automatically.

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
powershell -File tests\e2e\update.ps1                 # the Update button, against a local stand-in for GitHub (publishes two builds)
```

The end-to-end script runs the app on a throwaway data folder (`LIGHTBROWSER_HOME`) against a local test server and checks session isolation between two profiles on the same site, login persistence across restarts, that the tab limit does not log anyone out, and renderer/browser crash recovery. Set `LIGHTBROWSER_HOME` yourself to run LightBrowser from a different data folder (for example a USB stick).

## Limits

- This is not an anti-detect browser. All profiles share your IP address and device fingerprint, so it will not get around a site that blocks several accounts from one machine.
- Passwords are only as safe as your Windows login.
- No bookmarks or download manager.
