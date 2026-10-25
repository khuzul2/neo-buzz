# Pedal Tracker

A managed **generator** machine for [ReBuzz](https://github.com/wasteddesign/ReBuzz),
inspired by the BTDSys [Matilde Tracker](https://github.com/Buzztrax/buzzmachines/tree/master/Matilde).

A modern, .NET-based reimagining of the classic Buzz tracker: pattern-driven
sample playback with per-track effect commands, supporting **all bit depths
and sample rates** the ReBuzz wavetable can hold, with high-quality
interpolation up to a 32-tap polyphase windowed sinc.

> **Status: v1.7.3** — production build.  Compatible with ReBuzz 1827
> (and earlier builds).  Fixes a regression where multi-out trackers
> played only one track on ReBuzz 1827: the engine changed the private
> type of its parameter `pvalues` field from a dictionary to an array,
> which silently broke the reflection-based multi-track note recovery.
> The recovery now detects the field shape at runtime and works on
> both old and new builds.  Master `Mute` parameter and 16 per-track
> mute switches fade audio output via `Mute Inertia` while the machine
> renders continuously — voice state stays alive across mute toggles,
> so peer-controlled swap workflows (multiple trackers playing
> identical patterns alternating mute) stay perfectly in phase with
> the song.  The standard ReBuzz UI mute button on the machine still
> engages the host bypass for hard-mute kill — distinct tool from the
> parameter.  Other features: multi-output per track, Matilde Tracker
> pattern import, two effect columns per track, verified
> Matilde-compatible semantics for volume curve and `09xx` sample
> offset, dropped-setter recovery via raw `pvalues` polling, six
> interpolation modes, anti-click voice handling, multisample-aware
> layer selection, transport-stop fade-out, and live wavetable
> invalidation.

---

## Why managed?

Matilde is a 32-bit native Buzz machine and runs fine in ReBuzz via the native
machine bridge. Pedal Tracker is a from-scratch managed reimplementation in
C#/.NET, which buys us:

- Full 64-bit operation, no IPC bridge.
- 32-bit float audio path throughout (ReBuzz's internal format), so 24-bit
  source samples are *exactly* preserved.
- Trivial build & deploy (single DLL drop into `Gear\Generators`).
- All the audio-thread-safety patterns documented in
  `ReBuzz_ManagedMachine_Notes.md` (no allocations on hot path, multi-track
  note workaround for §14, atomic snapshot-based wave access).

---

## Pattern columns (per track)

| Column | Range | Default | Notes |
|---|---|---|---|
| **Note** | C-0 … B-9 | (none) | piano keyboard input; `NOTE-OFF` ends the voice |
| **Wave** | 0 … 200 | 0 = no change | wavetable slot selector with `↑↓` navigation |
| **Volume** | 00 … FE | 80 = unity | per-track gain, logarithmic scale (amplitude = (v/128)²); 0xFF = no change |
| **Cmd** | 00 … FE | 0 | first effect command |
| **Arg** | 00 … FE | 0 | argument for first effect |
| **Cmd2** | 00 … FE | 0 | second effect command (runs alongside the first) |
| **Arg2** | 00 … FE | 0 | argument for second effect |

The two effect columns run independently every row, so any two compatible
effects can be combined — e.g. `04` vibrato in column 1 with `0A` volume slide
in column 2 gives a tremolo-vibrato chord stab. If both columns set the same
continuous-state effect (two `04`s) the second-column settings win. If both
columns apply pitch deltas (e.g. `01` and `02`) they accumulate on `CurrentMidi`
additively. Portamento and vibrato auto-cancel only when the relevant command
is missing from *both* columns.

## Global parameters

| Parameter | Range | Default | Notes |
|---|---|---|---|
| **Master** | 0 … FFFE | 8000 = unity | master output gain |
| **Interpolation** | None / Linear / Cubic / B-Spline / Sinc 16 / Sinc 32 | Cubic | also available via right-click menu |

## Right-click menu

- **Reset all voices** — silences every track.
- **Reload wavetable** — clears the internal sample cache; useful after
  editing a wave in the wavetable UI.
- **Interpolation: …** — six choices, identical to the global parameter.

---

## Interpolation quality

| Mode | Taps | Cost (rel.) | Best for |
|---|---|---|---|
| **None** | 1 | 1× | Chiptune / glitch / SID-style |
| **Linear** | 2 | 2× | Drums / general-purpose at engine SR ≈ source SR |
| **Cubic** *(default)* | 4 | 4× | Catmull-Rom; great all-rounder |
| **B-Spline** | 4 | 4× | Smoother, slightly low-pass — good for pads |
| **Sinc 16** | 16 | 16× | Polyphase windowed sinc, Blackman-Harris — high quality |
| **Sinc 32** | 32 | 32× | Mastering quality, near-ideal anti-aliasing |

The two sinc filterbanks are precomputed at construction (4096 fractional
phases × N taps) so playback is a fixed dot-product per output sample with
no transcendental calls on the audio thread.

### Why this is "best possible"

A 32-tap windowed sinc with a Blackman-Harris window yields about 92 dB of
sidelobe rejection and a flat passband to ~95% of Nyquist. Stop-band aliasing
products lie below the noise floor of any 16-bit mix, so for practical purposes
this is the upper limit of what real-time sample playback can deliver. Going
to 64 or 128 taps gives diminishing returns audible only in null tests.

---

## Sample bit depth & sample rate handling

ReBuzz's wavetable can hold any bit depth from 8-bit through 32-bit float at
any sample rate from a few kHz up into the hundreds. Pedal Tracker's load
path (`Wavetable.cs`) tries the following access strategies in order:

1. **`GetDataAsFloat(channel)`** — preferred, used when the layer exposes a
   pre-converted float buffer.
2. **`GetDataAsShort(channel)`** — 16-bit PCM (the historical Buzz default).
3. **`GetDataAsInt(channel)` / `GetDataAs24(channel)`** — 24-bit / 32-bit
   integer, normalised by 2³¹ so 24-bit precision is preserved.
4. **Indexed accessor** as a last-resort fallback.

All paths produce a single `WaveSnapshot` holding two normalised `float[]`
buffers (mono samples are duplicated into both channels). The audio thread
never sees the original storage format — bit-depth handling happens once
on load.

Sample-rate conversion is just the increment math:

```
increment = (sourceRate / engineRate) * 2^((midiNote - rootNote) / 12)
```

So a 96 kHz source on a 48 kHz engine (or vice versa) is handled by exactly
the same code path as ordinary pitch shifting.

---

## Effect commands

The Cmd / Arg columns dispatch tracker-style effect commands. The Cmd column
holds the command number and Arg the argument byte — entering `0000` clears
any active effect on that voice.

Continuous effects (pitch slides, portamento, vibrato, volume slide) persist
across rows until replaced or cleared. One-shot effects (sample offset, set
volume) apply only on the row they appear on.

| Cmd | Arg | Effect | Implemented |
|---|---|---|---|
| `01` | `xx` | Pitch slide **up**, `xx` in 1/16-semitone units per row (16 = 1 semi/row) | ✅ |
| `02` | `xx` | Pitch slide **down**, same scale | ✅ |
| `03` | `xx` | Tone portamento — slide pitch toward note column at `xx`/16 semi/row | ✅ |
| `04` | `xy` | Vibrato — `x` speed (0–F), `y` depth (0–F, ~12.5¢ per unit) | ✅ |
| `09` | `xx` | Sample offset — start at `xx`/256 of the way through the sample (Matilde semantics: 80 = midpoint, 40 = 25 %, etc). No argument starts at the end. | ✅ |
| `0A` | `xy` | Volume slide — `x` up rate, `y` down rate per row (units of 1/64) | ✅ |
| `0C` | `xx` | Set volume — `0x80 = unity`, `0x00 = silent`, `0xFE ≈ 3.94× boost` (logarithmic, same scale as Volume column) | ✅ |
| `0E` | `1x` | Fine pitch slide up — `x` in 1/256 semitone units (one-shot) | ✅ |
| `0E` | `2x` | Fine pitch slide down — same scale | ✅ |
| `0E` | `9x` | Retrigger every `x` ticks while voice is sustaining | ✅ |
| `0E` | `Ax` | Fine volume slide up — `x` in units of 1/64 (one-shot) | ✅ |
| `0E` | `Bx` | Fine volume slide down — same scale | ✅ |
| `0E` | `Cx` | Note cut after `x` ticks (0EC0 = cut immediately) | ✅ |
| `0E` | `Dx` | Note delay — defer the trigger by `x` ticks | ✅ |

### Effect details

**`03` tone portamento** uses the note column as the *target*, not as a
re-trigger: a row with `C-5` and `0301` slides the current pitch up toward
C-5 at 1/16 semitone per row. A subsequent row with `0300` (or just `03`
with no argument) keeps sliding at the previously-set speed. To cancel the
slide before it reaches the target, set the effect to `0000`.

**`04` vibrato** modulates pitch sinusoidally around the *current* pitch.
The high nibble of the argument is speed (LFO rate), the low nibble is
depth. Either nibble being zero preserves its previous value, so `0407`
sets depth to 7 keeping speed, then `0480` sets speed to 8 keeping depth.
Vibrato auto-cancels on note re-trigger and when a different effect command
takes over.

**`0A` volume slide**: the high nibble adds to volume each row, the low
nibble subtracts. Up wins if both are non-zero. Volume is clamped to
`[0, 2.0]` (allowing modest overdrive past unity).

**`0E` extended commands** are sub-coded into the argument byte, with the
high nibble selecting the sub-command. Fine pitch slides (`1x`, `2x`) and
fine volume slides (`Ax`, `Bx`) are one-shot per-row variants of `01`/`02`/`0A`
at much finer resolution. Note cut (`Cx`), delay (`Dx`) and retrigger (`9x`)
are sub-tick-scheduled — they fire at exact sample positions inside the row,
not just at row boundaries. Note delay reuses the same anti-click envelope
as a normal trigger, so a delayed note on top of a still-sustaining voice
remains click-free.

Open Matilde's `mi.cpp` for the canonical reference behaviour.

---

## Build & install

```powershell
dotnet build PedalTracker.csproj -c Release
```

The DLL is written directly into `<BuzzDir>\Gear\Generators\`. Override the
target with:

```powershell
dotnet build PedalTracker.csproj -c Release /p:BuzzDir="D:\MyReBuzz"
```

Restart ReBuzz to pick up the new generator.

---

## Audio-thread safety checklist (verified against `ReBuzz_ManagedMachine_Notes.md`)

- ✅ No allocations in `Work()` (all buffers pre-allocated)
- ✅ No LINQ / no `Song.Machines` / `Song.Wavetable` enumeration on audio thread
- ✅ Wave snapshots are atomic reference reads
- ✅ Sinc filterbanks built at construction, never inside `Work()`
- ✅ Multi-track simultaneous-note workaround applied (notes §14)
- ✅ `MasterInfo` accessed only inside `Work()` and setters
- ✅ Tick-edge detection via `PosInTick` (notes §13)
- ✅ Bit-depth conversion happens once at sample-load time, never during playback

> **Note on wavetable access:** snapshots are constructed inside parameter
> setters, which technically run on the audio thread. The mitigation is
> that snapshots are only built once per slot — subsequent reads are
> reference loads on a `ConcurrentDictionary<int, WaveSnapshot[]>`. Live
> edits to a cached wave clear the snapshot via the wave's
> `PropertyChanged` event so the next note re-builds the snapshot, again
> only once.

---

## Files

| File | Purpose |
|---|---|
| `PedalTracker.csproj` | .NET 10 / x64 project; output to `Gear\Generators` |
| `PedalTracker.cs` | Machine class, parameters, setters, `Work()`, context menu |
| `Voice.cs` | Per-track voice state + per-sample render loop |
| `Interpolation.cs` | Six interpolation kernels incl. polyphase sinc tables |
| `Wavetable.cs` | `IWave` / `IWaveLayer` snapshot loader, multi-layer aware |

---

## Roadmap

### v1.7.3 — ReBuzz 1827 compatibility fix

ReBuzz 1827 changed the private type of `ParameterCore.pvalues` from
`ConcurrentDictionary<int,int>` to `int[256]`.  Pedal Tracker reads
this field by reflection to recover multi-track notes (ReBuzz delivers
only one `SetNote` per parameter per tick, so simultaneous notes on
several tracks need recovering from the raw pvalues).  The old code
used an `as ConcurrentDictionary<int,int>` cast that silently returned
null on the new array type, disabling the recovery — so multi-out
trackers played only the last track that wrote the Note parameter.

The reflection now detects the backing field shape at runtime
(`int[]`, `ConcurrentDictionary`, or any `IDictionary`) and builds an
appropriate reader, so the same DLL works on ReBuzz 1827 and earlier
builds.  The new sub-tick timing and AudioBufferFillThread settings
in 1827 were investigated and ruled out as causes; they change audio
block sizing but not note delivery.

### v1.7 — always-render mute design

Rolled back the zero-CPU bypass that v1.5–v1.6.4 used to drive
`IMachine.IsMuted` from the master `Mute` parameter.  The bypass
worked by setting `IsMuted = true` once the master mute fade
reached zero, which engaged ReBuzz's host-side skip of `Work()`
when 'Process muted machines' was off.  CPU savings were real
but came with an unavoidable cost for peer-controlled swap
workflows: during the bypass, `Tick()` keeps firing (delivering
per-row setters) but `Work()` doesn't (no Render), so voice
state freezes mid-render and the unmute resume couldn't smoothly
pick up from where the live peer was playing.  Beats would jar
on swap.

v1.7 design: `MuteParam` and the 16 per-track mute switches now
fade output gain via `_muteGain` (and per-track `_trackMuteGain[]`)
in the publish loops while `Work()` runs continuously.  Voice
state stays alive across mute toggles.  Swap workflows hear
crossfades between continuously-rendered identical signals,
matching what a downstream gain machine would produce.

The `Mute Inertia` parameter still controls the fade time for
both master and per-track mutes.  Pattern automation, peer
control, and the UI parameter properties dialog all behave as
before from the user's perspective; only the host-side bypass
is no longer engaged.

The standard ReBuzz UI mute button on the machine remains a
hard-kill switch — it still engages the host's per-machine
bypass at `MachineWorkInstance.cs:229` because that's what the
button does in ReBuzz core.  Use the parameter for smooth
fades, the UI button for instant silence.

CPU impact: with v1.6.2's interpolation bypass keeping voices
at root pitch nearly free, the always-render cost for typical
drum-tracker usage is negligible.  For projects with many
muted trackers (e.g. 16+ instances on standby), CPU usage will
be marginally higher than v1.6.x.

### v1.6.3 — bypass-resume bug fix (no longer relevant after v1.7)

In v1.5/v1.6 the master mute used ReBuzz's per-machine `IsMuted` to
engage zero-CPU bypass once the fade reached zero.  This works fine
for a single instance, but with peer control toggling mute on
multiple instances simultaneously (e.g. flipping between groups of
{a,c,e,g} and {b,d,f,h} trackers), audio could "skip" — beats not
switching cleanly even with identical patterns.

The cause: when ReBuzz's bypass is active, our Work() doesn't run,
but `Tick()` still fires every audio buffer.  Per-row pattern setters
keep delivering data, accumulating stale `PendingNote` / `PendingWave`
/ `PendingCmd` etc. in voices over potentially many rows.  Active
voices are also frozen mid-render — their `Pos` doesn't advance.
On unmute, the latest-but-stale pending state would trigger a note
from a row that's now in the past, and any frozen voice would resume
playing the wrong moment of its sample.

Fix: detect resume from bypass (previous Work observed `IsMuted=true`,
this Work sees `false`, AND no Work cycles ran during the muted
period) and call `Voice.DiscardForResume()` on every voice — clears
all `HasNew*` flags, deactivates voices, clears active effect /
vibrato / portamento / sub-tick scheduling state, but PRESERVES
`ActiveWaveSlot` (so a note-only row after resume still knows which
sample to play) and per-track `Volume` (so the column setting
survives the mute).  Trade-off: at most one row of pattern data is
lost across the mute boundary, in exchange for clean phase-aligned
resume and zero CPU during the muted period.

### v1.6 — per-track mute switches

**Per-track mute switches**

Sixteen new global parameters (`Mute Track 1` through `Mute Track 16`)
that gate each track's audio independently in both the master mix and
the per-track multi-out channel.  Use them to mute the kick and snare
drums while leaving hi-hats playing — a core tracker workflow.

The 16 switches share the existing `Mute Inertia` time constant, so
they all fade with the same character as the master mute.  They are
deliberately independent of the master `Mute` parameter and the
`IMachine.IsMuted` UI button — switching them off does not trigger
ReBuzz's zero-CPU bypass, since the machine as a whole is still
producing audio (just not for the muted tracks).

Switches for tracks beyond the current `TrackCount` are still
declared as parameters (the global parameter list is fixed at
machine creation), but have no audible effect since no voices are
ever active on those slots.

### v1.5 — master mute with inertia

**Master mute with configurable inertia**

Two new global parameters: `Mute` (switch) and `Mute Inertia` (0-500ms,
default 50ms).  Toggling `Mute` from a pattern, the right-click menu, or
automation triggers a smooth fade-out over the inertia time and then
pulls down ReBuzz's per-machine mute flag.  When the user has
**Settings → Audio → Process muted machines** turned off (the default in
modern ReBuzz), this means the machine costs zero CPU once fully muted.
Un-muting clears the bypass first then fades back up.

The inertia parameter also covers extreme values: 0ms is a hard mute
(may click on rich content but predictable), 500ms is a long ambient-
style swell.  Mute affects all outputs (master AND per-track), so
externally-routed chains go silent in sync with the master.

The standard ReBuzz UI mute button still works and stays in sync with
the parameter — clicking it reflects in the parameter and vice-versa.
Important caveat: clicking the UI mute button hard-mutes (no fade)
because ReBuzz's bypass check happens before our render path can
react.  Use the parameter for smooth fades, the UI button for instant
kill.

Loading a saved song that was muted starts the machine fully silent
instead of fading from 1.0 to 0.0 (the fade only applies to
user-triggered mid-session toggles).

### v1.4 — multi-output per track

**Multi-output per track**

Each Pedal Tracker instance now exposes `Master` (the summed mix, output 0)
plus one stereo output per track (outputs 1..N).  The runtime output count
follows `TrackCount` automatically — adding a track grows the output list,
removing one shrinks it.  ReBuzz only allocates buffers for outputs you've
actually connected, so unused tracks cost nothing extra to render.

Connect the Master output as before for the normal stereo mix.  For
per-track routing, right-click the connection point and pick from the
list — outputs are labelled "Master", "Track 1", "Track 2", and so on.
Master volume only scales the master output; per-track outputs are dry,
so external effect chains see the clean per-voice signal independent of
the Master parameter.

Implementation notes:

- Switched from the single-stereo Work overload to the multi-out
  `bool Work(IList<Sample[]> output, int n, WorkModes mode)` overload —
  ReBuzz auto-detects which one you defined and sets `MULTI_IO` accordingly
- Each voice renders into its own dedicated stereo scratch pair (instead
  of a shared mix bus); the publishing step then sums them into the master
  and copies each individually into its corresponding per-track output
- Mono samples render as L=R (centre-panned mono) on their track output;
  stereo samples render with their actual L/R content — matches the
  existing master-mix behaviour
- Channel naming via `GetChannelName(bool input, int index)` so the
  connection menu shows readable labels

### v1.3 — Matilde import

Right-click any Pedal Tracker instance and pick a Matilde Tracker source
from the list. The importer:

- Wipes existing patterns on this Pedal Tracker instance (replace mode)
- Expands track count to match source if needed
- Creates one destination pattern per source pattern, preserving names
  and length
- Translates Note, Wave, Volume, Cmd, Arg, Cmd2, Arg2 columns
  per-row (most values are identity since both machines share the same
  byte conventions)
- Logs unknown effect command bytes as warnings (e.g. Matilde's combined
  `05`/`06` commands aren't split — use the two-column form natively if
  you need them)
- Leaves the song sequence alone — wire the new Pedal Tracker into your
  sequence manually

The importer reaches into Modern Pattern Editor's internal data model via
reflection, since MPE doesn't expose a "copy patterns from machine A to
machine B" API publicly. Failures yield a clear console message rather
than silent corruption. Tested against Modern Pattern Editor as shipped
with ReBuzz 1818-preview.

### v1.2 — production hardening

- Verified Matilde-compatible volume curve (amplitude = (v/128)²)
- Verified Matilde-compatible `09xx` sample offset (proportional, not absolute)
- Wave-column change resets track volume to unity (matches Matilde's `mi.cpp`)
- Row-edge clearing of carried effects (Matilde reads each row fresh)
- Transport-stop fade-out via `IBuzz.Playing` polling
- Loop-wrap kick reliability: dropped-setter recovery for Wave/Volume/Cmd/Arg
  via `pvalues` polling from inside the Note setter (extends the §14 multi-
  track-note workaround)
- Row-edge detection switched from `MasterInfo.PosInTick` to
  `Buzz.Song.PlayPosition` (more reliable under sub-tick timing)
- Diagnostic instrumentation removed for production performance

### v1.1 — two effect columns per track

**Audio engine**
- 32-bit float internal mixing throughout
- Six interpolation modes: None, Linear, Cubic (Catmull-Rom), B-Spline, Sinc 16, Sinc 32 (polyphase windowed sinc, Blackman-Harris window, ~92 dB sidelobe rejection)
- Forward and ping-pong looping
- Anti-click envelope on every note-on / note-off / retrigger (~1.5 ms linear fade)
- Multisample layer selection — picks the layer whose `RootNote` is the largest value ≤ played note
- Sample-rate-independent playback: any source rate, any engine rate
- All wavetable bit depths handled (Int16, Int24, Int32, Float32)
- Live wavetable invalidation via `IWave.PropertyChanged`

**Tracker effects**
- `01` / `02` pitch slides (continuous, per-row)
- `03` tone portamento — slide-to-note
- `04` vibrato (sine LFO around current pitch)
- `09` sample offset (one-shot)
- `0A` volume slide
- `0C` set volume (one-shot)
- `0E1x` / `0E2x` fine pitch slides
- `0EAx` / `0EBx` fine volume slides
- `0E9x` retrigger
- `0ECx` note cut
- `0EDx` note delay

**Integration**
- Pattern-editor parameter columns: Note, Wave, Volume, Cmd, Arg
- Right-click menu with diagnostics, test-trigger, interpolation switcher
- `DescribeValue` populates the ReBuzz status bar with friendly effect/value descriptions
- Audio-thread-safe wavetable access (no allocations in `Work()`)

### Future versions

- Per-track **Pan** parameter
- Honor `IWave.GetEnvelope` for volume/pan envelopes set in the F9 wavetable view
- Combined Matilde-style effects (`05` portamento+volslide, `06` vibrato+volslide)
- Per-voice ADSR envelopes
- MIDI keyboard input for live audition
- Dedicated WPF Attributes window
- Per-track VU meters
- Sample browser integrated with the wavetable

---

## Licence

MIT (placeholder — adjust to suit your distribution).
