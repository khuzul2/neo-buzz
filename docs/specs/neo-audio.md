# Neo Audio (NA1)

| | |
|---|---|
| Status | Built (NA1, NA2): engine review, the Neo Audio machine, the song's audio store, the import pipeline. Streaming for long files (NA1b) is not built; the seam for it is. |
| Branch | `audio/neo-audio-na1`, `audio/neo-audio-na2` |
| Related | [session-view.md](session-view.md) §1.0 (audio clips), [daw-modes.md](daw-modes.md) §4.4, [native-format.md](native-format.md) §12, [machines.md](machines.md) §3.2 |

## 1. What it is

The audio track of the Session View and the arrangement. **Neo Audio** is the Session Clip Player promoted into the Neo family: a manifest, an identity colour (category Samplers), an agent guide and versioned state. The Session engine still does the work (`ReBuzz/Core/Session/SessionClipEngine.cs`); the machine hands it each block.

Two decisions of the brief shape the rest. The audio of a clip belongs to the **song**, not to the wavetable (which has 200 slots, 16-bit-era limits and a separate editor). And a file is converted **once**, at import, to the song's sample rate, so the audio thread never resamples.

## 2. The machine

- `Machines/NeoAudio` (Windows), `Machines/Portable/NeoAudio`, `Machines/Studio/NeoAudio`; `Params.def` is the source of the generated parameter code and `Neo Audio.machine.json`. Parameters: `Monitor` (auto, in, off) and `Input` (in, master). Both were the Session Clip Player's, in the same order, so a saved song's values carry over.
- It is typed as an **effect** (one input, one output): the type comes from the `Work` signature, and a take records what is connected to it, which needs an input. A generator would refuse connections into it. (The brief called it a generator-like machine; the effect type is the deviation, and the reason.)
- State layout: version 0, recorded in `NeoBuzz.MachineTests/Layouts/state-layouts.txt` (`NeoBuzz.NeoAudio`).
- **The old name.** A song (.bmx, .bmxml or .nbl) that names "Session Clip Player" loads as Neo Audio when the old machine is not installed (`ReBuzz/MachineManagement/MachineAliases.cs`, used by `BMXFile` and `BMXMLFile`; a .nbl carries its machines in a BMX section, so it goes the same way). Where the old machine is installed (Classic keeps it), the song loads that. The song's machine type does not overwrite the installed machine's.
- **+ Track > New audio track** (and the resampling track) makes a Neo Audio in both the Classic menu and the studio's (`SessionView.Actions.cs`).

## 3. The audio store

`ReBuzz/Core/AudioLibrary`:

| Type | Role |
|---|---|
| `AudioAsset` | Id (content-addressed: SHA-256 of rate, channels and samples, 16 hex digits), name, source path, SHA-256 of the source file, channels (1 or 2), decoded float data at one sample rate, the wave it was migrated from (or -1). |
| `AudioAssetStore` | The song's assets (`ReBuzzCore.AudioAssets`): add (the same audio is one asset), get (follows aliases, used by a take whose audio does not exist yet), clear, replace. Cleared by New Song. |
| `IAudioAssetReader` | How the engine reads an asset: `Frames`, `Channels`, `SampleRate`, `Read(frame, channel, span)` (silence outside the audio) and `InMemory`. NA1 has one reader, in memory; NA1b adds a streaming one without the engine changing. |

Clips name their asset in the clip settings (`SessionDocument.AudioSettings.Asset`, saved in the SessionView sub-section). The old `Wave` field stays for clips an older song made.

