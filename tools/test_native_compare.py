# Fork (docs/specs/mcp-server.md §12.14): unit tests for tools/native-compare.py's gate on the natives with a source fix
# (judge_fixed), fed the groupings the package job has seen, so the verdict on each is known without Windows; for
# tools/native-zeroheap.py, which makes upstream's binaries run on a zeroed heap, on the binaries in RunTime/Gear; for
# tools/native-songs.py's verdicts and Gear swaps; and for the one exception to build.ps1's licence gate.
# Run: python3 -m unittest discover -s tools -p "test_*.py"

import importlib.util
import json
import os
import shutil
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


nc = load("native_compare", "native-compare.py")
zh = load("native_zeroheap", "native-zeroheap.py")
ns = load("native_songs", "native-songs.py")
mp = load("machine_profiler", "machine-profiler.py")

UP = ["shipped", "shipped 2", "shipped 3", "shipped 4"]
ROLES = {**{u: "upstream" for u in UP}, "package (rebuilt)": "package (rebuilt)", "R64": "rebuilt", "R86": "rebuilt",
         "Z64": "zeroed", "Z64 2": "zeroed"}
CASES = ["default", "quarter", "three-quarters", "quiet-input"]


def case(bits, apart=2.0):
    """One case's entries from {label: what it rendered} (equal values: the same bits; else `apart` dBFS apart)."""
    out = {}
    for label, b in bits.items():
        e = {"status": "ok", "sha1": f"sha-{b}"}
        if ROLES[label] != "upstream":
            e["as_upstream"] = [u for u in UP if bits[u] == b]
            e["vs"] = {o: [bits[o] == b, float("-inf") if bits[o] == b else apart, None if bits[o] == b else 0.5]
                       for o in bits if o != label and ROLES[o] != "upstream"}
        out[label] = e
    return out


def renders(up, ours="F", r86=None, zeroed="F", zeroed2=None):
    """Upstream's four instances as they come rendering `up` (four values), ours `ours`, the zeroed upstream `zeroed`."""
    return {**dict(zip(UP, up)), "package (rebuilt)": ours, "R64": ours, "R86": r86 or ours, "Z64": zeroed, "Z64 2": zeroed2 or zeroed}


def machine(*per_case):
    return {c: case(b) for c, b in zip(CASES, per_case)}


class FixedGateTests(unittest.TestCase):
    def gate(self, cases, label="package (rebuilt)"):
        return nc.judge_fixed(cases, ROLES)[label]

    def test_run_41983193582_passes(self):
        # WahMan in run 41983193582: at the defaults all four upstream instances agreed on a start that isn't the fix's
        # (the old gate, as one of them, failed); at the other cases they split as below. Upstream's binary on a zeroed
        # heap renders what the fixed build does.
        j = self.gate(machine(renders("GGGG"), renders("FHFH"), renders("FHFF"), renders("FHHF")))
        self.assertEqual(j["failures"], [])
        self.assertIn("the same bits as R64/R86/Z64/Z64 2 in every case", j["verdict"])
        self.assertIn("as 0/4, 2/4, 3/4, 2/4 of upstream's instances as they come", j["verdict"])

    def test_no_upstream_instance_alike_is_not_judged(self):
        # Run 42000620560's settled quiet-input: three of four upstream instances agreed on a sound that isn't the
        # zeroed start's, after 2 s of silence, and the fourth matched. None at all must pass as well.
        self.assertEqual(self.gate(machine(*[renders("XXXX")] * 4))["failures"], [])

    def test_a_broken_rebuild_fails(self):
        # The rebuild plays something upstream's binary on a zeroed heap doesn't (the same in every build of ours).
        j = self.gate(machine(renders("FFFF", ours="B"), *[renders("FFFF")] * 3))
        self.assertEqual(len(j["failures"]), 2)
        self.assertTrue(all("not upstream's binary from a zeroed start" in f for f in j["failures"]))
        self.assertIn("not the same bits as Z64 in default (2.0 dBFS, from 0.50 s)", j["failures"][0])

    def test_x86_unlike_x64_fails(self):
        j = self.gate(machine(renders("FFFF", r86="G"), *[renders("FFFF")] * 3))
        self.assertEqual(len(j["failures"]), 1)
        self.assertIn("not the same bits as R86 in default (2.0 dBFS, from 0.50 s)", j["failures"][0])
        self.assertIn("the fix leaves state unset", j["failures"][0])
        # and R86's own verdict names both what it's unlike
        self.assertIn("UNLIKE", self.gate(machine(renders("FFFF", r86="G"), *[renders("FFFF")] * 3), "R86")["verdict"])

    def test_a_zeroing_that_didnt_take_is_named(self):
        j = self.gate(machine(renders("FFFF", zeroed2="G"), *[renders("FFFF")] * 3), "Z64")
        self.assertIn("the zeroing didn't take", j["failures"][0])

    def test_a_diverged_case_fails(self):
        cases = machine(*[renders("FFFF")] * 4)
        cases["quarter"]["package (rebuilt)"]["status"] = "diverged"
        self.assertIn("quarter: diverged", self.gate(cases)["failures"])


