# Pedal MComp for Buzz 1503 — v1.0

A 4-band stereo soft-knee compressor for **Jeskola Buzz 1503 (32-bit)**, with
Linkwitz-Riley 24 dB/oct crossovers in a balanced binary-tree topology. Each
band has independent threshold / ratio / attack / release / makeup / bypass;
knee width, detection mode (peak/RMS), output gain, dry-wet mix, lookahead,
phase-linear crossover summation and a per-band Listen selector are global.
Every parameter slider shows its real-world value (dB / ms / Hz / ratio / %).

This is a port of Pedal MComp v1.3 for ReBuzz. The ReBuzz version lives in its
own repo; this repo is for Buzz 1503 only.

## Differences from the ReBuzz version

- **Target:** .NET Framework 4.8, x86 (Buzz 1503 cannot load modern .NET).
  `Compat.cs` supplies `MathF`, `Clamp` and float bit-casting, which net48
  lacks, so the DSP code is otherwise unchanged and the machine stays a
  single DLL.
- **Corrected FastMath.** The fast log/exp helpers copied from Pedal Comp had
  a 1.76 dB jump just below every power of two (every 6 dB of level), which
  made the gain reduction step and tick as the envelope crossed -6, -12,
  -18 dBFS and so on. This port uses the corrected, continuous versions.
- **Muted input.** Buzz 1503 keeps calling `Work()` without the READ flag when
  the input is muted, with the last block still in the input buffer. The
  machine checks the READ flag, returns silence, and clears all filter,
  envelope, lookahead and meter state, so nothing stale plays when the input
  comes back.
- **GUI width** follows the parameter window instead of a fixed 560 px
  (Buzz 1503 clips the GUI rather than widening the window). The refresh
  timer starts and stops with the window.
