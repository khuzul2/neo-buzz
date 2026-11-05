# External machines

Third-party Buzz/ReBuzz machines kept inside this repo so we can build, ship and modify them in one place.

> **Why not `/machines`?** The repo already has a `Machines/` folder (ReBuzz's own built-in machines). Windows file systems are case-insensitive, so a sibling `machines/` would collide with it. Everything third-party lives here instead.

## What's here

| Kind (`machines.json`) | Count | What `build.ps1` does with it |
|---|---|---|
| `rebuzz-managed` — C# machines for ReBuzz (.NET 10) | 19 | Built from source against our own ReBuzz build and added to the package |
| `native-nmake` — Buzztrax collection (~120 classic C++ machines) | 1 repo | Builds the 17 machines its entry lists under `natives` (FSM Infector, KickXP, PhatMan, PanzerDelay, Chorus2, WahMan, WahManPro, WahPro2; Oomek Aggressor 303, Exciter, Masterizer; Jeskola Delay, Cross Delay, Distortion, NiNjA dElaY, Trilok, Noise Generator) as x86 and x64 with `tools/build-natives.ps1`, in every full build (`-SkipNativeMachines` skips them). A rebuilt machine replaces upstream's binary only where its entry says `"ship": "rebuilt"` and its licence allows it (below, and Natives with sources). The rest of the collection (Fuzzpilz, Matilde, the other authors) is source only; its `makefile.nmake` is upstream's, unused |
| `buzz1503-managed` / `buzz1503-native` — written for classic Jeskola Buzz 1503 | 7 | Source only. Porting one to ReBuzz is mostly retargeting to `net10.0-windows` (see the `rebuzz-managed` twins) |
| `native-source` — Jeskola developer examples, PatternXP | 1 | Source only (no x64 build files yet) |
| `reference` — bmxplay | 1 | Not a machine. C reimplementations of classic DSP plus demo `.bmx` songs, useful for testing |

On top of this, upstream ReBuzz already ships **73 prebuilt machine DLLs** in `RunTime/Gear` (Jeskola, FSM, Joachims, Polac VST loader, Oomek, BTDSys PeerChord…). Those always go into the package and take priority over anything here with the same file name.

```
ExternalMachines/
├── machines.json          manifest: origin, commit, license, kind, build entry, patches
├── Directory.Build.props  default ReBuzzDir = this repo's build output
├── portable/              the Pedal machines built for net10.0, off Windows (below)
├── source/                vendored sources (no .git, no obj/bin)
│   ├── thepedal/          19 ReBuzz machines + buzz1503/ ports
│   ├── buzztrax-buzzmachines/
│   ├── jeskola-dev-examples/
│   ├── retracker/
│   └── reference/bmxplay/
└── binaries/
    ├── Gear/{Generators,Effects}/                    hand-placed prebuilt DLLs
    └── repos/<owner>__<repo>/{Generators,Effects}/   fetched GitHub release assets
```

## Off Windows

`portable/` builds the 19 Pedal machines as plain `net10.0`, without their WPF GUIs, for the studio and rebuzz-headless on macOS and Linux (docs/specs/studio-mvp.md S2). `ReBuzz.Host/AppGear.targets` builds them into the app's Gear; by hand: `dotnet build ExternalMachines/portable/Pedal.proj -p:GearDir=<folder>`. Each machine has a small project there that names its source folder and assembly, and lists its GUI-only files. Six sources keep their WPF code under `#if WINDOWS`, so the Windows builds above compile the same text as before (see `patches`).

## Prebuilt binaries

Precedence in the package: upstream `RunTime/Gear` > built from source > prebuilt. Nothing overwrites a file already in Gear, and `Foo.dll` / `Foo.x64.dll` count as the same machine.

- `binaries/Gear/` is for machines we only have as DLLs. Name 64-bit native machines `*.x64.dll` — ReBuzz loads any other native DLL in its 32-bit host.
- `tools/fetch-machine-binaries.ps1` downloads the latest GitHub release assets for every repo under `releaseBinaries` in `machines.json` into `binaries/repos/`. `build.ps1` uses a repo's binaries only when that machine failed to build from source, so they're a fallback. Run it on Windows; review `binaries/FETCHED.md` and commit what it fetched.
- Not scripted, because their download URLs can't be verified from the sandbox this was set up in: the [Buzz x64 2022 edition bundle](https://forums.jeskola.net/viewtopic.php?p=16694#p16694) (robotplanet.dk), jeskola.net's files, and the old buzzmachines.com archive. Download them by hand, unpack, and copy the `Gear/Generators` and `Gear/Effects` DLLs here.

## Adding or updating a machine

1. Copy the sources into `source/<author>/<machine>/` without `.git`, `bin` or `obj`.
2. Add an entry to `machines.json`: `origin`, full `commit` SHA, `license`, `kind`, and for buildable machines `project`, `assembly` (must end in `.NET` — ReBuzz only loads managed machines named `*NET.dll`) and `gearFolder`.
3. For `rebuzz-managed` projects, replace hard-coded `C:\Program Files\ReBuzz` paths with `$(ReBuzzDir)` and record it under `patches`.
   For a Pedal machine, add its project to `portable/` too (copy a neighbour's), and put WPF-only code under `#if WINDOWS`.
4. Run `.\build.ps1 -SkipCore` to rebuild only the machines and the package.

When re-syncing from upstream, re-apply the entries under `patches`.

## Natives with sources (U7)

`tools/build-natives.ps1` builds each machine of the Buzztrax entry's `natives` list from its sources, defines and libraries there, with the Buzztrax makefiles' flags (`/MT`, no optimisation switch, `WIN32`, `__MSVC__`, `_X86_` or `WIN64 _AMD64_`), against Buzztrax's MDK (`common/mdk`, static) and ReBuzz's own dsplib import libraries (`ReBuzzEngine/dsplib`, `dsplib64`), so a rebuilt machine loads the same `dsplib.dll` from `bin32` or `bin64` as the shipped one does. x64 DLLs are named `*.x64.dll`, which is how ReBuzz runs them in its 64-bit host (`bin64/ReBuzzEngine64.exe`, a process of their own); any other native DLL runs in `bin32/ReBuzzEngine32.exe`. One change was needed for the current MSVC: `FSM/dspchips/DSPChips.h` defined `copysign` as a macro, which rewrote `<cmath>`'s own declarations when Infector's `<complex>` included it after (`patches`).

Each entry records its licence (`license`, `redistribute`) and what ships (`ship`): `upstream` keeps the binary upstream ReBuzz ships, `rebuilt` puts the rebuilt one in its place. `tools/native-compare.py` renders every build of each machine side by side through `ReBuzz.exe --agent` in each full package run (docs/specs/mcp-server.md §12.14), and fails the run when a machine shipped rebuilt doesn't render as upstream's binary does.

## Licensing — read before publishing this repo

- **Most thepedal repos, retracker and the Jeskola examples have no license file.** By default that means all rights reserved: fine for a private repo and personal builds, not fine to redistribute publicly without asking the authors.
- `pedal-juno106`, `pedal-sh101`, `pedal-drum-matrix` and several buzz1503 ports are **GPL-3.0**. Shipping their binaries in our zip is fine as long as the source (this folder) is available with the package.
- Buzztrax machines carry **per-author licenses**; check each author folder. For the 17 natives (`machines.json`, `natives`): FSM is LGPL-2.1 (`FSM/COPYING`), Oomek GPL-3.0-or-later (each source's header): both allow building and redistributing with the source. The Jeskola folder states **no licence** (no file, no header; Cross Delay is Buzztrax's own rewrite, also unlicensed), so its six are built for measurement only (`"redistribute": false`) and the package keeps upstream's binaries of them.
- Buzz's `MachineInterface.h` (Oskari Tammelin) allows **freeware machines only**. Nothing built from it may be sold.

Recommendation: keep the repo private until the unlicensed sources are cleared with their authors, or publish without `source/thepedal` and `binaries/`.