class ZeroHeapTests(unittest.TestCase):
    def test_upstreams_wahs_import_zeroheap_and_nothing_else_changes(self):
        for gear in ("FSM WahMan", "FSM WahPro2", "FSM WahManPro"):
            src = os.path.join(ROOT, "RunTime", "Gear", "Effects", gear + ".x64.dll")
            with tempfile.TemporaryDirectory() as d:
                dst = os.path.join(d, "out.dll")
                fns = zh.patch(src, dst)
                self.assertIn("HeapAlloc", fns)
                with open(src, "rb") as f:
                    a = f.read()
                with open(dst, "rb") as f:
                    b = f.read()
                self.assertEqual(len(a), len(b))
                differ = [i for i in range(len(a)) if a[i] != b[i]]
                self.assertTrue(0 < len(differ) <= len(zh.PROXY), f"{gear}: only the import's name changes")
                dlls = [n.upper() for n, _, _ in zh.Pe(b).imports()]
                self.assertIn("ZEROHEAP.DLL", dlls)
                self.assertNotIn("KERNEL32.DLL", dlls)

    def test_the_def_forwards_all_but_heapalloc(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "z.def")
            zh.write_def(path, ["HeapAlloc", "HeapFree", "GetLastError", "HeapFree"])
            with open(path, encoding="ascii") as f:
                lines = f.read().split("\n")
            self.assertEqual(lines[:2], ["LIBRARY ZEROHEAP", "EXPORTS"])
            self.assertEqual([x.strip() for x in lines[2:5]],
                             ["GetLastError=kernel32.GetLastError", "HeapAlloc=NbZeroHeapAlloc", "HeapFree=kernel32.HeapFree"])


class LicenceExceptionTests(unittest.TestCase):
    """build.ps1 ships a native whose licence has no text only when its "licenseException" is exactly the exception it records."""

    def recorded(self):
        import re
        with open(os.path.join(ROOT, "build.ps1"), encoding="utf-8-sig") as f:
            m = re.findall(r"^\$NativeLicenceException = '([^']*)'", f.read(), re.M)
        self.assertEqual(len(m), 1, "build.ps1 records the exception once")
        return m[0]

    def test_build_and_the_profiler_record_the_same_exception(self):
        self.assertEqual(self.recorded(), "no licence in source; shipped by user decision 2026-11-06 for private use")
        self.assertEqual(mp.NATIVE_LICENCE_EXCEPTION, self.recorded())

    def test_every_exception_is_the_recorded_one_on_an_unlicensed_x64_rebuild(self):
        excepted = [n for n in nc.natives() if "licenseException" in n]
        self.assertEqual(sorted(n["gear"] for n in excepted), ["Jeskola Distortion", "Jeskola Noise Generator", "Jeskola Trilok"])
        for n in excepted:
            self.assertEqual(n["licenseException"], self.recorded(), n["gear"])
            self.assertTrue(n["license"].startswith("None"), f"{n['gear']}: the exception is for sources without a licence")
            self.assertIs(n["redistribute"], False, n["gear"])
            self.assertEqual((n["ship"], n.get("shipPlatform"), n["upstreamBinary"]), ("rebuilt", "x64", "x86"), n["gear"])

    def test_the_profiler_lists_what_ships_rebuilt(self):
        with tempfile.TemporaryDirectory() as d:
            os.makedirs(os.path.join(d, "ExternalMachines"))
            natives = [{"gear": "A", "ship": "rebuilt", "redistribute": True},
                       {"gear": "B", "ship": "rebuilt", "redistribute": False},
                       {"gear": "C", "ship": "rebuilt", "redistribute": False, "licenseException": mp.NATIVE_LICENCE_EXCEPTION},
                       {"gear": "D", "ship": "rebuilt", "redistribute": False, "licenseException": "shipped anyway"},
                       {"gear": "E", "ship": "upstream", "redistribute": False, "licenseException": mp.NATIVE_LICENCE_EXCEPTION}]
            with open(os.path.join(d, "ExternalMachines", "machines.json"), "w", encoding="utf-8") as f:
                json.dump({"machines": [{"id": "buzztrax-buzzmachines", "natives": natives}]}, f)
            self.assertEqual([n["gear"] for n in mp.rebuilt_natives(d)], ["A", "C"])


