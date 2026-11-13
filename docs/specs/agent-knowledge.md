# Agent knowledge — Plan

Status: approved by the user 2026-11-12: the whole plan (K0–K5), and **full control** for agents in K3.

Goal: an agent driving NeoBuzz Live over MCP has everything it needs. That means what the app can do, how each machine sounds and behaves, how to get from intent to result, and how to check what it hears. It also means facts that are current and verified, not copied by hand.

## 1. Where we stand (inventory, 2026-11-12)

A snapshot, as the plan was approved. The current figures are generated: `docs/agent-coverage.md` (machines, manifests, pages, presets, tools) and `docs/machine-health.md` (the profile).

| Layer | Today |
|---|---|
| Server | Our own MCP server (`ReBuzz.Agent/Mcp`). It runs over stdio (`rebuzz-headless`) or over Streamable HTTP from the studio (127.0.0.1, bearer token, port 7481). Modes: studio, live, headless. |
| Tools | About 63. All have schemas and annotations. Coverage: song, patterns, notes, arrangement, automation, Session View, machines, wires, parameters, presets, snapshots, render, analysis, mastering, transport, MIDI play, meters, undo. |
| Instructions | Short server instructions, 7 topic guides (`guide`, or `rebuzz://guides/*`) and 5 prompts. |
| Machine knowledge | 126 machines profiled, each with a manifest that gives units, ranges, safe ranges, roles and plain names. A manual is generated for each one (`rebuzz://machines/<slug>/manual`). Only **28** have a hand-written page (the Neo family and Dye Essence). Of the other ~98 manifests, 25 are curated, ~27 generated from source and ~46 are drafts with approximate mappings. |
| Guards | GuideTests: tools, parameters and presets named in the guides exist; every Neo machine has a page. CuratedManifestTests. An eval harness (`ReBuzz.AgentEval`, 5 tasks). |
| Gaps | No hand-written page for Pedal, FSM, Oomek, Jeskola, Rout or the built-ins. ~46 draft mappings. No manifest categories or tags. No tools for MIDI bindings, settings, view/UI or the audio device. Stale facts: `notes_heard` in the spec but not in code; "101 machines" against 126. Counts are hand-written, so they drift (all three fixed in K0, §7). |

## 2. Principles

1. **One source of truth, generated where possible.** The tool reference comes from the `ToolDef`s, catalogue figures from the profile, and parameter facts from the manifests. Hand-written text covers only what code can't know: how a machine sounds, when to use it, and pitfalls.
2. **Progressive disclosure** within an agent's context budget, as a ladder:
   1. server instructions (≤ 1,000 tokens);
   2. the guide index;
   3. topic guides (≤ 4,000 each);
   4. a machine page (≤ 2,500);
   5. full parameter detail on request.
   
   Every resource has a measured size, and tests enforce the budgets.
3. **Verified claims.** Every tool, parameter, preset, unit and range a page names is checked by tests. Sound claims ("bright above 3 kHz") are backed by profile evidence where possible.
4. **Clean room.** We write our own words. Original machine help files are not copied unless their licence allows it. No trademarked product names appear in our text.
5. **Shared with the UX.** Manifest categories and tags (§3, K0) are the same vocabulary as the studio's machine browser (UX P3d), so there is one list for people and agents.

## 3. Phases

| Phase | Contents | Exit |
|---|---|---|
| **K0 Foundations** | `category`, `tags` and a one-line `use_for` in the manifest format. They go on every machine and use the P3d categories: Synths, Drums, Samplers, Dynamics, EQ & Filter, Delay & Reverb, Modulation, Distortion, Utility, Control. A `rebuzz://catalog` resource (all machines by category, with use_for and status) and a `machine_find` tool (by intent, category or role). A generated tool reference, `rebuzz://reference/tools`. A machine page template (§4). A generated coverage report, `docs/agent-coverage.md`, per machine and per tool. Resource size budgets. Stale facts fixed, and counts generated. | Coverage report and budgets enforced by tests |
| **K1 Neo + Dye refresh** | Update the 28 pages for everything since they were written: Dye engines, arpeggiator and sequencer, MPE, the effects rack; mastering M5/M6 machines; Neo Learn. Add Explain How and Sound text to every Neo and Dye parameter. This is the follow-up Neo Learn deferred, so the agent and the studio inspector can explain any Neo control. | Every Neo/Dye parameter has How and Sound; pages pass the template test |
| **K2 Every shipped machine** | Promote the ~46 drafts to verified mappings, using profiler evidence on the Windows runners (natives only run there). Write a page for each of the ~98 non-Neo machines, in family batches: Pedal set, FSM, Oomek, Jeskola rebuilds, Rout, ReBuzz built-ins, others. Each page includes the Neo successor where one exists, and when `machine_replace` applies. | Every packaged machine has a curated manifest and a page (test: all, not only Neo) |
| **K3 App coverage** | Fill the tool gaps (§5): MIDI bindings, settings, view and UI control in studio mode, audio device and status, and export to file if `render` can't already write one. Each needs safety annotations, guide coverage and tests. | Every user-facing app area is reachable by a tool, or is listed as deliberately out of scope |
| **K4 Guides, recipes, prompts** | Refresh concepts and workflow. New guides: live performance (Session View), sound design per Dye engine, migrating old songs to Neo successors, teaching (an agent explaining sound design with the Explain text), performance and latency troubleshooting. Recipes as short step lists, checked by tests. New prompts: arrange, perform, teach, migrate. | Guides pass GuideTests; recipes run headless in tests |
| **K5 Evaluation** | Extend `ReBuzz.AgentEval`: one task per guide and recipe, plus knowledge questions (choose a machine for X; set Y to Z in real units). A baseline before K1, then figures after K2 and after K4. A regression gate in the existing `agent-eval` workflow. | Scores reported. No regression past the gate. |

