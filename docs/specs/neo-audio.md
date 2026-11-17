# Neo Audio (NA1)

| | |
|---|---|
| Status | Built (NA1, NA2): engine review, the Neo Audio machine, the song's audio store, the import pipeline. Disk streaming for long files is built (NA1b, section 13). NA5 adds the Pro warp mode (section 11). |
| Branch | `audio/neo-audio-na1`, `audio/neo-audio-na2`, `audio/neo-audio-na5` |
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
| `IAudioAssetReader` | How the engine reads an asset: `Frames`, `Channels`, `SampleRate`, `Read(frame, channel, span)` (silence outside the audio) and `InMemory`. Two readers: in memory, and (NA1b) one over the decoded cache file. The engine reads `AudioSource`, which switches between the two behind a stable object (section 13). |

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

- Audio over the streaming threshold (8 MiB of floats by default, about 25 s of stereo at 48 kHz) streams from the decoded cache (section 13); shorter audio is in memory as floats.
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

## 11. NA5: the Pro warp mode

A fifth warp mode, **Pro**, for clips in the Session and the Arrangement. Off, Repitch, Beats and Complex are unchanged. Pro is a stretcher by Signalsmith (Signalsmith Stretch 1.3.2 with Signalsmith Linear 0.6.6, both MIT), vendored under `NeoStretch/third_party/`.

**Integration: a native library (A), not a C# port (B).** A small C ABI library, `neostretch` (`NeoStretch/`, CMake), wraps the header-only C++ library. Reasons: the stretcher is about 3000 lines of templated, SIMD-friendly C++ over a custom FFT, and a port would have to be re-verified against upstream with every release and would run slower under the JIT; the repo already builds native libraries (LinkHelper, neoplug) with CMake in the three workflows, so the build, the loader (`NativeLibraries.TryLoad`) and the packaging steps exist. The library builds for win-x64 (MSVC, static runtime), macOS (a universal binary, arm64 and x86_64, checked with `lipo -info` in the Mac workflow and in `tools/mac-bundle.sh`) and linux (static libstdc++, only the `ns_` functions exported). The shim sets flush-to-zero and denormals-are-zero around every call, never throws across the ABI and allocates only in `ns_create`. `NeoStretch/test.cpp` (ctest) counts global allocations across hundreds of process calls (zero), checks finiteness and the absence of subnormals on noise, silence and bursts, and checks that a hit lands where it should.

**Missing library.** `NeoStretch.Available` is false when the library is not found (or `NEO_STRETCH_OFF=1`, which the tests use). Pro then plays as Complex, the best remaining mode, and one line is logged (a warning in the log). `audio_clip_get` shows `warp: "Pro"` as set and adds `warp_playing: "Complex"`. Nothing is silent and nothing throws. `NEO_NATIVE_DIR` names an extra folder to search first.

**Design.**
- A voice has three stretchers, created in `Prepare` off the audio thread: the sounding one, one for the fade of the previous sound, and a spare (see below). A jump, a stop or a launch over a playing clip fades the old stretcher out over 64 samples while the new one starts, so there are no clicks and no raw tails.
- The stretcher runs in fixed 256-sample chunks on a grid counted from the (re)start, behind a FIFO, so the output does not depend on the audio block size (tested: identical).
- Latency. The output trails its input by the library's latencies (about 2880 samples in, 4320 out at 48 kHz with the preset used: 120 ms block, 30 ms hop). A start uses the library's output seek so the first sample out is the audio at the beat; later chunks read the source up to the warp map's position for the chunk's end, plus the input latency. A Pro clip lines up with a Repitch clip of the same file within 1 ms (tested at three offsets and three block sizes).
- The warp map, tempo changes and the loop, region and fades of NA2 work as in Complex: the voice asks the map for the source position per chunk and the stretcher's ratio follows. Transpose and detune are the stretcher's pitch factor; a source at another rate than the output uses the factor times the rate ratio.
- Real time: no allocation, locks or exceptions on the audio thread (a test counts managed allocations across starts and stops; the native test counts native ones). The native memory is reported to the GC (`AddMemoryPressure`) and freed by the finalizer.

