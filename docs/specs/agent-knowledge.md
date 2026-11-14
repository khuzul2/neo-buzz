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
- *Key controls* lists 5 to 8 controls (bullets or table rows), each with its real unit and a safe starting value. A machine with fewer than five controls (Pedal Gain, Pedal Peer Control) lists all of them.
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
| Settings | `settings_get` and `settings_set` for every preference: General, Audio, MIDI, Sync (Link and MIDI clock), Appearance, Agent, Safety, Advanced engine flags | Changes go to an audit log (`settings_history`). Restart-required settings say so. Engine flags and safety settings are annotated destructive, so clients can ask the person. `settings_reset` restores one key or the defaults. |
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

## 8. K3 as built (2026-11-13)

**Shape.** The tools are portable (`ReBuzz.Agent/App/`): they talk to `IAppServices` (settings entries, MIDI bindings, the audio device, the studio view, recent songs, facts) and run engine work through `Engine(what, changes, work)`. `ReBuzz/Agent/AppBackend.cs` is the engine's side; the studio adds its Appearance and Agent settings and its window (`ReBuzz.Studio/Shell/StudioViewControl.cs`) in `AgentServer`. `AgentHost` registers them with one line (`AppTools.Register`). Tests: `ReBuzz.AgentTests/AppControlTests.cs` over fakes (schemas validated against every result, behaviour per tool), `ReBuzz.Studio.Tests/AppControlStudioTests.cs` against the real headless studio.

**What was built, against §5.**