Order: K0 first, then K1 and K3 in parallel. K2 runs after K0 in family batches, alongside the other threads. K4 comes after K1 and K3. K5's baseline runs early (after K0); its gate is added last.

## 4. Machine page template

Sections, each short:
1. **How it sounds**
2. **Use it for**
3. **Key controls**: the 5 to 8 that matter, in real units, with safe starting values.
4. **Starting points**: presets, or 3 to 5 parameter sets.
5. **Pitfalls**: level, CPU, latency, denormals, odd mappings.
6. **Pairs well with**
7. **Successor**: Neo successor and `machine_replace` notes, if any.
8. **Checklist**

The full parameter table stays generated beneath the page.

**Rules the template test checks** (`KnowledgeBudgetTests`):
- The headings are exactly these level-2 titles, in this order. *Successor* is optional (a machine with no Neo successor leaves it out); the others are required.
- *Key controls* lists 5 to 8 controls (bullets or table rows), each with its real unit and a safe starting value.
- *Checklist* has `- [ ]` items, each naming the tool that checks it.
- A page is at most 2,500 tokens (§7). Detail that doesn't fit moves to a deep-dive guide (`guide(topic="<machine>-in-depth")`, as Dye Essence and Neo EQ have) or to the generated manual.
- Every tool, parameter, preset, unit and range the page names is already checked by `GuideTests`.
- Own words only (§2.4); no other company's product names.

Until K1 the template check is a warning list (the test passes and prints which of the 28 pages differ); K1 flips `EnforceTemplate` and it becomes a failure.

## 5. App coverage (K3): full control (the user's decision)

Agents may do everything a person can do in the app. Guardrails make mistakes recoverable; they don't forbid anything.

| Area | Tools | Guardrails |
|---|---|---|
| MIDI bindings | `midi_bindings_list`, `midi_bind`, `midi_unbind`, `midi_learn` | One undo step each |
| Settings | `settings_get` and `settings_set` for every preference: General, Audio, MIDI, Appearance, Agent, Safety, Advanced engine flags | Changes go to an audit log (`settings_history`). Restart-required settings say so. Engine flags and safety settings are annotated destructive, so clients can ask the person. `settings_reset` restores one key or the defaults. |
| Audio device | `audio_status` (device, buffer, rate, latency, xruns); `audio_devices` lists them; `audio_set` changes device, buffer and rate | Auto-revert: if no audio flows within 10 s, or xruns spike, the previous device comes back and the agent is told why |
| View and UI (studio) | `view_show` (Arrangement, Session, Machines; Tracker or piano roll on a pattern), `machine_open_editor`, `select`, `panel_set` (browser, inspector, detail), `window_layout` | Never steals focus during recording. Every call is visible to the person watching. |
| Export | `export_audio` (song, loop, stems or per-track) to a WAV path at a chosen bit depth and dither; `export_midi` if cheap | Writes only to a path the agent names. It never overwrites unless asked. |
| App lifecycle | `app_status` (version, mode, CPU, memory), `song_recent`, `plugin_scan` once the plugin host exists | Read-only except the scan |

## 6. Effort and risks

- **Effort.** K0 and K1 are about one round each. K2 is the bulk, about 6 to 8 batches across ~98 machines. K3 and K4 are one round each. K5 is one round, then upkeep.
- **Native machines** run only on Windows. Mapping verification for drafts depends on the Windows runners and adds CI time.
- **Clean-room limits** for third-party machines: some behaviour can only be described from measurements.
- **Eval cost.** Agent evals call a model. The existing workflow's key and budget decide how often they run.

## 7. K0 as built (2026-11-12)

