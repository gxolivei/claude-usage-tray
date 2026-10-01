# Claude Usage Tray

A tiny Windows tray widget that shows how much of your Claude plan you've used: the
5-hour session and weekly limits, plus your recent Claude Code activity. It sits quietly in
the tray and changes color only when a limit gets close.

> Unofficial community project. Not affiliated with or endorsed by Anthropic.

## Features

- **Tray dial:** a monochrome circle that fills clockwise with your 5-hour session usage. It
  turns amber when any limit reaches 80%, red at 95%, and closes into a solid disc when a limit
  is used up. Hover for exact numbers and reset times.
- **Popup:** click the icon for session and weekly rings, a bar per limit, and charts of
  your Claude Code activity by hour and by day, broken down by model.
- **Notifications** when a limit crosses 80% and 95%.
- **Follows your Windows theme** (light or dark taskbar and apps).
- **Finds your Claude Code login and transcripts** on Windows and inside WSL distros.
- **Start with Windows** toggle in the tray menu.
- One small `.exe`, no installer, no runtime to download.

## Requirements

- Windows 10 or 11 (uses .NET Framework 4.x, which ships with Windows)
- [Claude Code](https://claude.com/claude-code) logged in with a Claude subscription
  (on Windows or in WSL)

## Install

1. Download `ClaudeUsage.exe` from the [latest release](../../releases/latest).
2. Run it. There's no installer: the widget runs from wherever you saved the file, so put
   it somewhere permanent first (for example `%LOCALAPPDATA%\Programs\ClaudeUsageTray`).
3. To launch it at sign-in, right-click the tray icon and check **Start with Windows**.
   This adds a per-user startup entry and needs no admin rights. If you later move the
   exe, toggle it off and on again.

**Can't see the icon?** Windows 11 hides new tray icons behind the **^** arrow. Drag it
onto the taskbar, or turn it on under Settings > Personalization > Taskbar > Other system
tray icons.

**Windows warnings on first run.** Browsers and Windows treat new downloads cautiously:

- Edge or Chrome may say the file isn't commonly downloaded: choose **Keep**.
- If the release is unsigned (the release notes say so), SmartScreen shows "Windows
  protected your PC": click **More info > Run anyway**.
- **Smart App Control** (Windows 11) blocks unsigned apps with no override. Use a signed
  release, or build from source.

**Check your download.** Each release lists the exe's SHA-256 hash and includes a
`SHA256SUMS.txt` file. In PowerShell:

```powershell
(Get-FileHash .\ClaudeUsage.exe).Hash
```

The result should match the release notes (case doesn't matter). Release builds are made
from the tagged source by [GitHub Actions](.github/workflows/release.yml), not on a
developer's machine.

## Build from source

The widget reads your Claude login token, so if you'd rather not trust a downloaded
binary, build it yourself. It takes a few seconds and needs nothing installed:

```cmd
git clone https://github.com/gxolivei/claude-usage-tray
cd claude-usage-tray
build.cmd
```

`build.cmd` compiles `src\Program.cs` with the `csc.exe` that ships with Windows. No SDK
or Visual Studio needed. The whole app is that one file, so it's practical to read before
you run it. Quit the running widget first so `ClaudeUsage.exe` can be overwritten.

## Privacy

- Your Claude Code OAuth token is read from your local `.credentials.json` and sent only
  to `api.anthropic.com`, to read your usage.
- Activity charts come from Claude Code's local transcript files. They are parsed on your
  machine and never uploaded.
- Crashes are logged locally to `%LOCALAPPDATA%\ClaudeUsageTray\crash.log`.

## Usage API polling

Plan usage comes from `https://api.anthropic.com/api/oauth/usage`, using the Claude Code
OAuth token. The endpoint is undocumented and **rate limits each access token**. That token
is also the one Claude Code itself uses. To avoid wearing it out, the widget fetches only:

| Trigger | When |
|---|---|
| Launch | once, as soon as the widget starts |
| Schedule | every **10 minutes** (`PollMinutes` in `Program.cs`) |
| Refresh | when you click Refresh (popup button or the tray menu's "Refresh now"); this restarts the 10-minute cycle |

Nothing else calls the API:

- Opening the popup rescans local transcripts only.
- Errors, including HTTP 429 "Rate limited", get no faster retry. The widget waits for the
  next scheduled poll or a manual Refresh.
- The server sends `Retry-After: 0` on 429, so that header is ignored.

Each launch fetches immediately, so relaunching the widget repeatedly (for example while
developing) uses up the token's allowance too. Because the endpoint is undocumented, it may
change or break without notice.

## Releasing

Push a version tag (`git tag v1.2.3 && git push origin v1.2.3`). The
[release workflow](.github/workflows/release.yml) builds, signs when signing is
configured, computes the checksum, and publishes the GitHub release. Its header lists the
Azure Artifact Signing secrets and variables it needs.

## License

[MIT](LICENSE)
