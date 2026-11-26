## How it sounds

A modal synth for tuned percussion. Every note rings a body of up to 32 decaying modes (soft, hard, metal and bright bars, a tube, a bell, a tine, a plucked string, glass, a plate, or a Custom series with Stretch and Inharmonicity). A mallet is a short smooth pulse: Hardness is its contact time (0.3 to 12 ms), Position moves the strike along the body, Decay is how long the fundamental rings and Damping tilt makes the high modes die sooner, and Release damping sets how fast letting go of a key stops the note (5 ms is a hand on the bar). 16 voices; a stolen voice fades out in 5 ms.

It is one machine with two jobs. With `Source` on Mallet the notes strike the bodies. With `Source` on Resonator nothing is struck: the audio coming into the machine rings one body for each held note, so a drum loop or noise turns into pitched ringing that follows your keys. It has Mix and Amount like an effect, so it sits on a track as an instrument (Mix 100 %) or as a resonator over a loop. Amount at 0 % passes the input through bit for bit, so a track on Amount 0 plays no notes.

| Model | Modes (ratio to the note) |
|---|---|
| Soft bar | 1 : 4 : 10 (an undercut bar, marimba-like) |
| Hard bar | 1 : 3 : 6 |
| Metal bar, Bright bar | 1 : 4 : 10 (with Tremolo), and a free bar 1 : 2.76 : 5.40 : 8.93 |
| Tube, Pluck | a near-harmonic series, and a stiff string (Inharmonicity) |
| Bell, Tine, Glass, Plate | hum, prime, tierce, quint, nominal; a clamped bar 1 : 6.27 : 17.5; ring doublets; a square plate |

## Use it for

- Marimba, xylophone, vibraphone and glockenspiel lines, chimes, bells, kalimba and plucked tines, glass tones.
- Resonator over drums or noise: pitched ringing tuned to the chord you hold.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Model | Soft bar | picks the body; Custom opens Stretch and Inharmonicity |
| Hardness | 2 ms | contact time: 0.3 ms is a hard stick, 12 ms a soft felt head |
| Position | 30% | 50 % is the fundamental alone, the ends bring overtones |
| Decay | 2 s | the fundamental's T60; 0.2 s for knocks, 10 s for chimes |
| Damping tilt | 50% | how much sooner the high modes die |
| Tremolo depth | 0% | 60 % with Tremolo rate 5 Hz is a vibraphone's motor |
| Source | Resonator | rings the audio input on the held notes |
| Mix | 60% | keeps some of the input beside the ringing; check `peak_db` with a chord |

## Starting points

- "Bars — Soft Wooden Bars", "Bars — Bright Metal Bars", "Bars — Tremolo Bars".
- "Chimes — Hollow Tube Chimes", "Bells — Church Bell", "Plucked — Thumb Tines", "Glass — Glass Harmonics".
- "Resonator — Resonated Drums": route a drum loop into Mallet, hold a chord.

## Pitfalls

- Modes above 0.45 of the sample rate are dropped, so high notes on a bright model have fewer modes.
- A strike on a node (Position 50 % with a bar's second mode) leaves that mode out; the tone changes with Position.
- Resonator mode needs audio on the input and held notes: no notes, no sound; no input, no ringing.
- Amount 0 % is the dry input, so the notes are silent; 100 % is the settings as they stand, 200 % rings longer and harder.
- `Stretch` and `Inharmonicity` act on the Custom and Pluck models only.
- A high Hardness (a short contact) on every voice is a sharp click: soften it or lower the velocity to hardness.

## Pairs well with

- Neo Verb and Neo Delay for tails, Neo EQ to tame the highs, Neo Drums as the Resonator's source.

## Checklist

- [ ] `param_set(machine="Mallet", changes=[{"param": "Model", "value": "Bell"}, {"param": "Decay", "value": "6"}])`, then `render(probes=["Mallet"])`.
- [ ] `machine_readings(machine="Mallet")` gives `rms_db` and `peak_db`: `peak_db` under 0 dBFS with a chord.
- [ ] `analyze(source="Mallet", metrics=["spectrum"])`: the first lines sit at the model's ratios above the note.
- [ ] Resonator: `param_set(machine="Mallet", changes=[{"param": "Source", "value": "Resonator"}])`, feed it a loop, and check the spectrum for peaks at the held notes.