class NativeSongsTests(unittest.TestCase):
    """tools/native-songs.py's verdict on one machine in one song, and its Gear swaps."""

    E = {"gear": "Jeskola Distortion", "folder": "Effects", "role": "rebuilt",
         "upstream": ("u", "Jeskola Distortion.dll"), "ours": ("o", "Jeskola Distortion.x64.dll")}

    def read(self, file, params=None, status="ok"):
        return {"health": "ok", "tracks": 0, "file": file, "params": params or {"Threshold@-": 100, "Amount@-": 96}, "wav": "w", "status": status}

    def judge(self, a=None, b=None, sound=(True, float("-inf"), None), role="rebuilt", feeders=()):
        e = dict(self.E, role=role)
        return ns.judge(e, a or self.read("Effects/Jeskola Distortion.dll"), b or self.read("Effects/Jeskola Distortion.x64.dll"),
                        list(sound) if sound else None, feeders)

    def test_the_same_parameters_and_bits_pass(self):
        self.assertEqual(self.judge(), ([], []))

    def test_within_the_line_passes_with_a_note(self):
        fails, notes = self.judge(sound=(False, -150.5, 0.4))
        self.assertEqual(fails, [])
        self.assertIn("within -100 dBFS", notes[0])

    def test_a_different_sound_fails_a_rebuilt_machine_and_is_reported_for_the_others(self):
        self.assertIn("plays 3.0 dBFS apart from 0.40 s", self.judge(sound=(False, 3.0, 0.4))[0][0])
        for role in ("fixed", "candidate"):
            fails, notes = self.judge(sound=(False, 3.0, 0.4), role=role)
            self.assertEqual(fails, [], role)
            self.assertIn("reported", notes[0], role)
        fails, notes = self.judge(sound=(False, 3.0, 0.4), feeders=["N2"])
        self.assertEqual(fails, [])
        self.assertIn("fed by N2", notes[0])

    def test_silence_proves_nothing(self):
        # run 42181254466: a process's first render left the natives silent in both phases, the same bits; and FSM Infector
        # is silent at both fractions of its ranges (run 42184661818): noted per song, failed when silent in every song
        silent = dict(self.read("Effects/Jeskola Distortion.x64.dll"), peak_dbfs=float("-inf"))
        fails, notes = self.judge(b=silent)
        self.assertEqual(fails, [])
        self.assertEqual(notes, ["silent in phase B (peak -inf dBFS): nothing to compare"])

    def test_a_parameter_that_moved_fails(self):
        fails, _ = self.judge(b=self.read("Effects/Jeskola Distortion.x64.dll", {"Threshold@-": 100, "Amount@-": 95}))
        self.assertEqual(fails, ["parameters differ: Amount@- 96 -> 95"])

    def test_the_wrong_file_fails(self):
        # the swap didn't take: phase B still loaded upstream's 32-bit DLL
        fails, _ = self.judge(b=self.read("Effects/Jeskola Distortion.dll"))
        self.assertEqual(fails, ["phase B loaded Effects/Jeskola Distortion.dll, not Effects/Jeskola Distortion.x64.dll"])

    def test_a_diverged_or_missing_machine_fails(self):
        self.assertEqual(self.judge(b=self.read("Effects/Jeskola Distortion.x64.dll", status="diverged"))[0], ["phase B: diverged"])
        # upstream's binary of a machine with a source fix may blow up from its heap's start (WahPro2's has): reported
        fails, notes = self.judge(a=self.read("Effects/Jeskola Distortion.dll", status="diverged"), role="fixed")
        self.assertEqual(fails, [])
        self.assertIn("phase A: diverged", notes[0])
        self.assertEqual(self.judge(b={"error": "not in the song"})[0], ["phase B: not in the song"])

    def test_the_swap_puts_one_machine_in_and_takes_it_out_again(self):
        with tempfile.TemporaryDirectory() as d:
            gear, hold = os.path.join(d, "Gear"), os.path.join(d, "held")
            os.makedirs(os.path.join(gear, "Effects"))
            for name, data in (("Jeskola Distortion.x64.dll", b"ours"), ("Jeskola Distortion.machine.json", b"{}"), ("Jeskola Delay.dll", b"d")):
                with open(os.path.join(gear, "Effects", name), "wb") as f:
                    f.write(data)
            up = os.path.join(d, "up.dll")
            with open(up, "wb") as f:
                f.write(b"upstream")
            sw = ns.Swap(gear, hold)
            sw.put("Effects", up, "Jeskola Distortion.dll")
            self.assertEqual(sorted(os.listdir(os.path.join(gear, "Effects"))),
                             ["Jeskola Delay.dll", "Jeskola Distortion.dll", "Jeskola Distortion.machine.json"])
            sw.restore()
            self.assertEqual(sorted(os.listdir(os.path.join(gear, "Effects"))),
                             ["Jeskola Delay.dll", "Jeskola Distortion.machine.json", "Jeskola Distortion.x64.dll"])
            with open(os.path.join(gear, "Effects", "Jeskola Distortion.x64.dll"), "rb") as f:
                self.assertEqual(f.read(), b"ours")

    def test_the_plan_finds_what_ships_rebuilt_and_the_candidates(self):
        with tempfile.TemporaryDirectory() as d:
            gear, cand = os.path.join(d, "Gear"), os.path.join(d, "R64")
            shipped = os.path.join(ROOT, "RunTime", "Gear")
            for sub in ("Effects", "Generators"):
                os.makedirs(os.path.join(gear, sub))
                os.makedirs(os.path.join(cand, sub))
            for folder, name, data in (("Effects", "Jeskola Distortion.x64.dll", b"rebuilt"),
                                       ("Effects", "FSM WahManPro.x64.dll", b"rebuilt with a fix")):
                with open(os.path.join(gear, folder, name), "wb") as f:
                    f.write(data)
            shutil.copyfile(os.path.join(shipped, "Effects", "Jeskola Delay.dll"), os.path.join(gear, "Effects", "Jeskola Delay.dll"))
            with open(os.path.join(cand, "Effects", "Jeskola Delay.x64.dll"), "wb") as f:
                f.write(b"x64 rebuild")
            got = {e["gear"]: (e["role"], e["upstream"][1], e["ours"][1]) for e in ns.plan(gear, shipped, cand)}
            self.assertEqual(got, {"FSM WahManPro": ("fixed", "FSM WahManPro.x64.dll", "FSM WahManPro.x64.dll"),
                                   "Jeskola Delay": ("candidate", "Jeskola Delay.dll", "Jeskola Delay.x64.dll"),
                                   "Jeskola Distortion": ("rebuilt", "Jeskola Distortion.dll", "Jeskola Distortion.x64.dll")})

    def test_the_dlls_platforms(self):
        self.assertEqual(ns.pe_machine(os.path.join(ROOT, "RunTime", "Gear", "Effects", "Jeskola Distortion.dll")), "x86")
        self.assertEqual(ns.pe_machine(os.path.join(ROOT, "RunTime", "Gear", "Effects", "FSM WahManPro.x64.dll")), "x64")

    def test_the_classic_songs_with_these_machines(self):
        songs = {os.path.basename(p): names for p, names in ns.classic_songs([{"gear": g} for g in ("Jeskola Distortion", "Jeskola Noise Generator")])}
        self.assertEqual(songs.get("Oomek_Aggressor_demo.bmx"), {"Dist": "Jeskola Distortion"})
        self.assertEqual(songs.get("Masterizer_test.bmx"), {"Noise.TR": "Jeskola Noise Generator"})

    def test_what_feeds_a_machine(self):
        graph = {"Master": ["B", "C"], "B": ["A"], "A": [], "C": ["B", "D"], "D": []}
        self.assertEqual(sorted(ns.fed_by(graph, "C", {"A", "D"})), ["A", "D"])
        self.assertEqual(ns.fed_by(graph, "B", {"D"}), [])


if __name__ == "__main__":
    unittest.main()
