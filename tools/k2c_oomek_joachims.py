# Fork (agent-knowledge.md K2 batch c): the three Oomek and the seven Joachims machines' Explain text and curation
# (see tools/k2c_knowledge.py). Keys are positions: "p3" the fourth global parameter.

OBT = "ExternalMachines/source/buzztrax-buzzmachines/Oomek/"
PROFILE = "profile (tools/machine-profiler.py, windows 2026-11-14)"
OWN = "its parameter descriptions and value texts (machine_describe, as profiled)"
HTML = "RunTime/Gear/Effects/"
CLOSED = "Closed source: names and behaviour come from its own texts and documentation page and from measurements, not from code."
PCT = {"a": 0.0, "b": 100.0}     # the texts truncate to a whole or tenth percent: the formula is exact

# ---- Joachims Hyperion: 30 bands -------------------------------------------------------------------------------------
HYP_HZ = [20000, 15800, 12400, 9800, 7700, 6100, 4800, 3800, 2970, 2340, 1850, 1460, 1150, 900, 710, 560, 440, 350, 275, 217, 171, 134, 106, 84, 66, 52, 41, 32, 25, 20]


def hyp_sound(f):
    if f <= 100:
        return "Boosting adds sub-bass weight and rumble; cutting cleans out low mud."
    if f <= 400:
        return "Boosting adds body and warmth; cutting thins the sound and clears boominess."
    if f <= 3000:
        return "Boosting brings the sound forward and makes it more vocal or nasal; cutting hollows it out."
    if f <= 8000:
        return "Boosting adds bite and clarity; cutting softens harshness."
    return "Boosting adds air and sparkle; cutting dulls the top."


def hyp_texts():
    t = {"p0": ("Switches the whole equaliser on or off.", "Off leaves the sound untouched, so you can compare before and after."),
         "p1": ("Raises or lowers the level into the bands: a table from -118 dB to +7.6 dB, 0 dB at 95.",
                "Use it to make room for boosts or to level-match after cuts."),
         "p32": ("Sets a 12 dB per octave high-pass that removes subsonic rumble and DC; raw 0 (the default) is off, and the corner sits about 1.28 times the number its text shows.",
                 "Higher values thin out the bottom; use a little to clean rumble without changing the tone."),
         "p33": ("Sets how wide each band is, 16 steps from 0.2 to 2.3 as its text shows (measured: 0.37 to 4.5 octaves between half-gain points at the low end, twice the number shown).",
                 "Wide bands overlap and sound smooth but add up; narrow ones leave gaps and act more like a surgical graphic equaliser.")}
    for i, f in enumerate(HYP_HZ):
        t[f"p{i + 2}"] = (f"Boosts or cuts the band around {f} Hz by up to 18 dB in 254 steps; the middle step is 0 dB and switches the band off to save CPU.", hyp_sound(f))
    return t


def hyp_params():
    p = {"p0": ("Enable", ""), "p1": ("Input gain", "gain.input", {"add_points": [(0, "-inf")]}), "p32": ("Highpass", "filter.highpass", {"add_points": [(0, 0.0)]}), "p33": ("Band width", "filter.bandwidth")}
    for i, f in enumerate(HYP_HZ):
        p[f"p{i + 2}"] = (f"{f} Hz band", "", {"a": -18.0, "b": 18.0, "description": f"Gain of the band around {f} Hz, -18 to +18 dB (the middle step is 0 dB and switches the band off)."})
    return p


# ---- Joachims Enceladus Tube EQ: three bands --------------------------------------------------------------------------
def tube_band(n):
    pre = f"p{(n - 1) * 5}"
    return {
        f"p{(n - 1) * 5}": (f"Sets band {n}'s boost or cut from -16 dB to +16 dB in 64 steps; the middle step is 0 dB.",
                           "Boost to bring the band forward and add tube warmth when Drive is up; cut to take it out."),
        f"p{(n - 1) * 5 + 1}": (f"Sets how much tube-style overdrive band {n} adds, 0 to 100 percent.",
                               "More drive adds soft, even harmonics and thickness to the band; at 0 it is a clean equaliser."),
        f"p{(n - 1) * 5 + 2}": (f"Sets band {n}'s centre frequency, about 20 Hz to 20 kHz on an exponential scale.",
                               "Move it to the part of the spectrum you want to colour."),
        f"p{(n - 1) * 5 + 3}": (f"Sets band {n}'s width, about 0.1 to 5 octaves.",
                               "Narrow bands are surgical; wide ones are broad tone shaping."),
        f"p{(n - 1) * 5 + 4}": (f"Chooses band {n}'s circuit model: transistors, tubes I or tubes II, each plain or warm.",
                               "Transistors are tighter, the tube models rounder and more coloured, and warm versions add more saturation."),
    }


