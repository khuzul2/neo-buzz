# Native song format (.nbl)

Status: NF1 built (spec, container, reader and writer, import of every old format, tests). NF2 (the lossy "Export for Buzz/ReBuzz (.bmx)") is next and is not part of NF1.

**Decision (2026-11-15).** NeoBuzz Live stops writing files that upstream ReBuzz must play. New songs save as `.nbl` ("NeoBuzz Live song"). `.bmx`, `.bmw` and `.bmxml` are read forever. Old machines are still hosted and the engine model does not change.

## 1. Goals

One file. Deterministic bytes (the same song saved twice is the same file). Atomic save. UTF-8 names. 64-bit sizes and positions in the file. No MIDI-binding limit. A version number and load-time migrations. What a build does not know is kept when it saves. Samples, plug-in states and the embedded history share one deduplicating store.

## 2. Decisions

| Question | Decision | Why |
|---|---|---|
| Container | Own single file: JSON manifest + table of content-addressed blobs (§3). Not a zip. | A zip's bytes depend on time stamps and tool choices; this one depends only on content. The blob rule is the history store's (SHA-256 of the raw bytes, deflate, `ChunkStore.Split` chunker), so a sample cut here has the hash it has in a snapshot and is held once. |
| Core engine data | NF1 keeps the **core sections** in the BMX section layout, built and loaded by `BMXFile` (the same code, the same model), inside the file as chunked blobs, with UTF-8 strings. | The core codec is proven against the demo songs and every machine; a second reader and writer for machines, patterns, sequences and waves would be a second model to keep equal (the brief: no second model). §8 lists what this does not yet give and how it is replaced section by section by migrations. |
| Side stores | The subsections the stores write (Session View, arrangement, events, markers, notes, bars, editor state) are **manifest fields**: XML documents become JSON (§5), anything else a blob. | The store keeps its own schema and code; the file holds it as data you can read. |
| History | The embedded snapshots are manifest entries; their chunks are blobs of the same table. | One dedup store. |
| Studio | `.nbl` default for new songs, Save As, recent files, autosave and the history embed. | Classic WPF keeps `.bmx` only: it is frozen (fixes only) and offers no `.nbl` in its dialogs. It still opens and saves a `.nbl` it is given by path (the loader chooses by extension). |

## 3. Container

```
8 bytes   89 'N' 'B' 'L' 0D 0A 1A 0A
u32       container version (1)       u32  flags (0)
u64       manifest length             u64  blob count
manifest  UTF-8 JSON
index     per blob, sorted by hash: 32 bytes SHA-256, 1 byte codec (0 raw, 1 deflate), u64 stored length
blobs     in index order
32 bytes  SHA-256 of all of the above
```

Little-endian. A blob is named by the SHA-256 of its raw bytes; deflate is used when it is smaller. Large data is cut with `ChunkStore.Split` (16-256 KB, content-defined) so an edit in the middle keeps most chunks. The reader checks the trailer, the sizes and each blob against its name; a damaged or truncated file is refused with a message, never half-loaded.

**Atomic save.** Written to `.<name>.tmp-<guid>` in the same folder, flushed to the disk, renamed over the target. A crash or a full disk leaves the old file whole; a failed save removes its temporary file.

**Deterministic.** No time stamps, no random ids. Blobs are sorted by hash, stores by name, sections in the order `BMXFile` builds them. The writer records the build's version in `writer.build` (a different build may write different bytes for the same song; the same build never does).

## 4. Manifest

```json
{
  "format": "neobuzz-live-song",
  "formatVersion": 1,
  "minReaderVersion": 1,
  "writer": { "app": "NeoBuzz Live", "build": "..." },
  "index":  { "bpm": 126, "tpb": 4, "songEnd": 64, "loopStart": 0, "loopEnd": 64, "machines": [ { "name": "Lead", "dll": "Neo Poly", "x": 0.1, "y": -0.4, "tracks": 1 } ] },
  "machines": [ { "name": "Comp", "stateVersion": 3, "params": { "threshold": 420, "ratio": 250 } } ],
  "core":   { "codec": "bmx-sections-v1", "strings": "utf-8", "sections": [ { "id": "MACH", "size": 4096, "chunks": [ { "blob": "<sha-256>", "size": 4096 } ] } ] },
  "stores": { "SongMarkers": { "kind": "xml", "doc": { "element": "SongMarkers", "attrs": { "version": "1" }, "children": [ ... ] } },
              "SequenceEditor": { "kind": "binary", "size": 90, "chunks": [ ... ] } },
  "history": { "manifests": [ { snapshot manifest } ], "chunks": [ "<sha-256>" ] }
}
```

