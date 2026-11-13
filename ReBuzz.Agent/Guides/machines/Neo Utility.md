## How it sounds

A set of exact, colourless tools; at its defaults it passes the input bit for bit.

- `Gain` in 0.1 dB steps (-60 to +24 dB); at its bottom, silence. `Pan` with a `Pan law`: Balance fades the far side linearly (stereo sources), Mono pan sums to mono, then pans (mono sources).
- `Width`: the side signal times the width. 0 % is mono, 100 % unchanged, 200 % twice the side.
- `Bass mono` makes everything below `Bass mono freq` mono (a 4th-order Butterworth high-pass on the side: -3 dB at the frequency, -48 dB two octaves below).
- `Invert L` and `Invert R` flip a channel's polarity; `Swap` swaps the channels. `DC removal` is a 5 Hz high-pass.
- `Matrix`, for mid/side work with any stereo machine: Encode M/S puts the mid (L + R)/2 on the left and the side (L - R)/2 on the right; Decode M/S turns them back (L = M + S, R = M - S). An encode and a decode in a row give back the input within -150 dB.

Order: Matrix Decode, inversions, Swap, DC removal, Width and Bass mono, Pan, Gain, Matrix Encode. Zero latency. Gain, pan, width, polarity and swap measure exact.

## Use it for

- Gain staging, stereo fixes, mono bass, polarity repair, and mid/side processing with a machine that has no mid/side mode.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Gain | -6 dB | trim a track to its level, then render and check |
| Pan | 0% | -100 % left to +100 % right |
| Pan law | Balance | Mono pan for mono sources |
| Width | 100% | 120-140 % wider; 50-80 % narrower; keep basses at 100 % or below |
| Bass mono | On | with Bass mono freq 120 Hz (80 Hz on a master) |
| Matrix | Off | Encode M/S before and Decode M/S after a machine |

## Starting points

- "Gain — Trim −6 dB", "Gain — Boost +6 dB", "Gain — Stage to −18 dBFS (−12 dB)".
- "Stereo — Mono", "Stereo — Narrow 50 %", "Stereo — Wide 140 %".
- "Mastering — Bass Mono 120 Hz", "Mastering — Clean Up (Bass Mono 80 Hz, DC)".
- "Fix — Flip Polarity", "Fix — Right Channel Out of Phase", "Fix — Swap Channels", "Fix — DC Offset".
- Mid/side around a machine: "Mid/Side — Encode (Before a Stereo Machine)" then "Mid/Side — Decode (After It)". Between them left is the mid and right the side, so Neo Comp with `Stereo link` 0 % compresses each on its own. A machine that treats both channels alike (Neo EQ) moves mid and side alike.

## Pitfalls

- Width above 100 % lowers the mono sum's correlation; check with `analyze(source="master", metrics=["stereo"])` (correlation above 0).
- Pan with Balance on a mono source only turns one side down: use Mono pan.
- Gain comes after everything else, so it does not change what the stereo tools see.
- Between an encode and a decode do not listen or pan, and avoid machines that mix the channels (Neo Verb, Neo Ensemble). A machine with latency there is fine.
- Out of phase: `analyze_mix` reports a low end out of phase, or the correlation reads below 0: flip `Invert R` and measure again.

## Pairs well with

- Neo Comp, Neo Drive and Neo Exciter in mid/side; Neo Imager for per-band width; Neo Analyzer for `correlation`.

## Successor

Neo Utility replaces Jeskola Gain and Pedal Gain: `machine_replace(machine="Gain")`.

## Checklist

- [ ] `param_set(machine="Utility", changes=[{"param": "Gain", "value": "-6 dB"}])` then `machine_readings(machine="Utility")`: `peak_db` 6 dB lower.
- [ ] `analyze(source="master", metrics=["stereo"])`: correlation above 0 after any width change.
- [ ] For mid/side: `machine_list` shows Encode, the machine and Decode in a row.