def tube_texts():
    t = {}
    for n in (1, 2, 3):
        t.update(tube_band(n))
    return t


def tube_params():
    p = {}
    for n in (1, 2, 3):
        b = (n - 1) * 5
        p[f"p{b}"] = (f"Band {n} boost", "", {"description": f"Band {n}'s boost or cut, -16 to +16 dB (its own text says band 1 for every band)."})
        p[f"p{b + 1}"] = (f"Band {n} drive", "", {**PCT, "description": f"Tube overdrive of band {n}, 0 to 100 percent (its own text says band 1)."})
        p[f"p{b + 2}"] = (f"Band {n} frequency", "", {"description": f"Centre frequency of band {n}, 19.6 Hz to 20 kHz (its own text says band 1)."})
        p[f"p{b + 3}"] = (f"Band {n} width", "", {"description": f"Width of band {n}, 0.1 to 5 octaves (its own text says band 1)."})
        p[f"p{b + 4}"] = (f"Band {n} mode", "", {"description": f"Circuit model of band {n} (its own text says band 1)."})
    return p


# ---- Joachims Jupiter 3 ------------------------------------------------------------------------------------------------
def jup_band(base, name, low):
    return {
        f"p{base}": (f"Sets the level above which the {name} band is compressed, a table from -100 dB to 0 dB; 0 dB (the default) compresses nothing.",
                    "Lower it until the band starts to be squeezed; louder passages are then held down toward the threshold."),
        f"p{base + 1}": (f"Sets how fast the {name} compressor reacts, about 1 ms to 0.6 s.",
                        "Short values catch transients and can click; long ones let them through and keep the punch."),
        f"p{base + 2}": (f"Sets how fast the {name} compressor lets go, about 1 ms to 7.8 s.",
                        "Short values give a pumping, lively sound; long ones a smooth, sustained level."),
        f"p{base + 3}": (f"Sets the {name} band's output level, a table from -100 dB to +15.5 dB (0 dB at 71).",
                        "Use it to rebalance the three bands after compressing."),
        f"p{base + 4}": (f"Adds an asymmetric tube-like saturation to the {name} band: -100 to +100 percent, 0 (at 64) switches it off.",
                        "More adds a warm, fat edge; its own text says it is a simple valve simulator."),
    }


def jup_texts():
    t = {"p0": ("Raises or lowers the level into the three bands: a table from -100 dB to +15.5 dB, 0 dB at 71.", "Use it to drive the compressors harder or to make room."),
         "p1": ("Sets where the low and mid bands meet, 0 to 100 percent of its scale; its own text says the frequency also depends on Steepness and FFT size, so use your ears.",
                "Lower values make the low band narrower; higher ones give it more of the body."),
         "p2": ("Sets where the mid and high bands meet, 0 to 100 percent of its scale (depends on Steepness and FFT size).",
                "Lower values give the high band more of the mids; higher ones keep it to the very top."),
         "p3": ("Sets how sharp the band-splitting filters are, 0 to 100 percent.",
                "Steeper splits separate the bands cleanly but can ring; gentler ones blend more smoothly."),
         "p4": ("Sets the low band's stereo width from 0 to 200 percent (100 at 64).",
                "Lower values narrow the bass toward the centre, as is usual on masters; higher ones widen it."),
         "p5": ("Chooses what you hear: bypass, enabled, or only the low, mid or high band soloed.",
                "Solo a band to set its compressor; bypass switches the whole machine out."),
         "p6": ("Sets one ratio for all three compressors, from 1:1 to 30:1 (30:1 by default); its own text says it works a little like a wet and dry mix.",
                "Higher ratios squeeze more; 1:1 does nothing."),
         "p22": ("Chooses the final limiter: off or one of four characters (smooth, boing, rusty, old).",
                 "The limiter catches peaks the compressors let through; each character has its own feel and distortion."),
         "p23": ("Sets the limiter's release time or shape, a table from -100 dB to +15.5 dB (0 dB at 71; its own text says it is release time or shape).",
                 "It changes how the limiter lets go after a peak; unverified what the dB figures mean here."),
         "p24": ("Chooses the size of the analysis, 1,024 to 65,536 samples (32,768 by default), which is also the delay it adds.",
                 "Bigger sizes split the bands more cleanly and delay the sound longer; at the default it is about 0.74 s at 44.1 kHz.")}
    t.update(jup_band(7, "low", True))
    t.update(jup_band(12, "mid", False))
    t.update(jup_band(17, "high", False))
    return t


