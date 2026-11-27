## How it sounds

A spectral effect for one track: it listens to the sound as a short-time spectrum (`FFT size` 1024-8192 points, four frames overlapping) and moves, holds, smears and delays the partials. Five sections, any number on at once, always in this order: `Pitch`, `Scramble`, `Freeze`, `Blur`, `Delay`. The latency is one frame (size minus one: 4095 samples at 4096, 85 ms at 48 kHz), reported to the host so the track stays in time; `Mix` blends against the dry input delayed by the same amount.

- `Pitch` shifts every partial by `Semitones` (-24 to +24) plus `Cents`, phase-locked so a sustained chord stays clear (each partial within 2 cents); `Formant keep` leaves the broad spectral envelope where it was, so a voice shifted up does not turn into a chipmunk.
- `Scramble` swaps bins inside bands of `Band width` bins; `Scramble amount` is the share of bins that move, `Scramble sync` a note value of the song (or Free at `Scramble rate` Hz) for how often the shuffle changes, `Scramble seed` which sequence (the same seed always repeats).
- `Freeze` holds the spectrum: magnitudes kept, each partial running on at its own frequency (a frozen sine stays within 0.5 dB for as long as you like). `Freeze trigger` is Switch (the `Freeze` switch only), Note (frozen while a key is down; a note wire, live MIDI or the track's note column) or Synced (caught at the start of every `Freeze every` period and held for `Freeze length`). `Hold blend` is how much of the output is frozen (100%) against live; every change takes at least 20 ms.
- `Blur` smooths each frequency's level over `Blur time` (0-4000 ms) and across `Smear` bins either side: soft attacks, long ghostly tails.
- `Delay` delays frequencies by whole frames, the time running from `Low time` at the bottom of the spectrum to `High time` at the top (0-2000 ms each); `Delay sync` rounds both to a note length of the song; `Feedback` (always below 100%, bounded) repeats it and `Damping` darkens every repeat.
- `Amount` (0-200%, 100% is the settings as they stand) scales the shift, scramble amount, blur and smear, the delay times and feedback; `Amount` 0 or `Mix` 0 passes the input bit for bit (delayed by the latency).

## Use it for

- Frozen chords and drones from whatever plays; washed pads and ghostly tails; glitchy bin shuffles locked to the beat; octave shimmer and formant-kept pitch shifts of voices and leads; rising spectral echoes on drums and plucks.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| FFT size | 4096 | smooth for chords and pads; 1024 keeps drums tight, 8192 is cleanest for sustained tones |
| Semitones | +12 | an octave up; +7 a fifth, -12 an octave down |
| Formant keep | On | for voices and anything with a body that should not move |
| Scramble amount | 70% | below 30% only grit, above 80% a cloud |
| Freeze trigger | Synced | catch the sound at each bar line; Note freezes while a key is down |
| Blur time | 1800 ms | a long smear; 300 ms only softens attacks |
| Feedback | 55% | repeats that die away; 95% rings for a minute |
| Mix | 70% | keep some of the dry under the effect |

## Starting points

- "Freeze — Frozen Chord", "Blur — Long Spectral Smear", "Blur — Washed Pad", "Scramble — Bin Shuffle Glitch".
- "Pitch — Octave Up Shimmer", "Pitch — Formant Kept Fifth Down", "Delay — Rising Spectral Echo", "Scramble — Robot Scramble".

## Pitfalls

- A fresh instance has every section off and passes the sound (delayed): turn on the sections you want. Presets name the ones they use.
- Frames this long smear transients: drums through `Blur` or a big `FFT size` lose their snap; use 1024 or 2048 for them. Partials closer than about three bins blur into each other in a pitch shift, so chords shifted at 1024 or 2048 are less clean than at 4096 or 8192.
- Changing `FFT size` while playing crossfades to the new size (about a frame and a half to start, then 30 ms); the reported latency changes with it.
- `Freeze` with the Synced trigger needs the song or the tempo to run; with the song stopped it follows the tempo from beat 0. `Note` does nothing without a note.
- The delay's time is per bin and rounds to whole frames (one hop, a quarter of the FFT size).

## Pairs well with

- Neo Verb after a frozen chord or a blur for a bigger room; Neo Delay or Neo Comp after the spectral delay; Neo Chop before it for stuttered input to freeze; a note source into its note wire to play freezes.

## Checklist

- [ ] `machine_describe(machine="Prism")`: the five section switches; turn off the ones you do not want before judging.
- [ ] Read the latency (`machine_readings`: latency_samples) and `render` a bypassed copy against the processed one: the track should stay in time.
- [ ] `analyze(source="Prism", metrics=["spectrum"])`: a frozen or blurred sound holds its level; a pitch shift moves the peaks by the interval.
- [ ] `compare` with `Amount` at 50% and 100% to find how far to push it.
