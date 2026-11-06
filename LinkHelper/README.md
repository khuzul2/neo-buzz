# ReBuzzLink

Ableton Link for NeoBuzz Live's Session View (`docs/specs/session-view.md`, 0.8), in a process of
its own. Its name, `ReBuzzLink`, is the helper process's and stays.

**Licence: GPL-2.0-or-later.** This program links [Ableton Link](https://github.com/Ableton/link)
(GPL-2.0-or-later, or a proprietary licence from Ableton), included here as a git submodule at
`LinkHelper/link` (tag `Link-4.1.1`, with its `asio-standalone` submodule). NeoBuzz Live itself
stays MIT: it only starts this program and exchanges lines of text with it over standard input and
output, as it would with any other program. The Windows package ships `ReBuzzLink.exe` with this
folder's source and Link's licence beside it.

## Build

```
git submodule update --init --recursive LinkHelper/link
```

Windows:

```
cmake -S LinkHelper -B build/LinkHelper -A x64
cmake --build build/LinkHelper --config Release
```

`build.ps1` does this and puts `ReBuzzLink.exe` next to `NeoBuzzLive.exe`.

macOS and Linux (`docs/specs/mac-engine.md` M6):

```
cmake -S LinkHelper -B build/LinkHelper
cmake --build build/LinkHelper
```

This makes `build/LinkHelper/ReBuzzLink` (Release; on macOS for 11.0 and later). Building
`rebuzz-headless` afterwards copies it beside the app, where ReBuzz looks for it (`--link`).

## Protocol

See the comment at the top of `main.cpp`. ReBuzz reads `state` lines (about 100 a second): the
beat Link's session has at a host time, for the quantum ReBuzz set, with the tempo, the number of
peers, whether the session plays, and whether Link is on. Host times are microseconds of
the clock .NET's `Stopwatch` reads, so ReBuzz takes them as they are:

- Windows: `QueryPerformanceCounter`, Link's own clock there.
- macOS: `mach_absolute_time`, Link's own clock there (`Stopwatch` reads it as `CLOCK_UPTIME_RAW`).
- Linux: `CLOCK_MONOTONIC`. Link's own clock there is `CLOCK_MONOTONIC_RAW`, which drifts from it,
  so the helper gives Link `CLOCK_MONOTONIC`.