def jup_params():
    p = {"p0": ("Input gain", "gain.input"), "p1": ("Low/mid split", "dyn.crossover", {**PCT, "unit": "%", "description": "Split between the low and mid bands, 0 to 100 percent of its scale (not Hz: it depends on Steepness and FFT size)."}),
         "p2": ("Mid/high split", "dyn.crossover", {**PCT, "unit": "%", "description": "Split between the mid and high bands, 0 to 100 percent of its scale (not Hz)."}),
         "p3": ("Split steepness", "", PCT), "p4": ("Low stereo width", "width", {"a": 0.0, "b": 200.0}),
         "p5": ("Listen to", "solo"), "p6": ("Ratio", "dyn.ratio"), "p22": ("Limiter mode", ""), "p23": ("Limiter adjust", ""), "p24": ("FFT size", "")}
    for base, nm in ((7, "Low"), (12, "Mid"), (17, "High")):
        p[f"p{base}"] = (f"{nm} threshold", "dyn.threshold")
        p[f"p{base + 1}"] = (f"{nm} attack", "dyn.attack")
        p[f"p{base + 2}"] = (f"{nm} release", "dyn.release")
        p[f"p{base + 3}"] = (f"{nm} output", "dyn.makeup")
        d = {"description": f"Asymmetric tube-like saturation of the {nm.lower()} band, -100 to +100 percent (0 at 64)."}
        if base != 7:
            d["description"] += " Its own text says low."
        p[f"p{base + 4}"] = (f"{nm} beefyness", "", d)
    return p


