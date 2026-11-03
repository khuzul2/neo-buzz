# Fork (docs/specs/mcp-server.md §12.2, U1): unit tests for tools/machine_infer.py, fed value
# tables whose meaning is known. Run: python3 -m unittest discover -s tools -p "test_machine_infer.py"

import math
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import machine_infer as mi  # noqa: E402


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


if __name__ == "__main__":
    unittest.main()
