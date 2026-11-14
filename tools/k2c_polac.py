# Fork (agent-knowledge.md K2 batch c): the eight Polac machines' Explain text and curation (see tools/k2c_knowledge.py).
# Closed-source routing and plugin-host machines: their meaning is from their parameter texts, the host's own handling of them
# (ReBuzz/Core/ReBuzzCore.cs, ReBuzz/MachineManagement/MachineManager.cs) and the profile. Keys are positions.

PROFILE = "profile (tools/machine-profiler.py, windows 2026-11-14)"
OWN = "its parameter descriptions and value texts (machine_describe, as profiled)"
CLOSED = "Closed source: names come from its own texts and measurements, not from code."
PCT = {"a": 0.0, "b": 100.0}
HOST = ("ReBuzz/Core/ReBuzzCore.cs - MIDI input reaches a focused Polac VST or VSTi host with a conversion for its format",)
NOPLUG = "Hosts nothing until a plugin is loaded in its window, so the profile measured the empty host: a pass-through at 0 dB (effect) or silence (instrument); what a loaded plugin does was not tested."

VST_TRACK = {
    "t0": ("Chooses which plugin parameter this track's Param Val sets, 0 to 12,543 (the numbering was not checked).",
           "Each track steers one plugin parameter, so a pattern can automate the plugin from the tracker."),
    "t1": ("Sets the value of the chosen plugin parameter, 0 to 100 percent of its range.",
           "It moves the plugin's control from nothing to full; what that sounds like depends on the plugin."),
    "t2": ("Sets the MIDI channel this track's notes go out on, 1 to 16.",
           "Use different channels to drive separate parts of a multi-timbral plugin."),
}
VST_GLOBAL = {
    "gc": ("Chooses a global command, from a list of 18 including transpose, tempo sync, trigger sequencing, bypass, morph between two programs, automation slope and delay, velocity and delay commands, mute all tracks, system exclusive, rows per beat and time signature.",
           "It does not sound by itself; it sets how the host behaves, with the value in Global Command Value."),
    "gv": ("Holds the value for the chosen global command, 0 to 65,534 (its own default, 65,535, is one past the range and means not written).",
           "Its effect depends on the command; write it with the command."),
    "pr": ("Chooses the plugin's program (patch) number, 0 to 65,534.",
           "Changing it loads a different preset in the hosted plugin."),
}

EXPLAIN = {
    "Polac Audio Out": {
        "p0": ("Sets the level sent to the audio device, a table from silence up to +12 dB, with 0 dB at about raw 24,576 (the default).",
               "Use it to trim what reaches that output; above 0 dB it can overload the device."),
    },
    "Polac Audio In": {
        "p0": ("Sets the gain of the audio input brought into the song, a table from silence up to +12 dB, with 0 dB at about raw 24,576 (the default).",
               "Turn it up for a quiet source and down for a hot one; no sound arrives unless the device input has a signal."),
    },
    "Polac MIDI In": {},
    "Polac MIDI Bus II": {},
    "Polac Out": {
        "p0": ("Sets the output level, a table from silence to +12 dB, with 0 dB at 100 (the default) and +12 dB at 400.",
               "At the default the sound passes at the same level; higher values boost it and can clip."),
        "p1": ("Pans the output from -64 (left) to +64 (right), 0 at 64 (the default); the unit and its audible effect were not measured.",
               "Moving it away from the middle places the signal toward one side."),
    },
    "Polac Out II": {
        "p0": ("Sets the output level, a table from silence to +12 dB, with 0 dB at 100 (the default) and +12 dB at 400.",
               "At the default the sound passes at the same level; higher values boost it and can clip."),
    },
    "Polac VST 1.1": {
        "p0": ("Sets the level of the untouched input in the output, a table from silence to +12 dB (silence by default).",
               "Raise it to mix the dry sound in beside the plugin's output; at the default you hear only the plugin."),
        "p1": ("Sets the level of the plugin's output, a table from silence to +12 dB (0 dB at 100, the default).",
               "It balances the plugin against the dry sound."),
        "p2": ("Pans the output from -64 (left) to +64 (right), 0 at 64; the unit and its audible effect were not measured.",
               "Moving it away from the middle places the output toward one side."),
        "p3": VST_GLOBAL["gc"], "p4": VST_GLOBAL["gv"], "p5": VST_GLOBAL["pr"],
        **VST_TRACK,
    },
    "Polac VSTi 1.1": {
        "p0": ("Sets the level of the plugin's output, a table from silence to +12 dB (0 dB at 100, the default).",
               "It sets how loud the instrument is in the song."),
        "p1": ("Pans the output from -64 (left) to +64 (right), 0 at 64; the unit and its audible effect were not measured.",
               "Moving it away from the middle places the instrument toward one side."),
        "p2": VST_GLOBAL["gc"], "p3": VST_GLOBAL["gv"], "p4": VST_GLOBAL["pr"],
        **VST_TRACK,
    },
}

