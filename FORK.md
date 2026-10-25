# About this fork

A fork of [wasteddesign/ReBuzz](https://github.com/wasteddesign/ReBuzz) (MIT) that adds:

| What | Where |
|---|---|
| Third-party machines (sources + prebuilt drop-ins) in one place | [`ExternalMachines/`](ExternalMachines/README.md) |
| One-command build that produces a portable zip with every machine installed | [`build.ps1`](build.ps1) |
| Script to pull prebuilt machine DLLs from GitHub releases | [`tools/fetch-machine-binaries.ps1`](tools/fetch-machine-binaries.ps1) |
| CI job that runs the build and uploads the zip | [`.github/workflows/fork-package.yml`](.github/workflows/fork-package.yml) |
| Spec for an Ableton-style Session View | [`docs/specs/session-view.md`](docs/specs/session-view.md) |

## Building the package

Needs Windows with:

- Visual Studio 2026 with the **Desktop development with C++** and **.NET desktop development** workloads. Visual Studio 2022 works too; `build.ps1` then builds the C++ projects with the v143 toolset instead of v145.
- .NET 10 SDK (x64)
- git (for submodules)

Then:

```powershell
.\build.ps1                      # full build -> dist\ReBuzz-<date>-<sha>-x64.zip
.\build.ps1 -SkipCore            # rebuild machines + package only
.\build.ps1 -Version 0.1.0       # custom version label
.\build.ps1 -NativeMachines      # also try the Buzztrax C++ machines (path without spaces; don't compile yet)
Get-Help .\build.ps1 -Detailed   # all options
```

The zip contains `README-FIRST.txt` (runtime prerequisites), `GEAR-INVENTORY.txt` (every machine and where it came from) and `BUILD-INFO.txt` (machines that failed to build, if any).

No Windows machine at hand? Run the **Fork package** workflow from the Actions tab and download the zip from the run.

## Keeping up with upstream

```bash
git fetch upstream
git merge upstream/main
```

Fork-only changes live in new files wherever possible, so merges stay clean.
