# Neo Random — Specification

| | |
|---|---|
| Status | Implemented (RND1, issue #200) |
| Date | 2026-11-25 |
| Kind | Control (`Neo Random.NET.dll`, box name `Rnd`, 0 tracks, no audio output) |
| Scope | Four-mode stochastic and chaotic canvas modulator (Random, Sample & hold, 1/f-like Drift, Lorenz Chaos) sharing `NeoModulatorBase` and `ModulatorEditor` |

---

## 1. Design

Neo Random is a clean-room control machine built directly on NeoBuzz Live's shared modulator infrastructure (`NeoBuzz.Machine/NeoModulator.cs`, `NeoBuzz.Dsp/ModTargets.cs`, `NeoModulator/ModulatorEditor.cs`) with zero copy-pasted target or editor plumbing. All engine math (`Machines/NeoRandom/Engine/RandomEngine.cs`) uses `PortableMath` / `PortableMathF` and integer/IEEE basic ops only: 0 bytes allocated per update, zero denormals, and bit-exact identical trajectories across Linux x64, Windows x64, and macOS arm64.

### 1.1 Target Model & Host Integration
- Inherits `NeoModulatorBase` (`Kind = "RND"`): up to **8 targets** (any global or track parameter of any machine), each with depth `−100..+100 %`, polarity (`uni` or `bi`), optional range, safe-range clamping (master volume never pushed past `0 dB`, tempo refused), and curve.
- Writes run at most once per 64-sample sub-block, slewed by `Write glide`, and use `track | (1 << 16)` so modulation never records undo steps or clip events.
- User edits update the target's base; removing a target or the modulator restores its base value.
- New target slots on `Kind = "RND"` default to `uni` polarity in `ModulatorEditor`, `ModulationEdits`, and the agent `modulate` tool (selectable per target to `bi`).

### 1.2 PRNG, Seeding & Clock
- **PRNG:** PCG32 (64-bit state, odd increment) seeded via SplitMix64.
  - `Seed ∈ 1..9999`: deterministic bit-exact repeat from the same start, automatically re-seeded on `Seed` change, transport start (`!wasPlaying && playing`), or loop rewind (`beats < lastBeats - 1e-6`), guaranteeing bit-exact `null-test.py` renders.
  - `Seed = 0`: mixes a static `Interlocked` counter with `Environment.TickCount64` so every instance/run differs.
- **Clock & Sync:** Free `Rate` (`0.02..40 Hz`) or tempo-synced `Sync` (`8 bars` down to `1/64`) × `Feel` (`Straight ×1`, `Dotted ×1.5`, `Triplet ×2/3`). Song-locked when `Sync > 0 && playing && Retrigger == Free`, otherwise double-precision integration with integer step-index transitions `floor(phase + 1e-12)` (±1 sample grid accuracy).
- **Retrigger (`Free`, `Beat`, `Bar`, `Note`):** On a beat, bar, or listened-machine note trigger, resets `phase = 0` and `cycle = 0`; when `Seed > 0`, restarts the active mode's deterministic state from `Seed`; when `Seed = 0`, forces an immediate fresh transition across all 4 modes even if `cycle` was already `0`.

### 1.3 Modes
All modes produce a bounded internal signal `m ∈ [0, 1]`:
1. **`Random`:** Uniform draw `u ∈ [0, 1)` per step. `Smooth = 0 %` holds crisp steps; `Smooth s > 0 %` glides `prev → cur` across the first `s` fraction of each step via C¹ raised-cosine `0.5 − 0.5 cos(π · min(1, p / s))` (`100 %` = continuous curve through waypoints).
2. **`Sample and hold`:** Samples a uniform noise source on each step tick; updates with exact Bernoulli probability `Probability` (`0–100 %`), otherwise holds. `Smooth` optionally slews transitions.
3. **`Drift`:** Bounded 1/f-like slow random walk whose speed tracks `Rate` (or synced rate `f_eff`). Uses 5 geometric Ornstein–Uhlenbeck stages (`f_k = min(0.45 / dt, f_eff · {0.06, 0.18, 0.54, 1.62, 4.86})`) updated with exact variance-preserving discrete OU steps `s_k ← α_k s_k + sqrt(1 − α_k²) z_k` (`α_k = exp(−2π f_k dt)`, `z_k = 2(u_1 + u_2 + u_3 − 1.5)` unit-variance), combined with `γ = 0.25` weights normalized to unit RMS and soft-bounded via `0.5 + 0.5 tanh(0.85 · sum)`. No unconditional extra pole at `Smooth = 0 %`; optional one-pole smoothing applies only when `Smooth > 0 %`.
4. **`Chaos`:** Clean-room 3D Lorenz attractor (`σ = 10`, `β = 8/3`, `ρ = 26 + 14 · chaos ∈ [26, 40]`, strictly above the subcritical Hopf threshold `ρ_H ≈ 24.74`), initialized deterministically off the invariant `z`-axis with a 1.0-unit warm-up and integrated with bounded sub-stepped RK4 (`dt_sub ≤ 0.005`, `|x,y,z| ≤ 100`). Blends a seeded calm periodic orbit (`0 %`) into the full double-scroll Lorenz attractor (`100 %`) via `m = (1 − c) calm + c lorenz`. `Smooth = 100 %` outputs continuous flow; `Smooth = 0 %` samples & holds on steps.

### 1.4 Output Shaping
- **Quantise (`0..32`, unitless `int 0`):** `0` = Off; `1` = `0.5` (exact 1 level); `N ∈ 2..32` = `floor(m · N) / (N − 1)` (exact `N` evenly spaced levels).
- **Range / Depth / Signed Bias (`depth ∈ [0, 1]`, `bias ∈ [−1, +1]`):**
  - `Bipolar` (default): `Clamp(0.5 + depth · (q − 0.5) + 0.5 · bias, 0, 1)`.
  - `Unipolar`: `Clamp(depth · q + bias, 0, 1)`.

---

## 2. Parameters (`Machines/NeoRandom/Params.def`, `StateVersion = 0`, 14 parameters)

| Group | Parameters |
|---|---|
| Source (6) | `Mode` (`Random`, `Sample and hold`, `Drift`, `Chaos`), `Rate` (`0.02..40 Hz`), `Sync` (`Free`, `8 bars` … `1/64`), `Feel` (`Straight`, `Dotted`, `Triplet`), `Probability` (`0–100 %`), `Chaos` (`0–100 %`) |
| Shape (5) | `Smooth` (`0–100 %`), `Depth` (`0–100 %`), `Bias` (`−100..+100 %`, default `0 %`), `Range` (`Unipolar`, `Bipolar`), `Quantise` (`0..32`, `0` = off) |
| Behaviour (3) | `Seed` (`0..9999`, `0` = auto, default `1`), `Retrigger` (`Free`, `Beat`, `Bar`, `Note`), `Write glide` (`1 ms .. 1 s`, default `30 ms`) |

---

## 3. Acceptance Gates & Measured Results (48 kHz, Linux x64)

| Gate | Target | Measured |
|---|---|---|
| Distribution (Random, Smooth 0, 100k steps) | 20 bins each within ±3 % of 5,000 | Worst bin error `2.52 %` |
| Quantise N | Exact N distinct values (`N = 1, 2, 5, 12, 16`) | Exact `1` (`0.5`), `2`, `5`, `12`, `16` |
| Timing (Synced 1/16, 120 & 174 BPM) | Within ±1 sample of grid (Random & S&H) | `0.00` smp (120 BPM), `0.97` smp (174 BPM) |
| Timing (Free rate) | Within ±1 sample of quantized `v.Value(P.Rate)` | `0.92` smp (`3.998 Hz`), `0.55` smp (`13.697 Hz`) |
| Probability (S&H at 25 %, 10k steps) | Within `25 % ± 2 %` (plus 0 % & 100 % edges) | `2451 / 10000 = 24.51 %`; 0 % holds, 100 % updates every step |
| Drift (10 min bounded + spectrum) | Inside `[0, 1]`; PSD slope `∈ [−2.0, −1.0]` dec/dec; `dt`-invariant | `0.50 Hz`: `[0.001, 0.999]`, slope `−1.37` dec/dec; `2.00 Hz`: `[0.001, 1.000]`, slope `−1.43` dec/dec; `std(0.33 ms)=0.278` vs `std(1.33 ms)=0.286` |
| Chaos (10 min bounded + active + seed) | Inside `[0, 1]`; final-minute `std > 0.05` at 0 / 50 / 100 %; bit-exact repeat & distinct seeds | `0 %`: `[0.000, 1.000]` std `0.3535`; `50 %`: `[0.049, 0.945]` std `0.1990`; `100 %`: `[0.069, 0.934]` std `0.2301`; bit-exact same seed, divergent distinct seeds |
| Seed & Retrigger | All 4 modes bit-exact with `Seed > 0` (incl. pre-roll); `Seed = 0` differs; Beat/Bar/Note retrigger | All pass |
| Targets (`ModBank` + `NeoModulatorBase`) | Range/safe-range/polarity bounded, `Write glide` slew enforced, base restored, no undo steps | All pass |
| Hygiene | 0 bytes allocated, 0 denormals, `StateVersion = 0` round-trip, `PortableMathTests`, `ModulationCostTests`, `PresetTests` | `0 bytes` allocated; `StateVersionTests` (21/21), `PortableMathTests`, `PresetTests` (10 presets), `ModulationCostTests` pass |

---

## 4. Demo Pack & Gallery

- **Demo pack:** `listening-packs/200/` (local, git-ignored), generated by `NeoBuzz.MachineTests/NeoRandomPack.cs`: **5 files × 16.5 s** (48 kHz 16-bit stereo WAV, two presets back-to-back per file at 8.0 s each with 0.5 s silence between, driving `Neo Poly`'s `F1 cutoff` on a sustained Am9 chord), **0 clicks** across all 5 files.
- **Mac gallery:** `GalleryTests` shot `651-neo-random-editor` (`Lorenz swirl` loaded + `Neo Poly` `F1 cutoff` target attached before opening) captured in both dark and light themes.
