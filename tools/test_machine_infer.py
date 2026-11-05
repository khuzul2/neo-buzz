# Fork (docs/specs/mcp-server.md §12.2, U1): unit tests for tools/machine_infer.py, fed value
# tables whose meaning is known; U2 (docs/specs/machine-safety.md §3.1): the health categories
# (diverging, hot, needs a window), which effects get swept and the safe range's edge; U2 in depth
# (mcp-server.md §12.12): tools/machine_roles.py's measurements on synthetic signals, the suggested
# roles from features, the harmonic sieve and the hot ends that get a range. U6 (§12.13): the manifests generated
# from the machines' source (tools/source_manifests.py) are what their source generates, and its fits are exact.
# Run: python3 -m unittest discover -s tools -p "test_machine_infer.py"

import math
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import machine_infer as mi  # noqa: E402
import machine_roles as mr  # noqa: E402
import source_manifests as sm  # noqa: E402


def table(fmt, lo, hi, f, step=1):
    return [[r, fmt(f(r))] for r in range(lo, hi + 1, step)]


def param(name, rows, description=""):
    return {"name": name, "description": description, "table": rows}


class ParseTests(unittest.TestCase):
    def test_units_and_prefixes(self):
        self.assertEqual(mi.parse("800 Hz")[:2], (800.0, "Hz"))
        self.assertEqual(mi.parse("1.25 kHz")[:2], (1250.0, "Hz"))
        self.assertEqual(mi.parse("250 ms")[:2], (0.25, "s"))
        self.assertEqual(mi.parse("-6.0 dB")[:2], (-6.0, "dB"))
        self.assertEqual(mi.parse("−60.0 dBFS")[:2], (-60.0, "dB"))
        self.assertEqual(mi.parse("− 0.5 dBFS")[:2], (-0.5, "dB"))
        self.assertEqual(mi.parse("+98%")[:2], (98.0, "%"))
        self.assertEqual(mi.parse("4.5:1")[:2], (4.5, ":1"))

    def test_infinities_pan_and_tracker_hex(self):
        self.assertEqual(mi.parse("-inf. dB")[:2], (-math.inf, "dB"))
        self.assertEqual(mi.parse("L 50%")[:2], (-50.0, "%"))
        self.assertEqual(mi.parse("C")[:2], (0.0, "%"))
        self.assertEqual(mi.parse("0040  (-54.2 dB)")[:2], (-54.2, "dB"))
        self.assertEqual(mi.parse("0000  (silent)")[:2], (-math.inf, "dB"))

    def test_notes_in_brackets_are_numbers_but_names_are_not(self):
        self.assertEqual(mi.parse("0 ms (instant)")[:2], (0.0, "s"))
        self.assertIsNone(mi.parse("2 Bars"))
        self.assertIsNone(mi.parse("1: Linear"))
        self.assertIsNone(mi.parse("12 dB / FIR"))
        self.assertIsNone(mi.parse("Saw"))

    def test_resolution_follows_the_last_digit_and_prefix(self):
        self.assertAlmostEqual(mi._resolution(mi.parse("1.6 kHz")), 50.0)
        self.assertAlmostEqual(mi._resolution(mi.parse("-23.5 dB")), 0.05)


class MappingTests(unittest.TestCase):
    def test_linear_db(self):
        e = mi.infer(param("Gain", table(lambda v: f"{v:+.1f} dB", 0, 96, lambda r: -24 + r * 0.5)))
        self.assertEqual((e["kind"], e["unit"], e["confidence"]), ("db", "dB", "values"))
        self.assertAlmostEqual(e["a"], -24.0, places=3)
        self.assertAlmostEqual(e["b"], 24.0, places=3)

    def test_db_fader_with_minus_infinity_at_the_bottom(self):
        rows = [[0, "-inf dB"]] + table(lambda v: f"{v:.1f} dB", 1, 181, lambda r: -80 + (r - 1) * 0.5)
        e = mi.infer(param("Volume", rows))
        self.assertEqual((e["kind"], e.get("flag")), ("db", True))
        self.assertAlmostEqual(mi.value_at(e, 161, 0, 181), 0.0, places=2)
        self.assertEqual(mi.value_at(e, 0, 0, 181), -math.inf)

    def test_exponential_frequency_shown_in_hz_and_khz(self):
        f = lambda r: 20 * (1000.0 ** (r / 1000))
        fmt = lambda v: f"{v / 1000:.2f} kHz" if v >= 1000 else f"{v:.0f} Hz"
        e = mi.infer(param("Cutoff", table(fmt, 0, 1000, f, 1)))
        self.assertEqual((e["kind"], e["unit"]), ("freq", "Hz"))
        self.assertAlmostEqual(e["a"], 20, delta=0.5)
        self.assertAlmostEqual(e["b"], 20000, delta=200)

    def test_time_in_ms_becomes_seconds(self):
        e = mi.infer(param("Release", table(lambda v: f"{v * 1000:.1f} ms", 0, 127, lambda r: 0.005 * (400 ** (r / 127)))))
        self.assertEqual((e["kind"], e["unit"]), ("time", "s"))
        self.assertAlmostEqual(e["a"], 0.005, delta=0.0002)

    def test_power_curve(self):
        e = mi.infer(param("Glide", table(lambda v: f"{v:.3f} s", 0, 1000, lambda r: 2.0 * (r / 1000) ** 3)))
        self.assertEqual(e["kind"], "power")
        self.assertAlmostEqual(e["exponent"], 3.0, delta=0.05)

    def test_ratio_with_limit_on_top(self):
        rows = table(lambda v: f"{v:.2f}:1" if v < 10 else f"{v:.0f}:1", 0, 126, lambda r: 40 ** (r / 127)) + [[127, "Limit"]]
        e = mi.infer(param("Ratio", rows))
        self.assertEqual((e["kind"], e["unit"], e.get("flag")), ("ratio", ":1", True))

    def test_irregular_values_become_a_table(self):
        values = [0.3, 0.4, 0.5, 0.7, 1.0, 1.4, 2.0, 3.0, 4.5, 7.0, 10.0]
        e = mi.infer(param("Q", [[i, f"{v:.1f}"] for i, v in enumerate(values)]))
        self.assertEqual(e["kind"], "table")
        self.assertEqual(e["points"][0], [0, 0.3])
        self.assertEqual(e["points"][-1], [10, 10.0])

    def test_plain_numbers_are_linear_without_a_unit(self):
        e = mi.infer(param("VCF Reso", table(str, 0, 127, lambda r: r)))
        self.assertEqual((e["kind"], e["unit"], e["a"], e["b"]), ("linear", "", 0.0, 127.0))