`index` is derived on every save for tools that list a song without loading it; the loader ignores it. `history` exists only with the "embed history" option.

## 5. What maps where

| Song data | Where in `.nbl` (v1) |
|---|---|
| Machines: dll, name, position, tracks, attributes | `core` sections MACH, PARA, XNOC… (old Buzz machines keep positional values, mapped by the PARA definition: name, type, range). |
| Parameter values of our versioned machines (`IVersionedMachine`, machines.md §3.2) | manifest `machines`: `{name, stateVersion, params:{<Params.def key>: raw}}`. On load the values go through the machine's `MigrateKeyed(stateVersion, …)`, so a layout that changed since the song was saved loads by identity; the machine's own data goes through `MigrateData` as for every format. |
| Opaque machine data, plug-in states, modulation | The machine's data blob in `core` (chunked, hashed: an unchanged state is the same blob in every save and in the history). Modulation lives in machine state. |
| Connections (amp, pan, channels) | `core` CONN |
| Patterns, columns, TAP2 engine columns, MPE, PXP data | `core` PATT, XPAT, XTAP, TAP2, machine data |
| Sequences, clips | `core` SEQU; trims, gain, clip ids: store `ArrangementEvents`; lane roles, folds, takes: `Arrangement` |
| Loops, automation with ramps | `core` TAP2 (ramp = event duration), SEQU, session clips in `SessionView` |
| Tempo and meter | Master parameters in `core`; the bar list in store `SequenceEditor` |
| Markers, notes | stores `SongMarkers`, `SongNotes` |
| Session View | store `SessionView` |
| Arrangement view state, editor and UI state | stores `Arrangement`, `SequenceEditor`, `ModernSequenceEditor(Horizontal)` |
| Groups | `core` MGRP |
| MIDI bindings | `core` MIDI and MID2 (16-bit machine parameter index; a test song binds 704 parameters) |
| Waves | `core` WAVT, WAVE, CWAV as chunked blobs, stored once |
| Embedded history | `history` + the shared blobs |

XML to JSON: `{"element":"Name","attrs":{...},"children":[...]}` or `"text"`. A document is stored as JSON only if converting it back gives the same bytes; comments, CDATA, namespaces, mixed content and formatting whitespace make it a `binary` store instead (chunks), so a store that changes its output can never be corrupted by the conversion.

## 6. Versions, migrations, forward tolerance

- `formatVersion` is the schema this file was written with; `minReaderVersion` is the oldest reader that can load it. A reader refuses a file with `minReaderVersion` above its own, naming the version it needs.
- `NblMigrations` holds steps `v -> v+1` that edit the manifest and may read and add blobs (`NblReadContext`). They run when a file is loaded, until it is at the version this build writes. A file with a newer `formatVersion` whose `minReaderVersion` is not above ours is read as far as it is known.
- Unknown top-level members and unknown stores are kept in `ReBuzzCore.NativeCarry` (with the blobs they refer to) and written back by the next save of that song. A new song, or one opened from another format, carries nothing. A known store the song no longer has is not carried back.
- Machine migrations later: the manifest and PARA keep parameter identity, so a migration can rewrite a machine's section by name.

## 7. In the apps

- **Open** accepts `.nbl`, `.bmx`, `.bmw`, `.bmxml`. The loader chooses by extension.
- **Save** writes by the song's file extension (`GetReBuzzFile`). New songs: Save As proposes `Untitled.nbl`. A song opened from `.bmx`, `.bmw` or `.bmxml` has `ReBuzzCore.LegacyOrigin`; **Save asks for a name** (default: the same name with `.nbl`) and the original is not written. After that save it is a `.nbl` like any other. Choosing a `.bmx` or `.bmxml` name in Save As still writes that format.
- **Autosave** uses `.nbl` for new and `.nbl` songs, and keeps `.bmx`/`.bmxml` for songs of those kinds; recovery lists all three.
- **History embed** puts the snapshots in the `.nbl` (§5); loading with no history folder imports them as before.
- **Agent.** `song_open` takes `.nbl`. `song_save` takes `.nbl` (recommended), `.bmxml`, `.bmx`; new optional `format` (`nbl` default, `bmxml`, `bmx`) names the type when `path` has no extension, and for the new file beside an opened old song when `path` is omitted: the original is never overwritten by `song_save` without a path. rebuzz-headless uses the same tools and loader.
- **File associations** add `.nbl`.

