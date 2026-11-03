# Machine safety: unhealthy machines, a guard for every machine, and a safe master

| | |
|---|---|
| Status | Decided 2026-11-03: H1 a global safeguard for every machine, plus a safety limiter on the master output with a clip indicator; H2 A; H3 A; H4 A |
| Scope | What the profiler's "unhealthy" means; how ReBuzz keeps any machine from blowing up the song or the speakers; how agents stay in safe ranges; which machines get Neo successors |
| Related | mcp-server.md §12 (machine upgrades U1–U7, `docs/machine-health.md`), machines.md (Neo machines), studio-mvp.md (the studio), daw-modes.md (AR0 runs beside this), plugin-host.md (plugins get the same guard) |

---

## 1. What "unhealthy" means today

The profiler (U1, `tools/machine-profiler.py`) calls a machine unhealthy when it doesn't load, crashes or hangs the server, or puts out NaN, infinity or a peak above +24 dBFS. It tests at the defaults, with test signals, and with **every parameter at its minimum, then at its maximum, all at once**. That last test is blunt. Of the four machines it flagged, read from their parameter ranges (to be confirmed per parameter, §3.1):

| Machine | Source | Found | Likely cause | Verdict |
|---|---|---|---|---|
| Jeskola Flanger | closed | +157 dBFS at all-min, +149 at all-max | Feedback runs −100 %…+100 %: at either end the loop never decays | **A real fault**, at the ends of one parameter |
| Joachims Hyperion | closed | +221 dBFS at all-max | A 30-band graphic EQ: every band at full boost, at the widest bandwidth, adds up | **Mostly the test.** A band near 20 kHz may also be unstable |
| Jeskola Bass | closed | +25 dBFS at all-max (Volume ≈198 %, resonance max); +2 dBTP at the defaults | Loud, bounded, 1 dB over the line | **Hot, not diverging** |
| Info (WDE, `Info.NET.dll`) | binary only | doesn't load headless | A WPF rich-text song-notes machine: it needs a window | **Not an audio fault** |

The 14 warnings are mostly the profiler's own limits: drum machines played by triggers, audio inputs, a VST host with no plugin, a tracker without waves.

## 2. Decisions

| # | Decision | Decided |
|---|---|---|
| H1 | Protect against a machine blowing up | **A global safeguard for every machine** (§3.2), **plus a safety limiter on the master output** with a clip indicator that shows overs above +X dB, as Ableton's master meter does (§3.3) |
| H2 | Joachims Hyperion | **A:** a graphic mode for Neo EQ (31 bands at the ISO centres), with a mapping for `machine_replace` |
| H3 | Info | **A:** a Song Notes panel in the studio, saved with the song, importing Info machines' text |
| H4 | Timing | **A:** layers 1–3 (§3.1–§3.4) now, beside AR0 (≈2 weeks). Successors (§4) with the next machine batch, Neo Ensemble first |

## 3. Now

### 3.1 Layer 1: measure precisely (profiler, part of U2)

- **Each parameter alone** at its minimum and maximum, the others at their defaults, for every machine that misbehaved all at once (and every effect with a feedback, resonance, gain or Q parameter by name). The draft records which parameter and which end.
- **Diverging, hot or needing a window**, instead of one "unhealthy" label:
  - *diverging:* NaN or infinity, or a level that keeps growing over a 4 s render (the last second louder than the second by more than 6 dB, or above +60 dBFS);
  - *hot:* loud but steady (above 0 dBFS, under +60 dBFS, not growing): a warning with the setting, not unhealthy;
  - *needs a window:* a managed machine whose construction fails without a UI (WPF types in the exception, or a known GUI-only machine): its own status, not unhealthy, and `gear_list` says "works in ReBuzz on Windows with its window".
- **`safe_range` per parameter** in the draft (min and max raw values) where a sweep finds the edge, with the reason ("feedback at ±100 % diverges").
- Re-profile the natives in the Windows package run and the Pedal machines on Linux; `docs/machine-health.md` shows the three categories.

### 3.2 Layer 2: a guard on every machine (engine, Windows and macOS)

After each machine's `Work`, before its output reaches any input, the engine checks the block it produced:

- **Trips on:** any NaN or infinity, or any sample above **+60 dBFS** (1000 × full scale; nothing musical gets there).
- **Then:** that block of the machine's output becomes silence, the machine is stopped (Buzz's `Stop`, which clears most machines' buffers) and **muted until cleared**. The machine is marked *diverged*:
  - the studio draws it red in the machine view, with "Diverged: muted" in its tooltip and the status line; *Reset and unmute* is on its menu;
  - WPF shows it muted, with a status-bar message;
  - agents see `diverged: true` with the time and the reason in `machine_describe` and `studio_status`, and `machine_set_state` clears it.
