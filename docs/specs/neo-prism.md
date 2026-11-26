# Neo Prism (PRI1, issue #188)

A spectral effect per track. Clean room: published phase-vocoder and spectral processing (Dolson; Laroche and Dolson 1999) and our own DSP, on the shared `Machines/NeoBuzz.Dsp/Stft.cs` (sqrt-Hann windows, hop N/4, latency N - 1; no change to it).

## Order and sections
Five sections, any of them on at once, always in this order: **Spectral pitch, Scramble, Freeze, Blur, Spectral delay**. Each section's on/off is a weight that ramps over 30 ms, so switching never clicks.

- **Spectral pitch:** -24 to +24 semitones and +-100 cents. Peaks are found per frame, frequency comes from the phase advance, each peak is rewritten as a sine-window lobe at the new frequency (+-4 bins) with the output phase advanced at the new frequency, and the bins of a region follow their peak's phase (Laroche-Dolson), so a chord keeps each partial. **Formant keep** moves the partials and leaves the smoothed spectral envelope where it was.
- **Scramble:** inside bands of Band width bins (2 to 64), Scramble amount (% of the bins) swap places; a new seeded permutation comes at Rate (0.1 to 20 Hz) or on the song grid (1/16 to 2 bars); the same seed gives the same sequence. A peak guard follows the shuffle (no parameter): each frame goes back to the time domain with its input, and when the shuffled peak is more than 10 % above the input peak it is scaled down by the ratio (gain falls 70 % of the gap per frame, rises 15 %, never boosts), so no setting raises the peak by more than about 1 dB.
- **Freeze:** holds the magnitudes, each region's phase advancing at its own frequency, so a held sine keeps its level (within 0.5 dB over 30 s). Triggered by the switch, a held note (note wire, live MIDI or the track column) or the song (Every 1 beat to 4 bars, Length 1/16 to 1 bar: the sound is caught at the start of each period). **Hold blend** crossfades live and frozen over at least 20 ms.
- **Blur:** a one pole per bin on the magnitudes (0 to 4000 ms), then Smear (0 to 24 bins either side) across frequency.
- **Spectral delay:** each bin is delayed by a whole number of frames; the time tilts from Low time (20 Hz) to High time (20 kHz) straight on the log-frequency axis (0 to 2000 ms); Delay sync rounds both times to a multiple of a note at the song's tempo; Feedback (0 to 99 %, always < 100 %) and Damping (extra high-frequency loss per repeat).

## Output
FFT size 1024, 2048, 4096 (default) or 8192, the latency is size - 1 and is reported to the host. A size change builds nothing on the audio thread: all four chains exist up front; the new chain runs on the same input, fills for a frame, and the old one is crossfaded out over 30 ms. **Mix** (dry/wet, the dry path delayed by the chain's latency) and **Amount** (0 to 200 %, 100 % = the settings, 0 % = bit-exact dry; it scales the semitones and cents, the scramble amount, the blur time and smear, the delay times and the feedback) through the shared `FxStage`.

## State
Version 0, 27 parameters (`Params.def`). Presets in `Presets.txt` (eight).

## Gates and their tests
`NeoBuzz.MachineTests/NeoPrismTests.cs`: transparency at every size (-136 dB), Mix and Amount bit-exact, latency equals the measured delay to the sample, pitch (+7 and -12 within 2 cents; chord within 2 cents with no new peak above -40 dB at 4096 and 8192), freeze level, delay arrival within one hop, 95 % feedback bounded over 60 s, clicks (detector and +-3 ms), 0 bytes per block at every size, denormals, CPU at 4096, state and presets. Limit: chord quality at 1024 and 2048 is weaker (peaks -29 to -42 dB down, up to 3.85 cents at -12) and is not a gate.