class ChoiceTests(unittest.TestCase):
    def test_names_are_choices(self):
        e = mi.infer(param("Wave", [[0, "Saw"], [1, "Square"], [2, "Triangle"]]))
        self.assertEqual((e["kind"], e["choices"]), ("enum", ["Saw", "Square", "Triangle"]))

    def test_names_and_numbers_mixed_are_choices(self):
        e = mi.infer(param("HPF", [[0, "Off"], [1, "1"], [2, "2"], [3, "3"]]))
        self.assertEqual((e["kind"], e["choices"]), ("enum", ["Off", "1", "2", "3"]))

    def test_switches(self):
        self.assertEqual(mi.infer(param("Bypass", [[0, "Off"], [1, "On"]]))["kind"], "switch")
        self.assertEqual(mi.infer(param("Mute", [[0, "no"], [1, "yes"]]))["kind"], "switch")
        e = mi.infer(param("DCO Saw", [[0, "0"], [1, "1"]], "Saw waveform on/off"))
        self.assertEqual((e["kind"], e["choices"]), ("switch", ["Off", "On"]))

    def test_texts_that_say_nothing(self):
        self.assertIsNone(mi.infer(param("Char", [[r, "-"] for r in range(10)]))["kind"])


class DescriptionTests(unittest.TestCase):
    def test_a_range_with_its_curve(self):
        e = mi.infer(param("Env A", table(str, 0, 127, lambda r: r), "Attack time (1 ms – 4 s exponential)"))
        self.assertEqual((e["kind"], e["unit"], e["a"], e["b"], e["confidence"]), ("time", "s", 0.001, 4.0, "description"))

    def test_a_linear_range(self):
        e = mi.infer(param("DCO PW", table(str, 0, 127, lambda r: r), "Manual pulse width 50%–95%"))
        self.assertEqual((e["kind"], e["unit"], e["a"], e["b"]), ("linear", "%", 50.0, 95.0))

    def test_unit_stated_in_the_description_or_name(self):
        e = mi.infer(param("Delay", table(str, 5, 25, lambda r: r), "Base delay per voice in ms (5–25)"))
        self.assertEqual((e["kind"], e["unit"], e["a"], e["b"]), ("linear", "s", 0.005, 0.025))
        e = mi.infer(param("Attack ms", table(str, 0, 500, lambda r: r), "Time for gate to open fully"))
        self.assertEqual((e["unit"], e["b"]), ("s", 0.5))

    def test_db_anchors(self):
        e = mi.infer(param("Threshold", table(str, 0, 60, lambda r: r), "0 = 0 dBFS (no effect), 60 = −60 dBFS (maximum)"))
        self.assertEqual((e["kind"], e["a"], e["b"]), ("db", 0.0, -60.0))

    def test_anchors_that_contradict_the_texts_give_no_unit(self):
        e = mi.infer(param("Rate", table(str, 0, 100, lambda r: r), "Chorus LFO rate — 0=slow (0.1 Hz), 100=fast (5 Hz)"))
        self.assertEqual((e["kind"], e["unit"], e["confidence"]), ("linear", "", "values"))

    def test_anchors_in_another_unit_than_stated(self):
        e = mi.infer(param("Gain", table(str, 0, 200, lambda r: r), "Output level in percent (100 = unity / 0 dB, 200 = +6 dB)"))
        self.assertEqual((e["unit"], e["b"]), ("%", 200.0))

    def test_every_value_named(self):
        e = mi.infer(param("LFO Shape", table(str, 0, 4, lambda r: r), "0=Sine, 1=Triangle, 2=Square, 3=Saw, 4=Sample&Hold."))
        self.assertEqual((e["kind"], e["choices"]), ("enum", ["Sine", "Triangle", "Square", "Saw", "Sample&Hold"]))


class AgreementTests(unittest.TestCase):
    def test_agrees_with_an_exact_manifest_entry(self):
        rows = table(lambda v: f"{v:.0f} Hz", 0, 1000, lambda r: 20 * (1000.0 ** (r / 1000)), 10)
        e = mi.infer(param("Cutoff", rows))
        exact = {"min": 0, "max": 1000, "kind": "freq", "a": 20, "b": 20000}
        self.assertTrue(mi.agrees(e, exact, rows))
        self.assertFalse(mi.agrees({"kind": "linear", "a": 20, "b": 20000}, exact, rows))


