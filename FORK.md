# About this fork

A fork of [wasteddesign/ReBuzz](https://github.com/wasteddesign/ReBuzz) (MIT) that adds:

| What | Where |
|---|---|
| Third-party machines (sources + prebuilt drop-ins) in one place | [`ExternalMachines/`](ExternalMachines/README.md) |
| One-command build that produces a portable zip with every machine installed | [`build.ps1`](build.ps1) |
| Script to pull prebuilt machine DLLs from GitHub releases | [`tools/fetch-machine-binaries.ps1`](tools/fetch-machine-binaries.ps1) |
| CI job that runs the build and uploads the zip | [`.github/workflows/fork-package.yml`](.github/workflows/fork-package.yml) |
| Ableton Link helper, `ReBuzzLink.exe`: a separate program under GPL-2.0-or-later (as Link is), which ReBuzz runs and talks to over stdin/stdout; the package ships its source and Link's licence beside it | [`LinkHelper/`](LinkHelper/README.md) |
| Ableton-style Session View: clip launching, launch grid, keyboard and MIDI control, clip editing with undo, recording into the arrangement (0.3; Ctrl+Tab switches Session ↔ Sequence), mixer with sends/returns, cue, meters and group tracks (0.4), live hardening: launches timed to what you hear, MIDI off the UI thread, dropout counter, autosave (0.4.1), clip and scene properties: launch modes, per-clip quantization, legato, loop region and start marker, velocity, scene tempo and time signature, clip panel (0.5), faithful arrangement: recording and scene insert keep partial patterns, loop regions, legato and velocity (0.5.1), performance: clip and scene follow actions, crossfader, launch undo, select on launch, scenes stopping other clips, tempo nudge and tap tempo (0.6), hardening from three reviews: follow actions against queued requests, launch undo in step, stale references, songs loading, undo keeping performance settings, identity that follows the song (renames, retired names, exact tracks) with object-bound session undo, settings apart from undo, one launch path (0.6.1), exact restorations when any view's undo brings back a pattern, sequence or machine, and exact recording undo (0.6.2), foundations for recording: engine MIDI columns played once and kept through undo, a MIDI release doing what its press did, removed tracks following the song (0.6.3), session recording: record arm, takes into empty slots (fixed length or until stopped), overdub, Capture MIDI, automation, metronome and count-in, into the clip's engine MIDI and automation columns (0.7), piano roll, notes to and from the Pattern View, takes that grow, Capture while stopped, native editor moves recorded (0.7.1), hardening before 0.8: Pattern View moves that never deadlock or lose notes, chords on note lanes, exact tempo, thread-safe MIDI devices (0.7.2), sync: Ableton Link through a GPL helper process (ReBuzzLink.exe), MIDI clock in and out, ticks phase-locked to the clock followed (0.8a), control surfaces: Launchpad Mini MK3/X/Pro MK3, APC mini mk2, APC40 mkII, Push 2 (User mode) with session rings and lights, and a script API for more (0.8b), audio clips: audio tracks (the Session Clip Player machine), audio files dropped on slots at their detected tempo, warping in Beats, Complex (a phase vocoder), Re-Pitch and Off modes, warp markers, gain and transpose, audio takes recorded from a track's input, and resampling the master to consolidate a performance (1.0) | [`docs/specs/session-view.md`](docs/specs/session-view.md), `ReBuzz/Core/Session/`, `ReBuzz/AppViews/Session/` |
| Fixes found by running upstream's `ReBuzzTests` in CI: machine delay compensation, the x64 VC++ 2010 runtime `bin64\dsplib.dll` needs, stale test expectations; two found building the mixer (connection amp ramps went the wrong way within each block; new connections ramped down from unity); and engine hardening (0.4.1): no synchronous UI calls from the audio, MIDI and IPC threads, no UI events under the audio lock, no allocation per audio buffer, pattern positions without a full scan per block; and (0.5.1) opening a .bmxml song lost all its subsections (Session View layout, MIDI map, mixer), and a pattern whose length changed ran over the next event in the sequence editors; and (0.6.1) .bmx files truncated arrangement positions past 65,535 ticks and could write an invalid pattern index; and (0.6.3) engine MIDI column events were sent up to a dozen times, deleting a machine deleted every pattern's columns of its own machine, undo could make a second pattern of one name, short ticks could divide by zero on the audio thread | `ReBuzz/Core/`, `ReBuzz/Audio/WorkManager.cs`, `build.ps1`, `ReBuzzTests/` |

## Building the package

Needs Windows with:

- Visual Studio 2026 with the **Desktop development with C++** and **.NET desktop development** workloads. Visual Studio 2022 works too; `build.ps1` then builds the C++ projects with the v143 toolset instead of v145.
- .NET 10 SDK (x64)
- git (for submodules)
- The **VC++ 2010 SP1 x64 redistributable** (`msvcr100.dll`), which upstream's `bin64\dsplib.dll` imports. Without it no 64-bit native machine loads. `build.ps1` copies it into the package's `bin64`, so people using the zip don't need it.

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
