# Fork (docs/specs/mcp-server.md §12.14): unit tests for tools/native-compare.py's gate on the natives with a source fix
# (judge_fixed), fed the groupings the package job has seen, so the verdict on each is known without Windows; and for
# tools/native-zeroheap.py, which makes upstream's binaries run on a zeroed heap, on the binaries in RunTime/Gear.
# Run: python3 -m unittest discover -s tools -p "test_*.py"

import importlib.util
import os
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


if __name__ == "__main__":
    unittest.main()