def db(x):
    return 10 ** (x / 20)


class LevelTests(unittest.TestCase):
    """A level test's per-second peaks (linear) over its 4 s render, then over 30 s when it grew."""

    def test_steady_under_full_scale_is_fine(self):
        self.assertEqual(mi.level_verdict([db(-9)] * 4)[0], "ok")
        self.assertEqual(mi.level_verdict([0.0] * 4)[0], "silent")

    def test_loud_but_steady_is_hot(self):
        v, facts = mi.level_verdict([db(12)] * 4)
        self.assertEqual(v, "hot")
        self.assertAlmostEqual(facts["peak_dbfs"], 12.0, places=1)
        self.assertEqual(mi.level_verdict([db(59)] * 4)[0], "hot", "under +60 dBFS and steady")

    def test_nan_infinity_or_above_plus_60_diverges(self):
        v, facts = mi.level_verdict([db(-9)] * 4, nonfinite=True)
        self.assertEqual((v, facts["peak_dbfs"]), ("diverging", "inf"))
        self.assertEqual(mi.level_verdict([db(-9), db(70), db(70), db(70)])[0], "diverging", "above +60 dBFS, even steady")

    def test_a_level_that_keeps_growing_needs_a_longer_look(self):
        # The last second more than 6 dB over the second, still rising: a runaway or a long attack.
        self.assertEqual(mi.level_verdict([db(-30), db(-20), db(-10), db(0)])[0], "growing")
        self.assertEqual(mi.level_verdict([db(-30), db(-20), db(-17), db(-15)])[0], "ok", "5 dB: not growing")
        self.assertEqual(mi.level_verdict([db(-40), db(-9), db(-9), db(-9)])[0], "ok", "an onset in the first second isn't growth")
        self.assertEqual(mi.level_verdict([db(-20), db(-20), db(0), db(-6)])[0], "ok", "no longer rising at the end")

    def test_over_30_s_a_runaway_keeps_growing_and_an_attack_settles(self):
        runaway = [db(-30 + 1.5 * s) for s in range(30)]          # 1.5 dB a second, Jeskola Flanger's loop slowed down
        self.assertEqual(mi.growth_verdict(runaway)[0], "diverging")
        attack = [min(1.0, (s + 1) / 8) * db(-6) for s in range(30)]  # an 8 s linear attack to -6 dBFS
        self.assertEqual(mi.growth_verdict(attack)[0], "ok")
        hot_attack = [min(1.0, (s + 1) / 8) * db(6) for s in range(30)]
        self.assertEqual(mi.growth_verdict(hot_attack)[0], "hot")
        self.assertEqual(mi.growth_verdict([db(-9)] * 29 + [db(80)])[0], "diverging", "above +60 dBFS at the end")

    def test_a_lossless_loop_never_decays(self):
        # Feedback at ±100 %: noise piles up, its power growing with time (3 dB per doubling), for good.
        lossless = [db(-9) * ((s + 1) / 2) ** 0.5 for s in range(30)]
        self.assertTrue(mi.piling_up(*mi.level_verdict(lossless[:4])), "over 4 s it rose 2 dB or more")
        self.assertEqual(mi.growth_verdict(lossless)[0], "hot", "not diverging: hot")
        self.assertTrue(mi.never_decays(lossless))
        # Feedback at 98 %: it piles up at first, then settles.
        settling = [db(-9) * (1 - math.exp(-(s + 1) / 3)) ** 0.5 * 4 for s in range(30)]
        self.assertFalse(mi.never_decays(settling))
        self.assertFalse(mi.never_decays([db(-9)] * 30), "steady")
        self.assertFalse(mi.never_decays(lossless[:12]), "needs the 30 s render")

    def test_levels_stay_json(self):
        _, facts = mi.level_verdict([0.0] * 4)
        self.assertIsNone(facts["peak_dbfs"], "silence: null, not -Infinity")


class HealthTests(unittest.TestCase):
    def test_a_known_window_machine(self):
        self.assertIn("WPF", mi.needs_window("Info", "TargetInvocationException: Exception has been thrown by the target of an invocation."))

    def test_wpf_in_the_exception_needs_a_window(self):
        why = mi.needs_window("Some Notes", "machine_add failed inside ReBuzz: TargetInvocationException: … → "
                                            "System.InvalidOperationException: The calling thread must be STA, because many UI components require this.")
        self.assertIsNotNone(why)
        self.assertIsNotNone(mi.needs_window("X", "→ System.Windows.Markup.XamlParseException: 'Initialization of System.Windows.Controls.RichTextBox' threw"))

    def test_other_load_failures_are_problems(self):
        self.assertIsNone(mi.needs_window("Broken", "machine_add failed inside ReBuzz: DllNotFoundException: libfoo.so"))

    def test_status(self):
        self.assertEqual(mi.health_status(["feedback diverges at its maximum"], []), "unhealthy")
        self.assertEqual(mi.health_status([], ["hot at its defaults (+2.1 dBFS peak)"]), "warn")
        self.assertEqual(mi.health_status(["doesn't load"], [], window=True), "needs_window", "its own status, not unhealthy")
        self.assertEqual(mi.health_status([], []), "ok")

    def test_effects_swept_by_parameter_names(self):
        names = ["freq", "depth", "feedback", "min. delay", "Resonance", "LM Q", "Output Gain", "FB PingPong", "Mix", "Quality", "Regen"]
        self.assertEqual(mi.sweep_worthy(names), ["feedback", "Resonance", "LM Q", "Output Gain", "FB PingPong", "Regen"])


