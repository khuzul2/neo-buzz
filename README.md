# NeoBuzz Live

NeoBuzz Live is a fork of [ReBuzz](https://github.com/wasteddesign/ReBuzz), the modular digital audio workstation built upon the foundation of [Jeskola Buzz](https://jeskola.net/buzz/), by WDE and the ReBuzz contributors (MIT). It adds a clip-launching Session View for playing live, the Neo machines, an MCP server through which AI agents work in your songs, and a studio for macOS; [FORK.md](FORK.md) lists everything it adds and how it is built.

Songs are `.nbl`. Songs in the old Buzz and ReBuzz formats (`.bmx`, `.bmw`, `.bmxml`) are not opened: NeoBuzz Live says so in one plain message. The machines are the Neo machines (and Dye Essence); machines from Buzz and ReBuzz are not loaded.

## Download

- **Mac** (Apple silicon, macOS 12 or later): [NeoBuzz Live for Mac (latest main)](https://github.com/khuzul2/neo-buzz/releases/tag/mac-latest), `NeoBuzz-Live-macos-arm64.dmg`. Open it and drag NeoBuzz Live onto Applications. The app isn't notarized, so the first time you open it macOS asks you to allow it: System Settings → Privacy & Security → Open Anyway, or once in Terminal `xattr -dr com.apple.quarantine "/Applications/NeoBuzz Live.app"`. The disk image's *Read me first.txt* has the steps.
- **Windows** (x64): `NeoBuzz-Live-<version>-x64.zip`, from the **Fork package** workflow (Actions tab) or [`build.ps1`](FORK.md#building-the-package). Unzip anywhere and run `NeoBuzzLive.exe`; *README-FIRST.txt* lists what it needs: the [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-10.0.0-windows-x64-installer), the [latest Visual C++ Redistributable (x86 and x64)](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170) and, optionally, [Windows MIDI Services](https://github.com/microsoft/MIDI/releases/latest).

## AI agents

NeoBuzz Live is an MCP server ([docs/specs/mcp-server.md](docs/specs/mcp-server.md)), named `neobuzz-live`. With the app open, allow agents (Settings → Agent on Windows, Preferences ▸ Agent on a Mac; Ctrl+Shift+A or ⇧⌘A) and paste the line it shows into a terminal:

```
claude mcp add --transport http neobuzz-live http://127.0.0.1:7481/mcp --header "Authorization: Bearer <token>"
```

Without a window, Claude Code starts it on stdio: `claude mcp add neobuzz-live -- "C:\path\to\NeoBuzzLive.exe" --agent` on Windows, or `rebuzz-headless --agent` (built from `ReBuzz.Headless`) on macOS and Linux.

## Building

[FORK.md](FORK.md#building-the-package): `.\build.ps1` on Windows builds the solution, every machine and the zip; on a Mac, `tools/mac-bundle.sh` and `tools/mac-dmg.sh` make `NeoBuzz Live.app` and its disk image. The solution, projects and namespaces keep ReBuzz's names (`ReBuzz.sln`, `ReBuzz.*`), so upstream merges stay clean.

## Based on ReBuzz

NeoBuzz Live is based on ReBuzz, by WDE (wasteddesign) and the ReBuzz contributors: https://github.com/wasteddesign/ReBuzz. ReBuzz's MIT licence ([LICENSE](LICENSE)) covers it and is kept as it is. Upstream's own README, with its notes on building and the old Buzz machines, is in ReBuzz's repository.