**Manifest fields.** `category`, `tags` and `use_for` are in every one of the 126 manifests, the Neo family's generated beside their DLLs included. `Machines/Manifests/machine-catalog.json` is the one source: the ten categories (the studio browser's list, UX P3d), a vocabulary of 75 tags each with a one-line meaning, and each machine's category, tags and `use_for`. `tools/machine_catalog.py` is the shared helper; `gen-machine-params.py`, `source_manifests.py`, `native_manifests.py` and `machine-profiler.py` all write the three fields, so they survive regeneration, and `python3 tools/machine_catalog.py` checks the catalogue against the profile and the manifests. A machine added later needs its entry in the catalogue file (the generators stop and say so).

Tag vocabulary, by what they say: sound character (warm, bright, clean, gritty, lush, punchy, vintage, rhythmic); sources (bass, lead, pad, pluck, keys, organ, drums, kick, percussion, noise, chiptune, acid, fm, wavetable, subtractive, sampler, granular, physical-model, polyphonic, monophonic); processing (eq, filter, compressor, limiter, gate, de-esser, transient, glue, sidechain, multiband, saturation, distortion, bitcrush, exciter, wah, chorus, flanger, phaser, reverb, delay, echo, tempo-sync, stereo-width, pitch-shift, spectral, tape, dither, lfo); where it is used (vocal, mix-bus, master, drum-bus); utility and control (gain, mixing, metering, routing, audio-io, recording, looper, plugin-host, midi, sequencer, arpeggio, chords, automation, groove, notes). No tag or `use_for` names another company's product (`TrademarkTests` reads the manifests).

**`rebuzz://catalog`** lists every installed machine by category (the browser's order) with its manifest status (curated, generated or draft), `page` where it has a hand-written page, its `use_for`, a health note where it has one, and the tag list. Its counts come from the manifests it reads. **`machine_find`** (read-only, full input and output schemas) takes an intent, a category, a tag, a role (a curated or suggested parameter role, or a prefix such as `reverb`) and a kind, and returns ranked machines, each with the reason. Ranking: a tag the intent asks for counts 3 (times how many of its words ask for it; a word's main meaning counts fully, its weaker guesses by half); a category 2; a word of the machine's name 1.5; a word of its `use_for` 0.5; a role 3. Curated manifests (+0.6), a page (+0.4) and generated manifests (+0.3) break ties; a machine that diverges or needs a window loses 2. `gear_list` rows carry `category` and `use_for` too.

**`rebuzz://reference/tools`** is built each time it is read from the registered `ToolDef`s: name, first sentence, annotations (R read-only, W changes the song, D destructive, I idempotent) and each input with its type and the start of its description, which carries the units. The areas are named in `ToolReference.AreaOf`; a tool no area names appears under *Other*, and `CatalogTests` fails for any tool the guide checks know that is in *Other*. The server instructions and the workflow guide point at it and at the catalog.

**Budgets** (tokens estimated as characters / 4; `KnowledgeBudgetTests`): server instructions 1,000, each guide 4,000, each machine page 2,500. The generated catalog and tool reference are measured, not capped. Where a guide or page was over, it was split by topic, so no fact was dropped:
- composition → composition + `genres` (forms, moods, templates);
- sound-design → sound-design + `synth-recipes` (Neo Poly, Neo Wave) + `fm-recipes` (Neo FM6);
- mixing → mixing + `mixing-dynamics` (compression, sidechain);
- mastering → mastering + `mastering-chain`;
- the Dye Essence page → a one-paragraph page with its gotchas and preset map, plus `dye-essence-in-depth` (signal flow, how to use it);
- the Neo EQ page → the page plus `neo-eq-in-depth` (graphic mode, linear phase and the curve layer, the preset map).
The prompts include the parts that belong to them (compose adds `genres`, design-sound the recipes, mix and master their second guide).

**Coverage.** `tools/agent-coverage.py` writes `docs/agent-coverage.md`: per machine the category, manifest status, whether it has a page, the share of its parameters with a role (curated, and with a suggested one for drafts), its factory presets and its safe ranges; per tool how many guides and pages name it, how many calls with arguments they show, and whether its own description has an example. `KnowledgeBudgetTests` runs `--check` and fails when the committed file is stale (like `docs/machine-health.md`, regenerate it and commit).

**Stale facts.** `notes_heard` was never built: its job (is what plays what was written?) is done with `analyze(metrics=["pitch", "tempo"])` (fundamental over time, onsets) against `notes_read`; the spec no longer lists it. The "101 machines" figure in the specs is marked as the count at the time, with the generated figures named. Agent-facing texts (the server instructions, guides, prompts, tool descriptions) hold no hand-written machine, tool or guide counts; the catalog and the tool reference state theirs from the data.
