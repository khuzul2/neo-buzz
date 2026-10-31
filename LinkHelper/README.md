# ReBuzzLink

Ableton Link for ReBuzz's Session View (`docs/specs/session-view.md`, 0.8), in a process of
its own.

**Licence: GPL-2.0-or-later.** This program links [Ableton Link](https://github.com/Ableton/link)
(GPL-2.0-or-later, or a proprietary licence from Ableton), included here as a git submodule at
`LinkHelper/link` (tag `Link-4.1.1`, with its `asio-standalone` submodule). ReBuzz itself stays
MIT: it only starts this program and exchanges lines of text with it over standard input and
output, as it would with any other program. The ReBuzz package ships `ReBuzzLink.exe` with this
folder's source and Link's licence beside it.

## Build

```
git submodule update --init --recursive
cmake -S LinkHelper -B build/LinkHelper -A x64
cmake --build build/LinkHelper --config Release
```

`build.ps1` does this and puts `ReBuzzLink.exe` next to `ReBuzz.exe`.

## Protocol

See the comment at the top of `main.cpp`. ReBuzz reads `state` lines (about 100 a second): the
beat Link's session has at a host time, for the quantum ReBuzz set, with the tempo, the number of
peers, whether the session plays, and whether Link is on. Host times are microseconds of
`QueryPerformanceCounter`, the same clock as .NET's `Stopwatch`.
