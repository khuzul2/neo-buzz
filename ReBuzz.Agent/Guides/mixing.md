# Mixing

You can't hear, so you measure: probes on every part, the master analyzed after every change, versions kept as snapshots. The numbers are starting points; the measurements decide.

## Routing that mixes well

- Each source → its inserts (EQ, compressor, drive) → Master. Name machines by role ("Kick", "Bass", "Pad", "Bass Duck", "Hall").
- The **fader** is the connection gain into Master: `connection_set(changes=[{"from": "Bass", "to": "Master", "gain_db": -4}])`. A Neo synth's `Volume` is its trim.
- A **send** is a second connection from the source to a reverb at 100 % mix: `connect(from="Pad", to="Hall", gain_db=-10)`. Its level is that connection's gain, independent of the fader: when you lower a fader, lower its send by the same dB to keep the wet/dry ratio.
- **Probes** capture parts during a render without changing the song: `render(from=…, to=…, probes=["Kick", "Bass", "Pad"])`. Each probe is a machine's output after its first connection's gain, aligned with the master, with its own id ("r4:Bass") for `analyze`. Probe the last machine of a chain (the "Bass Duck" compressor, not "Bass") to measure the processed part.
- `analyze_mix(section="Drop")` probes every track feeding Master in one call and reports per-track loudness, peaks, crest and bands, the masking pairs, the headroom and the biggest problems, worst first. Run it before and after each stage.
- `machine_list` shows every connection with its gain and pan; read it before changing gains, since `connection_set` takes absolute values.

## Gain staging

| Point | Target | Measured with |
|---|---|---|
| Each source, on its probe | sample peak -16 to -8 dBFS | `render(probes=[…])`: each probe's peaks |
| The loudest source (kick or bass) | peak about -10 dBFS | the same |
| Into level-sensitive effects (Neo Comp, Neo Drive) | the same; set thresholds from a measurement, not from the preset | the probe of the machine feeding them |
| Master before mastering | true peak -6 dBTP or lower; integrated about -23 to -16 LUFS | `render`'s loudness and peaks |
| Anywhere | 0 clipped samples | `analyze(source=…, metrics=["peaks"])` |

- Neo synths start at `Volume` −6 dB; their presets are levelled so a chord's loudest 50 ms sits between −30 and −6 dB RMS.
- The sum decides: parts peaking together add up, so if the master peaks above −6 dBTP, lower everything by the excess. Two equal, unrelated parts are +3 dB; the same sound twice, in phase, is +6 dB; a 4-note chord is about +6 dB over one note; 7 unison voices about +8.5 dB.
- Too hot at the master: lower every connection into Master by the same dB in one `connection_set`, not the loudest part alone (that changes the balance).

## Balance

Start with the most important part at its level (the kick in dance music, the lead or vocal line in pop) and bring the others up under it. Measure each part's integrated loudness on its probe over the loudest section and subtract the master's.

| Part | Dance (LU under the master) | Pop and songs |
|---|---|---|
| Kick | −3 to −6 | −6 to −9 |
| Bass | −4 to −7 | −6 to −9 |
| Snare, clap | −6 to −10 | −6 to −9 |
| Hats, shakers | −14 to −20 | −15 to −20 |
| Percussion | −12 to −18 | −14 to −20 |
| Lead or vocal line | −5 to −9 | −2 to −5 |
| Chords, keys | −8 to −12 | −8 to −12 |
| Pads | −10 to −16 | −10 to −16 |
| Risers, FX | −12 to −20 | −14 to −20 |
| Reverb returns | −12 to −20 | −12 to −20 |

Parts add up in power, so they can't all sit at the loud end of their range: if most do, something has to come down.

**By reference.** When the user gives a reference track (a WAV path), `compare(a="r6", b="C:/Refs/track.wav")` lists loudness, dynamics, stereo and band differences, with the bands level-matched so only tone differences show. `analyze(source="C:/Refs/track.wav", metrics=["loudness", "spectrum", "stereo", "dynamics"])` gives its full numbers.
- Compare shapes, not levels: level-matched band differences within ±2 dB are close; 3 dB off is a real difference.
- Tilt within ±0.5 dB/oct of the reference. Without one: −2 to −3.5 dB/oct is balanced (third-octave levels, pink noise 0); above −2 bright, below −4 dark.
- Stereo: compare correlation and the per-band width.
- A mastered reference is louder and denser: compare loudness and PLR only after mastering.

