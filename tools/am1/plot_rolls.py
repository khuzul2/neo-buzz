#!/usr/bin/env python3
"""Piano-roll plots of Neo Audio to MIDI on real recordings: what was annotated against what the engine played (docs/specs/audio-to-midi.md section 8).
Reads the JSON excerpts tools/PitchEval writes (--am1-plots DIR) and writes one PNG each, next to it.

  Melody: the annotated notes as grey bars and the notes the machine played as coloured bars over them, on a piano roll, with a ruler of seconds.
  Drums: a lane per drum (kick, snare, hat); the annotated hits as grey ticks above, the machine's below.

    plot_rolls.py DIR
"""
import json, os, sys
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

NAMES = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
GREY, BLUE, ORANGE, TEAL = "#8a8f98", "#2f6fdb", "#d9822b", "#1b9e8f"


def melody(ax, d):
    lo = min([n[2] for n in d["truth"] + d["out"]] + [60]) - 2
    hi = max([n[2] for n in d["truth"] + d["out"]] + [60]) + 3
    for n in range(int(lo), int(hi) + 1):
        if n % 12 == 0:
            ax.axhline(n - 0.5, color="#d0d3d9", lw=0.6)
            ax.text(d["from"], n - 0.4, f"C{n // 12 - 1}", fontsize=7, color="#5c6370", va="bottom")
    for s, e, m in d["truth"]:
        ax.barh(round(m), e - s, left=s, height=0.8, color=GREY, alpha=0.55, edgecolor=GREY, lw=0.5, label="annotated")
    for s, e, m in d["out"]:
        ax.barh(round(m), max(e - s, 0.03), left=s, height=0.42, color=BLUE, edgecolor="none", label="played")
        ax.plot([s], [round(m)], marker="|", color=ORANGE, ms=7, mew=1.2, linestyle="none", label="note on")
    ax.set_ylim(lo, hi)
    ax.set_ylabel("MIDI note")


def drums(ax, d):
    for c, name in enumerate(["kick", "snare", "hat"]):
        y = 2 - c
        ax.axhline(y, color="#d0d3d9", lw=0.6)
        ax.text(d["from"], y + 0.36, name, fontsize=8, color="#5c6370")
    for t, c in d["truth_hits"]:
        ax.plot([t, t], [2 - c + 0.05, 2 - c + 0.32], color=GREY, lw=2, solid_capstyle="butt", label="annotated")
    for t, c in d["out_hits"]:
        ax.plot([t, t], [2 - c - 0.32, 2 - c - 0.05], color=TEAL, lw=2, solid_capstyle="butt", label="played")
    ax.set_ylim(-0.6, 2.7)
    ax.set_yticks([])


def main():
    d = sys.argv[1]
    for name in sorted(os.listdir(d)):
        if not name.endswith(".json"):
            continue
        x = json.load(open(os.path.join(d, name)))
        fig, ax = plt.subplots(figsize=(11, 3.6 if x["mode"] == "melody" else 2.6), dpi=110)
        (melody if x["mode"] == "melody" else drums)(ax, x)
        ax.set_xlim(x["from"], x["to"])
        ax.set_xlabel("seconds")
        ax.set_title(x["title"], fontsize=9, loc="left")
        h, l = ax.get_legend_handles_labels()
        seen = dict(zip(l, h))
        ax.legend(seen.values(), seen.keys(), loc="upper right", fontsize=7, ncol=3, frameon=False)
        for s in ("top", "right"):
            ax.spines[s].set_visible(False)
        fig.tight_layout()
        fig.savefig(os.path.join(d, name[:-5] + ".png"))
        plt.close(fig)
        print("wrote", name[:-5] + ".png")


if __name__ == "__main__":
    main()
