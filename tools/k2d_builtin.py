# Fork (agent-knowledge.md K2 batch d): ReBuzz's own machines and the Rout 909's Explain text and curation (see tools/k2c_knowledge.py,
# which applies them). The ten machines here that tools/source_manifests.py generates from their C# source (Audio In, Audio Out,
# Groove, Midi Output, Session Clip Player, AudioBlock, Connection Mixer Console, EnvelopeBlock, QuickNote, ManagedMultiIOEffect)
# were read against that source (Machines/<name>/<name>.cs) and ReBuzz/Core; the Rout 909 is closed source (its texts and profile).
# Keys are positions ("p3" is the fourth global parameter, "t0" the first track parameter).

PROFILE = "profile (tools/machine-profiler.py, windows 2026-11-14)"
SRC = "Machines/{0}/{0}.cs — the machine's source: its declarations, Work and menu"

# ---- Connection Mixer Console: 16 strips of Volume and Pan -------------------------------------------------------------
MIX_EXPLAIN, MIX_PARAMS = {}, {}
for _i in range(16):
    MIX_EXPLAIN[f"p{2 * _i}"] = (
        f"Sets the volume of the connection assigned to strip {_i + 1}, 0 to 400 percent of the connection's own level (100 percent is unity, raw 16,384, the default).",
        f"Raises or lowers how loud that one connection is in its destination, as the machine view's connection slider does; above 100 percent it can overload the destination.")
    MIX_EXPLAIN[f"p{2 * _i + 1}"] = (
        f"Pans the connection assigned to strip {_i + 1} from -100 percent (left) through 0 (centre, raw 16,384, the default) to +100 percent (right).",
        "Places that one connection in the stereo field of its destination, as the machine view's pan slider does.")
    MIX_PARAMS[f"p{2 * _i}"] = (f"Strip {_i + 1} volume", "level")
    MIX_PARAMS[f"p{2 * _i + 1}"] = (f"Strip {_i + 1} pan", "pan")

# ---- the Groove machine: eight steps ------------------------------------------------------------------------------------
GROOVE_EXPLAIN = {"p0": (
    "Sets how many of the eight steps are used, 0 to 8; 0 (the default) turns the groove off.",
    "More steps make a longer swing pattern that repeats over the song; with 0 the timing is straight.")}
GROOVE_PARAMS = {"p0": ("Steps in groove", None)}
for _i in range(1, 9):
    GROOVE_EXPLAIN[f"p{_i}"] = (
        f"Sets how long step {_i} lasts relative to the others, 1.0x (raw 0, the default) to 3.0x (raw 127); only the ratios between steps matter, because the pattern is scaled to keep the average at 1.",
        f"A step above its neighbours is stretched and one below is squeezed, so notes landing on step {_i} come late or early against the grid; all steps equal means no swing.")
    GROOVE_PARAMS[f"p{_i}"] = (f"Step {_i} length", None)

