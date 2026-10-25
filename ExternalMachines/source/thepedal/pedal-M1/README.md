# Pedal M1

A managed C# generator machine for ReBuzz: 8-voice polyphonic, dual-PCM
oscillator synthesis loosely modelled on the Korg M1's voice
architecture.

This is **architecture-faithful, not sample-faithful** — Pedal M1 reads
its oscillator samples from the ReBuzz wavetable, so the timbre comes
from whatever you load. Korg M1 ROM samples are not included (and are
not legally includable). Load your own piano / strings / brass samples
to get M1-style patches.

## Voice signal flow

```
  OSC1 ──┐
         ├── mix ── VDF (lowpass) ── VDA ── output
  OSC2 ──┘
              │              │
              │              │
   FilterEnv ─┘    AmpEnv ───┘
   LFO1 ──────┴──────────────┴── (also → pitch)
   LFO2 ──────┴──────────────┴── (also → pitch)
```

Each voice owns: two oscillator slots (each holding up to 4 multisample
zones with independent octave / transpose / detune / level), ADSR amp
envelope, ADSR filter envelope, gentle 2-pole lowpass filter, two LFOs
with phases that restart on note-on.

## Polyphony

8 voices, mapped one-to-one with pattern tracks (track 0 → voice 0,
etc.). For chords, place notes on different tracks at the same row.

The ReBuzz multi-track delivery bug (where simultaneous track writes
collapse to last-track-only) is handled internally — chord rows
deliver all notes correctly.

## Parameters

### Oscillator section (per OSC1 / OSC2)

| Param     | Range    | Notes                                    |
|-----------|----------|------------------------------------------|
| Wave      | 0..200   | Primary wavetable index (zone 1); 0 = off|
| Octave    | -2..+2   | Octave shift (applies to all zones)      |
| Transpose | -12..+12 | Semitone shift; default 0                |
| Detune    | -50..+50 | Cents; default OSC2 +3 (mild detune)     |
| Level     | 0..127   | Mix level                                |

### Multisample zones (per OSC1 / OSC2)

| Param   | Range    | Notes                                    |
|---------|----------|------------------------------------------|
| Wave 2  | 0..200   | Zone 2 wavetable index; 0 = off          |
| Split 2 | 0..127   | MIDI note where zone 2 takes over (def 60)|
| Wave 3  | 0..200   | Zone 3 wavetable index; 0 = off          |
| Split 3 | 0..127   | MIDI note where zone 3 takes over (def 72)|
| Wave 4  | 0..200   | Zone 4 wavetable index; 0 = off          |
| Split 4 | 0..127   | MIDI note where zone 4 takes over (def 84)|

See **Multisamples** below for how these compose.

### Mix
**OSC Mode** — OSC1 only / OSC2 only / Both.

### Pitch
**Portamento** — glide time between notes.

### VDF (Variable Digital Filter)

| Param          | Notes                                            |
|----------------|--------------------------------------------------|
| Cutoff         | 20 Hz .. 18 kHz exponential                      |
| Resonance      | Gentle range; never self-oscillates              |
| Key Track      | 64 = none, 127 = full, 0 = inverse               |
| Filter EG Int  | Envelope sweep depth (0 = off)                   |
| Filter Vel     | Velocity to filter cutoff                        |
| Filter A/D/S/R | Filter envelope shape                            |

### VDA (Variable Digital Amplifier)

| Param       | Notes                            |
|-------------|----------------------------------|
| Amp A/D/S/R | Amp envelope shape               |
| Amp Vel     | Velocity sensitivity             |