EXPLAIN = {
    "Oomek Exciter": {
        "p0": ("Sets the gain into the shaper, 0.008 times to 2 times (1 times at 120).",
               "More drive creates more harmonics from the high-passed signal; too much sounds fizzy."),
        "p1": ("Sets a high-pass before the shaper, about 30 Hz to 7 kHz (its own estimate at 44.1 kHz).",
               "Higher values send only the top of the sound to be excited, so the added harmonics sit higher and the bass is left alone."),
        "p2": ("Sets a high-pass after the shaper, about 30 Hz to 7 kHz (its own estimate at 44.1 kHz).",
               "Higher values remove the shaper's lows so only bright harmonics are added back."),
        "p3": ("Sets the level of the excited signal added to the dry sound, from silence to +6 dB.",
               "More amount adds more sparkle and presence; the output grows louder as it rises."),
    },
    "Oomek Masterizer": {
        "p0": ("Sets the gain into the limiters, from silence to +24 dB (a square-law scale).",
               "Raising it drives the limiters harder for more loudness; the output rises until the thresholds hold it."),
        "p1": ("Sets the low band's limit, from silence to +6 dB.",
               "Lower values limit the bass harder and keep it tight; at the top it hardly acts."),
        "p2": ("Sets the mid band's limit, from silence to +6 dB.",
               "Lower values hold the mids down; they decide how forward the body of the mix sounds."),
        "p3": ("Sets the high band's limit, from silence to +6 dB.",
               "Lower values hold the top down and tame harsh or sizzling highs."),
        "p4": ("Sets how long the low band's limiter takes to let go, 40 to 960 ms.",
               "Short values pump with the bass; long ones give a steadier, smoother low end."),
        "p5": ("Sets how long the mid band's limiter takes to let go, 40 to 960 ms.",
               "Short values sound more active; long ones are smoother."),
        "p6": ("Sets how long the high band's limiter takes to let go, 40 to 960 ms.",
               "Short values keep the top lively; long ones are smoother."),
        "p7": ("Sets the crossover between the low and mid bands, about 16 to 820 Hz (its own estimate at 44.1 kHz).",
               "Lower values give the low band only the deepest bass; higher ones put more body in it."),
        "p8": ("Sets the crossover between the mid and high bands, about 1 to 8 kHz (its own estimate at 44.1 kHz).",
               "Lower values give the high band more of the mids; higher ones keep it to the very top."),
        "p9": ("Sets the limiters' knee from 0 (soft) to 32 (hard).",
               "A soft knee starts limiting gently and sounds more natural; a hard one is stricter and more obvious."),
    },
    "Oomek Aggressor 303": {
        "p0": ("Chooses the oscillator wave: saw or square.",
               "The saw is bright and buzzy; the square is hollow and rounder, the classic two acid timbres."),
        "p1": ("Sets the low-pass filter's cutoff before the envelope, 0 to 240 (square-law inside).",
               "Low values are dark and muffled; high values open the sound. The envelope adds to it on every note."),
        "p2": ("Sets the filter's resonance, 0 to 128.",
               "More resonance gives the squelchy, vowel-like acid peak; the top makes it whistle."),
        "p3": ("Sets how far the envelope opens the filter on each note, 0 to 100 percent.",
               "More gives a stronger squelch sweep on every note; 0 leaves the filter where Cutoff puts it."),
        "p4": ("Sets how fast the filter envelope falls, 0 to 128.",
               "Short decays give plucky, bubbling notes; long ones give open, sweeping ones."),
        "p5": ("Sets how strongly an accented note opens the filter and raises the level, 0 to 200 percent.",
               "Higher values make accented notes jump out; 0 makes the accent do nothing."),
        "p6": ("Tunes the oscillator, plus or minus 100 cents.",
               "Small values detune against other instruments; use it to tune the bass to the song."),
        "p7": ("Sets the output level, 0 to 200 percent (100 is unity).",
               "Above 100 percent it gets hot and can pass full scale."),
    },
    "Joachims Chopper 2": {
        "p0": ("Sets the mix between the dry signal and the chopped signal, 0 to 100 percent.",
               "At 100 percent you hear only the gaps; lower values blend the untouched sound back in."),
        "p1": ("Writing a value fires a gap; large values keep the sound open for a whole tick and shorter ones decay faster along a logarithmic curve (the steps are labelled short).",
               "Each write is one chop: write it on beats to make rhythmic gating and stutter."),
        "p2": ("Holds a customisable control that defaults to the gap's pan, -100 to +100 percent in steps of about 1.6 (what it controls is set with command 10).",
               "As pan it places each chop in the stereo field, which makes a stuttering ping-pong."),
        "p3": ("Sets the centre of the band-pass filter on the current gap, about 40 Hz to 10 kHz on an exponential scale; no effect while Bandwidth is at its maximum (off).",
               "Different values per gap make each chop sound at a different pitch or brightness."),
        "p4": ("Sets the band-pass filter's width, 1 to 128: the smaller, the narrower; 128 (the default) bypasses it.",
               "Narrow bands give thin, pitched blips; wide ones keep the full sound."),
        "p5": ("Sets the fade-in time of a chop, 0 to 50 ms.",
               "Short values keep the click of a hard gate; longer ones round it off but make the attack dull."),
        "p6": ("Plays sound from earlier in time instead of the current sound: 0 to 128 ticks back.",
               "It makes a chop repeat something that already played, which gives glitchy rearrangements."),
        "p7": ("Chooses a command, 1 to 255: reset or randomise the internal random pattern, set filter steepness, set how much the pattern affects decay, filter or pan, set mutation speed, choose gate or ducking mode, or choose what Assignable controls.",
               "Commands change how the machine behaves from then on; the next parameter is their value."),
        "p8": ("Holds the value of the current or last command, 0 to 65,534.",
               "It has no sound of its own; it sets the amount or seed that the command uses."),
    },
    "Joachims Enceladus Tube EQ v1": tube_texts(),
    "Joachims Hyperion": hyp_texts(),
    "Joachims Jupiter 3": jup_texts(),
    "Joachims Mars filter 2": {
        "p0": ("Chooses one of 13 distortion characters, or 0 for bypass: soft and round, tube-like, or ring-mod and FM mangling.",
               "Each character has its own harmonics and aggression; 0 passes the sound so you can compare."),
        "p1": ("Sets the level into the distortion, a table from -100 dB to +15.5 dB (0 dB at 71).",
               "Higher values drive it harder and make it louder and denser; the top can pass full scale."),
        "p2": ("Sets how intense the distortion is, 0 to 127; its meaning shifts a little per mode.",
               "More amount means more harmonics and harshness; in some modes it acts like PreGain."),
        "p3": ("Attenuates the treble after the distortion on a 0 to 127 scale; 127 (the top, its default) turns it off.",
               "Lower values roll off the harsh top and make the fuzz warmer."),
        "p4": ("Takes the bass out of the distortion on a 0 to 127 scale; 127 (the top, its default) turns it off.",
               "Lower values keep the bass cleaner and tighter while the rest is distorted."),
    },
    "Joachims Neptune 1.2": {
        "p0": ("Sets the level below which the spectrum is ignored, a table from -100 dB to +15.5 dB (0 dB at 71); the lowest step diverges.",
               "Raising it removes quiet content and noise; keep it above about -97 dB."),
        "p1": ("Shifts the pitch, -127 to +127 steps around 0 at 128 (its own text calls it a bad and messed up shifter).",
               "Expect a rough, metallic, robotic shift rather than a clean one."),
        "p2": ("Boosts or cuts frequency bands by their strength, -100 to +100 percent (0 at 127).",
               "Positive values exaggerate strong bands for a harsh, vocoded sound; negative values do the reverse; both are loud."),
        "p3": ("Clips frequency bands, 0 to 100 percent.",
               "More adds a gritty, distorted digital character to the spectrum."),
        "p4": ("Mutes weak frequency bands, 0 to 100 percent.",
               "More strips away the quiet parts of the spectrum and leaves a thin, ghostly sound."),
        "p5": ("Randomises the phase information, 0 to 100 percent.",
               "More makes the sound smeared and noisy, like a whisper of the original."),
        "p6": ("Sets the size of the analysis, 128 to 32,768 samples (8,192 by default), which is also the delay it adds.",
               "Bigger sizes are finer in pitch but smear transients and delay the sound more."),
    },
    "Joachims Saturn 1.4": {
        "p0": ("Tilts the tone at the start of the chain, -127 to +127 (0 at 127; soft to sharp).",
               "Negative values soften and darken; positive ones sharpen; the extremes get very loud."),
        "p1": ("Chooses a type of noise to add, 0 to 15 (what each number is was not identified).",
               "Different numbers give different noise textures; check them by ear."),
        "p2": ("Sets how much of the chosen noise is added, a table from silence to 0 dB.",
               "A little adds vinyl crackle or hiss, like an old record; full is very loud."),
        "p3": ("Sets the level of added room reverb, a table from silence to 0 dB.",
               "A little makes the sound feel recorded at a distance, which suits retro tones."),
        "p4": ("Sets the size of that room, 0 to 127 (30 by default); it also fades the noise out.",
               "Larger rooms give a longer, more distant tail."),
        "p5": ("Limits the bit depth: off, 8, 4, 2 or 1 bit.",
               "Lower depths add steps and hiss in the style of old digital gear; 1 bit is a harsh buzz."),
        "p6": ("Lowers the sample rate, about 410 Hz to 22 kHz, with no anti-alias filter.",
               "Lower values add aliasing and a gritty, dull, lo-fi sound."),
        "p7": ("Changes the frequency response in a way that sounds like old, rusty gear, 0 to 127.",
               "More makes the sound thinner and more telephone-like, with its level kept up."),
        "p8": ("Sets how much the pitch wobbles, 0 to 127.",
               "More gives the pitch drift of a worn record or tape."),
        "p9": ("Sets how fast the pitch wobbles, 1 to 127 (its own text repeats the amount's description).",
               "Low values drift slowly; high values warble."),
        "p10": ("Sets the ring modulator's frequency, 20 Hz to 22 kHz as a table.",
                "Lower values give a tremolo; higher ones add metallic, bell-like sidebands."),
        "p11": ("Sets the mix of the ring modulation, 0 to 100 percent.",
                "More gives a more robotic, metallic sound; 0 leaves it off."),
        "p12": ("Sets the overdrive's amount, 0 to 127.",
                "More adds a gradual, retro-gear style saturation."),
        "p13": ("Sets the overdrive's shape, -127 to +127 (0 at 127); its own text says 0 gives transistor-like clipping and other values tube-like.",
                "It changes the harmonic character of the drive between harder and softer clipping."),
        "p14": ("Tilts the tone at the end of the chain, -127 to +127 (0 at 127).",
                "Negative values soften and darken; positive ones sharpen; the extremes get very loud."),
        "p15": ("Sets a 12 dB per octave high-pass, 5 Hz to 200 Hz (about 24 raw gives 40 Hz); it is also a DC remover.",
                "Higher values thin the bass; low ones only remove subsonic rumble."),
    },
}

