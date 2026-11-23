# Fork (docs/specs/clips.md §10.1, wave CU2): unit tests for tools/clip-change-report.py on synthetic WAVs and a synthetic migration log.
# No audio is committed: the files are made here, in a temporary folder.
# Run: python3 -m unittest discover -s tools -p "test_*.py"

import importlib.util
import io
import json
import math
import os
import struct
import tempfile
import unittest
from contextlib import redirect_stdout, redirect_stderr

HERE = os.path.dirname(os.path.abspath(__file__))


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


cr = load("clip_change_report", "clip-change-report.py")
RATE = 8000


def write_wav(path, frames, bits=16, float_format=False, channels=2):
    """frames: list of floats (the same in every channel)."""
    if float_format:
        body = b"".join(struct.pack("<f", v) * channels for v in frames)
        fmt = struct.pack("<HHIIHH", 3, channels, RATE, RATE * 4 * channels, 4 * channels, 32)
    else:
        scale = 2 ** (bits - 1) - 1
        pack = {16: lambda v: struct.pack("<h", int(round(v * scale))),
                24: lambda v: struct.pack("<i", int(round(v * scale)))[:3]}[bits]
        body = b"".join(pack(v) * channels for v in frames)
        fmt = struct.pack("<HHIIHH", 1, channels, RATE, RATE * bits // 8 * channels, bits // 8 * channels, bits)
    data = b"WAVE" + b"fmt " + struct.pack("<I", len(fmt)) + fmt + b"data" + struct.pack("<I", len(body)) + body
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", len(data)) + data)


def tone(n=2000, amp=0.5):
    return [amp * math.sin(2 * math.pi * 440 * i / RATE) for i in range(n)]


class ChangeReport(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.before = os.path.join(self.tmp.name, "before")
        self.after = os.path.join(self.tmp.name, "after")
        os.makedirs(self.before)
        os.makedirs(self.after)

    def put(self, name, b, a, **kw):
        write_wav(os.path.join(self.before, name + ".wav"), b, **kw)
        write_wav(os.path.join(self.after, name + ".wav"), a, **kw)

    def log(self, songs):
        path = os.path.join(self.tmp.name, "log.json")
        with open(path, "w") as f:
            json.dump({"version": 1, "songs": songs}, f)
        return path

    def run_main(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = cr.main(list(args))
        return code, out.getvalue(), err.getvalue()

    def test_identical_renders_are_identical(self):
        self.put("gen-a", tone(), tone())
        e = cr.report_song("gen/a", self.before, self.after, None, None, {})
        self.assertEqual((e["status"], e["identical"], e["unexplained"]), ("identical", True, False))

    def test_the_first_differing_sample_and_the_size_of_the_difference(self):
        b = tone()
        a = list(b)
        for i in range(1000, 1100):
            a[i] += 0.01
        self.put("gen-a", b, a)
        e = cr.report_song("gen/a", self.before, self.after, None, None, {"gen/a": [{"tag": "midi-tick", "kind": "change", "count": 4}]})
        self.assertEqual(e["status"], "differs")
        self.assertEqual(e["first_diff_frame"], 1000)
        self.assertAlmostEqual(e["first_diff_seconds"], 1000 / RATE)
        self.assertAlmostEqual(e["peak_db"], 20 * math.log10(0.01), delta=0.6)       # 16-bit quantisation of the 0.01
        self.assertLess(e["rms_db"], e["peak_db"])
        self.assertLess(e["relative_db"], 0)
        self.assertEqual(e["tags"], {"midi-tick": 4})
        self.assertTrue(e["explained"])
        self.assertFalse(e["unexplained"])

    def test_a_difference_with_no_cause_tag_is_unexplained_and_info_lines_do_not_explain(self):
        b = tone()
        a = [v * 0.5 for v in b]
        self.put("gen-a", b, a)
        log = {"gen/a": [{"tag": "off-explicit", "kind": "info", "count": 2}]}
        e = cr.report_song("gen/a", self.before, self.after, None, None, log)
        self.assertTrue(e["unexplained"])
        self.assertEqual(e["tags"], {})
        self.assertEqual(e["info"], ["off-explicit"])
        self.assertEqual(e["first_diff_frame"], 1)

    def test_a_loss_explains(self):
        self.put("gen-a", tone(), tone(amp=0.4))
        e = cr.report_song("gen/a", self.before, self.after, None, None, {"gen/a": [{"tag": "pxp-sp-dropped", "kind": "loss", "count": 3}]})
        self.assertFalse(e["unexplained"])

    def test_formats_are_read_and_compared_across_bit_depths_of_one_file_pair(self):
        for kw in ({"bits": 24}, {"float_format": True}):
            with self.subTest(**kw):
                self.put("gen-a", tone(), tone(), **kw)
                self.assertTrue(cr.report_song("gen/a", self.before, self.after, None, None, {})["identical"])

    def test_a_longer_render_differs_where_the_shorter_one_ends(self):
        self.put("gen-a", tone(1000), tone(1500))
        e = cr.report_song("gen/a", self.before, self.after, None, None, {})
        self.assertEqual((e["status"], e["first_diff_frame"]), ("differs", 1000))

    def test_a_missing_render_is_missing_and_hashes_alone_say_identical_or_differs(self):
        write_wav(os.path.join(self.before, "gen-a.wav"), tone())
        self.assertEqual(cr.report_song("gen/a", self.before, self.after, None, None, {})["status"], "missing")
        self.assertEqual(cr.report_song("gen/b", None, None, "aa", "aa", {})["status"], "identical")
        e = cr.report_song("gen/b", None, None, "aa", "bb", {})
        self.assertEqual((e["status"], e["unexplained"]), ("differs", True))

    def test_the_command_line_exit_code_and_outputs(self):
        self.put("gen-same", tone(), tone())
        self.put("gen-tagged", tone(), tone(amp=0.3))
        self.put("gen-bare", tone(), tone(amp=0.2))
        log = self.log({"gen/tagged": [{"tag": "block-shift", "kind": "change", "count": 10}], "gen/bare": []})
        out_json = os.path.join(self.tmp.name, "r.json")
        out_md = os.path.join(self.tmp.name, "r.md")
        code, text, err = self.run_main("--before", self.before, "--after", self.after, "--log", log, "--out", out_json, "--markdown", out_md)
        self.assertEqual(code, 1)
        self.assertIn("UNEXPLAINED: gen/bare", err)
        self.assertNotIn("gen/tagged", err)
        with open(out_json) as f:
            data = json.load(f)
        self.assertEqual({n: e["status"] for n, e in data["songs"].items()}, {"gen-same": "identical", "gen/tagged": "differs", "gen/bare": "differs"})
        self.assertEqual(data["songs"]["gen/tagged"]["tags"], {"block-shift": 10})
        self.assertTrue(os.path.getsize(out_md) > 0)
        code, _, _ = self.run_main("--before", self.before, "--after", self.after, "--log", log, "--no-fail")
        self.assertEqual(code, 0)
        code, _, _ = self.run_main("--before", self.before, "--after", self.after, "--log", log, "--only", "gen-same,gen/tagged")
        self.assertEqual(code, 0)

    def test_the_golden_hashes_of_one_platform_are_read(self):
        path = os.path.join(self.tmp.name, "g.json")
        with open(path, "w") as f:
            json.dump({"platforms": {"linux-x64": {"songs": {"gen/a": {"sha256": "ab"}, "gen/b": {"status": "open failed"}}}}}, f)
        self.assertEqual(cr.load_hashes(path, "linux-x64"), {"gen/a": "ab"})
        with self.assertRaises(SystemExit):
            cr.load_hashes(path, "windows-x64")


if __name__ == "__main__":
    unittest.main()
