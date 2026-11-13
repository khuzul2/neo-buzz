# Dye Essence: analysis, import and the analytic engines

The three engines that work from a sample's analysis: the Spectral engine, partials imported into the Harmonic engine and modes imported into the Modal engine. The rest of Dye Essence is in `guide(topic="dye-essence-in-depth")`.

## The analysis

A sample (built-in, or your own WAV) is analysed once, off the audio thread, when it is loaded (a second or less; the editor shows "analysing"). The result is up to 64 tracked partials per frame (frequency and level, frames about 43 ms apart at 48 kHz) and a 24-band noise residual. It is kept in the song beside the sample, so a song reloads to the same sound without analysing again. No phases are kept: a sharp attack softens into a short swell, so the engines below suit sustained and evolving sounds, and the Sample engine suits drums and plucks.

## Spectral engine

Plays `a sample` (built-in or user) from a model made when the sample is loaded (up to 64 tracked partials and a 24-band noise, kept in the song), so speed and pitch are independent. `a spectral speed` 0–400 % (0 holds one moment at `a position`), `a key tracking`, `a formant keep` (keeps the spectral envelope), `a harmonic snap` (partials pulled to a harmonic series), `a tonal noise` (50 % as analysed; 0 only the noise, 100 % only the partials), `a blur` (smears changes), `a spectral partials` (1–64, the cpu control), `a spectral tilt` (±24 db an octave) and `a spectral stretch` (±100 %). loop, reverse and `a root` as the sample engine. no phases are kept, so attacks soften: use it for sustained and evolving sounds, the sample engine for drums and plucks.

## Harmonic: import partials from a sample

`A import partials` Sample (slot A; B and C the same) replaces the two sets with the partials of the slot's `A sample` (built-in or User; load your own with `machine_data_import kind="sample"` for that slot: the engine stays Harmonic). `A import from` (0–100 %, default 50 %) is where in the sample the table is read, `A import span` (0 %: that one frame; above it an average of the frames to there, a partial that sounds in half of them at half the power) and `A import ratios` (100 %: each partial where it was analysed, so a bell or a bar stays metallic; 0 %: whole multiples of the pitch, the same levels as a clean tone). Rows are sorted by place, the lowest strong partial (within 14 dB of the strongest) sounds the key played, and `A partials` keeps the lowest rows. `A tilt`, `A stretch` and `A damp` still apply; the two sets, `A position` and `A odd even` do not (the table has no sets). The table is worked out from the stored analysis and these parameters, so presets and songs carry it, and moving `import from` while a note is held retunes the partials. Without an analysis yet (a user sample still analysing) the slot plays its sets.

## Modal: mode table from an impulse response

`A mode table` Impulse response (slot A; B and C the same) takes the body's modes from the slot's `A sample` read as an impulse response: a short struck or clicked sound of a bell, a box, a plate or a room. `A ir start` (0–100 % of the sample) skips the click or strike, `A ir length` (50 ms–2 s) is how much is analysed, `A ir decay` (25–400 %, 100 % as measured) scales every mode's ring. Each partial track of at least three frames is a mode: its frequency, its decay (a line fitted through its level in dB) and its gain; the strongest `A modes` (up to 64) sound, and the lowest strong one sounds the note played, so the body plays across the keyboard. `A exciter` and `A force` still choose what excites it (Plucked starts the modes at their measured gains); `A geometry`, `A position`, `A material`, `A ring time`, `A hf damp` and `A body size` do not apply (the recording has its own). Modes that decay faster than 50 ms or slower than 20 s are limited to those.

## Recipes

- A clarinet or voice as a clean organ: Harmonic, `A import partials` Sample, `A sample` Choir, `A import ratios` 0 %, `A import from` 40 %.
- A bell from any sound: Harmonic with `A sample` Bell, `A import ratios` 100 %, `A damp` 40 %; or Modal with `A mode table` Impulse response on Bell, `A exciter` Plucked, `A modes` 12–16.
- A frozen choir: Spectral, `A spectral speed` 0 %, `A position` 35 %, `A blur` 40 %.
- Compare: `render` the same chord with `A import ratios` at 0 % and 100 % and `plot` the spectrum.

## Checklist

- [ ] The slot's engine is the one you meant: `param_get(machine="Dye", params=["A engine", "A sample"])`, and a user sample loaded with `machine_data_import(machine="Dye", kind="sample", path=…, item=0)` for slot A.
- [ ] An import is on: `param_get(machine="Dye", params=["A import partials"])` for Harmonic, `["A mode table"]` for Modal; read the value text.
- [ ] It sounds as intended: `render`, then `analyze(source=…)`; compare `A import ratios` at 0 % and 100 %, or `A modes` at 8 and 32.
