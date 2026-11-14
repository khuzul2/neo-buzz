## How it sounds

A source that brings one stereo pair of the audio device's input into the song, with one Bypass switch. It adds nothing to the sound; you hear whatever the device is fed. The pair and mono or stereo are chosen in its menu, not by a parameter.

## Use it for

- Live input (a microphone, a synth, a record player) as a source to run through effects.
- A signal to record: connect it to a Session Clip Player and record a take.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Bypass | no | no passes the input; yes makes the machine silent |

## Starting points

- Add it, connect it to an effect or to the master, then check the meter: `machine_add` then `meters_read(machines=["Audio In"])`.
- To record the input, connect it to a Session Clip Player with `connect(from="Audio In", to="Clips")` and use `session_record(action="start", machine="Clips", bars=4)`.

## Pitfalls

- It is silent until the audio device has a signal on the chosen pair; unverified here (the profile had no input), so which device channel arrives was not checked.
- Live input through speakers can feed back: lower the monitoring first.
- A note does nothing: it is a source, not an instrument.

## Pairs well with

- Any effect you want to run the input through; Session Clip Player to capture it.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="Audio In")`: its one parameter is `Bypass`.
- [ ] `param_get(machine="Audio In")`: `Bypass` reads no.
- [ ] `meters_read(machines=["Audio In"])`: it shows a level when the device input has a signal.