## 8. Not in NF1

- The core is still the BMX section codec inside blobs. What that keeps: 16-bit machine indices in some sections, 16-bit sequence machine count, section sizes under 4 GB each (the container itself is 64-bit), `int` tick positions in the engine (the file stores them as variable-width values up to 64 bits). What it gives up that the brief lists: machine/pattern/connection data as manifest fields. Plan (NF1b): one migration per section replaces its blob with JSON entities plus blobs, loader and writer together, tested by the same round-trip and import tests.
- Parameter values are integers as in Buzz; real-valued parameters need the engine model to change, which is out of scope.
- NF2: "Export for Buzz/ReBuzz (.bmx)" (lossy) reuses `BMXFile.Save` and is a menu item and a `format="bmx"` path of `song_save`.

## 9. Tests

`ReBuzz.Studio.Tests/NblFormatTests.cs`: deterministic container, damage and truncation refused, atomic save, XML as JSON only when exact, migrations, newer files, a song saved/opened/saved to the same bytes with its markers, notes and bars, UTF-8 names and an event past 65,535 ticks, unknown members and stores kept (200 KB of blobs), old song saved as a new `.nbl` with the `.bmx` untouched, song tools, and the demo songs (open `.bmx`, save `.x.bmx` and `.nbl`, open the `.nbl`, save `.y.bmx`: `x == y` byte for byte, and `.nbl` stable). The fork features' own tests also run with `.nbl`: `DawModesSeamTests`, `ArrangementViewTests`, `ArrangementRecordingTests`, `AutomationLaneTests`, `SongNotesTests`, `DyeStressTests` (704 MIDI bindings, 8.4 MB of machine state). The embedded history: `SnapshotTests`. `tools/song-roundtrip.py` adds the `.nbl` pass on Linux CI (same 12 songs, with a render of each).

## 10. Measures

Linux, 2 shared cores at load 3-4 (indicative; times in ms, one run each, `NblFormatTests.TheDemoSongsBecomeNblAndComeBackTheSame` and `DyeStressTests.ASongWithItAllSavesAndOpens`). "Saved .bmx" is what this build writes for the song, not the file that was opened (the fork adds the placeholders' data and sections).

| Song | Opened .bmx | Saved .bmx | .nbl | Save .bmx | Save .nbl | Open .bmx | Open .nbl |
|---|---|---|---|---|---|---|---|
| Elak_SVF (tiny) | 1,606 | 3,512 | 5,136 | 1 | 1 | 36 | 38 |
| Oomek_Aggressor_demo | 8,178 | 18,117 | 8,019 | 2 | 3 | 88 | 90 |
| Jeskola_NiNjA_dElaY | 73,043 | 74,049 | 6,162 | 1 | 2 | 44 | 53 |
| FrequencyUnknown_O-Delay_delaytest | 109,764 | 328,319 | 262,410 | 3 | 11 | 69 | 88 |
| MadBrain_Dynamite6_Demo | 59,235 | 144,350 | 16,713 | 23 | 25 | 93 | 205 |
| FSM_Infector_Demo | 378,333 | 436,984 | 15,194 | 29 | 21 | 51 | 124 |
| Dye Stress (big: 704 parameters, 704 MIDI bindings, 8.4 MB of machine state) | | 8.5 MB | 5.4 MB | 206 | 410 | 268 | 294 |
| Dye Stress as .bmxml | | 12.4 MB | | 239 | | 461 | |

Reading: files are smaller whenever there is data worth deflating (a third to a 30th of the .bmx); a tiny song costs about 5 KB more than its .bmx (the manifest, the index and the stores as JSON). Save is slower where there is much to hash and deflate (the big song: 410 ms against 206), open is within about 2x everywhere; both are far below a second on the biggest song. The demo songs' machines are placeholders on Linux; the Windows package run repeats the per-extension tests with the real machines (`ReBuzzTests`, `.nbl` added to the save/open tests).
