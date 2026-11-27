# Neo Space

| | |
|---|---|
| Status | Built (SPC1, issue #194): engine, editor, 8 presets, agent guide, gates in `NeoBuzz.MachineTests/NeoSpaceTests.cs`, listening-pack generator `NeoSpacePack.cs`. The sound is for the user to approve. |
| Branch | `machines/neo-space-spc1` |
| Code | `Machines/NeoSpace/` (`NeoSpace.cs`, `NeoSpaceEditor.cs`, `SpaceWaves.cs`, `Params.def`, `Presets.txt`, `Engine/SpaceEngine.cs`, `Engine/SpaceIr.cs`, `Engine/SpaceRooms.cs`), the two flavours in `Machines/Portable/NeoSpace` and `Machines/Studio/NeoSpace` |
| Builds on | `StereoConvolver` (the shared zero-latency partitioned convolver), `FxStage` (Mix and Amount), `Biquad`, `Fft`, `PortableMath`, `LevelMeter`, the Neo machine framework (`docs/specs/machines.md`) |
| Clean room | The rooms are made by our own code from published acoustics (below). No impulse response from anywhere else is used, stored or downloaded; a wave is the user's own. No code, table or preset of another reverb. |

## 1. What it is

A convolution reverb for one track or a send, with no latency. The sound is convolved with an impulse response (IR) that is one of nine rooms made by the machine's own code, or any wave of the song.

| Room | What it is | Reverberation time (low / mid / high) |
|---|---|---|
| Small room | a living room, a close source | 0.42 / 0.32 / 0.20 s |
| Ambience | a treated room: first reflections, a faint tail | 0.55 / 0.40 / 0.26 s |
| Drum room | a big hard live room | 1.00 / 0.85 / 0.60 s |
| Chamber | a stone chamber | 1.75 / 1.45 / 1.05 s |
| Plate | a metal plate: no room, a dense bright tail from the first millisecond | 1.70 / 2.10 / 2.50 s |
| Hall | a concert hall | 2.70 / 2.20 / 1.50 s |
| Large hall | a big hall | 4.60 / 3.80 / 2.40 s |
| Cathedral | a stone nave | 8.00 / 6.40 / 3.20 s |
| Tunnel | a long narrow space | 5.00 / 4.20 / 2.80 s |
| Wave | the song's wave in the slot named by `Wave` (1 to 200) | the wave's own |

Controls (the order is the state's order; a new one is only ever appended): `Room`, `Wave`, `Pre-delay` (0 to 250 ms), `Length` (5 to 100 %), `Low cut` (20 Hz to 1 kHz; the lowest is off), `High cut` (1 to 20 kHz; the highest is off), `Width` (0 to 200 %), `Reverse`, `Mix` (the dry/wet), `Amount` (0 to 200 %) and `Output` (-24 to +24 dB, the wet level only).

State: versioned, version 0 (`Layouts/state-layouts.txt`). `Wave` is a slot number: **the song saves the wave, the machine saves only its slot**, so a song that is reopened reads the waves from the song file and the room follows them.

## 2. The convolver

The shared `StereoConvolver`: a non-uniformly partitioned FFT convolution with **zero latency**. The first 64 taps are a direct sum in the time domain, then stages of 64, 256, 1024 and 4096-sample partitions on offset block grids, so that every stage's FFT is spread over the blocks before its output is needed and the work per audio block is flat. The result is what the direct convolution gives, to better than -130 dB (the gate is -100 dB, measured on noise and on a click train through the 8.85 s Cathedral). The machine reports `GetLatency()` = 0, at every setting; the dry sound is never delayed.

## 3. The IR is built off the audio thread

Nothing that allocates runs on the audio thread. A change to anything that shapes the IR (the room, the wave, `Pre-delay`, `Length`, the cuts, `Width`, `Reverse`, or the sample rate) is written as a request by the audio thread (`SpaceEngine.Poll`: a value comparison and a write) and a single background builder thread makes the new IR and its convolver. The hand-over is a small state machine (0 idle, 1 asked, 2 building, 3 ready, 4 being written), so there is never more than one build and a change made while one is building is asked for again when it ends. At the next block the audio thread takes the finished convolver and **crossfades 50 ms (equal power) from the old to the new**; both run for that time, and the old one is then dropped. Nothing clicks, and 0 bytes are allocated on the audio thread (gates: 2000 blocks with a swap every 250 blocks, 0 bytes; pink noise through Hall, Cathedral and Small room: 0 clicks).

Mix, Amount and Output are not part of the IR: they are smoothed gains, applied at once.

A machine that has rested (silent input for longer than the tail) clears the convolvers' histories at the next input, so an old tail never plays into a new note.

For renders, `Busy` (the agent server's wait, `mcp-server.md` §3.5) asks for the IR on the song's thread, reads the wave there, and stays true until the IR is made: an offline render starts with its room.

## 4. The rooms

Each room is made by `SpaceRooms.Generate` from a definition (a shoebox of dimensions, a source and a receiver pair, and reverberation times in three bands: below 450 Hz, between 450 Hz and 4 kHz, above 4 kHz), all by deterministic code on `PortableMath`: the same bits on every machine.

1. **Early reflections: the image-source method.** The source is mirrored in the walls of the box (Allen and Berkley, 1979); every image whose path is shorter than the mixing time (see 3) becomes a tap, at the time of its path length at 343 m/s, with the amplitude of 1/distance and the product of the wall reflection coefficients along it (the image's order in x, y and z: so many hits on each pair of walls; the floor and the ceiling can differ). The coefficients come from the reverberation time per band by **Eyring's** formula (Eyring, 1930), `beta = exp(-0.0805 V / (S T))` in pressure per hit, in each of the three bands. Each tap is a windowed-sinc fractional delay (so that it sits between samples), the taps of each band are summed in a train per band and filtered into place with a split of three 12 dB/octave crossovers. Two receivers 0.2 m apart (the left and right ears) give the stereo image.
2. **The tail: a diffuse field.** After the mixing time (10 to 110 ms by the room's volume) the sound field is statistically diffuse (Schroeder, 1962; Moorer, 1979): a noise with an exponential decay. The tail is a white noise from a seeded generator (different seeds for left and right, so the tails are decorrelated) split into three bands by 36 dB/octave low-passes, each band with its own decay `exp(-6.91 t / T)` (60 dB in the band's reverberation time) and its own start level from the critical distance (`d_c = 0.057 sqrt(V / T)`, the distance at which the direct and the reverberant sound are equal: Sabine, 1922; Kuttruff). The image taps and the noise cross-fade at the mixing time with equal power, so no dip is heard where they meet.
3. **A plate** has no images: only the noise, with a short build-up and no silence before it.
4. **The end:** a 30 ms cosine fade, and the level (see 5).

The decay is measured in the tests per octave band (Schroeder backwards integration, T30 doubled) against the design: the designs and the measures agree within 30 % at 250 Hz and 1 kHz and 40 % at 8 kHz (the measure at the top is the loosest, as the band split is finite). The geometry is tested too: the six first-order images of the small room arrive at the times its box gives (3.06, 4.24, 4.40, 4.87, 5.66 and 6.64 ms after the direct sound).

A wave is read at its own rate and resampled to the host's (`ImpulseResponses.Resample`), mono (the same in both channels) or stereo; a DC offset is taken out and a lead of silence before the sound is trimmed, so a clip that starts late is not a pre-delay of its own.

## 5. The shaping, in order

All of it on the builder's thread, with `PortableMath`:

1. a wave's DC offset and lead;
2. **Length**: the IR is cut to a fraction (5 to 100 %), the end faded with a raised cosine (a tenth of what is kept, 5 to 500 ms; 10 ms for the whole);
3. **Reverse** (after the trim, so the trimmed IR plays backwards);
4. **Low cut** and **High cut**: two second-order Butterworth sections each (24 dB/octave), off at their ends;
5. **Width**: the side signal (L - R) scaled, 0 mono, 200 % twice;
6. **Pre-delay**: that much silence in front (the IR never exceeds 10 s: the end fades);
7. **the level**.

**The level.** An IR with unit energy would make a bass-heavy tail (and every real room's tail is) far louder on music than a bright one, because music has its energy in the lows. The IR is therefore levelled so that **pink noise** (equal energy per octave, 20 Hz to 20 kHz) comes out at its own level through it, the two channels together: `SpaceIr.PinkLevel` is the mean over the audio band of the power response weighted by 1/f, and the IR is scaled to make it 1. At Mix 100 % a room sounds as loud as what went in, whatever its colour or cuts.

## 6. Mix, Amount, Output

Through the shared `FxStage`:

- wet = Mix x Amount x Output;
- dry = 1 - Mix x min(1, Amount);
- Amount 0 % is the bypass: the input bit for bit; Mix 0 % the same; Amount 200 % doubles the wet (+6 dB), the dry staying at what Mix leaves;
- every gain is smoothed (no zipper noise under automation: a sweep of Mix and Amount, through 0 %, gives 0 new clicks).

## 7. The editor

The house style (`docs/specs/ux-overhaul.md`): four sections in the Delay and Reverb identity. **Room** (the room choice, the Wave slot, Pre-delay and Length), **Response** (a picture of the IR: its level in dB over time, the pre-delay as a shaded gap in front, the trim as a line with the cut part dimmed, reversed when Reverse is on, the room's name and the kept length, a line that says "Preparing the room", "Reading the wave" or, in the warning colour, "Wave 5 is not in the song: the hall plays"), **Tone** (Low cut, High cut, Width, Reverse) and **Output** (Amount and Mix side by side, 40 px knobs, Output and the two meters).

## 8. The gates (all in `NeoSpaceTests.cs`)

| Gate | Measured |
|---|---|
| Accuracy against the direct sum, noise through a 0.7 s IR | -132 dB |
| Accuracy, a click train through the 8.85 s Cathedral | -132 dB |
| Latency | 0 samples at block sizes 256, 64, 37 and 1; the same bits at each |
| Swap | 0 new clicks; 0 bytes on the audio thread, 8 swaps over 2000 blocks; after 50 ms the output is that of a new engine on the new room |
| Mix and Amount | Amount 0 and Mix 0: the input bit for bit; 200 %: twice the wet; monotonic |
| Audio thread | 0 bytes per block over 2000 blocks; 0 denormals in a 30 s tail |
| CPU | 4 s stereo IR at 48 kHz, 256-sample blocks: 89 times real time at the reference's speed, 1.1 % of a core: 0.41 of Neo Verb's cost |
| State | the Wave slot round-trips; a missing wave plays the hall and the editor says so |

## 9. Literature

- J. B. Allen and D. A. Berkley, "Image method for efficiently simulating small-room acoustics", J. Acoust. Soc. Am. 65 (1979).
- C. F. Eyring, "Reverberation time in 'dead' rooms", J. Acoust. Soc. Am. 1 (1930); W. C. Sabine, Collected Papers on Acoustics (1922).
- M. R. Schroeder, "Natural sounding artificial reverberation", J. Audio Eng. Soc. 10 (1962); M. R. Schroeder, "New method of measuring reverberation time", J. Acoust. Soc. Am. 37 (1965) (the backwards integration used to measure the decay).
- J. A. Moorer, "About this reverberation business", Computer Music Journal 3 (1979).
- W. G. Gardner, "Efficient convolution without input-output delay", J. Audio Eng. Soc. 43 (1995) (the zero-latency partitioned scheme).
- H. Kuttruff, Room Acoustics (the diffuse field, the critical distance, the mixing time).

## 10. Limits

- The rooms are shoeboxes with three reverberation times: no furniture, no curved walls, no air absorption beyond the band decay. They are made, not recorded; a recorded space is a `Wave`.
- A response longer than 10 s is cut. A new response takes a fraction of a second to build (the longest, the Cathedral, the most): the old one plays meanwhile.
- A wave that is replaced in the song while it plays is read again and crossfaded in; a wave deleted falls back to the hall.
- Three of the large rooms have a short gap between the first reflections and the denser part (physically right: the image density has not yet built); on a very sparse source this can be heard.