CURATE = {
    "Joachims Chopper 2": {
        "type": "gate",
        "summary": "Gapper: cuts the sound into rhythmic chops fired by writing Trigger, with a band-pass filter on each gap, a de-clicking attack, a time-travel control that plays earlier sound, an internal random pattern and commands to control it. Version 2.2. For stutter and rhythmic gating.",
        "params": {"p0": ("Dry/wet mix", "mix.wet", {**PCT, "unit": "%", "description": "Mix between the dry signal and the chopped signal, 0 to 100 percent."}), "p1": ("Gap trigger", ""), "p2": ("Assignable (pan)", "pan"),
                   "p3": ("Gap filter frequency", "filter.cutoff"), "p4": ("Gap filter bandwidth", "filter.bandwidth"), "p5": ("Attack", "env.attack"),
                   "p6": ("Travel back", ""), "p7": ("Command", ""), "p8": ("Command value", "")},
        "quirks": [
            "A played machine: nothing chops until Trigger is written (a pattern column, so use automation_write); at the defaults the profile measured -0.8 dB, so it passes the sound.",
            "Command and Value work as a pair: Command picks what to change (1 reset the random pattern, 5 filter steepness, 11 gate or ducking mode, 16 what Assignable does) and Value is its amount.",
            "Attributes Rows per beat (leave at 4 unless the pattern editor differs) and Filter steepness are set on the machine.",
            "Unverified: the Trigger step labels read 'short' for every value, so the decay each step gives was not measured.",
            CLOSED,
        ],
        "evidence": [HTML + "Joachims Chopper 2.html - its author's documentation page (the parameters, commands and attributes)", OWN, PROFILE + " - -0.8 dB at the defaults"],
    },
    "Joachims Enceladus Tube EQ v1": {
        "type": "EQ",
        "summary": "Three-band equaliser with tube-style drive: each band has a boost or cut of plus or minus 16 dB, an overdrive amount, a centre frequency, a width and one of six circuit models (transistors, tubes I, tubes II, each plain or warm). For warm, coloured tone shaping.",
        "params": tube_params(),
        "quirks": [
            "Bands 2 and 3 carry band 1's descriptions in the machine's own texts (a copy mistake in the machine); the names Band 1, 2, 3 here follow their order.",
            "The bands default to 0 dB boost, 25 percent drive and centres near 60 Hz, 630 Hz and 6.3 kHz (read from the measured mapping).",
            "Boost at its maximum is hot: +5.0 dBFS peak for band 3 at +16 dB on the profile's signal.",
            "Level is 0 dB at the defaults; stereo.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " - 0 dB at the defaults, band 3's boost hot at its top"],
    },
    "Joachims Hyperion": {
        "type": "EQ",
        "summary": "Thirty-band graphic equaliser with a band every third of an octave from 20 Hz to 20 kHz, plus or minus 18 dB each, a high-pass and a Bandwidth control for how wide the bands are; bands at 0 dB are switched off to save CPU. Neo EQ in graphic mode replaces it.",
        "params": hyp_params(),
        "quirks": [
            "Bands do not compensate each other's overlap: at the default Bandwidth every band at +6 dB makes +43 dB at 3.2 kHz (measured for its successor mapping), so raise bands gently and lower Input gain.",
            "Bandwidth is the octaves between a band's half-gain points at low frequencies, twice the number its text shows (1.0 at the default is 2.0 octaves); Highpass is a second-order Butterworth filter whose corner is about 1.28 times the number shown.",
            "Hot: single bands at +18 dB peak above full scale (+3.9 dBFS and more in the profile); every parameter at its top at once diverges. Unverified: the text at Input gain raw 0 was not a number in the profile, so it is entered as silence.",
            "Stereo, 0 dB at its defaults; its high bands move with the sample rate (measured at 44.1 kHz).",
            "Enable is on by default; off bypasses the equaliser.",
        ],
        "evidence": ["Machines/Manifests/Successors/Joachims Hyperion to Neo EQ.successor.json - its measurements on windows (the bandwidth and high-pass tables, the overlap and the sample-rate remarks)",
                     HTML + "Joachims Hyperion.html - its author's page (bands, bandwidth, high-pass)", OWN, PROFILE + " - 0 dB at the defaults, hot bands"],
    },
    "Joachims Jupiter 3": {
        "type": "dynamics",
        "summary": "Three-band compressor and limiter for the master: a phase-correct split into low, mid and high bands, a threshold, attack, release, output and tube-like saturation per band, one shared ratio, a narrower low band and a final limiter with four characters. Adds a long delay (the FFT size). Last effect before the output.",
        "params": jup_params(),
        "quirks": [
            "It delays the sound by its FFT size, 32,768 samples (about 0.74 s) by default: the profile's short test window caught only silence, so its level and sound at the defaults are unverified. Its own page says to place it last before the master and not to use it for low latency.",
            "At its defaults the thresholds are at 0 dB, so nothing is compressed until a threshold is lowered.",
            "Mid and high Output and Beefyness carry the low band's texts in the machine (copy mistakes); the Split values are percent of an internal scale, not Hz.",
            "Listen to defaults to Enabled; set it to Bypass to switch it out and to LOW, MID or HIGH to solo a band while setting it.",
            CLOSED,
        ],
        "evidence": [HTML + "Joachims Jupiter 3.html - its author's page (bands, delay, limiter, usage notes)", OWN, PROFILE + " - silent at the defaults (the delay)"],
    },
    "Joachims Neptune 1.2": {
        "type": "spectral",
        "summary": "Spectral effects through an FFT: a rough pitch shifter, boosting and clipping of frequency bands, a cleaner that mutes weak bands and a phase scrambler, with a volume threshold and a selectable FFT size. Adds latency equal to the FFT size. For glitchy, vocoded and ghostly sounds.",
        "params": {"p0": ("Threshold", "dyn.threshold"), "p1": ("Pitch shift", ""), "p2": ("Spectral boost", "fx.amount", {"a": -100.0, "b": 100.0}),
                   "p3": ("Spectral distortion", "", PCT), "p4": ("Spectral clean", "", PCT), "p5": ("Phase scramble", "", PCT), "p6": ("FFT size", "")},
        "quirks": [
            "Latency: 8,191 samples at the default FFT size of 8,192 (profile), and it grows with the FFT size.",
            "Threshold at its lowest step (-100 dB) diverges; the safe range is -97.7 dB to +15.5 dB (raw 2 to 128).",
            "Spectral boost is hot at both ends: +9.6 and +10.0 dBFS peak in the profile.",
            "Its pitch shifter is, in its own words, bad and messed up: rough by design.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " - 8,191 samples latency, 0.18 s tail, divergence at Threshold's minimum, hot Spectral boost"],
    },
    "Joachims Saturn 1.4": {
        "type": "distortion",
        "summary": "All-purpose vintage degrader: input and output tone tilt, record noise, a small room, bit-depth and sample-rate reduction, an antique frequency response, pitch wobble, ring modulation, tube-style overdrive and a high-pass. For lo-fi character.",
        "params": {"p0": ("Input tilt", ""), "p1": ("Noise type", ""), "p2": ("Noise amount", "", {"add_points": [(0, "-inf")]}), "p3": ("Room level", "mix.wet", {"add_points": [(0, "-inf")]}), "p4": ("Room size", "reverb.size"),
                   "p5": ("Bit depth", ""), "p6": ("Sample rate", ""), "p7": ("Antique", ""), "p8": ("Detune amount", ""), "p9": ("Detune speed", ""),
                   "p10": ("Ring mod frequency", ""), "p11": ("Ring mod mix", "", PCT), "p12": ("Tube drive", "drive"), "p13": ("Tube shape", ""),
                   "p14": ("Output tilt", ""), "p15": ("Highpass", "filter.highpass", {"a": 5.0, "b": 200.0})},
        "quirks": [
            "Noise needs its sample files (Joachims Saturn 1.4.raw and 01 to 06 beside the DLL); the package ships them.",
            "The two tilt controls are hot at their top: +18.7 dBFS peak in the profile; Noise amount at its top is +1.8 dBFS.",
            "Unverified: what each Noise type is (its page speaks of vinyl scratches at 33 rpm and says the noise is optimised for a 96 kHz sample rate) and the Detune speed text repeats the amount's.",
            "Stereo, 0 dB at its defaults; a tail of 0.05 s from the room.",
            CLOSED,
        ],
        "evidence": [HTML + "Joachims Saturn 1.4.html - its author's page (each stage described)", OWN, PROFILE + " - 0 dB at the defaults, hot at the tilts' tops"],
    },
}