## Frequency slots

| Part | Body | Character | Trouble | High-pass from |
|---|---|---|---|---|
| Kick | sub 45–65 Hz, punch 80–120 Hz | click 2–5 kHz | boxy 250–500 Hz | 25–30 Hz |
| Bass, 808 | fundamentals 40–120 Hz (E1–B2) | harmonics 600 Hz–1.5 kHz, what small speakers hear | mud 150–300 Hz | 25–35 Hz |
| Snare | body 150–250 Hz | crack 2–5 kHz, snares 6–10 kHz | box 400–800 Hz | 80–120 Hz |
| Clap | 800 Hz–1.5 kHz | 2–5 kHz | 300–500 Hz | 150–250 Hz |
| Hats, cymbals | 6–12 kHz | air 12–16 kHz | harsh 3–5 kHz | 300–600 Hz |
| Pads | 200 Hz–5 kHz | | mud 250–500 Hz | 150–250 Hz |
| Chords, keys, piano | 100 Hz–5 kHz | definition 2–4 kHz | 250–400 Hz | 80–150 Hz |
| Lead synth | 300 Hz–6 kHz | presence 2–4 kHz | harsh 3–5 kHz | 150–300 Hz |
| Plucks, arps | 500 Hz–8 kHz | | | 200–400 Hz |
| Vocal | fundamental 85–180 Hz (male), 165–255 Hz (female); body 200–500 Hz | presence 3–5 kHz, air 10–16 kHz | sibilance 5–9 kHz | 80–120 Hz |
| Reverb returns | | | mud below 300 Hz, fizz above 10 kHz | 200–400 Hz |

- One of kick and bass owns 40–60 Hz. The other gives way: sidechain ducking, a high-pass at 40–50 Hz, or a different note. Tune the kick to the key's root or fifth.
- **Finding fights.** `analyze_mix` lists the masking pairs and their bands, worst first. To see one, `plot(kind="spectrum", sources=["r4:Kick", "r4:Bass"])` overlays the two. By hand: analyze each probe's spectrum over the same range (`analyze(source="r4:Bass", metrics=["spectrum"])`); two parts fight in a band when both play at once, both are within 10 dB of their own loudest band there, and they are within 6 dB of each other. Cut the less important one there (−2 to −4 dB, Q 1–2), or move it: another octave, a pan, sidechain.

## EQ starting points

Neo EQ is being built; its parameter names aren't final. Read them with `machine_describe(machine=…)` and set each band's type, frequency, gain and Q (or slope) from this table. Until it's installed: the synths' own filters (Neo Poly `F2 type` HP), Neo Verb's `Low cut` and `High cut` on returns, Neo Drive's `Low cut`, `High cut` and `Tone` (it still saturates a little).

| Part | Move | Frequency | Gain | Q or slope |
|---|---|---|---|---|
| Kick | high-pass | 25–30 Hz | | 24 dB/oct |
| | cut box | 300–400 Hz | −3 to −6 dB | 1.5–2 |
| | click | 3–5 kHz | +2 dB | 1 |
| | weight, only if thin | 50–60 Hz | +2 dB | 1.5 |
| Bass | high-pass | 25–35 Hz | | 24 dB/oct |
| | mud, if any | 200–300 Hz | −2 to −4 dB | 1.4 |
| | small-speaker definition | 700 Hz–1 kHz | +2 dB | 1 |
| Snare | high-pass | 80–100 Hz | | 12 dB/oct |
| | body | 200 Hz | +2 dB | 1.4 |
| | box | 500–800 Hz | −3 dB | 2 |
| | crack | 4–5 kHz | +2 to +3 dB | 1 |
| Hats | high-pass | 300–500 Hz | | 12 dB/oct |
| | harshness | 3–5 kHz | −2 dB | 2 |
| | air | 10 kHz shelf | +2 dB | |
| Pads | high-pass | 150–250 Hz | | 12 dB/oct |
| | mud | 300–500 Hz | −2 to −3 dB | 1 |
| | room for the lead | 2–4 kHz | −2 dB | 1 |
| Lead | high-pass | 150–200 Hz | | 12 dB/oct |
| | presence | 2–3 kHz | +1.5 dB | 1 |
| Keys, piano | high-pass | 80–150 Hz | | 12 dB/oct |
| | mud | 250–400 Hz | −2 dB | 1.4 |
| Vocal | high-pass | 80–100 Hz | | 12 dB/oct |
| | mud | 200–400 Hz | −2 dB | 1.4 |
| | presence | 3–5 kHz | +2 dB | 1 |
| | sibilance | 6–8 kHz | −3 dB | 4 |
| Reverb return | high-pass, low-pass | 200–400 Hz, 6–10 kHz | | 12 dB/oct |