| Area | Tools | As built |
|---|---|---|
| Settings | `settings_get`, `settings_set`, `settings_reset`, `settings_history` | Every `[BuzzSetting]` of Global's General, Engine (section `advanced`, class engine) and MIDI settings, plus the studio's Appearance and the Agent switch; audio (`AudioThreads`, `WorkAlgorithm`; device, buffer, rate and input are listed but written with `audio_set`) and safety (limiter, ceiling, clip threshold, machine guard; class safety) from the preferences store. Keys are `section.Name`. Restart need is read from the description ("restart") or set. `settings_set` and `settings_reset` carry the destructive annotation (one tool covers engine and safety keys, so the annotation is per tool, not per key; each row says its `class`). The audit log is `settings-history.jsonl` beside the agent logs: time, key, from, to, and (UX P3f) `source`: Agent for a tool call, Manual for a change made in the studio's Preferences, which uses the same entries and `SettingsService`; an open window follows the agent's changes. A section or `all` reset leaves `agent.AllowAgents`, `agent.Port` and the audio-tool keys alone. |
| MIDI | `midi_bindings_list`, `midi_bind`, `midi_unbind`, `midi_learn` | Parameter bindings (the ones the song saves in MID2). Each change is a `DelegateAction`: one undo step, and a re-bind first removes the old controller (the engine's own bind adds without removing). `midi_learn` listens on `core.MIDIInput` for a control change, outside the engine queue, then binds in its own undo group; it times out with an error and changes nothing. |
| Audio | `audio_status`, `audio_devices`, `audio_set` | Status from the engine's counters (the same as studio_status). `audio_set` applies as Preferences does, then watches: no audio (the device clock moves by a quarter second) within 10 s, or more than 3 dropouts in the next 1.5 s, restores the previous settings and returns `applied: false, reverted: true, reason`. Buffer and rate are for PortAudio drivers; WASAPI and ASIO say to use their control panel. Tested with a fake device. |
| View and UI | `view_show`, `machine_open_editor`, `select`, `panel_set`, `window_layout` | Studio (Avalonia) only; elsewhere "Studio only" with the mode. Refused while recording: the pattern editor (it takes the keyboard) and machine windows. View switches, selection and panels still work. |
| Export | `export_audio` | Calls `render` through the registry (master plus a probe per stem machine), then resamples (`sample_rate`) and normalises (`normalize_db`, the master's gain on the stems too) if asked, and writes 16, 24 or 32-bit files itself, with dither. The studio's Export Audio sheet (⇧⌘R, ux-overhaul.md decisions 93-96) calls this same tool, so its files are byte for byte the tool's. It checks every target path first and writes nothing when one exists, unless `overwrite=true`. |
| App | `app_status`, `song_recent` | CPU is the process's, sampled over 0.3 s. |

**Deliberately out of scope.** `export_midi` (not cheap: it needs a pattern-to-MIDI writer), `plugin_scan` (no plugin host yet), the Session View's own MIDI note mapping (made in that view), and the input device, which stays in Preferences. The WPF host (ReBuzz.exe) gets the settings, MIDI, audio, export and app tools; its views have no control, so the view tools say so.

**Guide.** `app-control` (budget 4,000 tokens), in the workflow guide's table and the server instructions.

## 9. K4 as built (2026-11-14)

**New guides** (each under the 4,000-token budget, each ending in a checklist with a tool call in every item, all in the workflow guide's table and the `guide` tool's topic list):
- `live-performance`: the Session View played live. The grid (`session_get`, `session_scene_set`, `session_clip_set` with its launch modes and quantizations), launching and stopping, playing and recording takes, controllers (`midi_learn`, `midi_bind`) and Neo Macro knobs, a live `snapshot_restore`, watching levels with `meters_read`, what not to do while the person listens, and a rehearsal order. Studio and live mode; headless says so.
- `dye-sound-design`: one section per Dye Essence engine (Wavetable, Analog, Sample, Granular, Harmonic, Modal, Spectral): what it is for, the parameters to move first with starting values (checked by GuideTests like every parameter table), a factory preset to start from and what to measure; slot combinations. It points to the in-depth guides rather than repeating them.
- `migrating-songs`: `machine_replace` for a song of classic machines. The pairs table is the set of `*.successor.json` mappings (a test compares the two both ways, so a new mapping needs a row), dry run, the render comparison and its tolerance, whole-song order, and what to do for a machine with no pair.
- `teaching`: explaining sound design with the Explain text (`how`, `sound`, `unit`, range and curve from `machine_describe`), the Neo Learn machines as one-idea lessons (the table names each machine's first controls from its manifest), and a lesson loop of say, change in steps, ask, measure, put back.
- `performance`: dropouts and latency. `audio_status`, `app_status` and the status line's CPU figure; the Windows drivers in the order the studio offers them (ASIO, WASAPI shared with low latency, shared, exclusive behind `audio.WasapiExclusive`, WDM-KS behind `audio.ShowAdvancedDevices`; ux-overhaul.md decision 98 and P6a); which drivers take a buffer from `audio_set`; buffer against latency; `render(cpu=true)` for each machine's ms per second of audio and `patch_describe` for a patch's CPU; a ladder of fixes down to `audio.AudioThreads`, `audio.WorkAlgorithm` and `advanced.SubTickResolution`.
- `recipes`: see below.

**Refreshed.** `concepts` (control machines, Neo Patch, Neo Learn, Dye Essence's slots, `how` and `sound`, undo and snapshots, the live tools), `workflow` (where to build a sound, movement from the modulators, the guide table and the prompt list), `troubleshooting` (pointers to `performance`).

**Recipes.** `recipes` holds ten recipes as numbered lists in which every step is one backticked tool call with JSON values: hear-a-bass, movement-from-a-modulator, safe-experiment, arrange-a-section, two-scenes, controller-for-the-cutoff, explain-a-control, engine-swap, replace-a-classic, check-the-load. `ReBuzz.Studio.Tests/RecipeTests.cs` reads the guide itself, turns each step's `name=value` arguments into a tool call on the studio's agent server (the real engine and the real Neo machines, headless Avalonia, studio mode, so the live tools play), runs every step of a recipe (after the recipe a line "Needs: x." names) and checks each step succeeds and the recipe's result. `r1` in a recipe is the id its first render returned. The portable `GuideTests` check that every recipe has steps and a result line, and, like for every guide, that its tools, arguments, parameters, values and presets exist. To add a recipe: write it in the guide and add its test and its id to `EveryRecipeInTheGuideHasATestHere`. The harness is the Studio test rig and not `ReBuzz.AgentTests` because that project has no engine; ReBuzzTests' `Rig` is the same idea on Windows only.

**Prompts.** `arrange` (form, goals; the composition and genres guides and the recipes), `perform` (set, controller; live-performance and performance), `teach` (topic required, machine, level; the teaching guide and the machine's page) and `migrate` (machines, keep_old; migrating-songs), registered with the five that existed. Each is a step plan from real tools; `GuideTests.PromptsRenderWithTheirArguments` renders them with and without arguments, and the tool, argument, parameter and trademark checks cover their text like any guide.

**Found by running the recipes.** `machine_add(gear="Neo LFO", name=…)` without `connect_to="none"` fails ("can't connect to Master"): a control machine has no output. The guides said it without `connect_to`; they now say `connect_to="none"`. The tool is unchanged (a default that skips machines with no output would be better; left to the owner of `ReBuzz/Agent/MachineTools.cs`).

**Not done.** K5 (evaluation): see §10. The migrate recipe runs `machine_replace` on Pedal Plate, a managed machine; the native classics (Jeskola, FSM) are covered on Windows by `AgentReplaceTests`.

## 10. K5 as built (2026-11-14)

**How the evaluation drives the agent.** Two ways, one set of tasks. The scripted suite (`ReBuzz.AgentEval run`, in `fork-ci` against `rebuzz-headless` and in `fork-package` against `NeoBuzzLive.exe`) runs each task's setup and its reference solution through the MCP tools and scores the song it saves; no model, no cost. The real-agent run (`agent-eval.yml`, weekly or by hand) gives each task's brief to Claude Code (`claude -p`, only the app's MCP tools, no files, no shell) and scores the song the agent saves with the same scorers. That run calls a paid model and needs the repository secret `ANTHROPIC_API_KEY`; without it the workflow skips with a notice.

**New tasks (33, `ReBuzz.AgentEval/Tasks/KnowledgeTask.cs`, `KnowledgeTasks.cs`).** Named by prefix so the workflow can give them their own caps:
- `recipe_*` (10): one per recipe in the recipes guide. The reference solution replays the recipe's steps from the guide text (the same parser as `RecipeTests`), so a recipe changed in the guide changes its task.
- `guide_*` (15): one per guide that no older task exercises: app-control, build-a-synth, concepts, dye-essence-analysis, dye-essence-effects, dye-essence-in-depth, dye-sound-design, fm-recipes, live-performance, migrating-songs, mixing-dynamics, neo-eq-in-depth, performance, synth-recipes, teaching. The other guides are exercised by the older tasks, named in `KnowledgeTasks.GuideCoverage` (workflow and composition, genres, sound-design, mixing, troubleshooting, mastering, mastering-chain, recipes).
- `ask_*` (8): choose a machine for a job (reverb, compressor, limiter, bass synth: the answer set is `machine_find(tag=…)`, so the catalog is the truth, not a list in the task) and set values in real units (a synth's cutoff, release and volume; a compressor's threshold, ratio, attack and release; the song's tempo, resolution and loop; a Dye Essence effect slot).

Every brief says what the person wants, never which guide to read: finding the guide (`guide`, `rebuzz://catalog`, `machine_find`) is part of the task. Every check reads the saved song through the tools: machines and their wiring, parameters read back in real units (`param_get`), placed notes, arrangement markers and automation, the session grid, MIDI bindings, the song's notes, an exported file's header, a render that must not be silent. Where an answer is words (explain a control, the heaviest machine), the song's notes hold it and the check is a word-overlap with the machine's own `how`/`sound` text or a named machine within half of the top CPU cost. No model grades anything. Tasks have three to seven criteria; the starting song fails every one of them (the pass rule needs every criterion at 0.5 or more and a mean of 0.85).

**Self-test (Linux, `fork-ci`).** `selftest` fails when a guide or a recipe has no task, when a brief names its guide or recipe slug, `guide(` or lacks the save path, or when a recipe no longer parses; it also covers the gate rules.

**Scripted baseline.** `eval/baseline.json` has the 33 new tasks, each at 1.0, measured on `rebuzz-headless` (all 41 tasks, 7 checks: about 2.5 minutes). The tasks avoid what a headless host cannot do (no device, no `session_launch`); two tasks need classic machines the quick package leaves out (`recipe_replace_a_classic`, `guide_migrating_songs`: Pedal Plate, Pedal Comp): their setup throws `TaskSkipped` when `gear_list` lacks them and the run leaves them out (not a failure; `prepare` exits 3), so they are scored on `rebuzz-headless` and in a full package build. A recipe step that needs a device or studio mode (the launch in `two-scenes`, the dropout reads in `check-the-load`) is not in the reference solution or the check.

**Real-agent gate (`ReBuzz.AgentEval gate`, `AgentGate.cs`).** The last step of `agent-eval.yml` compares `agent-results.json` with `eval/agent-baseline.json` and fails the run when a task's total fell by more than the task margin (0.34: on a three-criterion task two criteria lost; one lost criterion is run-to-run noise) or when the mean over the tasks that ran fell by more than the mean margin (0.05: about two criteria across forty tasks, which a flaky run rarely does and a worse guide, tool description or error text does). A partial run (`tasks` input) is gated on what it ran; a task without a baseline entry is information. Without the baseline file the step prints the baseline the run would write as a notice and passes: commit it as `eval/agent-baseline.json` to accept a run (`gate --update-baseline` writes it from a local `agent-results.json`). The margins are the file's `task_margin` and `mean_margin`. The knowledge tasks have their own caps in the workflow (inputs `knowledge_max_turns` 40, `knowledge_max_budget_usd` 1); the model, the key and the song tasks' caps are unchanged.

**Cost.** One full real-agent run is 41 `claude -p` sessions: 8 song tasks (cap USD 5 each, as before) and 33 knowledge tasks (cap USD 1 each), so at most USD 73 and in practice far less (the knowledge tasks are 5 to 25 tool calls). The workflow prints each task's turns and cost in its report table. Run it for a baseline, after K1/K2 batches and after changes to the guides or tool descriptions, not on every push.

**Not done.** Baselines "before K1 and after K2" (spec §3) need the real-agent run on those heads; this one is for K4's head. Per-machine-page tasks (K2's ~100 pages) are not in; the `ask_*` tasks use the catalog's own answers.
