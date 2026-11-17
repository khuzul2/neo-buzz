# The browser (BR1)

The left bar's Browser panel finds anything to put in a song: machines, plug-ins, presets, clips, samples. One model (`MachineBrowserModel`) feeds the panel and the canvas popup (Shift+Cmd+N).

## Layout
- Two columns: sources on the left, results on the right, a preview strip below, one search box over everything. Under 380 pt it is one column and the sources become a dropdown.
- Sources: **Collections** (Favourites and 7 colour tags, saved in settings key `BrowserCollections`), **Categories** (Instruments, Drums, Audio Effects, MIDI and Control, Plug-ins by vendor from the Neo Plug scan, Presets, Clips, Samples, Neo Audio), **Places** (added folders, Gear samples, this song's audio, Recent). The + button adds a folder.
- Results are a list or a tree (toggle); a search lists every kind in sections.

## Index
- A background indexer reads each place: name, path, length, rate, channels, tempo and key (WAV incl. acid tempo, AIFF, FLAC, Ogg, MP3 headers; file-name hints for tempo and key).
- Store: one binary file, `browser-index.bin`, in the settings folder, searched in memory from an immutable snapshot (lock-free reads). Chosen over SQLite because it needs no native library on any platform, loads fast, and a damaged file just triggers a rescan. 100k entries search in under 50 ms (benchmark test `BrowserTests`).
- Rescans are incremental (path, size, modified time). A `FileSystemWatcher` per place, debounced 400 ms, keeps it current. Progress shows in the panel.

## Preview
- Speaker toggle ("Listen"): plays on the cue output, else Master, mixed in `WorkManager` before the master tap (`BrowserPreview.MixInto`, no allocation). "Sync" warps to the song tempo with the warp engine and the song position while the transport runs. The strip shows the waveform and length.
- Not done: previewing a machine or preset by playing a phrase (needs a scratch machine on the audio thread; left out).

## Drag data (shared with DD1)
Format `neobuzz-browser-item` (version 1), JSON: `kind` (machine, plugin, preset, audio, midi, pattern), `id` (gear name; file path; `Machine/Pattern`; a song wave is `wave:N`), optional `preset`, `label`. `BrowserDragData.Format()/Parse()`; Studio reads and writes it with `BrowserDragSource`. Every result row is draggable. Arrangement, Session and Machine View drops are DD1's: [drag-drop.md](drag-drop.md).

## Hot-swap (Machine view)
- Drop on a machine box replaces it (`MachineEditing.HotSwap`: replace, restore the name, recreate pattern names and lengths, one undo step). A preset on a machine of the same gear loads the preset. A drop on empty canvas adds the machine.
- Context menu "Hot-swap" puts the browser in swap mode (banner, Esc cancels); the next double-click on a result replaces the machine.
- Refusals are said in the status line (Master, incompatible kinds).

## Agent tools
`browser_search {query, kind?}`, `browser_places {op: list|add|remove, path?}` (studio only, via `IBrowserControl`), `machine_swap {machine, gear|plugin}` (same path as hot-swap), plus the existing `machine_replace`.
