# Neo Chip — Specification

| | |
|---|---|
| Status | Implemented (CHI1, issue #186) |
| Date | 2026-11-24 |
| Kind | Generator (`Neo Chip.NET.dll`, box name `Chip`, 4 tracks, stereo out) |
| Scope | Four-channel sound-chip instrument: three pulse channels, one LFSR noise channel, 16-step ADSR and hardware-style envelopes, per-track arpeggio, pitch sweep, vibrato, and optional 4-bit DAC |

---

## 1. Design

Neo Chip is a clean-room four-channel sound-chip synth built on `NeoBuzz.Dsp` (`BlepTable.Shared`, `BlepBuffer`, `PortableMath`/`PortableMathF`, `Smoother`, `SmoothRamp`, `StealFade`, `StateMigrations`). All audio-thread math uses `PortableMath`/`PortableMathF` only, allocates 0 bytes per block, holds no UI locks, flushes recursive states against denormals, and renders bit-identically on x64 and arm64.

### 1.1 Voices
- **Three pulse channels (Channels 1–3, `ChipPulseOsc`):**
  - Base duty selectable per channel: `12.5%`, `25%`, `50%`, `75%` (`0.125`, `0.25`, `0.50`, `0.75`).
  - Continuous per-channel **PWM**: sine LFO at `PWM rate` (`0.1–20 Hz`) modulates duty by `±0.375 × PWM` clamped to `[0.05, 0.95]`. Duty transitions are smoothed (~10 ms) and the oscillator tracks its `high` state sample-by-sample so moving duty across phase always emits a matching `±2` BLEP step.
  - Pulse waveforms stay in `[-1, +1]`.
  - **Band-limited default (`Raw = Off`):** 48-tap 4-term Blackman–Harris windowed-sinc BLEP (`BlepTable.Shared`, latency `Z = 24` samples).
  - **Raw mode (`Raw = On`):** naive stepped pulse delayed by `Z = 24` samples so BLEP and Raw are phase-aligned, crossfaded over 10 ms via `SmoothRamp`.
- **One noise channel (Channel 4, `ChipNoiseLfsr`):**
  - Maximal-length linear-feedback shift register (never locks at 0):
    - **Long (15-bit):** $x^{15} + x^{14} + 1$, exact period **32767** clock steps.
    - **Short (7-bit, metallic):** $x^{7} + x^{6} + 1$, exact period **127** clock steps.
  - Clock frequency follows the played note: `clockHz = Clamp(noisePitchHz * 2^((note - 69 + modSemi) / 12), 20, 8 * sampleRate)`. Above `sampleRate`, the LFSR steps in a bounded loop (up to 16 steps per sample) and averages the `±1` outputs across sub-steps so 7-bit metallic fundamentals and high-pitched noise hats track cleanly above Nyquist.

### 1.2 Tracks, Notes & Voice Allocation
- **Tracker tracks 1–4 (`key = 0..3`):** tracks 1–3 drive pulse channels 1–3 directly; track 4 drives the noise channel.
- **MIDI / piano roll (`IChannelNoteSink`):**
  - `Noise MIDI ch` (`All`, `1..16`, default `10`): notes on the selected MIDI channel (or any channel when `All`, used by noise-drum presets) route exclusively to the noise channel with a last-note stack.
  - Other MIDI channels route across Pulse 1–3 according to `Voice mode`:
    - **Poly:** persistent round-robin cursor across Pulse 1–3; prefers free, then oldest released, else steals the **oldest** held channel (monotonic `noteSeq++`) with a 2 ms C¹ cubic-Hermite (`3t² − 2t³`) `StealFade` to exact silence before starting the stolen note.
    - **Mono:** Pulse 1 with a 32-entry last-note priority stack.
    - **Unison:** all three pulse channels stacked with a last-note stack, detuned by `-Detune`, `0`, `+Detune` cents.
  - **Phase reset, pre-fill & note edges:** `ChipPulseOsc.Reset()` pre-fills `BlepBuffer` and `rawDelay` with `+1f` so a silent note-on has no step click at sample `BlepTable.Z` (24). Pulse oscillator phase resets **only** when the voice was silent (`!Env.IsAudible`, including after a Poly `StealFade` completes at exact zero). Same-channel non-silent retriggers (Mono, Unison, Noise, Tracker tracks) keep continuous oscillator phase, glide envelope smoothly from live `y2` (ADSR Attack starts from `level = y2`), and smooth velocity transitions via a 1.5 ms `Smoother`. Note-on always fades in over ≥ 1 ms; hard note-off (`AllNotesOff(true)`) and minimum-release note-off always fade out over ≥ 2 ms.
  - **Velocity depth (`0–100 %`):** linear amplitude scaling `(1 - depth) + depth * (vel / 127)`.
  - Real note-off with release, CC64 sustain pedal, and ±2 st pitch bend.

### 1.3 Envelope (`Stepped` 16-level or `Smooth` ADSR, plus Stepped Hardware Shapes)
- **`Envelope` parameter (`Stepped` default, `Smooth`, state version 1):**
  - **`Stepped` (0, default and v0→v1 migrated value):** ADSR outputs 16 discrete 4-bit levels (`step / 15f`, `step ∈ {0..15}`) passed through a cached two-stage critically damped (C¹-continuous) slew so the 16-step stair-step character stays audible while eliminating 1-sample step clicks (`τ = 0.50 ms` clean / `0.18 ms` raw; note-on & wrap edges `τ = 0.50 ms` clean / `0.45 ms` raw for ≥ 1 ms rise; hard note-off `τ = 0.80 ms` clean / `0.65 ms` raw for ≥ 2 ms fall, flushed to exact `0f` at `≤ 1e-6f`).
  - **`Smooth` (1):** ADSR uses the exact same Attack, Decay, Sustain (`sustain step / 15`) and Release (clamped ≥ 2 ms) times, but outputs the continuous level with no 16-level quantisation and no per-step slew—eliminating short-decay/release stepping crackle while keeping the ≥ 1 ms note-on fade, ≥ 2 ms note-off and hard note-off fades, Mono legato continuous phase/gain behaviour, and Poly 2 ms voice-steal fade.
- **Hardware-style shapes** (always **stepped** in both `Stepped` and `Smooth` settings; equal step dwell, period from `Env period` in ms or `Env sync` locked to song BPM):
  - `Saw down` (repeating `15 → 0`), `Saw up` (repeating `0 → 15`), `Triangle` (repeating `0 → 15 → 0`), `Decay shot` (one-shot `15 → 0` then idle), `Attack hold` (one-shot `0 → 15` then holds `15` until note-off). Note-off enters stepped Release to `0`.

### 1.4 Per-Track Arpeggio & Motion
- Independent per-channel state reset on each note-on:
  - **Arpeggio:** `Off`, `Up`, `Down`, `Up-Down` (no repeated endpoints), `As written`; `1–3` active steps (`Arp step 1..3`, `−24..+24 st`); free rate (`1–60 Hz`) or tempo-synced (`1/16`, `1/16T`, `1/32`, `1/32T`, `1/64`, `1/64T`). Uses `Math.Floor(elapsedSamples / stepSamples)` for sample-accurate step transitions with zero drift.
  - **Pitch sweep:** linear `−96` to `+96 st/s` clamped safely to valid MIDI range.
  - **Vibrato:** sine LFO (`0–200 ct`, `0.5–20 Hz`, `0–2000 ms` delay with 20 ms smooth fade-in).

### 1.5 Output & Headroom
- Equal-power pan per channel (`cos/sin`, `0.7071` at center, `1.0` at hard pan) combined with `Stereo spread` (`0–100 %`).
- **Lo-res DAC:** quantises each channel's mono post-envelope signal in `[-1, +1]` to 4-bit `k / 7` plateaus with exact zero at silence and a narrow continuous transition knee (`|u − k| ∈ (0.35, 0.50]`) so slewed envelope glides do not snap into 1-sample `1/7` clicks; mixed linearly across all 4 channels and crossfaded via a 10 ms `SmoothRamp`.
- **Mix headroom:** fixed per-channel scaling `ChannelScale = 0.20f` so a 3-note pulse chord plus a noise hit at velocity 127, `Volume = 0 dB`, and `Lo-res DAC = On` peaks at or below `0 dBFS` across all duty settings.
- **DC blocker:** gentle ~5 Hz stereo one-pole high-pass (`dcR = exp(-2π·5/sr)`, flushed with `DspMath.Flush`) after channel mixing and `Lo-res DAC`.

---

## 2. Parameters (`Machines/NeoChip/Params.def`)

42 global parameters (`StateVersion = 1`; v0 → v1 appends `Envelope = Stepped (0)`) + 4 tracks (`Note`, `Velocity`):

| Group | Parameters |
|---|---|
| Channels (16) | `P1..P3 duty` (`12.5%`, `25%`, `50%`, `75%`), `P1..P3 PWM` (`0–100%`), `P1..P3 PWM rate` (`0.1–20 Hz`), `P1..P3 pan` (`−100..+100%`), `Noise mode` (`Long`, `Short`), `Noise pitch` (`100 Hz – 48 kHz`), `Noise pan` (`−100..+100%`), `Noise MIDI ch` (`All`, `1..16`, default `10`) |
| Voice (4) | `Voice mode` (`Poly`, `Mono`, `Unison`), `Detune` (`0–50 ct`), `Velocity depth` (`0–100%`), `Raw` (`Off`, `On`) |
| Envelope (8) | `Env mode` (`ADSR`, `Saw down`, `Saw up`, `Triangle`, `Decay shot`, `Attack hold`), `Envelope` (`Stepped`, `Smooth`, appended at index 41), `Env attack` (`1 ms – 5 s`), `Env decay` (`1 ms – 5 s`), `Env sustain` (`0–15`), `Env release` (`1 ms – 5 s`), `Env period` (`5 ms – 4 s`), `Env sync` (`Free`, `1/64` … `1/1`) |
| Arpeggio (7) | `Arp mode` (`Off`, `Up`, `Down`, `Up-Down`, `As written`), `Arp steps` (`1–3`), `Arp step 1..3` (`−24..+24 st`), `Arp rate` (`1–60 Hz`), `Arp sync` (`Free`, `1/16` … `1/64T`) |
| Motion (4) | `Pitch sweep` (`−96..+96 st/s`), `Vib depth` (`0–200 ct`), `Vib rate` (`0.5–20 Hz`), `Vib delay` (`0–2000 ms`) |
| Output (3) | `Stereo spread` (`0–100%`), `Lo-res DAC` (`Off`, `On`), `Volume` (`−40..+6 dB`, bottom `−∞`) |

---

## 3. Acceptance Gates & Measured Results (48 kHz, Linux x64)

| Gate | Target | Measured |
|---|---|---|
| Duty cycle (440 Hz) | Within ±1 % for 12.5 / 25 / 50 / 75 % (clean & raw) | `12.50%`, `25.00%`, `50.00%`, `75.00%` (0.00 % error) |
| Band-limited aliasing (up to C7) | Raw off ≤ −60 dB; Raw on measurably higher | C4 `−97.0 dB`, A4 `−95.5 dB`, C6 `−98.2 dB`, C7 `−93.4 dB` (Raw C7: `−17.2` to `−23.4 dB`) |
| Noise LFSR periods | Exactly 32767 (Long) and 127 (Short) | 32767 (Long) and 127 (Short), all non-zero states unique |
| Noise pitch tracking | Follows note including > sampleRate | 7-bit 55.88 kHz clock → 440 Hz peak `+42.9 dB` over floor; Long centroid C3 `971 Hz` → C6 `5091 Hz` |
| Volume steps (Stepped & HW) | Exactly 16 discrete levels (`0/15..15/15`) with slewed plateaus; HW modes stay stepped in both settings | Verified across ADSR and all 5 hardware envelope modes; HW bit-identical in Stepped and Smooth |
| Stepped bit-identicality | All 12 presets × 3 lines bit-identical to `cb8c406a` in Stepped mode; 8 non-Smooth presets unchanged | All **36/36** renders match `cb8c406a` SHA-256 bit-for-bit |
| Smooth release cleanliness (25% pulse, 165 / 330 / 659 Hz) | ≥ 8 dB lower between-harmonic energy (2–20 kHz, outside ±12 %) than Stepped | **+9.0 dB** (`−30.4` → `−39.4 dB` at 165 Hz), **+23.1 dB** (`−30.6` → `−53.7 dB` at 330 Hz), **+38.3 dB** (`−30.4` → `−68.7 dB` at 659 Hz) |
| Gain-trace anti-click gate | Stepped & Smooth held ADSR ≤ `1/15` per 1 ms, 0 gain clicks; ≥ 1 ms note-on, ≥ 2 ms release/hard-off; continuous Mono legato & 2 ms Poly steal through 0 | Stepped 1 ms Δ `0.0392`/`0.0605`, Smooth 1 ms Δ `0.00668` (limit `0.06667`), sustain `0.66667` (`10/15`), `0` clicks across all traces |
| Arpeggio timing (1/32 synced) | Within ±1 sample at 120 and 174 BPM | `0.00` samples (120 BPM) and `≤ 0.52` samples (174 BPM) over 16 steps |
| ADSR stage timing | Within ±5 % | `< 0.5 %` error across 20–600 ms stages |
| Hardware envelope period | Within ±1 % | `50.00 ms` (`0.008%`), `200.00 ms` (`0.000%`), `124.35 ms` (`0.519%`), 174 BPM sync `< 0.2%` |
| Poly voice allocation & velocity | 3-note chord, 4th steals oldest, note-off exact, velocity depth exact | Passes; vel 1 vs 127 at 0 / 50 / 100 %: `0.00` / `−5.95` / `−42.08 dB` (exact) |
| Mix headroom (3-note chord + noise, vel 127, Vol 0 dB, DAC on) | Peak ≤ 0 dBFS across all duties | `−0.96 dBFS` (12.5%), `−2.01 dBFS` (25%), `−4.38 dBFS` (50%), `−2.03 dBFS` (75%) |
| Lo-res DAC 4-bit grain | Audible difference in clean & raw modes | Residual `−25.4 dB` (clean) and `−25.5 dB` (raw) |
| Presets (12) | All > −30 dBFS RMS; pairwise distance ≥ 4.0 dB; `PresetTests` RMS ∈ `[−30, −6]` & peak ∈ `[−20, −1]` dBFS | Min RMS `−18.1 dBFS`; min pairwise distance `8.88 dB`; all 12 pass `PresetTests` bounds |
| Audio-thread allocation | 0 bytes across 2000 moving-parameter blocks | `0 bytes` (`AllocationCheck.Run`) |
| Denormals & silence cost | 0 subnormals in 20 s silence; cost ≤ 1.5× playing | `0` subnormals; silence costs `0.101×` playing (`634.8×` real time) |
| Zipper noise under automation | ≤ −60 dB at block-rate sidebands | `−104.7` to `−132.3 dB` across automated parameters |
| Modulation cost | Ratio ≤ 1.5× with all continuous params moving | `≤ 1.15×` |
| CPU throughput | Within Neo instrument budget (`Floor(25, 25)`) | `65.7×` real time at reference speed (`31.6×` raw) |
| Portable math & state version | No libm/SIMD sum issues; `StateVersion = 1` v0→v1 migration to `Stepped (0)` | `PortableMathTests` and `StateVersionTests` pass (`eea71a6218c453fd`) |

---

## 4. Listening Pack & Gallery

- **Listening pack (Round 3):** `listening-packs/186/` (local, git-ignored), generated by `NeoBuzz.MachineTests/NeoChipPack.cs` at 48 kHz 16-bit stereo WAV, 8.0 s each on the `melody` line for the four `Envelope = Smooth` presets (`Lead — Square hero`, `Lead — Thin 12 %`, `Bass — Pulse bass`, `Pad — Unison detune`), verified by both the frame-peak audio click detector and the direct gain-trace second-difference click detector (`|d2| > 0.015`): **4 files, 0 audio clicks, 0 gain-trace clicks** (max gain `d2 ≤ 0.01013`).
- **Mac gallery:** `GalleryTests` shots `645-neo-chip-editor` (`Arp — Major sparkle`, showing the `Envelope` Stepped/Smooth switch beside `Mode`) and `646-neo-chip-hardware` (`Raw — Lo-res grit`) captured in both dark and light themes with parameter values applied before opening the editor.