EXPLAIN = {
    "ReBuzz Audio In": {"p0": (
        "Switches the input off: no (the default) passes the audio device's input into the song, yes makes the machine output nothing.",
        "At no you hear the live input at its original level (watch for feedback with open speakers); at yes this source is silent.")},
    "ReBuzz Audio Out": {"p0": (
        "Does nothing: it exists only because a machine needs a parameter, 0 or 1.",
        "It has no effect on the sound; the output channel is chosen in the machine's menu, not here.")},
    "ReBuzz Groove": GROOVE_EXPLAIN,
    "ReBuzz Midi Output": {
        "t0": ("Chooses the MIDI output device this track sends to, by its number in the app's output list, 0 to 255 (0 by default).",
               "Nothing is heard in the song; the device you pick plays whatever this track sends."),
        "t1": ("Chooses which controller a new Controller value goes to: 0 to 127 is a controller number, 224 is the pitch wheel, anything else sends nothing.",
               "It decides what the Controller value moves on the external device, such as modulation or volume."),
        "t2": ("Sets the value sent to the chosen controller when it changes: 0 to 127 for a controller (higher is clipped to 127), 0 to 16,383 for the pitch wheel with 8,192 as its centre.",
               "It moves that controller on the external device; the wheel's centre leaves the pitch unbent."),
        "t3": ("Chooses the MIDI channel this track's notes and controller changes go out on: raw 0 is channel 1, raw 15 is channel 16.",
               "Use a different channel per track to address different parts of a multi-timbral device."),
    },
    "Session Clip Player": {
        "p0": ("Chooses when you hear the input while this track is open to it: auto (only while the track is armed, the default), in (always) or off (never).",
               "At auto you monitor what you record; at in the input is always audible through the track; at off only the clips are heard."),
        "p1": ("Chooses what a take records: in (the machines connected to this one, the default) or master (the song's own output, resampling).",
               "At in a take holds the connected source; at master it captures everything the song plays, to bounce or layer it."),
    },
    "AudioBlock": {"p0": (
        "Mutes the machine: no (the default) lets the clips on its timeline play, yes makes it output nothing.",
        "At yes the clips go quiet while the song carries on; at no they play in step with the song position.")},
    "EnvelopeBlock": {"p0": (
        "Chooses what a newly drawn envelope point starts from: the target parameter's current value (the default) or its default value.",
        "It does not sound by itself; it decides where a new envelope line begins, so a fresh point either keeps what you hear or resets it.")},
    "QuickNote": {"p0": (
        "Does nothing: it exists only because a machine needs a parameter, 0 or 1.",
        "It has no effect on the sound; the machine only holds a text note.")},
    "Connection Mixer Console": MIX_EXPLAIN,
    "ManagedMultiIOEffect": {
        "p0": ("Sets the length of the delay in the delay path, 100 ms to 2 s (300 ms by default); the delay buffer is resized each time it is set.",
               "A longer time spaces the repeats of the delay input further apart."),
        "p1": ("Sets how much of the delay's earlier content is fed back, 0 to 100 percent (50 by default).",
               "More feedback gives more repeats; at 100 percent the repeats never fade and the level keeps building."),
        "p2": ("Sets how much noise is added to the noise input, 0 to 100 percent (50 by default); the noise is scaled by that input's own peak-to-peak level.",
               "More noise is a harsher hiss on the noise path; with a silent noise input none is added."),
    },
}

