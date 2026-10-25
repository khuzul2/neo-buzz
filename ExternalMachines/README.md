# External machines

Third-party Buzz/ReBuzz machines kept inside this repo so we can build, ship and modify them in one place.

> **Why not `/machines`?** The repo already has a `Machines/` folder (ReBuzz's own built-in machines). Windows file systems are case-insensitive, so a sibling `machines/` would collide with it. Everything third-party lives here instead.

## What's here

| Kind (`machines.json`) | Count | What `build.ps1` does with it |
|---|---|---|
| `rebuzz-managed` — C# machines for ReBuzz (.NET 10) | 19 | Built from source against our own ReBuzz build and added to the package |
| `native-nmake` — Buzztrax collection (~120 classic C++ machines) | 1 repo | Only with `build.ps1 -NativeMachines`: builds the nmake subset (FSM, Fuzzpilz, Matilde, Oomek) as x64. **Doesn't compile yet with current MSVC** (old headers clash with `<cmath>`), and upstream Gear already ships all of these except Fuzzpilz UnwieldyTracker. The other authors' folders are source only |
| `buzz1503-managed` / `buzz1503-native` — written for classic Jeskola Buzz 1503 | 7 | Source only. Porting one to ReBuzz is mostly retargeting to `net10.0-windows` (see the `rebuzz-managed` twins) |
| `native-source` — Jeskola developer examples, PatternXP | 1 | Source only (no x64 build files yet) |
| `reference` — bmxplay | 1 | Not a machine. C reimplementations of classic DSP plus demo `.bmx` songs, useful for testing |

On top of this, upstream ReBuzz already ships **73 prebuilt machine DLLs** in `RunTime/Gear` (Jeskola, FSM, Joachims, Polac VST loader, Oomek, BTDSys PeerChord…). Those always go into the package and take priority over anything here with the same file name.

```
ExternalMachines/
├── machines.json          manifest: origin, commit, license, kind, build entry, patches
├── Directory.Build.props  default ReBuzzDir = this repo's build output
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

## Prebuilt binaries

Precedence in the package: upstream `RunTime/Gear` > built from source > prebuilt. Nothing overwrites a file already in Gear, and `Foo.dll` / `Foo.x64.dll` count as the same machine.

- `binaries/Gear/` is for machines we only have as DLLs. Name 64-bit native machines `*.x64.dll` — ReBuzz loads any other native DLL in its 32-bit host.
- `tools/fetch-machine-binaries.ps1` downloads the latest GitHub release assets for every repo under `releaseBinaries` in `machines.json` into `binaries/repos/`. `build.ps1` uses a repo's binaries only when that machine failed to build from source, so they're a fallback. Run it on Windows; review `binaries/FETCHED.md` and commit what it fetched.
- Not scripted, because their download URLs can't be verified from the sandbox this was set up in: the [Buzz x64 2022 edition bundle](https://forums.jeskola.net/viewtopic.php?p=16694#p16694) (robotplanet.dk), jeskola.net's files, and the old buzzmachines.com archive. Download them by hand, unpack, and copy the `Gear/Generators` and `Gear/Effects` DLLs here.

## Adding or updating a machine

1. Copy the sources into `source/<author>/<machine>/` without `.git`, `bin` or `obj`.
2. Add an entry to `machines.json`: `origin`, full `commit` SHA, `license`, `kind`, and for buildable machines `project`, `assembly` (must end in `.NET` — ReBuzz only loads managed machines named `*NET.dll`) and `gearFolder`.
3. For `rebuzz-managed` projects, replace hard-coded `C:\Program Files\ReBuzz` paths with `$(ReBuzzDir)` and record it under `patches`.
4. Run `.\build.ps1 -SkipCore` to rebuild only the machines and the package.

When re-syncing from upstream, re-apply the entries under `patches`.

## Licensing — read before publishing this repo

- **Most thepedal repos, retracker and the Jeskola examples have no license file.** By default that means all rights reserved: fine for a private repo and personal builds, not fine to redistribute publicly without asking the authors.
- `pedal-juno106`, `pedal-sh101`, `pedal-drum-matrix` and several buzz1503 ports are **GPL-3.0**. Shipping their binaries in our zip is fine as long as the source (this folder) is available with the package.
- Buzztrax machines carry **per-author licenses**; check each author folder.
- Buzz's `MachineInterface.h` (Oskari Tammelin) allows **freeware machines only**. Nothing built from it may be sold.

Recommendation: keep the repo private until the unlicensed sources are cleared with their authors, or publish without `source/thepedal` and `binaries/`.
