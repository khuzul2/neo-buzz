## How it sounds

An algorithmic reverb with four spaces, dense and smooth (no flutter, decorrelated stereo tail), with exact decay times: the mid-band T60 measures within −5 % … +2 % of `Decay`. Modulation keeps long tails from ringing metallic; freeze holds a tail forever; shimmer feeds it back an octave up.

| Mode | Character | Use it for |
|---|---|---|
| Hall | big, smooth, slow build | pads, strings, leads, long sends |
| Room | short, early-reflection heavy | drums, percussion, a sense of place |
| Chamber | dense and even, between room and hall | vocals, keys |
| Plate | bright, instant density | snares, vocals, 80s drums |

## Signal flow

`Pre-delay` → input diffusion (`Diffusion`) → early reflections (`Early`) → a 16-line feedback network (`Size`, `Decay`, `Low decay` × the mid decay, `High damping`: above it the tail decays four times faster; `Mod depth`, `Mod rate`) → `Late` → `Width` → `Low cut`, `High cut` → `Mix`. `Freeze` sets the feedback to unity and stops new input; `Shimmer` pitch-shifts the feedback up 12 semitones.

## How to use it

- On a send: `Mix` 100 %, the send level on the connection into it. "Send — Hall (100% wet)" and "Send — Plate (100% wet)" are ready.
- As an insert on one sound: `Mix` 15–35 %.
- Pre-delay against tempo: 60000 ÷ BPM × the fraction of a beat (a 1/64 note at 124 BPM is 30 ms; 1/32 is 60 ms).
- Decay against tempo: the tail should have fallen by the next downbeat or chord change: drums 1–2 beats, pads 1–2 bars.
- Keep returns clean: `Low cut` 200–400 Hz, `High cut` 6–10 kHz.
- Darker and farther: lower `High damping` (3–6 kHz). Warmer: `Low decay` 1.2–1.5×.

A plate for snares and vocals on a send:

| Parameter | Value | Why |
|---|---|---|
| Mode | Plate | |
| Decay | 1.6 s | |
| Pre-delay | 20 ms | the attack stays dry |
| High damping | 12 kHz | bright |
| Low cut | 200 Hz | |
| Mix | 100% | send |

## Gotchas

- `Freeze` On takes no new input: automate it with steps (On, then Off) so the reverb hears the next section.
- `Shimmer` above 50 % with a long decay builds up: watch the level.
- `Width` above 100 % lowers the tail's correlation: check the mix in mono.
- Long decays on sends add up across parts: high-pass and low-pass the return.

## Preset map

| Preset | What |
|---|---|
| "Hall — Concert" | 2.4 s hall, 25 ms pre-delay |
| "Hall — Cathedral" | 8 s, huge |
| "Hall — Dark Strings" | 3.5 s, damped at 3.5 kHz |
| "Room — Small Studio" | 0.6 s, close |
| "Room — Live Drums" | 1.1 s, 120 Hz low cut |
| "Chamber — Vocal" | 1.8 s, dense |
| "Chamber — Echo Chamber" | 2.6 s |
| "Plate — Bright Vocal" | 1.9 s, damped at 14 kHz |
| "Plate — Snare" | 1.2 s, no pre-delay |
| "Ambient — Shimmer" | 9 s with 45 % shimmer |
| "Ambient — Infinite Pad" | 20 s, heavy modulation |
| "Ambient — Long Dark Hall" | 6 s, damped at 2.5 kHz |
| "Send — Hall (100% wet)" | 2.2 s hall for a send |
| "Send — Plate (100% wet)" | 1.6 s plate for a send |
