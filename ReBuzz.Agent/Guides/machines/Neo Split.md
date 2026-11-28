## How it sounds

A splitter, not an effect: one stereo input and four outputs that add up to the input, each wired to a chain of its own. It changes nothing by itself, so it has no `Mix` and no `Amount` (like the meters). Unwired outputs cost nothing, and a wire added while the song plays starts without a click.

The outputs are named `Out 1` to `Out 4` in every mode (the host reads the names once); what each carries depends on `Mode`:

| Mode | Out 1 | Out 2 | Out 3 | Out 4 |
|---|---|---|---|---|
| Bands (2 bands) | Low | High | silent | silent |
| Bands (3 bands) | Low | Mid | High | silent |
| Bands (4 bands) | Low | Low Mid | High Mid | High |
| Transient / Body | Transient | Body | silent | silent |
| Mid / Side | Mid | Side | silent | silent |

- **Bands**: Linkwitz-Riley crossovers (`Slope` 24 or 48 dB/oct, never 12: that cannot sum flat). Each band is 6 dB down at its crossover and the bands sum flat; the phase is turned by the crossovers (an allpass), so a chain that is mixed back should leave the bands in step. No latency.
- **Transient / Body**: the onsets on Out 1, the rest on Out 2, with 2 ms of look-ahead (reported to the host, the same on both). Out 1 plus Out 2 is the input, delayed.
- **Mid / Side**: Out 1 is (M, M) and Out 2 is (S, -S) with M = (L+R)/2 and S = (L-R)/2; together they are L and R again. A mono input leaves Out 2 at exactly 0.

## Use it for

- Parallel processing: a compressor on the lows and an exciter on the highs, a gate or a short reverb on the hits and a compressor on the tails, a width control on the sides only. Wire each chain to the master or to one bus.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Bands | the split; a change crossfades over 20 ms |
| Bands | 3 | 2, 3 or 4; outputs after the last band are silent |
| Slope | 24 dB/oct | 48 dB/oct for a narrow overlap, a little more ringing |
| Crossover 1 | 120 Hz | between band 1 and 2; Crossover 2 (1 kHz) and Crossover 3 (6 kHz) follow, each at least a third of an octave above the one before; a moved crossover glides |
| Sensitivity | 50 % | Transient / Body: how large a rise counts as a hit |
| Transient length | 20 ms | how long after an onset the sound still counts as the transient |
| Body smoothing | 10 ms | how slowly the body comes back |

## Starting points

- "Bands — Two-Way Bass and Top", "Bands — Three-Way Mix", "Bands — Four-Way Master", "Bands — Sub Under 60 Hz", "Transient — Sharp Hits", "Transient — Soft Attacks", "Transient — Long Body", "Mid / Side — Mid and Side".

## Pitfalls

- The outputs are only useful when wired: `connect(from="Split", to="Bass Comp", from_output="Out 1")`. Without `from_output` a wire reads Out 1. A wire's output is saved in the song.
- Mixing the chains back at unequal levels or after a nonlinear effect does not give the input; at unity and with linear chains the sum is the input (Bands: flat in level).
- Changing `Mode` changes the latency (0 in Bands and Mid / Side, 2 ms in Transient / Body); the host compensates it.
- In Bands, the editor's crossover handles and the three knobs are the same parameters; a crossover cannot be dragged closer than a third of an octave to its neighbour.
- Transient / Body finds hits by the rise of the level: a sustained pad leaves Out 1 silent; a very quiet hit after a loud one may stay on Out 2.

## Pairs well with

- Neo Comp, Neo Exciter or Neo Drive on a band; Neo Imager on the sides; Neo Gate and Neo Space on the transient; Neo Utility on each return.

## Checklist

- [ ] `machine_describe(machine="Split")` lists `output_ports` Out 1 to Out 4; `connect(from="Split", to="Low Chain", from_output="Out 1")` and the same for the others.
- [ ] `param_set(machine="Split", changes=[{"param": "Crossover 1", "value": "150 Hz"}])`, then `render(probes=["Split"])`: each chain hears its band only.
- [ ] `machine_readings(machine="Split")` gives `out1_db` to `out4_db`: the used outputs are live, the others at -120.