class EdgeTests(unittest.TestCase):
    def test_bisects_to_the_last_safe_value(self):
        tried = []

        def diverges(raw):
            tried.append(raw)
            return raw >= 120
        self.assertEqual(mi.edge(64, 128, diverges), 119)
        self.assertLessEqual(len(tried), 7, "about log2(64) tests")

    def test_towards_the_minimum(self):
        self.assertEqual(mi.edge(64, 0, lambda raw: raw <= 3), 4)

    def test_only_the_end_diverges(self):
        self.assertEqual(mi.edge(64, 128, lambda raw: raw >= 128), 127)
        self.assertEqual(mi.edge(64, 0, lambda raw: raw <= 0), 1)

    def test_next_to_the_default(self):
        self.assertEqual(mi.edge(64, 65, lambda raw: True), 64)


class RecurateTests(unittest.TestCase):
    """U3 (mcp-server.md §12.9): a new profile updates a curated manifest's measurements, never its curation."""

    curated = {
        "status": "curated", "summary": "A delay for echoes.", "quirks": ["Feedback is per track."],
        "health": {"status": "warn", "problems": ["old"]}, "profile": {"old": True},
        "parameters": [{"index": 0, "name": "Dry", "plain_name": "Dry signal", "min": 0, "max": 1}],
        "track_parameters": [
            {"index": 2, "name": "Feedback", "plain_name": "Echo feedback", "role": "delay.feedback", "min": 0, "max": 128,
             "safe_range": {"min": 1, "max": 128, "reason": "never decays at its minimum"}},
            {"index": 3, "name": "Wet", "plain_name": "Echo level", "min": 0, "max": 128},
        ],
    }

    def draft(self, **changes):
        d = {"status": "draft", "summary": "Delay (profiled draft).", "health": {"status": "ok", "problems": []}, "profile": {"new": True},
             "parameters": [{"index": 0, "name": "Dry", "min": 0, "max": 1}],
             "track_parameters": [{"index": 0, "name": "Feedback", "min": 0, "max": 128,
                                   "safe_range": {"min": 1, "max": 128, "reason": "never decays at its minimum"}},
                                  {"index": 0, "name": "Wet", "min": 0, "max": 128}]}
        d.update(changes)
        return d

    def test_keeps_the_curation_and_takes_the_measurements(self):
        m, warnings = mi.recurate(self.curated, self.draft())
        self.assertEqual(m["status"], "curated")
        self.assertEqual(m["summary"], "A delay for echoes.")
        self.assertEqual(m["track_parameters"][0]["plain_name"], "Echo feedback")
        self.assertEqual(m["profile"], {"new": True})
        self.assertEqual(m["health"]["status"], "ok")
        self.assertEqual(warnings, [])
        self.assertEqual(self.curated["profile"], {"old": True}, "the input is left alone")

    def test_safe_ranges_follow_the_profile_but_never_widen(self):
        d = self.draft()
        d["track_parameters"][0]["safe_range"] = {"min": 4, "max": 120, "reason": "diverges", "min_text": "-93.8%", "max_text": "87.5%"}
        d["track_parameters"][1]["safe_range"] = {"min": 0, "max": 100, "reason": "hot"}
        m, warnings = mi.recurate(self.curated, d)
        self.assertEqual((m["track_parameters"][0]["safe_range"]["min"], m["track_parameters"][0]["safe_range"]["max"]), (4, 120))
        self.assertEqual(m["track_parameters"][1]["safe_range"]["max"], 100, "a new range is added")
        self.assertEqual(len(warnings), 2)
        d = self.draft()
        del d["track_parameters"][0]["safe_range"]
        m, warnings = mi.recurate(self.curated, d)
        self.assertEqual(m["track_parameters"][0]["safe_range"]["min"], 1, "one the profile no longer finds stays, reported")
        self.assertIn("no longer finds", warnings[0])

    def test_a_curated_pitch_convention_stays_and_a_new_one_is_reported(self):
        c = dict(self.curated, notes={"input": "tracks", "pitched": True, "pitch_offset": 0})
        m, warnings = mi.recurate(c, self.draft(notes={"input": "tracks", "pitched": False}))
        self.assertTrue(m["notes"]["pitched"], "from its source, where the profile heard no pitch")
        self.assertEqual(warnings, ["notes: the profile now hears pitched False, curated True"])

    def test_reports_renamed_and_resized_parameters(self):
        d = self.draft(parameters=[{"index": 0, "name": "Dry thru", "min": 0, "max": 1}])
        d["track_parameters"][1]["max"] = 255
        _, warnings = mi.recurate(self.curated, d)
        self.assertTrue(any("Dry: not in the new profile" in w for w in warnings), warnings)
        self.assertTrue(any("Dry thru: new in the profile" in w for w in warnings), warnings)
        self.assertTrue(any("track Wet: its range is now 0…255" in w for w in warnings), warnings)


RATE = 44100


def tones_signal(tones, amp=0.25):
    x = [0.0] * int((tones[-1][2] + tones[-1][3] + 0.1) * RATE)
    for _, f, start, dur in tones:
        a, b = int(start * RATE), int((start + dur) * RATE)
        for i in range(a, b):
            x[i] = amp * math.sin(2 * math.pi * f * (i - a) / RATE)
    return x


def one_pole(x, fc):
    k = 1 - math.exp(-2 * math.pi * fc / RATE)
    y, out = 0.0, []
    for v in x:
        y += k * (v - y)
        out.append(y)
    return out