**Starts and the audio thread's budget.** A scene launch can start many Pro clips in one block, and eight seeks (about 9 ms) would not fit a 256-sample block (5.3 ms at 48 kHz). Two measures keep the worst case small:
- The spare, a third stretcher per voice, is sought ahead by one low-priority worker thread (`ProWorker`, a tick every 2 ms when idle). It is sought at the beat the last start was at (a launch and a loop going round start in the same place) and at the tempo and output rate the engine last ran at; a clip's first spare is asked for when its params are set (the UI thread, `Prepare`), at beat 0. A start that finds the spare ready for exactly this start (same audio, beat, input length and pitch) takes it as its playing stretcher, with no work on the audio thread. One state word (`spareState`: requested, the worker's, ready, taken) hands it over, with no locks; the old fading stretcher becomes the next spare, asked for at once.
- A start with no spare does its seek in the block only if the mixer's budget has one left (`SeekBudget`: one per block per mixer). A clip that does not get it plays as Beats (the stand-in: pitch and time right at any tempo and with any transpose, and cheap) with the usual 64-sample fade-in, asks again each block, and when its turn comes the stand-in fades out under the stretcher's start (the same 64-sample crossfade). At worst a scene of 16 unanticipated starts takes 16 blocks (85 ms at 256 samples and 48 kHz) to be all Pro; until then a clip is a Beats clip. (It was Repitch, which is the wrong pitch at a tempo other than the clip's or with a transpose; `TheStandInForAProStartWithNoSeek...` plays a sine at 0.8x and at +7 semitones and finds the stand-in within 8 cents of Pro's pitch, where Repitch is 386 cents off at 0.8x. The few cents are the seams of Beats' slices on a steady tone.)

Measured (`NeoStretchTests`, 16 Pro clips from one block, 48 kHz, 256-sample blocks, the time of the whole mixer block with 16 Pro voices rendering): worst block 2.2 ms with no spare and 2.0 ms with spares (most of it is the 16 voices' steady rendering), against a budget of half a block (2.7 ms). Before this change the same block held 16 seeks, about 17 ms of seeking alone. Once settled every hit is within 1 ms of Repitch's, and with spares ready it is from the first.

**Saving.** Pro is written as `warp="Complex" pro="true"` in the clip's `<Audio>` settings. An older reader does not know `pro`, loads Complex, which is the best mode it has, and keeps playing; this needs no format bump (`formatVersion` and `minReaderVersion` stay), because an unknown attribute is ignored by the readers of that format (native-format.md). A newer reader maps Complex with `pro` back to Pro. A reader that rewrites the clip drops the flag, which is the mode it can play.

**Agent and UI.** `audio_clip_set warp:"pro"` (case-insensitive), `audio_clip_get` reports it; the guide `Neo Audio.md`, GuideTests, `tools/agent-coverage.py` and `docs/agent-coverage.md` are updated. The clip panel's warp list has Pro (the only place the mode is chosen).

**CPU.** One stereo voice, 44.1 kHz, 256-sample blocks, measured in `NeoStretchTests` (Debug build of the managed side; the native library is optimized): Repitch about 0.9 % of a core, Beats 0.44 %, Complex about 7.9 %, Pro about 1.5 %. The native benchmark at 48 kHz gives about 1.1 % per stereo voice and 0.73 % per mono voice. Pro is therefore about five times lighter than Complex and about as cheap as Repitch's interpolation.

**Tests.** `ReBuzz.SessionTests/NeoStretchTests.cs` (45: sixteen starts in one block within a budget with and without spares, a loop finding its spare, loading, pitch at several tempos, length, transpose and detune, tempo change while playing, other source rates, onset alignment with Repitch within 1 ms, block-size independence, loop seams, stop and launch without clicks, mode changes, no managed allocation, finiteness, the missing-library fallback and its log, the save and load round trip, an old reader); `AudioHardeningTests` has Pro in every mode row and a Pro group of clips in the soak; `NeoStretch/test.cpp` (ctest, run by the three workflows); `ReBuzz.Studio.Tests/NeoAudioTests` (the agent sets Pro, saves and reopens).

**Limits.**
- A start costs the library's output seek (about 1.1 ms of native time). The audio thread never pays more than one of them per block per mixer (see Starts and the audio thread's budget above).
- A start with no spare ready plays as Beats for a block or more (a steady tone has the seams of Beats' slices, and drum attacks are Beats', not Pro's) when others start in the same block. The spare is made for the last start's beat (beat 0 for a clip not yet started), so a first launch of an unseen clip at another beat, or one after a tempo change, is unanticipated.
- Drum attacks are softened compared with Beats at large stretch (the library's phase vocoder smears a transient by a few milliseconds); Beats stays the mode for slicing drums.
- The Intel Mac library is built (a universal binary) so a development build on an Intel Mac plays Pro; the shipped app itself is osx-arm64 only. Without the library, Pro plays as Complex.
- The 16-voice limit is unchanged; a Pro voice holds three stretchers (the playing one, the one fading out, the spare), so 16 stereo voices hold about 50 MB.
- The tonality limit (8 kHz, pitch shifting) applies only when the source and output rates are equal.
- Blind-test renders for judging the modes by ear are made by the worker, not shipped in the repo (they are synthetic, and kept outside it).

## 12. NA3 as built: the Audio page, warp markers, waveforms in the Session

**The page.** Selecting an audio clip in the Session or in the Arrangement shows its **Audio** page in the detail panel (the Tracker and the piano roll have nothing for it); double-clicking the clip, or the Inspector's "Audio settings", opens it. It has the waveform with the clip's region handles (start, loop start, loop end) and the warp markers, and controls for warp (the model's own modes) and what Beats keeps, tempo (with halve, double and Detect), transpose, detune, gain, fade in and out (Arrangement only: fades play there), reverse, loop and the region in bars.beats.ticks. In the Session the loop and region are the slot's clip settings; in the Arrangement they belong to the arranged clip (`ClipEditor.SetLoop`, `SetRegion`).

**One write path.** Every edit is one `AudioClipEdit` handed to `SessionLayout.EditAudio`, which is also what `audio_clip_set` calls: the same validation, one undo group, the same result. A test makes the same change by the page and by the tool and compares.

**Warp markers.** Double-click adds a marker (pinned to a transient of `AudioAnalysis` when one is within a few pixels; the audio's two ends are pinned first, so nothing moves), a drag warps the audio between its neighbours and snaps to the beat grid (Shift is free; the grid follows the zoom), double-click, right-click or Del removes one. A drag is one undo step, committed on release.

**Session slots.** A slot of an audio clip draws a mini waveform from the PeakCache (a column every two pixels through the clip's warp map and gain) into a geometry that is kept until the slot is resized, the peaks arrive or the audio changes; a frame draws that geometry and one line, so a grid of slots costs nothing while it plays.

**Fixes.** The Inspector says "Generator" for Neo Audio (it was "Effect"); a short Arrangement audio clip shows its whole name on the first line of its tooltip.

**Tests.** `ReBuzz.Studio.Tests/NeoAudioClipPageTests.cs` (each control: value, undo, redo, saved and opened again as .nbl; markers by mouse and keys with and without snap; both views; the agent against the page; the slots). Gallery: `mac-037`, `038`, `039`, `461`.

**Limits.** No drops (DD1), recording (NA4), Pro mode (NA5) or disk streaming. Removing the last inner marker leaves the two pinned ends (the map stays straight). Detect finds a tempo only where the audio has one.

## 13. NA1b as built: disk streaming

**Decoded cache.** One file per asset in `LocalApplicationData/NeoBuzz/AudioCache/<asset id>.nbdc`, keyed by the asset id (the SHA-256 of rate, channels and samples, so it is shared across songs and survives reopen). Format `NBDC0001`: a 128-byte header, a CRC32 per (page, channel) piece, then 4096-aligned pages of 8192 frames (the channels' pieces one after another, so one read fetches a page). It is written as `.part` and renamed when whole. `AudioCache` (ReBuzz/Core/AudioLibrary/AudioCache.cs) also holds the size, `Clear`, `Trim` and `Touch`.

**One source, two backings.** `AudioSource` keeps its identity but is switchable: float arrays, or a page table over a `StreamedAudio`. The engine and voices are unchanged. An asset over the threshold is imported or loaded into memory, plays from it, and a background job writes the cache, opens it, waits until the head and pins are resident (`WaitSettled`) and only then drops the floats: the switch is bit-identical and has no click. `AssetStorage` is Memory, Filling, Streaming, Repairing or Failed; progress is reported while filling. A song opened with a valid cache file builds the asset straight from it (no decode, no memory peak). `AudioAssetStore.Add`, `ReplaceWith` and the store API are unchanged.

**Audio thread.** Reads take a `Volatile.Read` of a `Page[]` table. A missing page is silence plus an `Interlocked` count of missed samples; a voice turns that into one underrun per block and recovers on the next block. No I/O, lock or allocation. Everything that is not the audio thread (analysis, peaks, save, `ToArrays`, `ForAnalysis`, offline render for Consolidate) uses exact reads of the file, never the lossy path. A reversed long source is a mirrored view, not a copy.

**Prefetch.** One `StreamPrefetcher` thread (BelowNormal, 2 ms tick) reads the voices' hints (seqlock, written by the audio thread) and loads pages in stages: near the playhead, the read-ahead window (2 s, widened by speed, reverse, loop wraps and warp), then the pins. It keeps a 96 MB resident budget, evicting the least recently wanted pages, and recycles pages through a 2 s pool. `ClipVoice.Anticipate` and `SessionCore.AnticipateAudio` hint a queued launch from the UI thread, so a quantised launch finds its pages.

**Pins.** The first 500 ms of every streamed asset, and, for each clip using it, map sample 0, Start, LoopStart and up to 48 warp markers (each from 4096 frames before to 500 ms after). Arrangement lookahead is covered by these pins plus the playhead window, not by a separate scan.

**Policy and settings.** `streaming.txt` (key=value, in the cache folder's parent): ThresholdBytes 8 MiB, HeadMilliseconds 500, ReadAheadSeconds 2, ResidentBytes 96 MiB, CacheLimitBytes 4 GiB, Enabled. `NEO_STREAMING_OFF=1` turns streaming off. Preferences > Audio > Disk streaming shows the cache size, Clear, the threshold and the limit. The limit is enforced by LRU on files the open song does not use.

**Repair.** A missing cache file is copied back from the open handle when it was only unlinked; a damaged piece (CRC) or a lost file makes the asset Repairing and the cache is made again from its recipe while it plays silence for what is not there: `NblRecipe` (decodes the song file's blob page by page and checks the id) or `FileRecipe` (re-imports the source file).

**Agent.** `audio_clip_get` reports storage, streams, cache_progress, underruns, missed_samples and resident_mb; `studio_status` has `stream_underruns`. `ResetAudioDropoutCounters` resets the stream counters.

**Measured** (Debug build, 2 cores, no native Pro library): 30 minutes of stereo at 48 kHz is 659 MB of floats; streamed it adds 1 MB of managed memory (4 MB after playing, 3.7 MB of pages resident). 16 streamed voices (8 Repitch, 4 Beats, 2 Complex, 2 Pro) with random seeks, loops, reverse and warp for 30 s in real time at 256-frame blocks: 0 underruns, 0 missed samples, 0 bytes allocated, worst block 41.5 ms (Debug DSP), worst prefetch pass 38 ms. Per block, streamed against memory: Repitch 0.078 / 0.045 ms, Beats 0.064 / 0.035 ms, Complex 0.627 / 0.523 ms.

**Tests.** `ReBuzz.SessionTests/NeoAudioStreamingTests.cs` (format, damage, reads, reversed view, bit-identical switch in each warp mode, cold start, unwarned jump, rebuild, trim, settings, eviction, pins, the 30-minute asset, zero allocation, the 16-voice run, and two Soak tests: 3 minutes of 16 voices and 10 minutes in every mode) and `ReBuzz.Studio.Tests/NeoAudioStreamingSongTests.cs` (save, reopen from the cache and from the blobs, rebuild; short audio stays in memory).

**Limits.** An asset with no recipe (not imported from a file, not yet saved) whose cache is damaged cannot be made again and plays silence. A first open with no cache decodes to memory before switching (a transient peak). Saving .nbl still builds its container in memory. A jump with no warning (a seek the hints did not see) can underrun once. Consolidate reads a streamed clip into memory for the render.
