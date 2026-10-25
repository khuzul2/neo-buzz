# Pedal Drum Matrix — Build Handoff (resume here)

A single stereo-in / stereo-out **drum-fx rack** for ReBuzz: six serial slots,
each a swappable effect, six BCR2000-mappable performance controls, plus a
built-in output limiter and auto-gain leveler. Current state: **v0.7**,
working, not yet built against the real ReBuzz SDK in this session.

---

## 1. What it is / why

Replaces the old "6 effects → patch machine → 6 effects" rig with one machine.
Consolidating ~15 machines + their connections into one removes the per-machine
and per-connection host overhead ReBuzz incurs per tick (the *reducible* part of
CPU load; the fixed ~5 ms-per-chunk host floor from the May-2026 dropout
investigation stays — measure with Pedal Profiler2's Unaccounted % / SOLO).

Design priorities settled with the user:
- Slots behave like plugin slots — each independently set to any effect.
- Six peer/MIDI-mappable controls = the six slot **Char** rotaries; the six
  **Mode** switches map to the BCR2000 encoder pushes.
- "Play freely, don't worry about volume" → built-in Limiter + Auto Gain.

---

## 2. Files (in `pedaldrummatrix/`)

| File | Contents |
|---|---|
| `PedalDrumMatrix.NET.csproj` | Build §1.2 props, deploy to `Gear\Effects`, `.NET` AssemblyName |
| `PedalDrumMatrix.cs` | Machine: 6 slots, params, I/O scaling, Work, limiter+AGC wiring |
| `Slot.cs` | Slot host: owns every fx instance, click-free type crossfade, Amount/Char smoothing, Mode pass-through, tail flag |
| `DrumFx.cs` | `IDrumFx`, `FxType`, factory, all 10 effects, `Tail`, `Dsp.Ftz`, `OutputLimiter`, `AutoGain` |
| `README.md` | User-facing summary |

Delivery convention: one zip, one subdir `pedaldrummatrix/` (Build §5).

---

## 3. Architecture

- Single stereo **`EffectBlock`**: `bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)`.
- Six **serial** slots: slot 1 → 2 → … → 6 → AutoGain → Limiter → out.
- Each `Slot` pre-builds one instance of **every** `FxType` (no audio-thread
  allocation on swap). Type change = index change + 10 ms equal-power crossfade.
- `IDrumFx.Process(ref l, ref r, amount, p1, sw)` — `amount`=slot Amount (wet/
  intensity), `p1`=slot Char (0..1), `sw`=slot Mode (bool). `IsRinging` reports
  whether a feedback tail is still audible.
- **Tail-aware WM_NOIO**: machine ORs every slot's `IsRinging`; on `WM_NOIO` it
  keeps rendering tails (feeding silence in) and only returns `false` (sleeps)
  once the whole rack is quiet.
- Amount and Char are per-sample smoothed (Core §32, 20 ms). Mode is a plain
  switch (no smoothing).

---

## 4. Parameters (declaration order = preset contract, Build §3.3 — APPEND ONLY)

Group 1 globals, indices 0-based in declaration order:

```
0  Slot1 Type     (enum: None,Bitcrush,Drive,Filter,RingMod,Comb,Stutter,Delay,Reverb,Gate)
1  Slot1 Amount   (0..127)
2  Slot2 Type
3  Slot2 Amount
4  Slot3 Type
5  Slot3 Amount
6  Slot4 Type
7  Slot4 Amount
8  Slot5 Type
9  Slot5 Amount
10 Slot6 Type
11 Slot6 Amount
12 Slot1 Char (0..127, def 64)     ← appended block (Char+Mode per slot)
13 Slot1 Mode (Off/On)
14 Slot2 Char
15 Slot2 Mode
16 Slot3 Char
17 Slot3 Mode
18 Slot4 Char
19 Slot4 Mode
20 Slot5 Char
21 Slot5 Mode
22 Slot6 Char
23 Slot6 Mode
24 Limiter   (Off/On, def On)      ← appended
25 Auto Gain (Off/On, def Off)     ← appended
```

`MaxTracks = 0` (no per-track params). No `MachineState` blob — all state is
parameters, so song-save and presets come for free.

---

## 5. Per-effect Char (rotary) + Mode (switch) meanings

| Effect | Amount | Char (rotary) | Mode (switch) | Tail? |
|---|---|---|---|---|
| Bitcrush | crush depth | bit-crush ↔ decimation tilt | anti-alias filter | no |
| Drive | drive amount | bias / asymmetry | hard clip vs soft tanh | no |
| Filter | cutoff sweep (18k→150 Hz) | resonance (Q 0.5–8) | lowpass vs highpass | no |
| RingMod | wet + base freq (30 Hz→3 kHz) | carrier fine tune ±1 oct | ring-mod vs AM | no |
| Comb | wet + pitch/feedback | feedback damping | +/− feedback sign | yes |
| Stutter | wet + slice | repeats (2–8) | reverse slice | yes |
| Delay | **mix** (decoupled) | **feedback** | ping-pong | yes |
| Reverb | wet + tail length | damping (bright↔dark) | bright tilt | yes |
| Gate | depth + rate (8→1 ticks/cyc) | duty cycle | triplet timing | no |
| None | — | — | — | no |

Tempo-synced effects (Delay ~6 ticks, Gate, Stutter slice) read
`host.MasterInfo.SamplesPerTick`.

---

## 6. Output stage (after the slot chain)

- **Auto Gain** (peak-targeting leveler, before the limiter): lifts quiet
  output toward ~-6 dBFS peak. Targets PEAK not RMS so drum transients keep
  punch and it rarely hits the limiter. Dual detector — fast env (30 ms) gates
  adaptation so gaps/decays **freeze** the gain (never boosts silence/tails or
  ratchets between hits); slow peak (300 ms) sets the target level. Gain up
  slow (700 ms, no pumping), down faster (120 ms). Boost capped +18 dB, cut
  −12 dB.
- **Limiter** (zero-latency): stereo-linked peak follower (1 ms attack /
  100 ms release) into a cubic soft-clip ceiling at 0.95. Catches whatever the
  chain / AGC throws at it.
- "Set and forget" = both On.

---

## 7. Hard-won gotchas already handled (do not regress)

1. **Sample scale is ±32768, not ±1.0** (Core §38 / PedalComp §1). Machine
   normalises input ×(1/32768), runs all DSP in ±1, denormalises ×32768 on
   output. Skipping this made the limiter silence everything and broke Drive /
   Bitcrush-bits. **Any new absolute-threshold DSP must live inside the
   normalised section.**
2. **Denormals.** Feedback states (Comb/Delay/Reverb) flushed via `Dsp.Ftz`;
   decaying tails otherwise hit subnormal floats and spike CPU → dropouts.
3. **csproj** carries the six mandatory props (Build §1.2): `net10.0-windows`,
   `UseWPF=true`, `DebugType=none`, `DebugSymbols=false`,
   `GenerateDependencyFile=false`, `NoWarn=MSB3277`. Deploys `$(TargetPath)`
   (dll only) to `C:\Program Files\ReBuzz\Gear\Effects\` with
   `ContinueOnError=true`. AssemblyName `Pedal Drum Matrix.NET`.
4. **ValueDescriptions** must be an inline array literal in the attribute (a
   `static readonly string[]` reference won't compile) — that's why the 10-name
   palette literal is repeated on each Slot Type param.
5. **Append-only params** — reordering breaks every preset/song.

---

## 8. Not yet verified / known caveats

- **Never compiled against the ReBuzz SDK this session** (no SDK here). Pure C#
  files (`DrumFx.cs`, `Slot.cs`) are self-contained; the likely failure point if
  any is the SDK boundary in `PedalDrumMatrix.cs`: confirm `MachineDecl` field
  names (`ShortName`, `Author`, `MaxTracks`), `ParameterDecl` usage, and
  `host.MasterInfo.SamplesPerSec` / `SamplesPerTick` against an existing effect
  (pedal-comp / pedal-shaper). Also confirm the `BuzzGUI.Interfaces.dll`
  HintPath matches the install.
- Mode switches that flip topology (Drive hard/soft, Filter LP/HP) can click on
  toggle — no crossfade on Mode yet.
- BCR2000: use **absolute CC** for rotaries; set pushes to **toggle/latching**
  for the Off/On Mode params. Do NOT enable Inc/Dec (that's Doepfer-only;
  PeerCtrl §7.1).

---

## 9. Roadmap to v1.0

1. **Build + load test** against real ReBuzz; fix any SDK-boundary names.
2. **Lock the parameter layout** (currently 26 params) before shipping presets.
3. **Factory presets** `Pedal Drum Matrix.prs.xml` (auto-loads by name;
   Presets §3) — capture good slot combinations; this is how textures are
   recalled.
4. **GUI** (headline feature): labelled plugin-style slots showing each effect's
   Char/Mode meaning, plus an output meter. Needs `<UseWPF>true</UseWPF>` (already
   set) and a GUI factory (Core §26).
5. Nice-to-haves: global dry/wet (parallel-blend whole rack vs dry); expose
   AutoGain target + timing and Delay division / Gate duty as params; tiny
   crossfade on topology-changing Mode switches; serial/parallel rack mode.

---

## 10. Version history this session

- v0.1 scaffold: csproj + pass-through EffectBlock skeleton.
- v0.2: Bitcrush, Drive, Filter, RingMod, Comb, Delay, Gate.
- v0.3: full palette (added Stutter, Reverb).
- v0.4: per-slot Char (rotary) + Mode (switch) controls, BCR2000-mapped.
- v0.5: output limiter + denormal flush-to-zero.
- v0.6: **sample-scale fix** (±32768) — limiter no longer silences output;
  Drive/Bitcrush now correct.
- v0.7: Auto Gain leveler (peak-targeting, dual-detector).

---

## 11. Roster note (for the project's impact-analysis files)

When this lands as a repo, add a row to `ReBuzz_ManagedMachine_Notes_Roster.md`
(type **effect**) and consider a dedicated addendum. Depends on: Build §1.2/§1.3/
§2/§6.2; Core §32 (smoothing), §33 (WM_NOIO), §38 (sample scale); PedalComp §1
(scale), §2 (non-negative MinValue); Presets §3 (when presets ship). Stamp the
ReBuzz build it's tested on.
