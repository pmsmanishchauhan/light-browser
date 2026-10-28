# LightBrowser

A small Windows browser for people who log in to the same websites with many different accounts, for example one OTA extranet login per hotel client. Each profile has its own cookies, logins and site data, so you can be signed in to the same site as several clients at once.

It is a thin shell (a few MB) around the Microsoft Edge WebView2 engine that ships with Windows 10/11, so it does not bundle a browser and stays light on memory. All profiles share one WebView2 environment; a profile only costs memory while it has open tabs.

## Features

- Unlimited profiles, each with isolated cookies, sessions, local storage and saved passwords.
- Logins persist across restarts.
- Tabs per profile. Restored tabs load lazily: only the first one opens, the rest load when clicked.
- Profile list with search (Ctrl+K), groups, colours, notes (shown on hover) and an "open" dot.
- Close profile (right-click) to free its memory without losing logins.
- Trash with restore; "Delete forever" wipes the profile's data.
- Colour bar and window title show which client profile you are in.
- Popups (OAuth, login windows) open as tabs in the same profile and keep `window.opener`.
- Shortcuts: Ctrl+T new tab, Ctrl+W close tab, Ctrl+L address bar, Ctrl+K profile search, middle-click closes a tab.

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

## Limits

- This is not an anti-detect browser. All profiles share your IP address and device fingerprint, so it will not get around a site that blocks several accounts from one machine.
- Passwords are only as safe as your Windows login.
- No history, bookmarks, download manager or per-profile proxy.
