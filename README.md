# Glideslope™

Glideslope is a small desktop app for Windows 11 and Linux that shows how fast you are using your AI coding
allowance. It has one card each for **Claude Code**, **OpenAI Codex** and **Google Antigravity** (the Gemini card),
and each card answers one question: am I ahead of or behind an even pace toward my next reset? English is the
original language; the interface has AI translations in Spanish, French, German, Italian, Brazilian Portuguese,
Japanese, Simplified Chinese, Korean, Russian, Indonesian, and Traditional Chinese.
Glideslope selects a supported system language when it starts; you can choose a language in Settings and restart
to apply it.

If you're a native speaker and spot a translation that sounds wrong, please open a GitHub issue with the exact
text you saw and the exact wording you recommend. I'll review the correction and include it in a patch release.

- The big number is how much of the weekly allowance is left.
- The pace line says how far over or under budget you are, in hours ("13.4 hours over budget · 113% pace"), colored
  from bright green to red.
- A chart shows the whole weekly window, an even-pace guide and your real readings, one per refresh. Past weeks
  can be browsed with ‹ › Now.
- The 5-hour window, Codex reset credits and Claude's separate Fable weekly limit show when the provider reports them.

## Working with the cards

- **Snap.** Drag a card next to another and release. They snap together 10 px apart, and the card you dragged
  takes the other card's size. Resizing one card in a group resizes the whole group. Hover the middle of a joined
  edge for an **Undock** button (or Ctrl + drag a card out). One setting turns snapping on and off, for cards and
  for screen edges.
- **Mini mode.** The ▭ button shrinks a card's whole group to a small card with the percentage, reset time, pace
  line and bars. ▣ brings it back. Each card remembers its size in each mode.
- **Ctrl peek.** Hold Ctrl over a mini card to see it full size; let go and it goes back. This is enabled by
  default and can be turned off in Settings.
- **Adjustable card size.** Click "Aa" on a full or mini card to open its size slider (70 % to 150 %);
  ↺ resets it to 100 %. Settings also has separate **Card size** and **Mini card size** sliders, so you can
  set the zoom for each mode independently.
- **Tray.** Glideslope keeps running in the tray. Start at sign-in starts it hidden in the tray. The tray menu
  shows the cards or exits.
- Settings (⚙): which providers to show, theme (system, dark, light), always on top, refresh interval (5 to 30
  minutes), how long to keep history, and where the log file is.

## How Glideslope reads usage

Every card reads its numbers through the vendor's **official command-line tool**, the same way you
would at a terminal. Glideslope never calls a vendor's web endpoints, never reads or copies a login
token, and never sends a model request to get a reading.

| Card | What runs | Spend |
| --- | --- | --- |
| Claude | `claude auth status --json`, then `claude -p --no-session-persistence --strict-mcp-config --setting-sources project,local --output-format json /usage` (run from an empty folder so no hooks or MCP servers load) | none (`num_turns` 0, checked on every run; a run that ever spends a turn stops the card's reads until the Claude CLI is updated; that stop survives restarts) |
| Codex | `codex app-server` over its documented JSON protocol, `account/rateLimits/read` | none |
| Gemini | `agy -p /usage --output-format json` (Google Antigravity) | none (checked on every run) |

So you need the tool for each card you use, installed and signed in: Claude Code, the Codex CLI, or Antigravity.
Sign-in stays with each vendor's own tool. If a tool reports you are signed out, the card says so and tells you
which command to run; Glideslope never signs you in itself.

**What this means for the Claude card.** Claude Code's CLI does not expose the weekly-limit "Reset for
free" credit that claude.ai shows under Settings › Usage, so the card cannot show it. The endpoint that
has it is behind the claude.ai web session. Anthropic's consumer terms prohibit automated access to
claude.ai outside their own clients, and their February 2026 clarification says a subscription's OAuth
token may not be used in any other product. Calling that endpoint would be a gray area at best, and
Glideslope deliberately stays out of gray areas. If Anthropic adds the credit to the CLI, the card will
pick it up.

Readings are stored only on your computer: settings in a JSON file and the history in a small SQLite database in
your per-user app-data folder. Glideslope itself sends nothing anywhere; only the vendors' own tools talk to their
services, as they do when you run them yourself.

## Installing on Linux

Each [release](https://github.com/dbeachy1/Glideslope/releases) has a `.deb` package for 64-bit Ubuntu and other
Debian-based systems. It includes its own .NET runtime, so nothing else needs installing. Download it, then:

```bash
sudo apt install ./glideslope_<version>_amd64.deb
```

The `./` matters: without it apt looks for a package by that name online. Glideslope then appears in the app menu. The
first launch asks whether to start it at sign-in. `sudo apt remove glideslope` uninstalls it and leaves your settings and
history in place.

**Tested only on Ubuntu 26.04 with GNOME** (Wayland session). Glideslope runs as an X11 app, through Xwayland on a
Wayland desktop, and what it needs from the desktop (window placement, raising windows, reading the Ctrl key) is
plain X11, so other desktops should work, but none has been tried. Window managers differ most in how they place and
raise windows, so snapping cards together and bringing them to the front are the likeliest places for a difference.

## Installing on Windows

Each [release](https://github.com/dbeachy1/Glideslope/releases) from 2.2.0 on has `Glideslope-Setup-<version>-x64.exe`
for Windows 11 x64. Run it. It installs for your user only (no admin rights) and offers start at sign-in. The
installer is not code-signed yet, so Windows SmartScreen may warn; choose **More info**, then **Run anyway**.

To build the installer yourself you need the .NET 10 SDK; the Inno Setup compiler comes from a pinned NuGet package.
From the repository root in PowerShell:

```powershell
.\packaging\windows\build-installer.ps1
```

It writes `artifacts\packages\windows\Glideslope-Setup-<version>-x64.exe`.

## Building and testing

The app is .NET 10 and [Avalonia](https://avaloniaui.net/) 12, one source tree for both platforms.

```powershell
dotnet build Glideslope.sln -c Debug
dotnet run --project src\Glideslope.App\Glideslope.App.csproj
```

The spec projects are console programs, not `dotnet test` projects. Run each one; it prints "… specs passed." and
exits 0:

```powershell
foreach ($s in 'Domain','Core.Layout','Storage','Monitoring','Providers','Shell') { dotnet run --no-build --project "src\Glideslope.$s.Specs\Glideslope.$s.Specs.csproj" }
```

`scripts\Invoke-NativeWindowProof.ps1` drives real card windows on a hidden Windows desktop. Any test or proof run
uses `--proof-root <folder>`, so it never touches your real settings or history.

| Project | What it holds |
| --- | --- |
| `Glideslope.Domain` | quota buckets, pace math, window identity |
| `Glideslope.Providers` | the three CLI readers |
| `Glideslope.Monitoring` | the polling scheduler |
| `Glideslope.Storage` | the SQLite history |
| `Glideslope.Core` | settings, the layout rules (snap, dock, resize, recovery), start at sign-in, single instance |
| `Glideslope.App` | the Avalonia app: cards, Settings, tray, the window coordinator |

## License and name

The code is licensed under the [Apache License 2.0](LICENSE). The logo files are not; see [`NOTICE`](NOTICE).
"Glideslope" and the Glideslope mark are trademarks of Douglas Beachy. Forks are welcome under their own name and
icon; see [`TRADEMARKS.md`](TRADEMARKS.md). Windows and Linux packages include these files and
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt) beside the app executable.
