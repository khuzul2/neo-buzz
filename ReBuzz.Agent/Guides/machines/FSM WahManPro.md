## How it sounds

An auto-wah with a choice of five filters (two- and four-pole low-passes and peaks) and nine LFO shapes sweeping the cutoff, with depth, rate, a settable phase and a cutoff glide. Mono, one track, hot.

## Use it for

- Wah sweeps with more variety than WahMan.
- Peak filters for vocal-like movement.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Cutoff | 120 | 264 Hz x 32^(v/240): 120 is about 1.5 kHz |
| Resonance | 100 | 0 to 240 |
| Filter type | 2pole LP | two- and four-pole low-passes and peaks |
| LFO rate | 1 Hz | 0.05 to 27 Hz |
| LFO depth | 60 | 0 to 240 cutoff steps |
| LFO shape | sine | nine waves |

## Starting points

- A wah: `param_set(machine="WahManPro", changes=[{"param": "Filter type", "value": "2pole LP"}, {"param": "LFO rate", "value": "1.5 Hz"}, {"param": "LFO depth", "value": "90"}])`.

## Pitfalls

- Hot: +10 dB at its defaults, +4.3 dBFS peak, and Resonance at its top reaches +13.7 dBFS over 30 s.
- It is mono: NeoBuzz Live feeds it left + right summed.
- This build starts the LFO phase at 0 (upstream's started wherever its memory held).
- Set LFO phase has an out-of-range default in the machine (255); NeoBuzz Live takes 127.

## Pairs well with

- Jeskola Gain after it to take the level down.

## Successor

No machine_replace rule for it: Neo Filter's LFO covers the idea, but only FSM WahMan has a fitted mapping, so set it up by hand.

## Checklist

- [ ] `analyze(source="WahManPro", metrics=["loudness"])`: about 10 dB above the input at the defaults: lower it.
- [ ] `param_get(machine="WahManPro")`: `Filter type` and `LFO shape` are what you set.
- [ ] `render(probes=["WahManPro"])`: peaks stay below full scale.