CURATE = {
    "Polac Audio Out": {
        "type": "utility",
        "summary": "Routing machine that sends its input to an output of the audio device, with one Volume control. An older machine for people who route outputs by hand; the audio device settings and the master do this now.",
        "params": {"p0": ("Output volume", "level", {"add_points": [(24576, 0.0)], "description": "Level sent to the audio device, silence at 0 and +12 dB at 32,768 (about 0 dB at the default 24,576)."})},
        "quirks": [
            "The profile has no measurement for it (it has no input or output the test could use), so which device output it uses and what it does to the signal are unverified.",
            "Volume is a table: silence at 0, about 0 dB at 24,576 (the default) and +12 dB at 32,768.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " - no signal measured through it"],
    },
    "Polac Audio In": {
        "type": "generator",
        "summary": "Routing machine that brings an input of the audio device into the song as a source, with one Gain control. An older machine; the Studio and the master record inputs another way.",
        "params": {"p0": ("Input gain", "gain.input", {"add_points": [(24576, 0.0)], "description": "Gain of the input brought into the song, silence at 0 and +12 dB at 32,768 (about 0 dB at the default 24,576)."})},
        "quirks": [
            "A note does nothing: it is a source of live input; with no input signal in the profile it was silent, so which device input it takes is unverified.",
            "Gain is a table: silence at 0, about 0 dB at 24,576 (the default) and +12 dB at 32,768.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " - silent with no device input"],
    },
    "Polac MIDI In": {
        "type": "control",
        "summary": "Control machine that receives MIDI from a device into the song, with no parameters of its own. An older routing machine; the app's MIDI settings and bindings cover this now.",
        "params": {},
        "quirks": [
            "It has no parameters and makes no sound; what it forwards, and to which machines, was not measured: unverified.",
            CLOSED,
        ],
        "evidence": [OWN + " - none: it has no parameters", PROFILE + " - a control machine, no sound"],
    },
    "Polac MIDI Bus II": {
        "type": "control",
        "summary": "Control machine that routes MIDI between machines, with no parameters of its own. An older routing machine; the app's MIDI settings and bindings cover this now.",
        "params": {},
        "quirks": [
            "It has no parameters and makes no sound; which MIDI it routes where was not measured: unverified.",
            CLOSED,
        ],
        "evidence": [OWN + " - none: it has no parameters", PROFILE + " - a control machine, no sound"],
    },
    "Polac Out": {
        "type": "utility",
        "summary": "Output stage with a volume control from silence to +12 dB and a panning control, a pass-through at its defaults. Neo Utility does the same with width and polarity.",
        "params": {"p0": ("Volume", "level", {"description": "Output level, silence at 0, 0 dB at 100 (the default) and +12 dB at 400."}), "p1": ("Pan", "pan", {"description": "Panning from -64 (left) to +64 (right), 0 at 64; the unit was not measured."})},
        "quirks": [
            "Pass-through at 0 dB at its defaults (profile): output equals input.",
            "Volume is a table: silence at 0, 0 dB at 100 (the default), +12 dB at 400.",
            "Unverified: the unit of Panning (its text shows -64 to +64) and what it does to the sound; the profile did not move it.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " - 0 dB at the defaults, no latency"],
    },
    "Polac Out II": {
        "type": "utility",
        "summary": "Output stage with a volume control from silence to +12 dB, a pass-through at its default. Neo Utility does the same with pan, width and polarity.",
        "params": {"p0": ("Volume", "level", {"description": "Output level, silence at 0, 0 dB at 100 (the default) and +12 dB at 400."})},
        "quirks": [
            "Pass-through at 0 dB at its default (profile): output equals input.",
            "Volume is a table: silence at 0, 0 dB at 100 (the default), +12 dB at 400.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " - 0 dB at the default, no latency"],
    },
    "Polac VST 1.1": {
        "type": "plugin host",
        "summary": "Hosts an effect plugin in the graph: a dry and a wet level, a pan, a program number, a global command with a value, and for each of its tracks a plugin parameter number and value and a MIDI channel. An older 32-bit-era host.",
        "params": {"p0": ("Dry level", "mix.dry", {"description": "Level of the untouched input in the output, silence at 0 (the default) to +12 dB at 400."}), "p1": ("Wet level", "mix.wet", {"description": "Level of the plugin's output, silence at 0, 0 dB at 100 (the default) and +12 dB at 400."}), "p2": ("Pan", "pan", {"description": "Panning from -64 (left) to +64 (right), 0 at 64; the unit was not measured."}), "p3": ("Global command", "", {"description": "A global command of the host: transpose, tempo sync, bypass, morph, automation slope and others (18)."}), "p4": ("Global command value", "", {"description": "Value of the global command, 0 to 65,534 (its own default, 65,535, is out of range and means not written; NeoBuzz Live takes 65,534)."}),
                   "p5": ("Program", "", {"description": "The plugin's program (patch) number, 0 to 65,534."}), "t0": ("Plugin parameter", ""), "t1": ("Plugin parameter value", "", PCT), "t2": ("MIDI channel", "")},
        "quirks": [
            NOPLUG,
            "MIDI input goes to it with a conversion for its format when it has the keyboard focus (the host's own handling).",
            "Up to 128 tracks; each track's three columns steer one plugin parameter on one MIDI channel.",
            "Unverified: which plugins it can load in this build, the numbering behind Plugin parameter, and the pan unit.",
            CLOSED,
        ],
        "evidence": [OWN, *HOST, PROFILE + " - an empty host passes sound at 0 dB"],
    },
    "Polac VSTi 1.1": {
        "type": "plugin host",
        "summary": "Hosts an instrument plugin in the graph: a level, a pan, a program number, a global command with a value, and for each of its tracks a plugin parameter number and value and a MIDI channel. An older 32-bit-era host.",
        "params": {"p0": ("Wet level", "level", {"description": "Level of the plugin's output, silence at 0, 0 dB at 100 (the default) and +12 dB at 400."}), "p1": ("Pan", "pan", {"description": "Panning from -64 (left) to +64 (right), 0 at 64; the unit was not measured."}), "p2": ("Global command", "", {"description": "A global command of the host: transpose, tempo sync, bypass, morph, automation slope and others (18)."}), "p3": ("Global command value", "", {"description": "Value of the global command, 0 to 65,534 (its own default, 65,535, is out of range and means not written; NeoBuzz Live takes 65,534)."}),
                   "p4": ("Program", "", {"description": "The plugin's program (patch) number, 0 to 65,534."}), "t0": ("Plugin parameter", ""), "t1": ("Plugin parameter value", "", PCT), "t2": ("MIDI channel", "")},
        "quirks": [
            NOPLUG,
            "MIDI input goes to it with a conversion for its format when it has the keyboard focus (the host's own handling).",
            "Up to 128 tracks; each track's three columns steer one plugin parameter on one MIDI channel.",
            "Unverified: which plugins it can load in this build, the numbering behind Plugin parameter, and the pan unit.",
            CLOSED,
        ],
        "evidence": [OWN, *HOST, PROFILE + " - an empty host is silent from a note"],
    },
}
