# Fork (docs/specs/mcp-server.md §12.14): unit tests for tools/native-compare.py's gate on the natives with a source
# fix (judge_fixed), fed the groupings the package job has seen, so the verdict on each is known without Windows.
# Run: python3 -m unittest discover -s tools -p "test_*.py"

import importlib.util
import os
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("native_compare", os.path.join(HERE, "native-compare.py"))
nc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(nc)

UP = ["shipped", "shipped 2", "shipped 3", "shipped 4"]
ROLES = {**{u: "upstream" for u in UP}, "package (rebuilt)": "package (rebuilt)", "R64": "rebuilt", "R86": "rebuilt",
         "U64": "unfixed"}
BASE = ["default", "quarter", "three-quarters", "quiet-input"]


def case(bits, apart=2.0):
    """One case's entries from {label: what it rendered} (equal values: the same bits; else `apart` dBFS apart)."""
    out = {}
    for label, b in bits.items():
        e = {"status": "ok", "sha1": f"sha-{b}"}
        if ROLES.get(label, "upstream") != "upstream":
            ds = {u: [bits[u] == b, float("-inf") if bits[u] == b else apart, None if bits[u] == b else 0.01]
                  for u in UP if u in bits}
            e["as_upstream"] = [u for u, d in ds.items() if d[0] or d[1] <= nc.TOLERANCE_DBFS]
            e["closest_upstream"] = min(ds.values(), key=lambda d: d[1])
            e["vs"] = {o: [bits[o] == b, float("-inf") if bits[o] == b else apart, None if bits[o] == b else 0.5]
                       for o in bits if o != label and ROLES.get(o) != "upstream"}
        out[label] = e
    return out


def fixed_bits(up, ours="F", r86=None, unfixed=None):
    """Upstream's four instances rendering `up` (four values), our builds `ours` (R86 and U64 may differ)."""
    return {**dict(zip(UP, up)), "package (rebuilt)": ours, "R64": ours, "R86": r86 or ours, "U64": unfixed or ours}


def machine(before, settled):
    """{case: entries}: the four cases from the start, then the four settled ones."""
    cases = {c: case(b) for c, b in zip(BASE, before)}
    cases.update({"settled " + c: case(b) for c, b in zip(BASE, settled)})
    return cases


SETTLED_OK = [fixed_bits("FFFF")] * 4


class FixedGateTests(unittest.TestCase):
    def gate(self, cases):
        return nc.judge_fixed(cases, ROLES)["package (rebuilt)"]

    def test_run_41983193582_passes(self):
        # WahMan in run 41983193582: at the defaults all four upstream instances agreed on a start that isn't the
        # fix's (the old gate failed); at the other cases they split as below. Once settled they render ours.
        j = self.gate(machine([fixed_bits("GGGG"), fixed_bits("FHFH"), fixed_bits("FHFF"), fixed_bits("FHHF")], SETTLED_OK))
        self.assertEqual(j["failures"], [])
        self.assertIn("once settled as upstream's instances (4/4, 4/4, 4/4, 4/4)", j["verdict"])
        self.assertIn("from the start as 0/4, 2/4, 3/4, 2/4", j["verdict"])
        self.assertIn("the same bits as R64/R86/U64", j["verdict"])

    def test_one_upstream_instance_settled_is_enough(self):
        # An instance whose start was NaN or far out never converges: three of four like that still pass.
        settled = [fixed_bits("FXYZ"), fixed_bits("XFXX"), fixed_bits("FFFF"), fixed_bits("XXXF")]
        self.assertEqual(self.gate(machine(SETTLED_OK, settled))["failures"], [])

    def test_a_broken_rebuild_fails_once_settled(self):
        # Our builds (and the unfixed one, built the same way) render something upstream's instances never do.
        bad = [{**dict(zip(UP, "UUUU")), "package (rebuilt)": "F", "R64": "F", "R86": "F", "U64": "F"}] * 4
        j = self.gate(machine([fixed_bits("UUUU")] * 4, bad))
        self.assertEqual(len(j["failures"]), 4)
        self.assertIn("settled default: unlike every instance of upstream's (2.0 dBFS, from 0.01 s)", j["failures"][0])
        self.assertIn("the rebuild doesn't play upstream's DSP", j["failures"][0])

    def test_upstream_disagreeing_after_the_preroll_is_named(self):
        bad = [fixed_bits("ABCD")] + SETTLED_OK[1:]
        j = self.gate(machine(SETTLED_OK, bad))
        self.assertEqual(len(j["failures"]), 1)
        self.assertIn("upstream's own instances render shipped | shipped 2 | shipped 3 | shipped 4", j["failures"][0])

    def test_x86_unlike_x64_fails(self):
        j = self.gate(machine([fixed_bits("FFFF", r86="G")] + [fixed_bits("FFFF")] * 3, SETTLED_OK))
        self.assertEqual(len(j["failures"]), 1)
        self.assertIn("not the same bits as R86 in default (2.0 dBFS, from 0.50 s)", j["failures"][0])
        self.assertIn("the fix leaves state unset", j["failures"][0])

    def test_a_fix_that_isnt_the_zeroed_start_fails(self):
        # A fix that starts somewhere else than a fresh allocation: alike in every instance and once settled,
        # but not upstream's code from a zeroed start.
        j = self.gate(machine([fixed_bits("FFFF", unfixed="Z")] * 4, SETTLED_OK))
        self.assertEqual(len(j["failures"]), 1)
        self.assertIn("not the same bits as U64 (upstream's code from a zeroed start) in default, quarter, three-quarters, quiet-input",
                      j["failures"][0])

    def test_a_diverged_case_fails(self):
        cases = machine(SETTLED_OK, SETTLED_OK)
        cases["quarter"]["package (rebuilt)"]["status"] = "diverged"
        self.assertIn("quarter: diverged", self.gate(cases)["failures"])

    def test_unfixed_build_gets_a_verdict_not_a_gate(self):
        j = nc.judge_fixed(machine([fixed_bits("GGGG", unfixed="G")] * 4, SETTLED_OK), ROLES)["U64"]
        self.assertIn("from the start as 4/4, 4/4, 4/4, 4/4", j["verdict"])
        self.assertEqual(j["failures"], [])


class CaseTests(unittest.TestCase):
    def test_fixed_effects_add_settled_cases(self):
        plain = nc.case_list("effect")
        cases = nc.case_list("effect", fixed=True)
        self.assertEqual([c[0] for c in plain], BASE)
        self.assertEqual([c[0] for c in cases], BASE + ["settled " + c for c in BASE])
        self.assertTrue(all(c[3] == 0 for c in plain))
        self.assertTrue(all(c[3] == nc.PREROLL for c in cases[4:]))
        self.assertEqual([c[1:3] for c in cases[4:]], [c[1:3] for c in plain])

    def test_preroll_outlasts_the_slowest_glide(self):
        # FSM WahPro2 at three quarters of its inertia (raw 45 of 0…60): 10·e^(−9·45/240) cutoff units per 64 samples.
        import math
        step = 10 * math.exp(-9 * 45 / 240)
        blocks = nc.PREROLL * 60 / nc.TEMPO * 44100 / 64
        self.assertGreater(step * blocks, 480 * 5)


if __name__ == "__main__":
    unittest.main()