**Migration.** A clip that names a wave and no asset is migrated the first time its audio is asked for: the wave's audio is copied into the store once per wave, the clip's settings get the asset id, and **the wave is not touched**. Editing the wave later no longer changes the clip (it is the song's own copy now).

## 4. In the .nbl

- A new manifest member **`audio`**: `{ "codec": "pcm-f32-shuffle-v1", "assets": [ { id, name, path, sha256, sampleRate, channels, frames, chunks: [ { blob, size } ] } ] }`. Only the assets clips use are written, sorted by id.
- The samples are written byte-shuffled (the four bytes of every float grouped by position, which compresses far better) and cut into chunks by `NblBlobSet.AddChunked`, so the same audio is one set of blobs however many clips or songs use it, and the container compresses them as for any blob.
- Reading verifies each asset's id against its samples (a damaged file is refused, naming the asset). The round trip is bit exact.
- **Version.** `formatVersion` is 2 (migration step 1 to 2 is empty: a version 1 file has no audio). A song that has audio writes `minReaderVersion` 2, so an older build refuses it by name instead of dropping the audio; a song without audio keeps `minReaderVersion` 1 and an older build opens it.

## 5. Saving as .bmx (and the NF2 export)

Upstream has no Neo Audio, so a legacy save writes each referenced asset into a **wave** of the wavetable while slots last (as float, mono or stereo), names the wave in the clip settings, saves, and frees the waves again (`SaveSongCore.Cleanup`); the open song is unchanged. Assets that find no free slot are not written. The loss report of the export (`BuzzExport.Analyze`) lists "N audio assets written as waves" (upstream does not play the clips; the waves are in the wavetable) and, for each asset left out, "Not written: the wavetable has no free slot". Reopening such a .bmx migrates the clips to assets again (section 3).

## 6. Import

`AudioImporter` (`ReBuzz/Core/AudioLibrary`): WAV, AIFF, FLAC, OGG (Vorbis) and MP3, decoded through **libsndfile** (the first two channels; longer than 30 minutes is refused), resampled once to the song's rate by `Resampler` (our own windowed sinc: Kaiser window, beta 9, 32 zero crossings each side, 512-step table; the cutoff drops to the lower Nyquist when going down, so nothing folds back), and given a SHA-256 of the file. The tempo is detected at import (`AudioAnalysis`: a loop as whole bars, else by transients, else 120).

**Why libsndfile, not AudioToolbox on macOS.** One code path for all three platforms; libsndfile (LGPL, dynamic link) is already shipped on every platform (Windows `3rdParty/libsndfile-1.dll` has the MPEG and Vorbis codecs; the mac bundle carries Homebrew's with its codec libraries; Linux uses the system library), and the wave import already uses it. AudioToolbox would add a second decoder with different rounding and no Linux or Windows equivalent. The packaging checks assert it is carried (`tools/mac-bundle.sh` verifies the codec libraries are in `libs/`; `tools/package-check.py` checks the Windows and macOS bundles).

Importing through the app: drag a file onto a slot, or the Audio section; through the agent:

| Tool | |
|---|---|
| `audio_import {path, track?, slot?, at_beats?}` | A new audio clip on a Neo Audio track (a new track named after the file when `track` is omitted), in a Session scene (`slot`, from 1; the first empty one by default), and/or the arrangement (`at_beats`). One undo step. |
| `audio_clip_get {machine, pattern}` | Warp, preserve, tempo, transpose, detune, gain, loop, region, markers, length and the asset's id, name, seconds, rate, channels and beats. |
| `audio_clip_set {machine, pattern, warp?, preserve?, tempo_bpm?, detect_tempo?, transpose?, detune?, gain_db?, loop?, region?, warp_markers?}` | Changes the clip, one undo step. |

## 7. The engine review

The audit covered `AudioClip`, `ClipVoice`, `PhaseVocoder`, `AudioAnalysis`, `AudioCapture`, `ClipMixer`, `Fft`, `SessionClipEngine`, `SessionClipHost` and the machine. No lock is taken on the audio thread, and nothing allocates there in steady state (the existing allocation tests and the new soak test).

| Issue | Severity | Fix |
|---|---|---|
| NaN or infinity in decoded audio poisons the vocoder's state for good | High | `AudioSource` silences non-finite samples once, when it is made |
| NaN, infinity or huge positions make the interpolator return NaN | High | `AudioSource.Read` returns silence outside +/-1e15 or for a non-finite position |
| Non-finite or absurd gain, semitones | Medium | `AudioClipParams` sanitises (gain 0 to 64, semitones +/-48) |
| Extreme warp markers (slopes that overflow, 1e12 samples per beat) | Medium | `WarpMap` drops markers whose slope is outside 1e-3 to 1e9 samples per beat |
| An unbounded beat-grid loop, and an O(n^2) slice proximity search, for huge or dense maps | Medium | A cap on grid slices; `SortedSet.GetViewBetween` |
| `ClipMixer.Begin` allocated on the audio thread for blocks over 256 frames | Medium | A fixed 4096-frame capacity, never reallocated; longer blocks are capped and the tail is silent |
| Recording dropped or truncated blocks over 256 frames | Medium | `Record` works in chunks of the input buffer |
| NaN or denormals in the mixed output | Medium | `Clean` on the way out of the engine |
| Vocoder denormal tails; a latent NaN in the locked phases | Low/Medium | Flush of subnormals; a finite check that resets the phase state |
| Infinite or NaN beats per sample, tempo or beat | Low | Guard at the top of `ClipVoice.Render` |
| `SourceAt` far outside the audio | Low | Clamped |
| `AudioCapture` stored NaN | Low | `Put` writes 0 for non-finite input |
| Tempo invented from noise, silence or DC | Low | A confidence floor; below it and with no bar fit the tempo is 120 |
| The 17th clip is dropped at `MaxClips`; a full mixer can cut a clip's tail | Low | Not changed (a limit, noted) |

**Tests.** `ReBuzz.SessionTests/AudioHardeningTests.cs` covers each row, every warp mode at tempo 20 and 999 BPM, transpose +/-48, mono and stereo, empty and very short and very long clips, loop edges (no click), a sample-rate change while playing, and the analysis of a 20-minute file. The **soak** (`[Category("Soak")]`): 32 voices (8 clips in each of the four warp modes) rendered for 10 minutes of audio, 0 bytes allocated per buffer, no NaN, a bounded peak. Measured here: the hardening suite and the soak ran in 9 minutes 20 seconds of wall time on one core, passing.

## 8. Tests

- `ReBuzz.SessionTests`: `AudioHardeningTests` (above), `AudioLibraryTests` (the store and its aliases and dedup, the reader, the resampler's pitch, level and alias rejection at four rate pairs, importing a file of each format with fixtures made by libsndfile in the test: WAV 16 and 24 bit, AIFF, FLAC, OGG, MP3; a mono float WAV at the song rate is bit exact; a file that is not audio is refused).
- `ReBuzz.Studio.Tests/NeoAudioTests.cs`: the name alias (unit, and a .bmxml song), the .nbl round trip (one asset for two clips, `minReaderVersion` 2, bit-exact audio after New Song and reopen, the same bytes when saved again), wave-based clips migrating and the wave untouched, the .bmx export and its report, and the agent tools. `NblFormatTests` expect `formatVersion` 2.
- `ReBuzzTests/SessionAudioTests.cs` (Windows) runs the engine through the real audio loop with a machine compiled from a short source; its wave-based clips go through the migration.
- `ReBuzz.AgentTests`: the guides name each new tool and its arguments; the catalogue has 0 problems (`tools/machine_catalog.py`).

## 9. Limits

- Everything is in memory (floats); 30 minutes of stereo at 48 kHz is about 690 MB, so long files wait for NA1b's streaming reader.
- Only two channels are kept of a file with more.
- A clip made before this version keeps its wave, which is not removed after the migration (a song saved as .nbl holds both until the wave is deleted).
- The Classic WPF clip panel still prints the wave's name for a clip that has none (it is frozen).

## 10. NA2 as built: audio clips in the Arrangement

**Own pattern per clip.** An audio clip on a lane is a run of `PlayPattern` events of an audio pattern, as any clip. Each clip has its own pattern and so its own settings (gain, warp, markers, fades, reverse); copies share only the asset. Alt-drag, Cmd-drag, Duplicate, Paste, a split, and a move to another Neo Audio track clone the pattern (`CloneAudioPattern`); a move to another track deletes the orphaned pattern. The loop switch is off for a new audio clip; with it on, dragging the right edge repeats the loop as for any clip.

**Drawing.** `PeakCache` (ReBuzz/Core/AudioLibrary) holds min/max peaks per asset: level 0 is 64-sample blocks, each next level 4 times coarser, up to 9 levels. It is built on a worker thread when an asset is first drawn (a flat line shows until it is ready), kept in memory beside the asset and never saved. The waveform is drawn per column through the clip's warp map (a stretched clip shows stretched audio), times the gain; warp markers are ticks; a "rev" badge shows for a reversed clip and "unwarped" for warp Off.

**Gestures** (each one undo step of the song; all through `ClipEditor`, so the agent reaches the same code):

| Gesture | Editor | Agent |
|---|---|---|
| Move, Alt copy, across Neo Audio tracks | `Move` | `arrangement_clip` move/copy with `to_machine` |
| Trim | `TrimLeft`, `TrimRight` | trim |
| Alt + right edge: stretch | `Stretch` (the warp follows: tempo and markers scale, the pattern length changes) | `stretch` (`end`), `audio_clip_set` `stretch_to_beats` |
| Loop switch, edge repeat | `SetLoop`, `TrimRight` | loop |
| Fade corner handles | `Fade` | `fade`, `audio_clip_set` `fade_in_beats`, `fade_out_beats` |
| Gain handle (+12 to -24 dB) | `SetGain` | `gain` |
| Split at the play position (Ctrl/Cmd+E) | `Split` | split |
| Consolidate (Ctrl/Cmd+J) | `Consolidate`: renders the range offline into a new asset and one new clip | `consolidate` |
| Reverse (R) | `Reverse` | `reverse`, `audio_clip_set` `reverse` |

**Crossfades.** Where one audio clip's start overwrites the end of another on a lane, the cut part keeps playing under the new clip for the overlap (`Tail` of the first), and the new clip fades in over it (`CrossIn`); the overlap is the crossfade (sine/cosine pair, equal power). Overlaps are found and cleaned up by `ClipEditor` after every edit; `Crossfade` sets or removes one by hand.

**Engine.** `ClipFades` (AudioClip.cs) holds the fade in, fade out, tail and crossfade flag of a clip, in beats at the clip's own tempo; they are saved with the clip's settings (`SessionDocument.AudioSettings`: `fadeIn`, `fadeOut`, `tail`, `crossIn`, `reverse`, written only when set; `FormatVersion` 2, an older song loads with none). `ClipMixer.Play` gets the position in the clip and what is left of it (from `PatternCore.ArrangementSinceClipStart` and `ArrangementUntilClipEnd`, set by `WorkManager` from the arranged events) and renders a clip with fades through scratch buffers and a per-sample envelope; `End` renders the tails of clips that have ended. Everything is sample accurate, block-size independent, and allocation-free (a test counts allocations). Clips without fades take the NA1 path unchanged. Reverse makes a reversed copy of the source and a mirrored warp map off the audio thread (an audio clip's source is a pair of float arrays), so the voice is not changed.

**Tempo.** A clip follows the song tempo unless its warp is Off. A file in which no tempo is found is imported with warp Off and the song's BPM; the tempo is set later with `audio_clip_set` (`tempo_bpm`), which turns warp on.

**Tests.** `ReBuzz.SessionTests/AudioClipEditingTests.cs` (fades, gain, crossfade against the expected envelope, tail after stop, allocation, reverse sample for sample, stretch, peak cache against brute force, settings round trip, an old song). `ReBuzz.Studio.Tests/NeoAudioArrangementTests.cs`: each gesture with the mouse or keys, then undo, redo, and the song saved and opened again as .nbl and as .bmx, plus the agent's tools. Gallery: `mac-036-arrangement-audio-clips.png`.

**Limits.** Consolidate renders at one tempo (the song's now), so tempo changes inside the range are not followed. The fade lengths are in beats of the clip, so they scale with a stretch. At most 16 clips play at once (NA1's limit). The audio thread's scratch is 4096 frames.

## 11. NA3 as built: the Audio page, warp markers, waveforms in the Session

**The page.** Selecting an audio clip in the Session or in the Arrangement shows its **Audio** page in the detail panel (the Tracker and the piano roll have nothing for it); double-clicking the clip, or the Inspector's "Audio settings", opens it. It has the waveform with the clip's region handles (start, loop start, loop end) and the warp markers, and controls for warp (the model's own modes) and what Beats keeps, tempo (with halve, double and Detect), transpose, detune, gain, fade in and out (Arrangement only: fades play there), reverse, loop and the region in bars.beats.ticks. In the Session the loop and region are the slot's clip settings; in the Arrangement they belong to the arranged clip (`ClipEditor.SetLoop`, `SetRegion`).

**One write path.** Every edit is one `AudioClipEdit` handed to `SessionLayout.EditAudio`, which is also what `audio_clip_set` calls: the same validation, one undo group, the same result. A test makes the same change by the page and by the tool and compares.

**Warp markers.** Double-click adds a marker (pinned to a transient of `AudioAnalysis` when one is within a few pixels; the audio's two ends are pinned first, so nothing moves), a drag warps the audio between its neighbours and snaps to the beat grid (Shift is free; the grid follows the zoom), double-click, right-click or Del removes one. A drag is one undo step, committed on release.

**Session slots.** A slot of an audio clip draws a mini waveform from the PeakCache (a column every two pixels through the clip's warp map and gain) into a geometry that is kept until the slot is resized, the peaks arrive or the audio changes; a frame draws that geometry and one line, so a grid of slots costs nothing while it plays.

**Fixes.** The Inspector says "Generator" for Neo Audio (it was "Effect"); a short Arrangement audio clip shows its whole name on the first line of its tooltip.

**Tests.** `ReBuzz.Studio.Tests/NeoAudioClipPageTests.cs` (each control: value, undo, redo, saved and opened again as .nbl; markers by mouse and keys with and without snap; both views; the agent against the page; the slots). Gallery: `mac-037`, `038`, `039`, `461`.

**Limits.** No drops (DD1), recording (NA4), Pro mode (NA5) or disk streaming. Removing the last inner marker leaves the two pinned ends (the map stays straight). Detect finds a tempo only where the audio has one.