- Cut narrow (Q 2–8 for resonances), boost wide (Q 0.7–1.4).
- More than +6 dB of boost or −8 dB of cut means the sound or the arrangement is wrong: fix it there.
- After each move, the band moves by about the gain you set; if it doesn't, the energy isn't where you thought.

## Compression starting points (Neo Comp)

**Threshold from a measurement.** The reduction at a level L over the threshold T is (L − T) × (1 − 1/ratio). Measure the part's loud level on its probe (`analyze(source=…, metrics=["dynamics"])`, `rms_percentiles_dbfs.p95`) and set T = p95 − reduction × ratio ÷ (ratio − 1): for 4 dB at 4:1, T = p95 − 5.3 dB. Then measure after the compressor: the crest factor falls by roughly the reduction on peaks.

| Part | Mode | Ratio | Attack | Release | Knee | Reduction | Mix | Start from |
|---|---|---|---|---|---|---|---|---|
| Kick | Clean | 4:1 | 10–30 ms | 50–100 ms | 6 dB | 3–6 dB | 100 % | |
| Snare | FET | 4:1 | 5–10 ms | 80–150 ms | 2 dB | 3–6 dB | 100 % | |
| Bass, even | Opto | 4:1 | 10 ms | 800 ms or Auto release | 18 dB | 3–6 dB | 100 % | "Opto — Bass Leveler" |
| Bass, aggressive | FET | 8:1 | 1 ms | 150 ms | 4 dB | 4–8 dB | 100 % | "FET — Bass Tamer" |
| Lead or vocal line | FET, then Opto | 4:1, then 3:1 | 0.25–3 ms, then 10 ms | 50–80 ms, then 1.5 s | 2, then 18 dB | 3–6, then 2–3 dB | 100 % | "FET — Vocal Grab", "Opto — Smooth Vocal" |
| Pads | Opto | 2:1 | 10 ms | 1.5 s | 18 dB | 1–3 dB | 100 % | |
| Plucks, keys | Clean | 3:1 | 5–15 ms | 100 ms | 6 dB | 2–4 dB | 100 % | "Clean — Gentle Leveler" |
| Drum bus | Glue | 4:1 | 3–10 ms | 100 ms or Auto | 3 dB | 2–4 dB | 100 % | "Glue — Drum Bus" |
| Parallel drums | FET | 8:1 | 1 ms | 80 ms | 0 dB | 10–15 dB | 25–40 % | "Parallel — New York Drums" |
| Mix bus | Glue | 2:1 | 10–30 ms | Auto release | 6 dB | 1–2 dB | 100 % | "Glue — Mix Bus" |

