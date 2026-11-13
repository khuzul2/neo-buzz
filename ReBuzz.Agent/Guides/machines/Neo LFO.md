## How it sounds

A low-frequency oscillator with no audio of its own: it moves up to 8 parameters of other machines in time. At 4 to 6 Hz on a pitch or cutoff it is a vibrato or a wobble; at a tempo length (`Sync`) it is a sweep that lines up with the beat; slow and random it is drift that keeps a pad alive. Each target keeps the value you set (its base) and the LFO swings around it, smoothly, never past the parameter's range or safe range.

- `Shape`: Sine, Triangle, Saw up, Saw down, Square, Smooth random, Sample and hold. `Smooth` rounds the corners (a square becomes a trapezoid).
- Speed: `Sync` Free uses `Rate` (0.02 to 40 Hz); any other `Sync` (8 bars down to 1/64) locks one cycle to the song's tempo, `Feel` makes it dotted or triplet. While the song plays and `Retrigger` is Free the phase follows the song position, so every render is the same.
- `Retrigger` restarts the cycle each Beat, each Bar, or on each Note of the machine it listens to (`modulate(source=…, listen_machine=…)`), so every note gets the same sweep.
- Per target: `depth` −100 … +100 %, polarity (bi swings both ways around the base, uni only adds), an optional range and a curve. `Write glide` is the least time a target takes to cross its whole range: no write is ever a bigger jump than that allows.

## Use it for

- Filter wobble, vibrato, tremolo, auto-pan, a slow drift on a pad's detune or a reverb's size, a tempo-locked sweep on a send.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Shape | Sine | smooth; Triangle for an even sweep, Square for a gate-like jump |
| Rate | 1.00 Hz | free speed; about 5 Hz is a wobble |
| Sync | Free | a bar length (1 bar, 1/4…) locks the sweep to the beat |
| Phase | 0° | where the cycle starts, or the offset when synced |
| Retrigger | Free | Note starts the same sweep on every note |
| Smooth | 0 % | rounds a square or sample and hold |
| Write glide | 30 ms | lower is steppier, higher is lazier |

## Starting points

- A filter wobble: `modulate(source="LFO", target_machine="Bass", param=<the cutoff>, depth=30)`, `Sync` 1/8, Shape Triangle.
- A vibrato: depth 3 to 6 % on a pitch or detune parameter, `Rate` 5 Hz, Sine.
- Auto-pan: depth 100 % on a pan parameter, `Sync` 1 bar, Sine.
- Slow drift: Shape Smooth random, `Rate` 0.1 Hz, depth 10 % on a pad's cutoff.

## Pitfalls

- A uni LFO at depth 100 % on a parameter already near its top clips at the top: lower the base or use bi.
- With `Retrigger` Note and nothing to listen to, the cycle never restarts: give `listen_machine`. Notes played from a MIDI keyboard are not seen, only those the song and `midi_play` send.
- Two modulators on the same parameter fight: the second takes the first's output as its base.
- The master volume is never pushed past 0 dB and tempo parameters are refused.

## Pairs well with

- Neo Filter, Neo Poly, Neo Wave and Neo Delay as targets; Neo Envelope for per-note movement, Neo Step for rhythmic movement.

## Successor

Neo LFO replaces Pedal LFO: `machine_replace(machine="LFO")` carries the wave and the phase over; its target, period in ticks, depth and centre do not (Neo LFO has its own targets, each with its depth), so point it again with `modulate`.

## Checklist

- [ ] `modulation_list(machine="LFO")` shows each target `active` with the range it reaches.
- [ ] `render(probes=["Bass"])` then `plot(kind="spectrogram", …)` shows the sweep at the rate you set.
- [ ] Removing the target (`modulation_remove(source="LFO", all=true)`) leaves the parameter at the value you set.
