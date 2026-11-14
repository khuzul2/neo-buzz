## How it sounds

A chorus and flanger built from up to eight delay lines, one per track. Each line has a short minimum delay, an LFO swing, its own feedback and level, and a stereo phase offset; the LFO runs in Hz or locked to the tempo.

## Use it for

- Thick, wide choruses from several lines at once.
- Jet flanges with feedback.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Minimum delay | 3 ms | 0.1 to 10 ms; short for a flange |
| Modulation depth | 2 ms | 0.1 to 10 ms swing |
| LFO rate | 0.5 Hz | 0.05 to 26 Hz, or locked to the tempo above that |
| Wet level | -6 dB | this line's level, -24 to 0 dB |
| Feedback | 30% | -100 to +100 percent; keep it under 60 for a calm flange |
| Dry level | 0 dB | -24 to 0 dB |

## Starting points

- A flanger: `param_set(machine="PhatMan", changes=[{"param": "Minimum delay", "value": "1 ms"}, {"param": "Feedback", "value": "50%"}, {"param": "LFO rate", "value": "0.3 Hz"}])`.
- A chorus: write two tracks with different `LFO rate` values (0.4 and 0.6 Hz) and `Feedback` at 0.

## Pitfalls

- Feedback at either end is hot: +6.1 and +8.4 dBFS peak over 30 s in the profile; all tracks' feedbacks together are held under 99.5 percent.
- Each track is a separate line: an unwritten track keeps its defaults.
- Its LFO shape (sine, triangle, saws and more) is a machine attribute, not a parameter.
- It is +2.4 dB louder than its input at the defaults.

## Pairs well with

- Jeskola Reverb after it.

## Successor

No machine_replace rule for it: Neo Ensemble covers chorus and flanger modes, but a line-by-line mapping was not built, so set it up by hand.

## Checklist

- [ ] `analyze(source="PhatMan", metrics=["loudness"])`: a little above the input at the defaults.
- [ ] `param_get(machine="PhatMan")`: `Feedback` is where you left it.
- [ ] `render(probes=["PhatMan"])`: no ringing that does not die away.
