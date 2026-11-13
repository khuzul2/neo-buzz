## How it sounds

A teaching additive synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. The sound is a stack of sine partials whose levels, spacing and movement you shape.

- `Brightness Tilt` slopes the partials' levels per octave (negative is darker). `Odd / Even` balances odd against even partials: 0 % is the full series, one way is odd only (hollow, clarinet-like), the other even only (thin).
- `Partials` is how many sound, up to 63. `Inharmonicity` stretches the spacing like a stiff string, up to bell and glass tones.
- `Spectral Comb` removes partials in a repeating pattern; `Shimmer` gives each partial its own slow level drift.
- Each control has a How and a Sound sentence: `machine_describe` returns them with the id, and `param_set` takes the name or the id. 16 notes; no limiter.

## Use it for

- Learning additive synthesis: drop `Partials` to 1 and add them back one by one. Then drawbar organs, hollow reeds, choirs, stiff strings, glass bells and pure subs.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Brightness Tilt | -6 dB/oct | negative is darker; the range is -24 to +6 |
| Odd / Even | 0% | neutral; to one side odd only, to the other even only |
| Partials | 8 | 8 for an organ, 1-3 for a sub, up to 63 |
| Inharmonicity | 0% | 60-100 % for bells |
| Shimmer | 0% | 40-70 % for choir pads |
| Sustain | 70% | 0 % for bells and plucks |
| Output | -6 dB | lower it if `peak_db` passes 0 dBFS |

## Starting points

- "Organ — Warm Drawbars", "Reed — Hollow Clarinet", "Pad — Choir Shimmer", "Keys — Stiff String", "Bell — Glass Chime", "Texture — Vocal Comb", "Lead — Bright Saw", "Bass — Pure Sub".
- Bell: `Inharmonicity` 60-100 %, long `Decay`, `Sustain` 0 %. Pure sub: `Partials` 1-3, negative `Brightness Tilt`.

## Pitfalls

- Unison 0 to anything above it jumps from 1 voice to 5.
- Partials above the Nyquist limit are dropped: a high note with high `Inharmonicity` has fewer partials than `Partials` says.
- A bright tilt with all 63 partials is loud and harsh: watch `peak_db`.
- Only these 12 controls exist: the other Dye Essence controls are fixed and cannot be set here.

## Pairs well with

- Neo Verb and Neo Ensemble for choirs, Neo EQ to tame bright tilts; Neo Poly or Neo Wave when you want filters and a modulation matrix.

## Checklist

- [ ] `machine_describe(machine="Learn")` shows the id, range, How and Sound of each control.
- [ ] `param_set(machine="Learn", changes=[{"param": "Partials", "value": 8}])`, then `notes_write` a chord and `render(probes=["Learn"])`.
- [ ] `machine_readings(machine="Learn")` gives `rms_db` near -22 dB and `peak_db` under 0 dBFS.
- [ ] `plot` the spectrum of the render and `compare` two settings: the partials appear as lines.
