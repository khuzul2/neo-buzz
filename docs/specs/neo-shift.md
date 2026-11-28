# Neo Shift (SHF1, issue #196)

A pitch shifter, a frequency shifter and a ring modulator in one effect per track. Clean room: published delay-line, phase-vocoder and single-sideband methods and our own DSP. The shared `Machines/NeoBuzz.Dsp/` is untouched, so Dye Essence's shifter effects (`FrequencyShifter`, `PitchShiftDelay`) render bit-identical to their base (hash test `ShiftDyeBaselineTests`).

## Literature (checked)
- H. Bode and R. Moog, "A High-Accuracy Frequency Shifter for Professional Audio Applications", Journal of the Audio Engineering Society, 1972 (AES E-Library): single-sideband frequency shifting on a 90-degree network.
- J. Dattorro, "Effect Design, Part 2: Delay-Line Modulation and Chorus", Journal of the Audio Engineering Society 45(10), 1997, pp. 764-788: the sweeping, crossfaded delay-line pitch shifter (splicer with raised-cosine crossfade).
- S. Roucos and A. M. Wilgus, "High Quality Time-Scale Modification for Speech", Proc. IEEE ICASSP, 1985: overlap-add with the lag chosen by cross-correlation (SOLA), the idea behind Fast's waveform alignment.
- J. L. Flanagan and R. M. Golden, "Phase Vocoder", Bell System Technical Journal 45(9), 1966, pp. 1493-1509.
- J. Laroche and M. Dolson, "New Phase-Vocoder Techniques for Real-Time Pitch Shifting, Chorusing, Harmonizing, and Other Exotic Audio Modifications", Journal of the Audio Engineering Society 47(11), 1999 (first given as "New phase-vocoder techniques for pitch-shifting, harmonizing and other exotic effects", IEEE WASPAA, 1999): peak finding and phase locking per region, used by Smooth.

## Modes
- **Pitch**, -24 to +24 semitones, Fine +-100 cents, Stereo detune 0 to 50 cents (left down, right up). Quality **Fast**: two read points sweep through a delay line and are crossfaded with complementary Hann weights over a Window of 10 to 100 ms; each time a point restarts, its delay is moved by the lag (at most a quarter window and 12 ms) that best correlates with the other point, so a held tone keeps its level and exact pitch. The phase runs in double precision; the latency is half the Window (even samples). **Smooth**: the shared `Stft` (2048, hop 512) with the Laroche-Dolson phase-locked pitch routine; latency 2047 samples.
- **Frequency**: single-sideband shift, +-2000 Hz (integer Hz plus Shift fine +-5 Hz in 0.01 Hz steps), by the 90-degree allpass pair designed for 80 dB (image more than 60 dB down from 40 Hz to 16 kHz). Feedback +-95 % through a 4 kHz low-pass (rising or falling barber-pole), Spread 0 to 100 % moves the right side the other way (right = shift x (1 - 2 x spread)).
- **Ring**: a carrier of 1 Hz to 5 kHz (sine from a double phase; triangle and square band-limited with BLEP and BLAMP), Depth 0 to 100 %: gain = (1 - d)/2 + (1 + d)/2 x carrier, so 0 % is amplitude modulation and 100 % is ring modulation.
- **Follow notes** (default off) with **Root** (default C4): in Pitch the latest note shifts by (note - Root) semitones, scaled by Amount; in Ring the note's frequency is the carrier. Without a note the parameters apply.

## Output and latency
Low cut (10 to 1000 Hz) and High cut (1 to 22 kHz) on the wet path, **Output** +-24 dB, then **Mix** and **Amount** through the shared `FxStage`. Amount 0 % (or Mix 0 %) is bit-exact dry delayed by the latency; 100 % is the settings as they are; 200 % doubles Pitch, Fine, Detune, Shift, Shift fine, Feedback, Spread and Depth, each held inside its range (Depth stops at a full ring). The latency belongs to the Quality and is the same in every mode (Frequency and Ring are delayed by it), so automating Mode never moves the song. A change of path (Mode or Quality) is crossfaded over 30 ms after the new path has filled. All controls are smoothed.

## State
Version 0, 20 parameters (`Params.def`), appended-only afterwards. Eight presets in `Presets.txt`.

## Gates and their tests (`NeoBuzz.MachineTests/NeoShiftTests.cs`)
Pitch: +7 st gives 659.26 Hz and -12 st gives 220 Hz within 1 cent in Fast and Smooth; Smooth's other lines at least 40 dB down (measured -57 dB or better). Frequency: a 100 Hz shift within 0.1 Hz, image at least 60 dB down from 40 Hz to 16 kHz. Ring: sidebands at f +- fc within 0.1 Hz, input at least 90 dB down. Sweeps of Pitch, Shift and Carrier: 0 clicks, no zipper above -60 dB. Feedback +-95 % for 60 s of noise: bounded, no denormals. Latency equals the measured delay per Quality. Mix and Amount bit-exact. 0 bytes per block over 2000 blocks in every path. CPU against Dye Essence's shifter effects is reported by the test.
