## How it sounds

A teaching additive synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. The sound is a stack of sine partials whose levels, spacing and movement you shape: a bright-to-dark tilt, odd and even harmonics, a partial count, stretched (inharmonic) spacing, a comb that removes partials in a pattern and a shimmer that makes each partial drift in level. Drawbar organs, hollow reeds, choirs, stiff strings, glass bells, vocal textures, pure subs.

## Signal flow

- `Brightness Tilt` slopes the partials' levels per octave: negative is darker, positive is brighter.
- `Odd / Even` balances the odd partials against the even: 50 % is the full series; to the left the even partials fade (odd only: hollow, clarinet-like), to the right the odd ones fade (even only: thin, octave-like).
- `Partials` is how many partials sound, up to 63 (fewer is softer and cheaper).
- `Inharmonicity` stretches the spacing like a stiff string; at full it makes bell and glass tones.
- `Spectral Comb` removes partials in a repeating pattern (a comb in the spectrum): low amounts thin the sound, high amounts leave sparse, vocal or hollow tones.
- `Shimmer` gives each partial its own slow level drift, so the sound moves and chorus-like swirls appear.
- `Attack`, `Decay`, `Sustain`, `Release` shape the level. `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## How to use it

- Organ: `Partials` 8, `Odd / Even` 50 %, `Brightness Tilt` slightly negative, short `Attack`.
- Clarinet: `Odd / Even` to the left (odd only), `Partials` 20.
- Bell: `Inharmonicity` 60-100 %, long `Decay`, `Sustain` 0.
- Choir pad: `Shimmer` 40-70 %, slow `Attack`, `Unison / Spread` 30 %.
- Pure sub: `Partials` 1-3, `Brightness Tilt` negative.

## Checklist

- Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- Change one control at a time with `param_set`, play a pattern (`notes_write`), then `render` with probes and check `machine_readings` (rms_db, peak_db, active_voices).
- `plot` a spectrum of the render to see the partials, and `compare` two settings.
- Writes via `param_set` show on the Explain strip with the Agent badge.

## Gotchas

- Unison 0 to anything above it jumps from 1 voice to 5 (level stays even; the sound gets wider).
- `Odd / Even` at 50 % (raw 500) is neutral.
- Partials above the Nyquist limit are dropped, so a high note with a high `Inharmonicity` has fewer partials than the control says.
- A bright tilt with all 63 partials is loud and harsh: watch `peak_db`.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.