- **Presets** ship as `Pedal MComp.NET.prs.xml` (Buzz loads a bundle with the
  DLL's name), and every preset now sets all 35 parameters, including
  Lookahead, Phase Linear and Spectrum View (all Off).
- **About** entry in the machine's right-click menu.

Parameters, ranges, defaults and order are identical to ReBuzz v1.3, so
presets are interchangeable.

## Signal flow

```
                      ┌─ LP@xover1 ──→ LOW ──── compressor ──┐
          ┌─ LP@xover2 ┤                                      │
          │            └─ HP@xover1 ──→ LO-MID ─ compressor ──┤
   input ─┤                                                   ├─→ sum
          │            ┌─ LP@xover3 ──→ HI-MID ─ compressor ──┤
          └─ HP@xover2 ┤                                      │
                       └─ HP@xover3 ──→ HIGH ─── compressor ──┘
```

Every band passes through exactly two LR4 stages, so all four bands share the
same group delay. Sum of all four bands is magnitude-flat for stationary
signals.

## Parameters

All continuous parameters use 0..127 integer encoding with logarithmic
mapping to the physical units. The rack slider displays the unit value via
the `ValueDescriptions` array — setting `L Attack` to 95 shows "30 ms"
rather than "95".

| #  | Name          | Range  | Units                                 |
|----|---------------|--------|---------------------------------------|
| 1  | Listen        | 0-4    | All / L / LM / HM / H                 |
| 2  | Xover L-LM    | 0-127  | 40 Hz – 600 Hz, log                   |
| 3  | Xover LM-HM   | 0-127  | 200 Hz – 3 kHz, log                   |
| 4  | Xover HM-H    | 0-127  | 800 Hz – 12 kHz, log                  |
| 5  | Knee          | 0-24   | dB, shared. 0 = hard knee             |
| 6  | Detection     | 0-1    | Peak / RMS, shared                    |
| 7-12  | L &lt;params&gt;  |    | see below                             |
| 13-18 | LM &lt;params&gt; |    | same six                              |
| 19-24 | HM &lt;params&gt; |    | same six                              |
| 25-30 | H &lt;params&gt;  |    | same six                              |
| 31 | Output Gain   | 0-48   | -24 to +24 dB; index 24 = unity       |
| 32 | Dry-Wet       | 0-100  | %                                     |
| 33 | Lookahead     | 0-127  | Off / 0.1 ms – 10 ms, log; default Off|
| 34 | Phase Linear  | 0-1    | Off / On; default Off                 |
| 35 | Spectrum View | 0-1    | Off / On; default Off (GUI only)      |

### Per-band parameters

| Sub-param  | Range  | Units                                       |
|------------|--------|---------------------------------------------|
| Threshold  | 0-60   | -0 dB to -60 dB below 0 dBFS                |
| Ratio      | 0-127  | log 1:1 to 40:1; index 127 = "Limit" (∞:1)  |
| Attack     | 0-127  | log 0.1 ms to 200 ms                        |
| Release    | 0-127  | log 1 ms to 2000 ms                         |
| Makeup     | 0-24   | dB                                          |
| Bypass     | 0-1    | Off / On; band still routes through crossover |

### Defaults

Crossovers near ~120 Hz / ~700 Hz / ~4 kHz. Threshold -18 dBFS, ratio ~2.5:1
on every band. Attack/release scaled per band — slow on Low (30 ms / 200 ms),
fast on High (3 ms / 30 ms) — reflecting that low-frequency content needs
longer time constants to avoid distortion while high-frequency transients
benefit from quick response. Knee 6 dB, peak detection. Output unity, fully
wet.

## Listen mode

`Listen = All` (default) sums all four bands. Setting it to L / LM / HM / H
solos that band so you can hear what the crossovers are doing during setup.
Per-band Bypass interacts: when you solo a band that's bypassed, you hear the
uncompressed crossover output for that band.

## GUI

The custom GUI embeds in the standard parameter window above the sliders.
Four columns side-by-side, one per band. In Buzz 1503 the GUI takes the
width of the parameter window (360 px minimum); columns and curves scale
with it.

```
┌─────────────┬─────────────┬─────────────┬─────────────┐
│      L      │     LM      │     HM      │      H      │
│   ┌──┐ ┌──┐ │   ┌──┐ ┌──┐ │   ┌──┐ ┌──┐ │   ┌──┐ ┌──┐ │
│   │██│ │  │ │   │██│ │█ │ │   │█ │ │██│ │   │█ │ │  │ │
│   │██│ │██│ │   │██│ │██│ │   │██│ │██│ │   │██│ │  │ │
│   │██│ │██│ │   │██│ │██│ │   │██│ │██│ │   │██│ │  │ │
│    IN  GR   │    IN  GR   │    IN  GR   │    IN  GR   │
│             │             │             │             │
│  ┌───────┐  │  ┌───────┐  │  ┌───────┐  │  ┌───────┐  │
│  │   /─  │  │  │   /─  │  │  │   /─  │  │  │   /─  │  │
│  │  /    │  │  │  /    │  │  │  /    │  │  │  /    │  │
│  │ /     │  │  │ /     │  │  │ /     │  │  │ /     │  │
│  └───────┘  │  └───────┘  │  └───────┘  │  └───────┘  │
│  T -18 dB   │  T -18 dB   │  T -18 dB   │  T -18 dB   │
│  R 2.5:1    │  R 2.5:1    │  R 2.5:1    │  R 2.5:1    │
└─────────────┴─────────────┴─────────────┴─────────────┘
```

- **IN meter** — post-crossover, pre-compressor input level. Three-zone
  green/amber/red gradient at -18 dBFS and -6 dBFS.
- **GR meter** — current gain reduction, fills downward from the top. Full
  scale = 24 dB GR.
- **Transfer curve** — input dB on the x-axis, output dB on the y-axis,
  both -60 to 0. Reference diagonal in grey shows the no-compression line.
  Amber dashed line marks the threshold. The cyan curve is the actual
  compression characteristic, recomputed each frame from the same
  `BandCompressor.SoftKneeGR` formula the audio path uses. An amber dot
  marks the current operating point (input level + applied GR), so you
  can see *where on the curve the compressor is sitting right now* —
  ties the meters to the curve at a glance.
- **Threshold / Ratio readout** below the curve.
- **Bypass overlay** — a band shows "BYPASS" overlaid in muted colour
  when that band's bypass switch is on. The IN meter keeps updating
  during bypass (handy for "is this band hot enough to need
  compression?" decisions).

The GUI refreshes at ~30 fps via `DispatcherTimer`. Meter values are
written to `volatile float` fields on each `BandCompressor` from the audio
thread and read from the UI thread — no locks, no allocations per frame
(PedalComp §7 pattern).



## Limitations / future work

- **No latency reporting to the host.** Buzz has no plugin-delay-compensation
  mechanism, so when Lookahead is non-zero the machine's output is
  delayed relative to non-delayed parallel channels in the rack. The internal
  dry path is delay-aligned so the Dry-Wet mix stays coherent; cross-channel
  alignment is the user's responsibility. Default-off avoids accidental
  surprises.
- **Phase Linear adds group delay, not zero phase.** The all-pass compensation
  makes the four bands sum phase-coherently at crossovers, but the combined
  filter chain has a frequency-dependent group delay — typical 1-3 ms total
  across all all-passes for usual crossover settings. "Phase linear" means
  *aligned across bands*, not *zero delay*.
- **GUI is read-only.** Meters and curves display, but the curves aren't
  click-draggable for parameter editing (use the parameter sliders below
  the embedded GUI). Adding drag-edit on the curves is a v1.x candidate.
- **No sidechain input.** Detection is from the band's own signal only. A
  sidechain input that feeds the detector while the program signal is
  compressed is a v2-class feature.
- **No M/S processing**, **no per-band saturation**, **no auto-makeup**,
  **no A/B compare**, **no internal output limiter** — bigger v2-class items
  that may end up as a separate Pedal MComp Pro machine rather than bolt-ons
  to v1.x.

## Preset bank

20 factory presets ship in `Pedal MComp.NET.prs.xml` and appear in the
machine's preset menu with no import step. Categories follow a
`Category - Description` convention, which groups them in the menu:

| Category    | Count | Use case                                        |
|-------------|-------|-------------------------------------------------|
| Utility     | 2     | Default (matches machine defaults), Bypass      |
| Mastering   | 4     | Transparent, Glue, Punch, Loudness              |
| Mix Bus     | 2     | Tighten, Vintage                                |
| Drum Bus    | 3     | Punch, Smash, Vintage                           |
| Vocal       | 4     | De-Ess, Air, Body Control, Smooth               |
| Bass        | 2     | Tighten, Sub Limit                              |
| FX          | 3     | Pumping, Squash, Lo-Fi                          |

The bank is generated by `gen_presets.py` (sparse overrides on top of the
machine defaults, with helpers such as `atk`, `rel`, `rat`, `xo1` to write
values in ms, ratio and Hz). Edit the `PRESETS` table and re-run it.

## Build & deploy

Requires a .NET SDK (or Visual Studio 2022) on Windows; the
`Microsoft.NETFramework.ReferenceAssemblies` package lets it build net48
without the Developer Pack. The Buzz SDK DLLs (`BuzzGUI.Interfaces.dll`,
`BuzzGUI.Common.dll`) are referenced from the Buzz install and not copied.

```
python gen_presets.py     # only if you edited the presets
dotnet build -c Release
```

The build copies `Pedal MComp.NET.dll` and `Pedal MComp.NET.prs.xml` to
`C:\Program Files (x86)\Jeskola\Buzz\Gear\Effects\`. Close Buzz first and
build from an elevated prompt, then check for the two `Deploying` lines in
the output (the copy is non-fatal, so a locked or protected folder does not
fail the build). For a different install location:

```
dotnet build -c Release -p:BuzzDir="D:\Buzz"
```

## Files

| File                          | Purpose                                         |
|-------------------------------|-------------------------------------------------|
| `Pedal MComp.NET.csproj`      | net48 / x86 build, deploy to Gear\Effects       |
| `PedalMComp.cs`               | Machine: parameters, Work loop, About entry     |
| `PedalMCompGui.cs`            | WPF GUI (bands, OUT meter, spectrum view)       |
| `BandCompressor.cs`           | Single-band DSP with lookahead delay line       |
| `Crossover.cs`                | LR4 LP/HP biquad pair, cached coefficients      |
| `AllPass.cs`                  | 2nd-order all-pass for Phase Linear             |
| `SpectrumAnalyzer.cs`         | Ring buffer + windowed FFT for spectrum view    |
| `FastMath.cs`                 | Fast LinToDb / DbToLin (corrected)              |
| `Compat.cs`                   | net48 shims: MathF, Clamp, float bit-casting    |
| `gen_param_labels.py`         | Generator for the ValueDescriptions arrays      |
| `gen_presets.py`              | Generator for the preset bundle                 |
| `Pedal MComp.NET.prs.xml`     | Preset bank (20 presets)                        |

The generator scripts are kept for reproducibility and are not deployed.

## Status

- Compiles against the .NET Framework 4.8 reference assemblies; DSP checked
  headless (flat band sum, compression, lookahead latency, mute and resume,
  FastMath continuity).
- **Not yet run in Buzz 1503.** To check on first load: the machine appears
  in the list; the GUI fits the parameter window; About works; the presets
  appear; audio passes through; muting the input silences the output; the
  window grows when Spectrum View is switched on (untested in 1503 for an
  embedded GUI).

## Source attribution

The single-band compressor DSP (`BandCompressor.cs`) is a port of Pedal Comp
v1, copied rather than referenced: each machine is its own deployable unit
and shares no runtime assemblies.

## Licence

GNU General Public License v3.0. See `LICENSE`.