CURATE = {
    "ReBuzz Audio In": {
        "type": "generator",
        "summary": "Brings one stereo pair of the audio device's input into the song as a source, with a Bypass switch. The pair and mono or stereo are chosen in its menu.",
        "params": {"p0": ("Bypass", "bypass")},
        "quirks": [
            "The input pair (0 to 31) and mono or stereo are machine state set from its menu, not parameters: param_set cannot change them.",
            "It outputs nothing until a block of input has been captured, and with no input signal the profile heard silence, which is why its health is warn.",
            "The capture is scaled to Buzz's sample range (x32768); in mono the left channel feeds both sides.",
            "Whether a particular device channel arrives was not measured: unverified (the profile had no input).",
        ],
        "evidence": [SRC.format("ReBuzzAudioIn"), PROFILE + " - silent with no device input"],
    },
    "ReBuzz Audio Out": {
        "type": "utility",
        "summary": "Sends what is connected to it to one of the audio device's extra outputs, or passes it on when its channel is 0. The channel is chosen in its menu.",
        "params": {"p0": ("Unused switch", None)},
        "quirks": [
            "Its one parameter does nothing; the output channel (0 to 31) is machine state set from its menu: param_set cannot change it.",
            "At channel 0 it is a pass-through (the profile: 0 dB, no latency); at any other channel the audio goes to that device output and the machine's own output is silent.",
            "Whether a device really has that many outputs was not measured: unverified.",
        ],
        "evidence": [SRC.format("ReBuzzAudioOut"), PROFILE + " - a pass-through at 0 dB at channel 0"],
    },
    "ReBuzz Groove": {
        "type": "control",
        "summary": "Swing for the whole song: up to eight steps, each tick's length scaled by its step's factor in turn.",
        "params": GROOVE_PARAMS,
        "quirks": [
            "It changes the song's timing, not its sound; it sets the master's groove pattern (ReBuzz/Core/ReBuzzCore.cs SetGroovePattern) when a parameter changes.",
            "The pattern is normalised so its average is 1: only the ratios between steps matter, and with every step at 1.0x (all raw 0) nothing swings.",
            "Steps above Steps in groove are ignored; 0 steps is off.",
            "Step length is 1.0 + raw x 2 / 127, so 3.0x at raw 127; the machine's own description gives the formula but no unit.",
        ],
        "evidence": [SRC.format("ReBuzzGroove"), "ReBuzz/Core/ReBuzzCore.cs — SetGroovePattern: zero lengths become 1 and the pattern is scaled to an average of 1"],
    },
    "ReBuzz Midi Output": {
        "type": "control",
        "summary": "Sends notes and controller changes from its tracks to a MIDI output device. It makes no sound itself; the device does.",
        "params": {"t0": ("MIDI output device", None), "t1": ("Controller number", None), "t2": ("Controller value", None), "t3": ("MIDI channel", None)},
        "quirks": [
            "It sends MIDI, not audio: the profile heard silence by design (health warn), and the device it drives was not tested: unverified.",
            "Controller number 0 to 127 is a controller, 224 is the pitch wheel, anything else sends nothing; the value only goes out when it changes.",
            "A note-off goes out on the channel its note went out on.",
            "Channel is 0-based: raw 0 is MIDI channel 1.",
        ],
        "evidence": [SRC.format("MidiOutput"), PROFILE + " - silent: it sends MIDI"],
    },
    "Session Clip Player": {
        "type": "effect",
        "summary": "A Session View audio track: plays the audio clips its track launches, warped to the song's tempo, and records takes from its input or the master.",
        "params": {"p0": ("Monitor", None), "p1": ("Take source", None)},
        "quirks": [
            "The work is done by the app's Session View engine (ReBuzz/Core/Session/SessionClipEngine.cs); the machine only hands it the block.",
            "Outside the app's engine it passes its input through; with no clip launched it is silent, which is why its health is warn.",
            "Clips are made in the Session View (dropping a file or session_clip_set), not by notes.",
        ],
        "evidence": ["Machines/SessionClipPlayer/SessionClipPlayer.cs — the machine's source: two parameters and a hand-off to the Session View engine",
                     "docs/specs/session-view.md — Audio tracks: a track of a Session Clip Player, clips warped to the song's tempo",
                     PROFILE + " - silent with no clip launched"],
    },
    "AudioBlock": {
        "type": "control",
        "summary": "Plays audio clips placed on a timeline in its own window, following the song's position (a Windows window), with a Mute switch.",
        "params": {"p0": ("Mute", "bypass")},
        "quirks": [
            "The clips live in its own timeline window and in its saved state, not in parameters: the Studio does not show that window, so it is for songs that already use it.",
            "Mute yes returns no audio from the machine; with no clips placed the profile heard silence (health warn).",
            "For new work the Session View's audio tracks (Session Clip Player) and the arrangement cover this: unverified how an AudioBlock song behaves in the Studio.",
        ],
        "evidence": [SRC.format("AudioBlock"), PROFILE + " - silent: no clips on its timeline"],
    },
    "EnvelopeBlock": {
        "type": "control",
        "summary": "Draws parameter envelopes over patterns in its own window and plays them into the machines they target. It makes no sound.",
        "params": {"p0": ("New point start", None)},
        "quirks": [
            "The envelopes are drawn in its own window and saved with the song; param_set cannot draw them (automation_write and modulate do the same work from here).",
            "New point start decides whether a fresh envelope point takes the target's current value or its default (Machines/EnvelopeBlock/EnvelopeBlock.cs).",
            "Unverified: how an existing EnvelopeBlock song plays in the Studio.",
        ],
        "evidence": [SRC.format("EnvelopeBlock"), PROFILE + " - a control machine, no sound"],
    },
    "QuickNote": {
        "type": "control",
        "summary": "A control machine that holds a text note in the song, edited in its own window. Its one parameter does nothing.",
        "params": {"p0": ("Unused switch", None)},
        "quirks": [
            "The text is machine state saved with the song; it sends nothing and makes no sound.",
            "For notes about the song as a whole, song_notes_write is the app's own place.",
        ],
        "evidence": [SRC.format("QuickNote"), PROFILE + " - a control machine, no sound"],
    },
    "Connection Mixer Console": {
        "type": "control",
        "summary": "A mixing console for up to 16 machine connections: each strip's Volume and Pan set that connection's amp and pan, as the machine view's sliders do.",
        "params": MIX_PARAMS,
        "quirks": [
            "A strip does nothing until a connection is assigned to it in the machine's window (the assignment is saved state, not a parameter); an unassigned strip's Volume and Pan are ignored.",
            "Volume is 0 to 400 percent of the connection's level (raw 16,384 is 100 percent, the default); Pan is -100 to +100 percent (raw 16,384 is the centre).",
            "connection_set sets a connection's amp and pan directly and is the simpler route from here.",
            "It makes no sound itself; MIDI controllers can be bound to its strips in its window.",
        ],
        "evidence": [SRC.format("ConnectionMixer"), "ReBuzz/Core/MachineConnectionCore.cs — the connection's Amp and Pan that the strips write", PROFILE + " - a control machine, no sound"],
    },
    "ManagedMultiIOEffect": {
        "type": "utility",
        "summary": "The host's example of a managed multi-input, multi-output effect: a clean input, a noise input that gets noise added, a delay input with feedback; a mix out and each part on its own output.",
        "params": {"p0": ("Delay time", "delay.time"), "p1": ("Delay feedback", "delay.feedback"), "p2": ("Noise amount", None)},
        "quirks": [
            "It is a worked example of three inputs and three outputs, not a mixing tool: output 1 is the sum of all three inputs, outputs 2 and 3 are the noise and delay paths alone.",
            "With only a clean input connected it passes it unchanged (the profile: 0 dB, no latency).",
            "Delay time resizes the delay buffer each time it is set, and its value reads back as 0 (a quirk of the example).",
            "The noise is scaled by the noise input's own peak-to-peak level, so a silent noise input stays silent.",
            "At 100 percent feedback the delay path never decays; the level keeps building while its input plays.",
        ],
        "evidence": [SRC.format("ManagedMultiIOEffect"), PROFILE + " - 0 dB with only the clean input connected"],
    },
}

