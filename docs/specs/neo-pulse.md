# Neo Pulse — Specification

| | |
|---|---|
| Status | Implemented (PUL1, issue #203) |
| Date | 2026-11-26 |
| Kind | Control / Note generator (`Neo Pulse.NET.dll`, box name `Pulse`, `note_out`, no audio) |
| Scope | Eight-lane Euclidean and probability rhythm generator on note wires: Bjorklund step rings (1–32 steps), second Euclidean accent rings, per-hit chance, ratchets (1–4 with chance), clock dividers (1x–4x), swing, fill, humanize, and repeatable seeding |

---

## 1. Design

Neo Pulse is a clean-room eight-lane rhythm generator following **Neo Arp** (`Machines/NeoArp`) on ReBuzz's note-wire architecture (`docs/specs/note-wires.md`). It implements `INoteCapable` (`AcceptsNotes => false`, `SendsNotes => true`), emits sample-accurate `NoteMessage` structs via `INoteEmitter`, allocates 0 bytes per block, holds no locks, and emits no audio.

### 1.1 Bjorklund Euclidean Algorithm (`PulseEuclidean`)
- Clean-room iterative sequence-pairing Bjorklund implementation (Toussaint, 2005) over fixed 32-entry stack buffers (`0` heap allocation), returning a `uint` bitmask of `steps` bits (`1 <= steps <= 32`, `0 <= pulses <= steps`).
- Rotation right-shifts the `steps`-bit mask by `rot = clamp(Rotation, 0, steps - 1)` so step `i` reads base bit `(i - rot + steps) % steps`.
- Each lane computes a second Euclidean **accent ring** (`Accent pulses`, `Accent rot`) over the same `Steps`; any firing hit whose step is set in the accent ring uses `Accent vel`, otherwise `Velocity`.

### 1.2 Beat-Domain Clock, Swing, Ratchets & Humanize
- Base step length: `baseStepBeats = RateBeats[Rate] * FeelFactors[Feel]` (`1/1 .. 1/64` × `Straight 1.0`, `Dotted 1.5`, `Triplet 2/3`).
- Lane step length: `laneStepBeats = baseStepBeats * Divider` (`1x`, `2x`, `3x`, `4x`).
- Step `s >= 0` fires at beat `stepBeat(s) = (s + ((s & 1) != 0 ? Swing / 100.0 : 0.0)) * laneStepBeats` with slot duration `slotBeats = stepBeat(s + 1) - stepBeat(s)`.
- Both non-ratcheted (`Ratchet == 1` or failed ratchet roll) and ratcheted (`Ratchet > 1`) hits scale `Gate` (`5..100 %`) against their actual slot (`slotBeats` or `slotBeats / r`), so swung odd steps never overrun step `s + 1`. Due `NoteOff`s are always dispatched before same-sample `NoteOn`s.
- `Humanize time` (`0..20 ms`) adds positive seeded beat delay queued in a fixed 512-entry pending buffer; `Humanize velocity` (`0..30 %`) scales velocity symmetrically clamped to `1..127`.

### 1.3 Seeding, Active-Note Keying & Multi-Target Routing
- **Deterministic seed (`Seed`):** `1..9999` drives a stateless SplitMix64 hash of `(seed, lane, globalStep, subHit, purpose)`, guaranteeing identical bars across runs, mid-bar starts and loop jumps. `Seed = 0` (`Free`) mixes an atomic per-play session salt.
- **Unique `(channel, note)` active table:** Active notes are keyed in O(1) by `(channel << 7) | note` storing `(active, ownerLane, offBeat)`. Retriggering an active `(channel, note)` from any lane emits `NoteOff` first and overwrites `(ownerLane, offBeat)` in place, preventing stale `NoteOff`s under cross-lane sharing or humanized overlap. Muting or editing a lane flushes only active entries where `ownerLane == lane` and drops pending hits for that lane. Transport stop, loop jump, and `Panic()` flush all active notes at offset 0; nothing is sent while stopped.
- **Multi-target routing:** ReBuzz provides one note-out port per note-out machine (`NoteMessage` carries `Channel` 0–15 and `Note` 0–127). To send lane 3 to a different machine than lane 1, add a second Neo Pulse instance (both stay sample-locked to the exact same song beat clock) and wire each to its own target, or fan out one instance when targets filter by disjoint notes or MIDI channels.

---

## 2. Parameters (`Machines/NeoPulse/Params.def`, `StateVersion = 0`)

127 global parameters (`MaxTracks = 0`):
- **Clock (7):** `Rate` (`1/1 .. 1/64`), `Feel` (`Straight`, `Dotted`, `Triplet`), `Swing` (`0..75 %`), `Humanize time` (`0..20 ms`), `Humanize velocity` (`0..30 %`), `Fill chance` (`0..100 %`), `Seed` (`0 = Free`, `1..9999`).
- **Lane 1 … Lane 8 (8 × 15 uniform params):** `L{N} steps` (`1..32`), `L{N} pulses` (`0..32`), `L{N} rotation` (`0..31`), `L{N} chance` (`0..100 %`), `L{N} accent pulses` (`0..32`), `L{N} accent rot` (`0..31`), `L{N} note` (`0..127`, `note` unit), `L{N} velocity` (`1..127`), `L{N} accent vel` (`1..127`), `L{N} gate` (`5..100 %`), `L{N} ratchet` (`1..4`), `L{N} ratchet chance` (`0..100 %`), `L{N} divider` (`1x`, `2x`, `3x`, `4x`), `L{N} channel` (`1..16`), `L{N} mute` (`Off`, `On`).

---

## 3. Editor (`Machines/NeoPulse/NeoPulseEditor.cs`)

Single WPF + Avalonia source (`NeoEditor`, Control identity colour, light and dark theme safe):
- **Clock & Probability:** `Rate`, `Feel`, `Swing`, `Fill`, `Hum time`, `Hum vel`, `Seed`.
- **Lanes:** 8-row `InteractiveGraph` with lane selector badge, `M` mute toggle, `1..32` Euclidean step strip (hits, accents, live lit current step), and inline drag/wheel cells for `Steps`, `Pulses`, `Rot`, `Chance`, `Note`, and `Div`.
- **Lane Detail:** synced 8-tab `Pages` bar (`Lane 1` … `Lane 8`) exposing all 15 lane controls.

---

## 4. Factory Presets (8) & Listening Pack

1. `Four on the floor — Euclidean hats` (Neo Drums)
2. `West African — Bell E(7,12)` (Neo Drums)
3. `Clave — Son E(5,16)` (Neo Drums)
4. `Percussion — Sparse probability` (Neo Drums, lanes 7–8 muted under 15 % fill)
5. `Odd meter — E(5,8) groove` (Neo Drums)
6. `Hats — Ratchet flurry` (Neo Drums)
7. `Polyrhythm — 3 vs 4 vs 5` (Neo Drums)
8. `Melodic — Pentatonic lanes` (Neo Poly)

Listening pack (`listening-packs/203/`, local, git-ignored): 8 × 15.0 s 48 kHz 16-bit stereo WAVs at 120 BPM with `README.md` and 0 clicks.