- **Never touches normal audio.** A block without a trip passes bit for bit, so every song renders exactly as before (the null and bit-identical tests prove it). No allocation on the audio thread; one pass over each block (max-abs and NaN in one loop, vectorised).
- Covers every machine: native, managed, Neo, placeholders, and the plugin host's machines later (plugin-host.md H1).
- A setting (on by default) can turn it off for a song's session, with a confirmation; it's never saved off.

### 3.3 Layer 2b: a safe master, and a clip indicator (H1)

The final output to the audio device gets a **safety limiter**:

- **Scrub:** NaN and infinity become 0 before anything else, so the device never sees them.
- **Limit:** a zero-latency peak limiter (instant attack, about 50 ms release, then a hard ceiling) at the **ceiling, 0 dBFS by default**. It does nothing below the ceiling, so normal playback is untouched.
- **Where:** on the device path only. Renders and exports (`render`, offline export, the null test) get the true signal, so they stay bit-identical and can be measured; they report their overs instead.
- **Clip indicator:** the master meter (the studio's transport area and the Master machine) turns red when the signal **before** the limiter goes above the **clip threshold, 0 dBFS by default**. It latches the highest over ("+3.2 dB") until clicked, as Ableton's does. A "limiting" lamp shows when the limiter reduces gain.
- **Preferences** (Audio): the clip threshold (+X dB, default 0 dBFS), the limiter's ceiling (default 0 dBFS) and the limiter on or off (on by default; turning it off asks for confirmation).
- **Agents:** `meters_read` and `studio_status` report the latched over and whether the limiter acted; `render` reports overs as now.
- WPF on Windows gets the limiter (it's in the engine) and a minimal clip lamp next to its master volume; the full meter is the studio's.

### 3.4 Layer 3: safe ranges for agents

- `param_set` keeps an agent inside a parameter's `safe_range` (from the draft or a curated manifest). Outside it the call is refused with the reason, unless the agent passes `allow_unsafe: true`. People keep the full range in every UI.
- `gear_list` and `machine_describe` show the safe ranges; `machine_add` warns only for diverging machines.

### 3.5 Song Notes (H3)

- A **Song Notes** panel in the studio (a tab or a side panel): rich text (bold, italic, lists, links), saved in the song as a "SongNotes" subsection, in .bmx and .bmxml.
- **Import:** opening a song with Info machines shows their text in Song Notes (read from the placeholder's kept data), marked as imported, with the machine left as it is so the song saves back unchanged.
- MCP: `song_notes_read` and `song_notes_write` (or fields on `studio_status` and `song_set`), so an agent can leave notes.
- Windows' WPF UI keeps the Info machine; the panel comes with P4.

## 4. Next machine batch: successors (H2, H4)

| Closed machine | Successor | What |
|---|---|---|
| Jeskola Flanger (and Chorus, FSM Chorus2, Pedal Chorus) | **Neo Ensemble** (chorus, flanger, phaser) | A new Neo machine, first in the batch. Feedback bounded (±99 %) with a saturating loop, so it can't run away |
| Joachims Hyperion | **Neo EQ, graphic mode** | 31 bands at the ISO centres, constant-Q, in Neo EQ (H2 A), and a Hyperion mapping for `machine_replace` |
| Jeskola Bass (1) | **Neo Poly**, mono/slide preset | No new machine; a mapping (cutoff, resonance, waveform, volume, slide), as for Bass 2/3 |

`machine_replace` (U5) moves a song over: parameters by role and unit, patterns' columns mapped, a render before and after compared. The original stays installed so old songs play. On the Mac every closed native is a silent placeholder, so a successor and `machine_replace` are how those songs play there.

## 5. Tests

- A test machine that diverges on purpose (a feedback loop at gain 1.01, and one that writes NaN after N blocks): the guard trips, mutes and flags it; the rest of the song keeps playing; *Reset and unmute* brings it back.
- The null test and the bit-identical render tests stay byte-equal with the guard and limiter on.
- 0 bytes allocated per buffer with the guard and limiter on (the 0.4.1 allocation test).
- The limiter: a +12 dBFS sine reaches the device at no more than the ceiling; the clip indicator latches +12 dB; a render of the same song still measures +12 dBFS.
- The profiler: Jeskola Flanger diverging at feedback ±100 % (its `safe_range` excludes both ends); Hyperion hot or diverging per band; Jeskola Bass hot; Info "needs a window".
- Song Notes round-trips in both formats; an Info machine's text is imported and the song saves back byte-equal in its core sections.
