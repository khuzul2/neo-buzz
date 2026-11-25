#!/usr/bin/env python3
# Fork (docs/specs/agent-knowledge.md, K0): docs/agent-coverage.md, what an agent can find out about every machine and every tool.
# Generated, like docs/machine-health.md, so the figures can't drift from the repository:
#   per machine: category, how far its manifest is trusted (curated, generated, draft), whether it has a hand-written page,
#   the share of its parameters that carry a role (and, for drafts, a suggested one), its factory presets, and its safe ranges;
#   per tool: how many guides and pages name it, how many calls with arguments they show, and whether its own description has an example;
#   per feature built since K3: the tool that covers it (K3b).
# The CatalogTests staleness test runs `--check`.
#
#   python3 tools/agent-coverage.py            rewrite docs/agent-coverage.md
#   python3 tools/agent-coverage.py --check    exit 1 when the committed file differs from what the repository generates

import glob
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "docs", "agent-coverage.md")
GUIDES = os.path.join(ROOT, "ReBuzz.Agent", "Guides")


# The features built since K3 (the studio's DAW modes, the Windows parity work) and the tool that covers each (docs/specs/agent-knowledge.md §11,
# K3b). Every tool named here must be registered: the script stops when one is not. An empty tool list is an honest gap, with the reason.
# (area, feature, where it is specified, tools, note)
FEATURES = [
    ("Mixer (MX1)", "One mixer everywhere (Full, Compact, Header densities): tracks, groups, returns and the Master chain, the Master volume in dB, and a colour per track", "ux-overhaul.md §11", ["machine_set", "machine_list", "connection_set", "machine_tracks"], "faders and sends are the wires' gains (connection_set), On and Solo are machine_set, a track's colour is machine_set(colour=...)"),
    ("Device panel (PP1)", "Pinned parameters in the Inspector (up to 8 per machine, saved in the song)", "ux-overhaul.md", ["machine_pin"], "the macros come from the manifest roles; machine_open_editor(window=\"parameters\") opens the panel"),
    ("Device panel (PP2)", "Locks (left out of Randomize and Morph, saved in the song), Randomize, Nudge and Morph between two presets", "ux-overhaul.md", ["param_lock", "param_randomize"], "hiding a parameter is a view setting of the Device panel (per machine type); the agent sees every parameter"),
    ("Arrangement (AR1)", "Clips: place, draw, move, trim, loop, split, join, copy, make unique", "daw-modes.md §8.2", ["arrangement_clip", "arrangement_place", "arrangement_clear", "arrangement_get"], "arrangement_clip is the Arrange view's own ClipEditor"),
    ("Arrangement (AR1)", "Lanes and their roles (main, take, automation)", "daw-modes.md §8.2", ["arrangement_lane", "arrangement_get"], "arrangement_get numbers each machine's lanes and gives the role"),
    ("Arrangement (AR1)", "Markers, loop, song end", "daw-modes.md §8.1", ["arrangement_markers", "song_set"], ""),
    ("Arrangement (AR1)", "Song meter (the bar list)", "daw-modes.md §8.1", ["song_meter", "arrangement_get"], ""),
    ("Piano roll (AR2)", "Notes in a clip or pattern", "daw-modes.md §8.3", ["notes_write", "notes_read", "notes_transform", "session_clip_set"], "the roll is a view of the same notes; view_show(editor=\"piano_roll\") opens it"),
    ("Piano roll (AR2)", "Showing the roll or the tracker", "daw-modes.md §8.3", ["view_show"], ""),
    ("Tracker lanes (AR3)", "Notes in the tracker's Note columns", "daw-modes.md §8.4", ["notes_write", "notes_read"], "input=\"tracks\""),
    ("Automation (AR4)", "Automation lanes and clips over the song", "daw-modes.md §8.5", ["arrangement_automation"], "list, add_lane, write, clear"),
    ("Automation (AR4)", "Automation inside a pattern", "daw-modes.md §8.5", ["automation_write", "automation_read"], ""),
    ("Envelopes (EV1)", "A clip's envelopes: points with ramps or steps in a Session or Arrangement clip, as the Detail panel's lane draws them", "daw-modes.md §8.8", ["clip_envelope"], "read, write, clear; the clip is a pattern, a scene's slot or an arrangement clip; the curves drawn on clips and the choice of the parameter a track shows are the studio's only"),
    ("Recording (AR5)", "Arming, recording takes, loop takes", "daw-modes.md §8.6", ["arrangement_takes", "session_record", "midi_play"], "recording needs studio mode"),
    ("Recording (NA4a)", "Track inputs and arm: Audio From (None, In N, In N/N+1, another track, Wired inputs), Monitor, MIDI From (all, a device, the keyboard) and channel; arm for audio tracks too", "neo-audio.md \"Inputs and arm\"", ["track_input", "session_record"], "track_input lists the audio input device and the MIDI devices; the Machine View's dot, the Session I/O row, the Arrangement header and the Inspector show the same state"),
    ("Recording (NA4b)", "Audio takes: record armed audio tracks into the Arrangement (loop passes are takes on take lanes, the newest plays), list the takes, comp ranges of them with a crossfade, choose one, flatten the comp, delete the unused takes", "neo-audio.md \"Takes and comping\"", ["takes", "arrangement_takes", "session_record"], "takes: op list, choose, comp, flatten, delete_unused; recording needs studio mode"),
    ("Recording (AR5)", "Comping a take onto the main lane", "daw-modes.md §8.6", ["arrangement_takes", "arrangement_lane"], "op=comp"),
    ("Vertical tracker (AR6)", "The tracker's orientation", "daw-modes.md §8.7", ["settings_set", "settings_get"], "appearance.VerticalTracker"),
    ("Modulation (P5b)", "The modulation matrix", "ux-overhaul.md §11 P5b", ["modulate", "modulation_list", "modulation_remove"], ""),
    ("Samples (P5b)", "The Samples view: waves, import, from a render", "ux-overhaul.md §11 P5b", ["wave_list", "wave_import", "wave_from_render"], ""),
    ("Machine view (P6b)", "Clone with patterns, and Copy / Paste of machines (P6c)", "ux-overhaul.md §9 rows 4 and 14", ["machine_clone", "machine_remove"], "a cut is a clone, then machine_remove"),
    ("Machine view (P6b)", "Machine groups: make, fill, empty, fold, rename, delete", "ux-overhaul.md §9 row 12", ["machine_group"], "moving a group is the view's drag only"),
    ("Machine view (MT1)", "Tracks: the song's tracks, groups and returns with their chains, and what is unrouted (the Machines view's Tracks mode)", "ux-overhaul.md §11 MT1", ["machine_tracks"], "a read; the model is worked out from the wires and never saved"),
    ("Note wires (NW1)", "Notes from one machine to another in the same block: free wires, a track's insert chain, with the loop check", "note-wires.md", ["note_wires", "note_wire_connect", "note_wire_disconnect", "note_chain"], "the Machines view's ports, drags and menus are the studio's; the tools make the same wires directly"),
    ("MIDI mapping (MM1)", "Controllers, mappings of both scopes (parameters and commands), knob groups, and a track's control pages", "midi-mapping.md", ["controller_list", "midi_map_list", "midi_map_set", "midi_map_clear", "track_page_get", "track_page_set"], "map mode, the per-control MIDI button and listening are the studio's; the tools set the same maps directly"),
    ("Machine view (P6b)", "Latency window: override and clear", "ux-overhaul.md §9 row 5", ["machine_latency"], ""),
    ("Machine view (P6c)", "Attributes", "ux-overhaul.md §9 row 14", ["machine_attributes"], ""),
    ("Machine view (P6c)", "A machine's own commands", "ux-overhaul.md §9 row 14", ["machine_command"], ""),
    ("Tools (P6b)", "Song properties: tempo, ticks per beat, speed", "ux-overhaul.md §9 row 13", ["song_set"], ""),
    ("Tools (P6b)", "MIDI bindings", "ux-overhaul.md §9 row 10", ["midi_bindings_list", "midi_bind", "midi_unbind", "midi_learn"], ""),
    ("Tools (P6b)", "Hard Disk Recorder", "ux-overhaul.md §9 row 8", ["master_record", "export_audio"], "master_record needs studio mode and an audio output; export_audio renders offline"),
    ("Tools (P6b)", "CPU monitor", "ux-overhaul.md §9 row 7", ["app_status", "render", "audio_status"], "render(cpu=true) gives each machine's cost"),
    ("Tools (P6b)", "Signal analysis", "ux-overhaul.md §9 row 6", ["analyze", "meters_read", "plot"], ""),
    ("Tools (P6b)", "Debug console", "ux-overhaul.md §9 row 9", [], "none: the agent's own log is the agent-logs folder; the engine's warnings reach a tool as its error text"),
    ("Tools (P6b)", "MIDI focus bar and the on-screen piano keyboard", "ux-overhaul.md §9 row 11", ["midi_play", "select"], "the focus follows select; the keyboard is a way to play, midi_play plays"),
    ("Plug-ins (H1 to H3)", "The plug-in host and its preferences", "plugin-host.md", ["gear_list", "machine_add", "machine_describe", "machine_command"], "placement (in process or a sandbox) and the crash state are in machine_describe; Placement and Reload plug-in are machine_command items; the Preferences page and plug-in editors are the studio's only"),
    ("Neo Audio (NA1)", "Audio files in the song: import, warp, tempo, transpose, gain, loop, region", "neo-audio.md", ["audio_import", "audio_clip_get", "audio_clip_set"], "the audio is kept in the song (an .nbl embeds it), not in the wavetable"),
    ("Drag and drop (DD1)", "Dropping a browser item or a file on the Arrangement, the Session or the Machine View, by the same rules", "drag-drop.md", ["drop_item"], "the drag itself is the person's; drop_item does what the drop does, plan=true says what it would do"),
    ("Neo Audio (NA2)", "Audio clips in the arrangement: waveforms, move across audio tracks, trim, stretch, loop, fades, crossfades, gain, split, consolidate, reverse", "neo-audio.md §10", ["arrangement_clip", "audio_clip_set", "audio_clip_get"], "arrangement_clip ops fade, gain, crossfade, reverse, stretch, consolidate and to_machine are the Arrange view's gestures; the waveform drawing is the studio's only"),
    ("Neo Audio (NA5)", "Pro warp mode: Signalsmith Stretch time-stretch and pitch shift for any material, in Session and the arrangement", "neo-audio.md §11", ["audio_clip_set", "audio_clip_get"], "audio_clip_set warp=\"pro\"; audio_clip_get reports it, and warp_playing when the native library is missing and the clip plays as Complex"),
    ("Neo Audio (NA3)", "Audio page of the clip panel: warp, tempo, detect, transpose, detune, gain, fades, reverse, loop, region and warp markers on a waveform; mini waveforms in Session slots", "neo-audio.md §12", ["audio_clip_set", "audio_clip_get"], "the page and audio_clip_set share one model call (SessionLayout.EditAudio); the waveform drawing and marker mouse gestures are the studio's only"),
    ("Browser (BR1)", "One search over machines, plug-ins, presets, patterns and indexed sample folders", "browser.md", ["browser_search"], "the panel's search; kind narrows it"),
    ("Browser (BR1)", "Places: the folders the browser indexes", "browser.md", ["browser_places"], "list, add, remove; the scan runs in the background"),
    ("Browser (BR1)", "Hot-swap: other gear in a machine's place", "browser.md", ["machine_swap"], "the Machine View's drop on a machine's box; one undo step"),
    ("Neo Audio (WM1)", "Warp markers of the Audio page's waveform: ghost marker, selection and group move, move along the audio, segment tempo, set bar 1, warp straight or as a loop, markers at the transients or on every bar, delete, quantize with a strength, zoom and scroll", "neo-audio.md §15", ["audio_warp_markers"], "audio_warp_markers does each edit with the page's own call (SessionLayout.WarpEdit); selecting markers or a time range, the ghost marker, the hover hints, zoom, scroll and follow are the studio's only"),
    ("Neo Audio (stems)", "Separate stems: a clip or a file into vocals, drums, bass and other on new audio tracks", "stem-separation.md", ["audio_separate"], "the Separate stems and Install models dialogs and the Preferences page are the studio's only"),
    ("Neo Audio (Slice to new MIDI track)", "Slice to new MIDI track: an audio clip cut at its transients, warp markers or a grid (up to 16 slices) into song waves, with a Neo Sampler Kit track and a MIDI clip of one note per slice, in the clip's scene and arrangement positions", "audio-to-midi.md section 10", ["audio_slice_to_midi"], "the Slice to new MIDI track menu of a Session slot, an arrangement clip and the Audio page; the dialog shows the slice count and disables the choices that would make more than 16"),
    ("Neo Audio (Convert to MIDI)", "Convert to MIDI: an audio clip's melody, harmony or drums as a MIDI clip on a new track with an instrument, in the clip's scene and arrangement positions", "pitch.md section 9", ["audio_convert_to_midi"], "the Convert to MIDI menu of a Session slot, an arrangement clip and the Audio page; the dialog adds the quantize choice the tool has as a parameter"),
    ("Dropped", "Wireless, machine templates and the Create Template window", "ux-overhaul.md §9 row 14", [], "dropped in the studio too"),
]


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read().replace("\r\n", "\n")