# ---- Rout 909 (curated before; the closed-source drum machine: Explain text only, a neutral summary) -------------------
_DRUM = {"BD": "bass drum", "SD": "snare", "LT": "low tom", "MT": "mid tom", "HT": "high tom", "RS": "rim shot", "CP": "hand clap",
         "CH": "closed hi-hat", "OH": "open hi-hat", "CC": "crash cymbal", "RC": "ride cymbal"}
_TUNE = {"BD Tune": "kick", "SD Tune": "snare", "LT Tune": "low tom", "MT Tune": "mid tom", "HT Tune": "high tom", "CC Tune": "crash", "RC Tune": "ride"}
ROUT = {}
for _i, _k in enumerate(_DRUM):
    ROUT[f"p{_i}"] = (f"Sets the level of the {_DRUM[_k]}, 0 to 198 percent (100 percent at raw 128, the default).",
                      f"Louder or quieter {_DRUM[_k]} against the other drums; above 100 percent it can overload the mix.")
_V = [("BD Tune", "pitch of the bass drum", "A lower value is a deeper thud, a higher one a tighter, higher kick."),
      ("BD Attack", "click at the start of the bass drum", "A higher value gives a harder, clicking beater; lower is rounder."),
      ("BD Decay", "decay of the bass drum", "A higher value rings longer; lower is a short punch."),
      ("SD Tune", "pitch of the snare", "Raises or lowers the snare's body tone."),
      ("SD Tone", "tone of the snare", "Moves the snare between a darker and a brighter character."),
      ("SD Snap", "amount of snappy noise in the snare", "A higher value adds more rattle and bite; lower is a duller drum."),
      ("LT Tune", "pitch of the low tom", "Raises or lowers the low tom."),
      ("LT Decay", "decay of the low tom", "Longer values ring out; shorter are dry thumps."),
      ("MT Tune", "pitch of the mid tom", "Raises or lowers the mid tom."),
      ("MT Decay", "decay of the mid tom", "Longer values ring out; shorter are dry thumps."),
      ("HT Tune", "pitch of the high tom", "Raises or lowers the high tom."),
      ("HT Decay", "decay of the high tom", "Longer values ring out; shorter are dry thumps."),
      ("CH Decay", "decay of the closed hi-hat", "A higher value lets the closed hat ring longer; lower is a tight tick."),
      ("OH Decay", "decay of the open hi-hat", "A higher value is a longer open hat; lower chokes it short."),
      ("CC Tune", "pitch of the crash cymbal", "Shifts the crash brighter or darker."),
      ("RC Tune", "pitch of the ride cymbal", "Shifts the ride brighter or darker.")]
for _j, (_n, _what, _snd) in enumerate(_V):
    ROUT[f"p{11 + _j}"] = (f"Sets the {_what}, 0 to 63 (32 by default; the machine's texts give no unit).", _snd)
ROUT["p27"] = ("Delays every other step by 0 to 0.5 tick (about 0.25 tick at raw 128); 0 is off, the default.",
               "A little delay gives the pattern a swung, shuffled feel; none leaves it straight.")
ROUT["p28"] = ("Chooses which steps the shuffle delays, from every 2nd to every 32nd step, 2 by default.",
               "Wider spacing swings only some of the steps, so the shuffle is felt over longer groups.")
EXPLAIN["Rout 909"] = ROUT
CURATE["Rout 909"] = {
    "new_summary": "Analog-style drum machine (bass drum, snare, toms, rim shot, clap, hi-hats, crash and ride) with per-voice level, tune and decay, and shuffle.",
    "add_quirks": ["Neo Drums is its successor: machine_replace carries the eleven drums over (see the page)."],
    "add_evidence": [PROFILE + " - re-measured in K2 batch d"],
}