class TestSignalTests(unittest.TestCase):
    def test_the_sweep_covers_the_band_six_periods_a_tone(self):
        t = mr.sweep_tones(step=4)
        self.assertEqual(len(t), 31)
        self.assertAlmostEqual(t[0][1], 20.6, places=1)
        self.assertGreater(t[-1][1], 20000)
        self.assertTrue(all(d >= 0.04 and d * f >= 5.99 for _, f, _, d in t))
        self.assertEqual(t[0][0], 16 - 12, "sent an octave down: Neo Poly's O1 octave +1")

    def test_a_low_pass_response_and_its_corner(self):
        tones = mr.sweep_tones(step=4, low=40, high=124)
        x = tones_signal(tones)
        y = one_pole(x, 1000)
        resp = mr.response(tones, RATE, x, y, y)
        f = mr.response_features(resp)
        self.assertLess(abs(f["max_db"]), 0.5)
        self.assertTrue(700 <= f["high_corner_hz"] <= 1400, f)
        self.assertLess(f["tilt_db"], -15)
        flat = mr.response_features(mr.response(tones, RATE, x, x, x))
        self.assertLess(flat["spread_db"], 0.1)
        self.assertGreater(flat["centroid_oct"] - f["centroid_oct"], 1.0, "a low-pass pulls the centroid down")

    def test_a_click_through_a_delay_its_echoes_and_decay(self):
        n = int(1.5 * RATE)
        x = [0.0] * n
        a = int(0.05 * RATE)
        for i in range(a, a + 44):
            x[i] = 0.5 if (i // 3) % 2 else -0.5
        y = list(x)
        for k, g in ((1, 0.5), (2, 0.25), (3, 0.125)):
            d = int(0.1 * k * RATE)
            for i in range(a, a + 44):
                y[i + d] += g * x[i]
        c = mr.click_features(RATE, x, y, y, 0.05, 0.001, 1.4)
        self.assertEqual(c["latency_samples"], 0)
        self.assertAlmostEqual(c["direct_db"], 0, delta=0.5)
        times = [t for t, _ in c["echoes"]]
        self.assertEqual(len(times), 3, c["echoes"])
        for t, want in zip(times, (0.1, 0.2, 0.3)):
            self.assertAlmostEqual(t, want, delta=0.006)

    def test_decay_rate_reads_an_exponential_tail(self):
        env = [-60.0 * (i / 1000.0) for i in range(2000)]          # 60 dB per second, 1 ms windows
        t60, span = mr.decay_rate(env)
        self.assertAlmostEqual(t60, 1.0, delta=0.05)
        self.assertGreaterEqual(span, 15)
        self.assertEqual(mr.decay_rate([-1.0] * 100)[0], None, "nothing falls: no T60")

    def test_level_steps_through_a_compressor(self):
        notes, jump_at, end = mr.step_notes(0.1)
        x = [0.0] * int((end + 0.1) * RATE)
        for v, s, d in notes:
            amp = 0.9 * v / 127
            for i in range(int(s * RATE), int((s + d) * RATE)):
                x[i] = amp * math.sin(2 * math.pi * 988 * i / RATE)

        def comp(sig, thr_db=-20, ratio=4.0, attack=0.01):
            env, out = 0.0, []
            ka = 1 - math.exp(-1 / (attack * RATE))
            for v in sig:
                env += ka * (abs(v) - env)
                lvl = 20 * math.log10(env) if env > 1e-6 else -120
                over = max(0.0, lvl - thr_db)
                out.append(v * 10 ** (-(over - over / ratio) / 20))
            return out
        y = comp(x)
        f = mr.step_features(RATE, x, y, y, notes, jump_at)
        self.assertGreater(f["compression_db"], 6)
        self.assertIsNotNone(f["knee_in_db"])
        self.assertLess(f["knee_in_db"], -10)
        fast, slow = f["attack_tau_s"], mr.step_features(RATE, x, comp(x, attack=0.1), comp(x, attack=0.1), notes, jump_at)["attack_tau_s"]
        self.assertIsNotNone(fast)
        self.assertGreater(slow, 2 * fast, "a slower attack: a longer time constant")
        flat = mr.step_features(RATE, x, x, x, notes, jump_at)
        self.assertLess(abs(flat["compression_db"]), 0.2)

    def test_a_held_notes_envelope(self):
        n = int(2.0 * RATE)
        x = []
        for i in range(n):
            t = i / RATE
            amp = min(1.0, t / 0.2) if t < 0.2 else (0.25 + 0.75 * math.exp(-(t - 0.2) / 0.1)) if t < 1.0 else 0.25 * math.exp(-(t - 1.0) / 0.05)
            x.append(0.5 * amp * math.sin(2 * math.pi * 220 * t))
        e = mr.note_envelope(RATE, x, x, 0.0, 1.0)
        self.assertAlmostEqual(e["attack_s"], 0.2, delta=0.03)
        self.assertAlmostEqual(e["final_db"], -12.0, delta=1.0)
        self.assertLess(e["half_s"], 0.15)
        self.assertAlmostEqual(e["release_s"], 0.21, delta=0.05)
        self.assertEqual(e.get("level_wobble_db"), 0.0, "an envelope isn't an LFO")

    def test_velocity_response(self):
        v = mr.velocity_response({16: -18.0, 32: -12.0, 64: -6.0, 127: 0.0})
        self.assertAlmostEqual(v["db_per_doubling"], 6.0, delta=0.1)
        self.assertIn("linear", v["curve"])
        self.assertIn("none", mr.velocity_response({16: -6.0, 127: -6.0})["curve"])

    def test_the_harmonic_sieve_hears_a_resonant_bass(self):
        f0 = 110.0
        x = []
        for i in range(16384 + 2000):
            t = i / RATE
            s_ = sum((1.0 / k) * (4.0 if k == 7 else 1.0) * math.sin(2 * math.pi * f0 * k * t) for k in range(1, 20))
            x.append(0.1 * s_ * math.exp(-t / 0.3))
        binhz, mags = mr.magnitude_spectrum(x, RATE)
        r = mr.pitch_sieve(binhz, mags, 45)
        self.assertTrue(r["pitched"])
        self.assertEqual(r["midi"], 45, "A2, not an octave below it")
        self.assertLess(abs(r["cents"]), 10)
        binhz, mags = mr.magnitude_spectrum([sum(math.sin(2 * math.pi * 440 * r * i / RATE) for r in (1.0, 1.414, 2.71, 3.97))
                                             for i in range(16384)], RATE)
        self.assertFalse(mr.pitch_sieve(binhz, mags, 69)["pitched"], "inharmonic partials: no pitch")

    def test_third_prominence_and_wobble(self):
        hz = [20 * 2 ** (i / 3) for i in range(30)]
        flat = [-20.0] * 30
        bump = list(flat)
        bump[20] = -2.0
        self.assertLess(mr.third_prominence(hz, flat, 55)[0], 1)
        p, at = mr.third_prominence(hz, bump, 55)
        self.assertAlmostEqual(p, 18, delta=0.1)
        self.assertAlmostEqual(at, hz[20])
        self.assertGreater(mr._wobble([3 * math.sin(i / 1.5) for i in range(30)]), 4)
        self.assertEqual(mr._wobble([i * 0.5 for i in range(30)]), 0.0, "a ramp doesn't swing")


def feats(level=0.0, spread=0.0, lo=20.0, hi=20000.0, prom=0.0, centroid=0.0, late=-120.0, t60=None, echoes=(), direct=0.0, steps=None, wobble=0.0, **click):
    r = {"level_db": level, "spread_db": spread, "low_corner_hz": lo, "high_corner_hz": hi, "prominence_db": prom, "centroid_oct": centroid,
         "max_db": level, "max_hz": 1000.0, "width_oct": math.log2(hi / lo), "wobble_db": wobble, "shape": [(1000.0, 0.0)]}
    c = dict({"late_db": late, "t60_s": t60, "echoes": list(echoes), "direct_db": direct, "latency_samples": 0}, **click)
    return {"response": r, "click": c, "steps": steps}


class RoleTests(unittest.TestCase):
    def role(self, lo, hi, default=None, mtype="filter or EQ", mid=None, **p):
        info = dict({"name": "P", "kind": "linear", "min": 0, "max": 100}, **p)
        r, why = mr.suggest_effect_role(info, {"min": lo, "max": hi}, default or lo, mtype, mid=mid)
        return r

    def test_a_cutoff_moves_a_corner(self):
        self.assertEqual(self.role(feats(spread=40, hi=200, centroid=-3), feats(spread=10, hi=8000, centroid=-0.5)), "filter.cutoff")

    def test_a_wet_level_with_a_tail_and_one_with_a_shape(self):
        dry, wet = feats(), feats(level=-3, late=-12, t60=2.0)
        self.assertEqual(self.role(dry, wet, mtype="reverb"), mr.NEED_MID)
        self.assertEqual(self.role(dry, wet, mtype="reverb", mid=feats(level=-1, late=-18, t60=2.1)), "mix.wet")
        # a low-pass's mix: half way the same corner; a cutoff moving out of the band: half way another corner
        lp = feats(spread=36, hi=1500, centroid=-2)
        self.assertEqual(self.role(dry, lp, mid=feats(spread=6, hi=1700, centroid=-1)), "mix.wet")
        self.assertEqual(self.role(lp, dry, mid=feats(spread=20, hi=5000, centroid=-1)), "filter.cutoff")

    def test_delay_time_feedback_and_a_reverbs_decay_and_predelay(self):
        self.assertEqual(self.role(feats(late=-10, echoes=[(0.1, -6), (0.2, -12)]), feats(late=-10, echoes=[(0.4, -6), (0.8, -12)]), mtype="delay"), "delay.time")
        self.assertEqual(self.role(feats(late=-10, t60=0.3, echoes=[(0.1, -6)]), feats(late=-5, t60=3.0, echoes=[(0.1, -6), (0.2, -7)]), mtype="delay"), "delay.feedback")
        self.assertEqual(self.role(feats(late=-10, t60=0.5), feats(late=-8, t60=4.0), mtype="reverb"), "reverb.decay")
        self.assertEqual(self.role(feats(late=-10, t60=2.0, echoes=[(0.01, -20)]), feats(late=-10, t60=2.1, echoes=[(0.2, -20)]), mtype="reverb"), "reverb.predelay")

    def test_a_resonance_and_a_choice_of_filter_types(self):
        self.assertEqual(self.role(feats(prom=0, hi=2000), feats(prom=18, hi=2200)), "filter.resonance")
        lp, hp = feats(spread=30, hi=1000, centroid=-2), feats(spread=30, lo=1000, centroid=2)
        lp["response"]["shape"], hp["response"]["shape"] = [(100.0, 0.0)], [(100.0, -30.0)]
        self.assertEqual(self.role(lp, hp, kind="enum", choices=["LP", "HP"], max=1), "filter.type")
        self.assertIsNone(self.role(lp, hp, kind="enum", choices=["Red", "Blue"], max=1), "a choice whose texts say nothing")

    def test_dynamics_from_the_steps(self):
        def st(comp, knee, atk=0.01, rel=0.05):
            return {"compression_db": comp, "knee_in_db": knee, "attack_tau_s": atk, "release_tau_s": rel, "curve": [(-48, 0), (-42, 0), (-36, 0), (-30, 0)]}
        d = feats(steps=st(8, -18))
        self.assertEqual(self.role(feats(steps=st(0.2, None)), feats(steps=st(20, -36)), default=d, mtype="dynamics"), "dyn.threshold")
        self.assertEqual(self.role(feats(steps=st(8, -18, atk=0.005)), feats(steps=st(3, -18, atk=0.15)), default=d, mtype="dynamics"), "dyn.attack")
        self.assertEqual(self.role(feats(steps=st(8, -18, rel=0.01)), feats(steps=st(8, -18, rel=0.4)), default=d, mtype="dynamics"), "dyn.release")
        self.assertEqual(self.role(feats(steps=st(0.2, None)), feats(steps=st(11, -18)), default=d, mtype="dynamics"), "dyn.ratio")

    def test_generator_roles_from_a_held_note(self):
        def g(env=None, pitch=57.0, centroid=1000.0, silent=False, **k):
            e = dict({"peak_db": -6.0, "attack_s": 0.005, "final_db": -3.0, "release_s": 0.05, "bright_late_hz": 1000.0, "bright_early_hz": 1000.0}, **(env or {}))
            return dict({"env": e, "pitch": pitch, "centroid_hz": centroid, "silent": silent}, **k)
        info = {"name": "P", "kind": "linear", "min": 0, "max": 100}
        role = lambda lo, hi, t="monosynth": mr.suggest_generator_role(info, {"min": lo, "max": hi}, g(), t)[0]
        self.assertEqual(role(g(), g(env={"attack_s": 0.6})), "env.attack")
        self.assertEqual(role(g(), g(env={"release_s": 0.9})), "env.release")
        self.assertEqual(role(g(), g(pitch=64.0)), "pitch.coarse")
        self.assertEqual(role(g(), g(pitch=57.3)), None, "under 0.4 semitones: nothing")
        self.assertEqual(role(g(centroid=300.0), g(centroid=4000.0)), "filter.cutoff")
        self.assertEqual(role(g(silent=True), g()), "level")
        self.assertEqual(role(g(), g(env={"length_s": 0.8}), "drums"), None)
        self.assertEqual(role(g(env={"length_s": 0.1}), g(env={"length_s": 0.8}), "drums"), "drum.decay")

    def test_feedback_by_how_fast_the_echoes_fade_and_a_resonance_is_never_a_mix(self):
        held = feats(late=-5, echoes=[(0.375, -8), (0.75, -9), (1.125, -10)], echo_fade_db_s=2.7)
        default = feats(late=-8, echoes=[(0.375, -9), (0.75, -16), (1.125, -23)], echo_fade_db_s=18.7)
        self.assertEqual(self.role(held, held, default=default, mtype="delay"), "delay.feedback", "±98 % fade alike: against the default")
        er_off = feats(late=-8, t60=0.95, echoes=[(0.04, -26)], early_db=1.7)
        er_on = feats(late=2.5, t60=0.77, echoes=[(0.02, -7), (0.04, -8.6), (0.07, -9.9)], early_db=5.8)
        self.assertEqual(self.role(er_off, er_on, mtype="reverb", name="ER Out"), "reverb.early", "reflections added, the reverb's own stay")
        ringing = feats(level=2.3, spread=13.5, prom=8.2, hi=8372, lo=5920, late=-22, t60=0.3)
        self.assertEqual(self.role(feats(), ringing, mtype="utility", name="Resonance"), "filter.resonance")

    def test_a_distortions_harmonics_first_and_a_filter_type_with_bare_numbers(self):
        def st(comp):
            return {"compression_db": comp, "knee_in_db": -18, "attack_tau_s": 0.001, "release_tau_s": 0.001, "curve": [(-36, 0), (-30, 0), (-24, -comp)]}
        clean, clipped = dict(feats(steps=st(0.5)), thd=1.0), dict(feats(steps=st(9)), thd=35.0)
        self.assertEqual(self.role(clean, clipped, mtype="distortion", name="+threshold"), "drive", "clipping squeezes the steps too")
        flat, bp = feats(), feats(level=-4, spread=47, lo=11840, hi=16744)
        bp["response"]["shape"] = [(1000.0, -40.0)]
        self.assertEqual(self.role(flat, bp, mtype="utility", name="Type", max=2, texts=["0", "1", "2"]), "filter.type")

    def test_key_tracking_follows_the_note_a_cutoff_doesnt(self):
        ends = lambda a, b: {"min": {"centroid_hz": a}, "max": {"centroid_hz": b}}
        self.assertEqual(mr.keytrack_role(ends(4000.0, 900.0), ends(3000.0, 3500.0), 36)[0], "filter.keytrack")
        self.assertIsNone(mr.keytrack_role(ends(64.0, 10400.0), ends(440.0, 10000.0), 36), "a cutoff brightens the high note less")
        self.assertIsNone(mr.keytrack_role(ends(56.0, 2320.0), ends(39.0, 3360.0), 36, 55.0, 440.0), "a near-silent end on the high note")
        self.assertIsNone(mr.keytrack_role(ends(5670.0, 856.0), ends(9860.0, 6260.0), 36), "darker on both notes: an envelope, not tracking")

    def test_a_blend_named_like_one_and_a_closed_filters_ring(self):
        def st(curve):
            return {"compression_db": curve[0] - curve[-1], "knee_in_db": -18 if curve[-1] < 0 else None, "attack_tau_s": 0.01, "release_tau_s": 0.05,
                    "curve": [(-36 + 6 * i, g) for i, g in enumerate(curve)]}
        dry, full = st([0, 0, 0, 0]), st([0, -2, -6, -8.4])
        even = st([0, -0.9, -2.5, -3.2])     # -20·log10((1 + g) / 2) of the full curve
        args = (feats(steps=dry), feats(steps=full))
        self.assertEqual(self.role(*args, default=feats(steps=full), mtype="dynamics", mid=feats(steps=even), name="Wet/Dry"), "mix.wet")
        self.assertEqual(self.role(*args, default=feats(steps=full), mtype="dynamics", mid=feats(steps=even), name="Ratio"), "dyn.ratio")
        # a low-pass closed at 20 Hz rings for 85 ms: not a tail, so not a mix's wet end
        closed = feats(level=-31, spread=97, hi=23.1, centroid=-5, late=-40, t60=0.085, echoes=[(0.04, -5.4)], wobble=3.1)
        self.assertEqual(self.role(closed, feats(), mid=feats(spread=20, hi=9000, centroid=-1)), "filter.cutoff")

    def test_categories_and_agreement(self):
        self.assertEqual(mr.category("filter.cutoff"), "filter")
        self.assertEqual(mr.category("eq.low.freq"), "filter")
        self.assertEqual(mr.category("delay.time"), "time")
        self.assertEqual(mr.category("mix.dry"), "mix")
        self.assertIsNone(mr.category("level"))
        self.assertTrue(mr.agrees("env.decay", "drum.decay"))
        self.assertTrue(mr.agrees("delay.time", "delay.time_left"))
        self.assertFalse(mr.agrees("mix.wet", "dyn.ratio"))


class HotEndTests(unittest.TestCase):
    def test_a_loop_or_resonance_that_rings_gets_a_range_a_level_control_doesnt(self):
        self.assertTrue(mi.rings("Resonance", 53.8, -3.2))
        self.assertTrue(mi.rings("FB Amp", 40.0, 0.0), "a feedback wins over the word amp")
        self.assertFalse(mi.rings("Resonance", 16.2, 0.0), "16 dB over the default: hot, no range")
        self.assertFalse(mi.rings("Output", 60.0, 0.0), "a level control's hot end is its job")
        self.assertFalse(mi.rings("Wet out", 40.0, 0.0))
        self.assertTrue(mi.level_control("Makeup Gain") and mi.level_control("Dry thru"))
        self.assertFalse(mi.level_control("Feedback") or mi.level_control("Q"))


class SourceManifestTests(unittest.TestCase):
    """U6 (mcp-server.md §12.13): manifests from the machines' source."""

    def test_committed_manifests_are_what_their_sources_generate(self):
        self.assertEqual(sm.run(write=False), [])

    def test_the_declarations_are_read_as_rebuzz_reads_them(self):
        glob, track = sm.machine_params(sm.PEDAL + "pedal-gain-multi/PedalGainMultiMachine.cs")
        self.assertEqual([p["name"] for p in glob], ["Gain", "Master Mute", "Inertia"])
        self.assertEqual([p["name"] for p in track], ["Solo", "Mute", "Volume", "Pan", "Mono"])
        self.assertEqual((glob[0]["min"], glob[0]["max"], glob[0]["default"]), (0, 181, 161))
        self.assertEqual((glob[1]["min"], glob[1]["max"]), (0, 1))
        # The MIDI output's Note, Volume, Delay and Cut hold no value: played, not in its manifest.
        _, track = sm.machine_params("Machines/MidiOutput/MidiOutput.cs")
        self.assertEqual([p["name"] for p in track], ["Device", "MidiCC", "MidiCCValue", "Channel"])

    def test_kinds_fit_exactly_or_fail(self):
        p = {"name": "X", "min": 0, "max": 96, "texts": None, "descriptor": None}
        m = sm.mapping(sm.P("X", "db", "(v - 48) * 0.5", unit="dB"), p, {})
        self.assertEqual((m["a"], m["b"]), (-24.0, 24.0))
        with self.assertRaises(sm.Problem):
            sm.mapping(sm.P("X", "db", "v * v / 96", unit="dB"), p, {})   # not a line
        r = {"name": "R", "min": 0, "max": 127, "texts": None, "descriptor": None}
        m = sm.mapping(sm.P("R", "ratio", "v >= 127 ? 1e6 : pow(40, v / 126)", unit=":1", flag="max_inf"), r, {})
        self.assertAlmostEqual(m["b"], 40 ** (127 / 126), places=6)
        self.assertTrue(m["flag"])

    def test_code_that_changed_is_caught(self):
        spec = dict(next(x for x in sm.SPECS if x["gear"] == "Pedal Gain"))
        spec["params"] = [dict(spec["params"][0], code=["Gain * 0.02f"])] + spec["params"][1:]
        with self.assertRaises(sm.Problem):
            sm.generate(spec, None)
        spec["params"] = spec["params"][1:]
        with self.assertRaises(sm.Problem):   # a parameter the source declares and this file doesn't
            sm.generate(spec, None)


if __name__ == "__main__":
    unittest.main()