def manifests():
    found = {}
    for f in glob.glob(os.path.join(ROOT, "Machines", "**", "*.machine.json"), recursive=True):
        n = f.replace("\\", "/")
        if "/bin/" in n or "/obj/" in n:
            continue
        m = json.loads(read(f))
        found[m["gear"]] = m
    return found


def presets():
    counts = {}
    files = glob.glob(os.path.join(ROOT, "Machines", "*", "Presets.txt")) + glob.glob(os.path.join(ROOT, "Machines", "Manifests", "*", "*.presets.txt"))
    for f in files:
        gear = None
        for line in read(f).split("\n"):
            m = re.match(r'^machine "(.+)"', line)
            if m:
                gear = m.group(1)
                counts[gear] = 0
            elif gear and re.match(r'^preset "(.+)"', line):
                counts[gear] += 1
    return counts


def status(m):
    s = m.get("status", "")
    if s:
        return s
    return "generated" if m.get("source", "").startswith("generated") else "curated"


def tools():
    """Every tool the app registers (a ToolDef with Name, Title and Input) in ReBuzz/Agent and ReBuzz.Agent."""
    found = {}
    files = glob.glob(os.path.join(ROOT, "ReBuzz", "Agent", "*.cs")) + glob.glob(os.path.join(ROOT, "ReBuzz.Agent", "**", "*.cs"), recursive=True) \
        + glob.glob(os.path.join(ROOT, "ReBuzz.Studio", "Shell", "Tools", "*Tools.cs"))
    for f in sorted(files):
        n = f.replace("\\", "/")
        if "/obj/" in n or "/bin/" in n:
            continue
        text = read(f)
        # A tool is a ToolDef initialiser: Name, Title, Description, Input (prompts and resources have Render and Read instead).
        spots = [m for m in re.finditer(r'Name\s*=\s*(?:"([a-z][a-z0-9_]*)"|(?:NotesTransform\.)?Name)\s*,\s*Title\s*=', text)]
        for i, m in enumerate(spots):
            end = spots[i + 1].start() if i + 1 < len(spots) else len(text)
            block = text[m.end():end]
            head = block.split("Input =")[0]
            if "Input =" not in block or "Render =" in head:
                continue
            found[m.group(1) or "notes_transform"] = "Example" in head
    return found


