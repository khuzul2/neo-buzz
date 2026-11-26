## How it sounds

A modal synth for tuned percussion. Every note rings a body of up to 32 decaying modes (soft, hard, metal and bright bars, a tube, a bell, a tine, a plucked string, glass, a plate, or a Custom series with Stretch and Inharmonicity). A mallet is a short smooth pulse: Hardness is its contact time (0.3 to 12 ms), Position moves the strike along the body, Decay is how long the fundamental rings and Damping tilt makes the high modes die sooner, and Release damping sets how fast letting go of a key stops the note (5 ms is a hand on the bar). 16 voices; a stolen voice fades out in 5 ms.

It is a normal instrument: a generator that plays by notes (keyboard, MIDI, a note wire), with no audio input. A resonator mode, where audio rings the bodies, is planned for later.

| Model | Modes (ratio to the note) |
|---|---|
| Soft bar | 1 : 4 : 10 (an undercut bar, marimba-like) |
| Hard bar | 1 : 3 : 6 |
| Metal bar, Bright bar | 1 : 4 : 10 (with Tremolo), and a free bar 1 : 2.76 : 5.40 : 8.93 |
| Tube, Pluck | a near-harmonic series, and a stiff string (Inharmonicity) |
| Bell, Tine, Glass, Plate | hum, prime, tierce, quint, nominal; a clamped bar 1 : 6.27 : 17.5; ring doublets; a square plate |

## Use it for

- Marimba, xylophone, vibraphone and glockenspiel lines, chimes, bells, kalimba and plucked tines, glass tones.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Model | Soft bar | picks the body; Custom opens Stretch and Inharmonicity |
| Hardness | 2 ms | contact time: 0.3 ms is a hard stick, 12 ms a soft felt head |
| Position | 30% | 50 % is the fundamental alone, the ends bring overtones |
| Decay | 2 s | the fundamental's T60; 0.2 s for knocks, 10 s for chimes |
| Damping tilt | 50% | how much sooner the high modes die |
| Tremolo depth | 0% | 60 % with Tremolo rate 5 Hz is a vibraphone's motor |
| Volume | 0 dB | notes peak around -4 dBFS alone; check `peak_db` with a chord |

## Starting points

- "Bars — Soft Wooden Bars", "Bars — Bright Metal Bars", "Bars — Tremolo Bars".
- "Chimes — Hollow Tube Chimes", "Bells — Church Bell", "Plucked — Thumb Tines", "Glass — Glass Harmonics", "Plates — Struck Steel Plate".

## Pitfalls

- Modes above 0.45 of the sample rate are dropped, so high notes on a bright model have fewer modes.
- A strike on a node (Position 50 % with a bar's second mode) leaves that mode out; the tone changes with Position.
- It has no audio input, no Mix and no Amount: it only plays notes.
- `Stretch` and `Inharmonicity` act on the Custom and Pluck models only.
- A high Hardness (a short contact) on every voice is a sharp click: soften it or lower the velocity to hardness.

## Pairs well with

- Neo Verb and Neo Delay for tails, Neo EQ to tame the highs.

## Checklist

- [ ] `param_set(machine="Mallet", changes=[{"param": "Model", "value": "Bell"}, {"param": "Decay", "value": "6"}])`, then `render(probes=["Mallet"])`.
- [ ] `machine_readings(machine="Mallet")` gives `rms_db` and `peak_db`: `peak_db` under 0 dBFS with a chord.
- [ ] `analyze(source="Mallet", metrics=["spectrum"])`: the first lines sit at the model's ratios above the note.