- Slow attack (10–30 ms) keeps transients; fast attack (under 1 ms) flattens them. Release short enough to recover before the next hit: under the note spacing.
- `Auto makeup` adds the gain a −12 dBFS signal loses; otherwise set `Makeup` to match the loudness before and after (compare the probe's integrated loudness).
- `SC low cut` 80–150 Hz on a bus stops the kick and bass from driving all the reduction.

Bass under control (Neo Comp after the bass):

| Parameter | Value | Why |
|---|---|---|
| Mode | Opto | smooth, program-dependent |
| Threshold | -20 dB | 3–6 dB of reduction on the loud notes |
| Ratio | 4:1 | |
| Knee | 18 dB | |
| Attack | 10 ms | the pluck stays |
| Release | 800 ms | |
| Auto makeup | On | |

## Sidechain with Neo Comp

Ducking the bass (or pads) under the kick with Neo Comp's second input.

1. `machine_add(gear="Neo Comp", name="Bass Duck", insert_after="Bass")`.
2. `preset_load(machine="Bass Duck", preset="Sidechain — Kick Duck")`, or set:

   | Parameter | Value | Why |
   |---|---|---|
   | Mode | Clean | |
   | Sidechain | External | detect from input 2 |
   | Threshold | -30 dB | below the kick's level at input 2 |
   | Ratio | 8:1 | |
   | Knee | 0 dB | |
   | Attack | 1 ms | ducks on the kick's attack |
   | Release | 150 ms | recovers before the offbeat at 124 BPM |

3. `connect(from="Kick", to="Bass Duck", input="Sidechain", gain_db=0)`. The kick keeps its own connection to Master.
4. Release against tempo: back up before the next 8th note. An 8th lasts 30000 ÷ BPM ms: 300 ms at 100 BPM, 242 at 124, 234 at 128, 214 at 140, 172 at 174. Release at 50–70 % of it.
5. Depth: bass 4–8 dB, pads and chords 2–5 dB, the audible "pump" 8–12 dB ("Sidechain — Pump": ratio ∞, release 300 ms).
6. Ghost trigger, to keep ducking where the kick stops (breakdowns): `machine_add(gear="Neo Poly", name="SC Kick", connect_to="none")` with the kick recipe from the sound-design guide, a note on every beat, and `connect(from="SC Kick", to="Bass Duck", input="Sidechain")`. Never connect it to Master.
7. Measure: `render(from=…, to=…, probes=["Bass Duck"])`, then `analyze(source="r5:Bass Duck", metrics=["dynamics"])`. `rms_over_time` should dip by the planned depth on each kick; the step is in `step_seconds` (render 2–4 bars so the steps are short).

The threshold follows the trigger: set it at the kick's peak level arriving at input 2 minus depth × ratio ÷ (ratio − 1). The kick's probe reads it after its Master connection; add the difference between the sidechain connection's gain and that one.

## Space: sends with Neo Verb

One or two send reverbs for the whole mix: a short one (Room or Plate, 0.6–1.8 s) for drums and percussion, a long one (Hall, 2–3.5 s) for pads, leads and vocals.

1. `machine_add(gear="Neo Verb", name="Hall", connect_to="Master")`, then `preset_load(machine="Hall", preset="Send — Hall (100% wet)")`, or:

   | Parameter | Value | Why |
   |---|---|---|
   | Mode | Hall | |
   | Decay | 2.4 s | about a bar and a quarter at 124 BPM |
   | Pre-delay | 30 ms | a 1/64 note at 124 BPM: the dry attack stays clear |
   | Low cut | 250 Hz | no mud |
   | High cut | 8 kHz | no fizz |
   | Mix | 100% | a send is all wet |

2. `connect(from="Pad", to="Hall", gain_db=-10)`. Send levels against the part's own: pads and leads −12 to −8 dB, snare −15 to −10, percussion −18 to −12, hats −20 or none, kick and bass none.

**Pre-delay against tempo** (ms): 60000 ÷ BPM × the fraction of a beat.

| BPM | 1/64 note | 1/32 note | 1/16 note |
|---|---|---|---|
| 90 | 41.7 | 83.3 | 166.7 |
| 100 | 37.5 | 75.0 | 150.0 |
| 110 | 34.1 | 68.2 | 136.4 |
| 124 | 30.2 | 60.5 | 121.0 |
| 128 | 29.3 | 58.6 | 117.2 |
| 140 | 26.8 | 53.6 | 107.1 |
| 174 | 21.6 | 43.1 | 86.2 |

- Decay against tempo: the tail should have fallen by the next downbeat or chord change. Drums 1–2 beats (0.5–1 s at 124 BPM), pads and leads 1–2 bars (1.9–3.9 s at 124).
- Depth: farther is quieter, darker, wetter and with less pre-delay; closer is drier, brighter, with more pre-delay.
- Delays: there is no Neo Delay yet; `gear_list(search="delay")` lists the installed ones. Times: 1/8 = 0.5 beat, dotted 1/8 = 0.75, 1/4 = 1 (ms = 60000 ÷ BPM × beats); feedback 20–40 %; filter the repeats to 300 Hz – 5 kHz.

## Stereo

- Mono below about 120 Hz: kick, bass and sub centred (pan 0). Basses on Neo Poly or Neo Wave: `Unison` 1 or `Uni spread` 0%.
- Neo Utility (being built; read its names with `machine_describe`): mono below 120 Hz on basses or the mix bus, width 100–130 % on pads, polarity flip, channel swap.
- Pans with `connection_set`: hats ±0.2–0.4, percussion ±0.3–0.6, doubled or answering parts ±0.5–0.8 on opposite sides; lead and snare centre; pads centre with width from unison.
- Targets on the master (`analyze(source=…, metrics=["stereo"])`): correlation 0.2–0.9, never below 0; `sub` and `low` correlation above 0.9 and width under 0.1 (the analyzer warns above 0.25 or under 0.5); mono loss under 3 dB; balance within ±1.5 dB.

## Order of work

1. Static balance with faders only. 2. High-pass everything that isn't kick or bass. 3. Kick against bass: who owns the sub, sidechain. 4. EQ by slots. 5. Compression. 6. Space. 7. Stereo. 8. Rides: automation per section (`Volume` on the Neo synths with `automation_write`). 9. Headroom and the final checks.

`snapshot_save(name="mix 1 balance")` before each stage, `snapshot_diff(a="mix 1 balance")` after, and `undo` or `snapshot_restore(name=…)` when a stage measures worse. `compare(a="r7", b="r8")` puts the two renders side by side. Compare versions at the same loudness (within 0.5 LU), or louder will seem better.

## The checks

| Check | Call | Pass |
|---|---|---|
| Everything at once | `analyze_mix(section=…)` | its problems list is empty, or each one is explained |
| Clipping | `render(probes=[…])` | 0 clipped samples on the master and every probe |
| Headroom | `render` | master true peak −6 dBTP or lower |
| Level | `analyze(source=…, metrics=["loudness"])` | about −23 to −16 LUFS integrated before mastering |
| Tonal balance | `analyze(source=…, metrics=["spectrum"])` | tilt −2 to −3.5 dB/oct; no band 3 dB over the slope unless the genre wants it |
| Stereo | `analyze(source=…, metrics=["stereo"])` | correlation 0.2–0.9; `sub` and `low` above 0.9; mono loss under 3 dB |
| Dynamics | `analyze(source=…, metrics=["dynamics"])` | PLR 12–18 dB before mastering; transients present |
| Parts | each probe's peaks and loudness | peaks −16 to −8 dBFS; loudness in the balance table's range |
| Fights | each probe's `spectrum` bands | no two main parts within 6 dB in their main band at once |

## Checklist

- [ ] No clipping on the master or any part: `render(probes=[…])` reports 0 clipped samples everywhere.
- [ ] Headroom for mastering: the master's true peak is -6 dBTP or lower in `render`'s measure.
- [ ] Balance: each probe's integrated loudness against the master sits in the balance table's range (`analyze(source="rN:Part", metrics=["loudness"])`).
- [ ] No part masks another in its main band: `analyze_mix(section=…)` lists no masking pair among the main parts (or each is resolved by sidechain, EQ or arrangement).
- [ ] Kick and bass don't fight: the sidechain dip shows in `analyze(source=…, metrics=["dynamics"])` on the ducked part.
- [ ] Tonal balance: `analyze(source=…, metrics=["spectrum"])` reads a tilt of −2 to −3.5 dB/oct, or matches the reference within ±0.5.
- [ ] Mono lows and safe width: `analyze(source=…, metrics=["stereo"])` shows correlation 0.2–0.9 and `sub`/`low` above 0.9.
- [ ] Reverb returns are filtered and timed: `param_get(machine=…)` shows Low cut 200–400 Hz and a pre-delay from the tempo table.
- [ ] Every change was kept only if it measured better: `snapshot_diff(a=…)` lists the changes since the first snapshot.
- [ ] Saved: `song_save(path=…)`.