def texts():
    out = {}
    for f in sorted(glob.glob(os.path.join(GUIDES, "*.md"))):
        out["guide " + os.path.basename(f)[:-3]] = read(f)
    for f in sorted(glob.glob(os.path.join(GUIDES, "machines", "*.md"))):
        out["page " + os.path.basename(f)[:-3]] = read(f)
    out["prompts"] = read(os.path.join(GUIDES, "Prompts.cs"))
    return out


def pct(n, d):
    return "-" if d == 0 else f"{round(100 * n / d)}%"


def generate():
    cat = json.loads(read(os.path.join(ROOT, "Machines", "Manifests", "machine-catalog.json")))
    profile = json.loads(read(os.path.join(ROOT, "Machines", "Manifests", "machine-profile.json")))["machines"]
    ms = manifests()
    pre = presets()
    pages = {os.path.basename(f)[:-3] for f in glob.glob(os.path.join(GUIDES, "machines", "*.md"))}
    order = {c: i for i, c in enumerate(cat["categories"])}
    rows = []
    for gear in profile:
        m = ms.get(gear)
        params = []
        if m:
            for key in ("parameters", "track_parameters"):
                params += [p for p in m.get(key, []) if isinstance(p, dict)]
        roled = sum(1 for p in params if p.get("role"))
        suggested = sum(1 for p in params if p.get("role") or p.get("suggested_role"))
        safe = sum(1 for p in params if p.get("safe_range"))
        rows.append({
            "gear": gear, "category": m.get("category", "") if m else "", "status": status(m) if m else "none",
            "page": gear in pages, "roles": pct(roled, len(params)), "suggested": pct(suggested, len(params)),
            "presets": pre.get(gear, 0), "safe": safe, "params": len(params), "roled": roled, "suggested_n": suggested,
        })
    rows.sort(key=lambda r: (order.get(r["category"], 99), r["gear"].lower()))

    tool_examples = tools()
    corpus = texts()
    trows = []
    for name in sorted(tool_examples):
        named = [k for k, t in corpus.items() if re.search(r"`%s`|(?<![\w_])%s\(" % (name, name), t)]
        calls = sum(len(re.findall(r"(?<![\w_])%s\(" % name, t)) for t in corpus.values())
        trows.append((name, len(named), calls, tool_examples[name]))

    n = len(rows)
    by_status = {s: sum(1 for r in rows if r["status"] == s) for s in ("curated", "generated", "draft", "none")}
    cats = ", ".join(f"{c} {sum(1 for r in rows if r['category'] == c)}" for c in cat["categories"])
    out = []
    out.append("# Agent coverage\n")
    out.append("Generated by `tools/agent-coverage.py` (docs/specs/agent-knowledge.md, K0) from the machine manifests, the profile, the factory presets, the guides and the tool definitions in the source. Do not edit by hand: run the script. The `CatalogTests` staleness test fails when this file is out of date.\n")
    out.append(f"**Machines.** {n} in the profile: {by_status['curated']} curated manifests, {by_status['generated']} generated, {by_status['draft']} draft" + (f", {by_status['none']} with no manifest" if by_status["none"] else "") + f". Hand-written pages: {sum(1 for r in rows if r['page'])}. Factory presets: {sum(r['presets'] for r in rows)} on {sum(1 for r in rows if r['presets'])} machines. Parameters with a role: {sum(r['roled'] for r in rows)} of {sum(r['params'] for r in rows)}; with a role or a suggested one: {sum(r['suggested_n'] for r in rows)}. Safe ranges: {sum(r['safe'] for r in rows)}. Categories: {cats}.\n")
    never = [t[0] for t in trows if t[1] == 0]
    out.append(f"**Tools.** {len(trows)} registered. Named by at least one guide or page: {sum(1 for t in trows if t[1] > 0)}. Shown in a call with arguments: {sum(1 for t in trows if t[2] > 0)}. With an example in their own description: {sum(1 for t in trows if t[3])}." + (f" Named nowhere: {', '.join(never)}." if never else "") + "\n")
    registered = set(tool_examples)
    for area, feature, spec, names, note in FEATURES:
        for name in names:
            if name not in registered:
                raise SystemExit(f"tools/agent-coverage.py: feature \"{feature}\" names {name}, which no tool definition registers")
    covered = [f for f in FEATURES if f[3]]
    out.append(f"**Features.** {len(FEATURES)} features built since K3: {len(covered)} covered by a tool, {len(FEATURES) - len(covered)} without one (each says why).\n")
    out.append("## Machines\n")
    out.append("Manifest: how far its units and roles are trusted. Page: a hand-written page in the manual. Roles: parameters with a curated role (with drafts' suggested roles counted in brackets when they add any). Presets: factory presets. Safe: parameters with a safe range.\n")
    out.append("| Machine | Category | Manifest | Page | Roles | Presets | Safe | Params |")
    out.append("|---|---|---|---|---|---|---|---|")
    for r in rows:
        roles = r["roles"] + (f" ({r['suggested']})" if r["suggested_n"] > r["roled"] else "")
        out.append(f"| {r['gear']} | {r['category'] or '-'} | {r['status']} | {'yes' if r['page'] else 'no'} | {roles} | {r['presets']} | {r['safe']} | {r['params']} |")
    out.append("\n## Features\n")
    out.append("The features built since K3 and the tools that cover them (`FEATURES` in `tools/agent-coverage.py`; a tool named there must exist). A feature with no tool says why.\n")
    out.append("| Area | Feature | Specified in | Tools | Note |")
    out.append("|---|---|---|---|---|")
    for area, feature, spec, names, note in FEATURES:
        out.append(f"| {area} | {feature} | {spec} | {', '.join(names) if names else '**none**'} | {note} |")
    out.append("\n## Tools\n")
    out.append("Guides: how many guides, pages and prompts name the tool. Calls: how many calls with arguments they show. Example: whether the tool's own description carries one.\n")
    out.append("| Tool | Guides | Calls | Example |")
    out.append("|---|---|---|---|")
    for name, g, c, e in trows:
        out.append(f"| {name} | {g} | {c} | {'yes' if e else 'no'} |")
    return "\n".join(out) + "\n"


def main(argv):
    text = generate()
    if "--check" in argv:
        have = read(OUT) if os.path.exists(OUT) else ""
        if have != text:
            print("docs/agent-coverage.md is stale: run python3 tools/agent-coverage.py")
            return 1
        print("docs/agent-coverage.md is current")
        return 0
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote docs/agent-coverage.md")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