### LFO 1 / LFO 2
Each with: Wave (Triangle / Sawtooth / Square / S+H / Smooth), Speed,
Delay, Fade, plus three depth parameters routing to Pitch, VDF, and
VDA independently. LFOs are per-voice — phase restarts on note-on
(M1's default key-sync behaviour).

### Master
**Volume** — global output level.

### Track parameters
- **Note** — z=C-4, s=C#-4 etc.
- **Volume** — velocity, 0..127, default 127.

## Multisamples

Each oscillator can hold up to **4 keyboard-zoned waves**, mimicking the
M1 multisample model where one "patch" is built from several samples
each covering a part of the keyboard. Zoning by note range avoids the
chipmunk effect when a single sample is pitched too far from its native
key.

The semantics are *higher zones override lower for notes at or above
their split point*. Concretely, when you play note N:

- Zone 4 wins if Wave 4 is set and N ≥ Split 4
- Else zone 3 if Wave 3 is set and N ≥ Split 3
- Else zone 2 if Wave 2 is set and N ≥ Split 2
- Else the primary Wave (which itself may be 0 → silence)

A zone is *disabled* when its Wave parameter is 0. Setting all of Wave
2/3/4 to 0 reproduces v1.0 single-wave behaviour exactly — which is the
default, so existing v1.0 patches and presets continue to work
unchanged.

**Example:** to map a 3-sample piano multisample (root notes C2 / C4 /
C6) across the keyboard:

| Param  | Value | Effect                                   |
|--------|-------|------------------------------------------|
| Wave   | C2 sample | Plays for notes below C3             |
| Wave 2 | C4 sample | Plays from C3 up to B4 (default Split 2 = 60) |
| Wave 3 | C6 sample | Plays from C5 upward (default Split 3 = 72) |
| Wave 4 | 0 (off)   | —                                    |

Each sample plays at its native pitch in the centre of its zone and
shifts ±1 octave at the edges. Set the wavetable's RootNote on each
sample correctly and the pitch maths takes care of itself.

**A few practical notes.** Zone selection happens once at note-on
based on the played MIDI note — a portamento glide does not change
which zone is sounding mid-note (standard sampler behaviour). Per-OSC
Octave / Transpose / Detune / Level apply globally to the whole
oscillator regardless of which zone is selected — this matches the M1
"multisample-level" parameter convention. Zones cannot overlap or have
gaps in this scheme; each zone "claims" notes from its split upward
until a higher zone takes over.

## Workflow notes

**Wave edits require reload.** Pedal M1 caches a snapshot of each wave
the first time it's used and serves subsequent triggers from that
cache. The cache holds for the lifetime of the loaded machine — if you
swap, edit, or replace a wave in the ReBuzz wavetable, reload Pedal M1
to pick up the new data. This trade was deliberate: live invalidation
caused snapshot rebuilds on the audio thread mid-playback, which
clicked audibly.

**Velocity comes from the track Volume column.** Default is 127 (full).
The Amp Vel / Filter Vel parameters control how much velocity affects
amp and filter respectively; both are 0 by default, so out of the box
velocity is captured but doesn't do anything. Dial them up to get
velocity-responsive patches.

## Limitations

- **Amp envelope is ADSR.** The M1's actual amp envelope is six-stage
  with a break-point between Decay and Sustain. Most patches don't use
  it; current release ships with ADSR for both filter and amp.
- **No pitch envelope.** Deferred to a future release.
- **No GUI.** Rack only.
- **Mixer headroom is ~6 dB below nominal.** Output is internally
  scaled by 0.5 to leave room for two oscillators plus modulation. If
  patches feel quiet, raise master Volume.
- **Cubic interpolation across loop wraps may click slightly** on waves
  with rough loop points. Well-designed sample libraries are fine.
- **First trigger of a freshly loaded wave allocates the snapshot on
  the audio thread.** For typical sample sizes (a few seconds of mono
  audio) this is brief, but if you notice a glitch on the first hit of
  a brand-new patch, play one silent trigger to warm the cache before
  performing.
- **Multisample zones do not crossfade.** Zone selection is a hard
  switch at the split point. Crossfade-at-boundary may come in a future
  release if it's missed.

## Build

`Pedal M1.NET.csproj` produces `Pedal M1.NET.dll` in
`bin/$(Configuration)/`. The Release configuration auto-deploys to
`C:\Program Files\ReBuzz\Gear\Generators\` via the `DeployToReBuzz`
target. Adjust the path in the csproj if your ReBuzz install lives
elsewhere.

Mandatory build properties (no `.pdb` / no `.deps.json` / suppress
MSB3277) are set per the project's standing build conventions.

```
dotnet build "Pedal M1.NET.csproj" -c Release
```

## Files

```
pedalm1/
├── Pedal M1.NET.csproj
├── PedalM1.cs        main machine: parameters, setters, Work, voicing
├── Voice.cs          per-voice state: 2 oscs, envs, filter, LFOs
├── Wavetable.cs      snapshot cache
├── Envelope.cs       ADSR with click-free retrigger
├── Lfo.cs            per-voice LFO with delay + fade-in
├── Filter.cs         2-pole TPT lowpass (gentle, non-self-oscillating)
├── Interpolation.cs  linear + Catmull-Rom cubic
└── README.md         this file
```

## Version

v1.1
- Added 4-zone multisample support per oscillator (12 new parameters,
  defaults preserve v1.0 single-wave behaviour).
- Fixed `BuzzByteToMidi` octave offset so split values now match the
  pattern editor's note labels: with default Split 2 = 60, Wave 2 takes
  over starting at "c-4" as the parameter description says (previously
  this fired at "c-5" — one octave too high). Pitch playback is
  unaffected because the offset cancels in the played-vs-root math.
- Fixed multi-track chord playback on newer ReBuzz builds. The §14
  pvalues polling assumed `ConcurrentDictionary<int,int>`; ReBuzz ≥1827
  changed the backing field to `int[]`, so the cast returned null and
  only the last-triggered track played. Polling now tolerates both
  shapes.

v1.0
- Initial release: 8-voice polyphony, dual PCM oscillator, ADSR amp
  and filter envelopes, gentle 2-pole LP filter, two per-voice LFOs.
