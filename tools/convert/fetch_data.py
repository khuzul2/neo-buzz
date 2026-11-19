#!/usr/bin/env python3
"""Fetches the evaluation data of Convert to MIDI (docs/specs/pitch.md section 9.6) onto the runner, with HTTP range requests into the
published zips (nothing is downloaded whole, nothing is committed or uploaded):

  gmd      Groove MIDI Dataset v1.0.0 (Magenta, CC BY 4.0): the test split, two recordings per drummer, 15 to 50 s of a drummer playing a
           beat on an electronic kit (Roland TD-11), with the MIDI the drummer played. Writes NAME.wav and NAME.json ([[seconds, GM note, velocity]]).
  maestro  MAESTRO v3.0.0 (Magenta, CC BY-NC-SA 4.0): the test split, spread over the years, 60 s of each recording (from 20 s in) with its
           MIDI (a Disklavier's, so the notes are exact). Writes NAME.wav and NAME.json ([[onset, offset, pitch]] seconds, MIDI note).

    fetch_data.py --gmd DIR [--gmd-per-drummer 2] --maestro DIR [--maestro-count 10]
"""
import argparse, collections, csv, io, json, os, random, wave


def notes_of(midi_bytes, t0=0.0, t1=1e9):
    import mido
    mid = mido.MidiFile(file=io.BytesIO(midi_bytes))
    t = 0.0
    on = {}
    out = []
    sustain = False
    held = []
    for msg in mid:
        t += msg.time
        if msg.type == "note_on" and msg.velocity > 0:
            on.setdefault(msg.note, []).append((t, msg.velocity))
        elif msg.type in ("note_off", "note_on"):
            starts = on.get(msg.note)
            if starts:
                s, v = starts.pop(0)
                out.append((s, t, msg.note, v))
    return out


def gmd(dest, per):
    from remotezip import RemoteZip
    random.seed(7)
    z = RemoteZip("https://storage.googleapis.com/magentadata/datasets/groove/groove-v1.0.0.zip")
    rows = list(csv.DictReader(io.TextIOWrapper(z.open("groove/info.csv"), "utf-8")))
    rows = [r for r in rows if r["audio_filename"] and 15 < float(r["duration"]) < 50 and r["beat_type"] == "beat" and r["split"] == "test"]
    by = collections.defaultdict(list)
    for r in rows:
        by[r["drummer"]].append(r)
    os.makedirs(dest, exist_ok=True)
    for drummer, items in sorted(by.items()):
        random.shuffle(items)
        for r in items[:per]:
            name = r["audio_filename"].replace("/", "_")[:-4]
            with open(os.path.join(dest, name + ".wav"), "wb") as f:
                f.write(z.read("groove/" + r["audio_filename"]))
            hits = [[round(s, 4), n, v] for s, e, n, v in notes_of(z.read("groove/" + r["midi_filename"]))]
            hits.sort()
            with open(os.path.join(dest, name + ".json"), "w") as f:
                json.dump(hits, f)
    print("gmd:", len(os.listdir(dest)) // 2, "recordings")


def maestro(dest, count, start=20.0, length=60.0):
    from remotezip import RemoteZip
    z = RemoteZip("https://storage.googleapis.com/magentadata/datasets/maestro/v3.0.0/maestro-v3.0.0.zip")
    rows = list(csv.DictReader(io.TextIOWrapper(z.open("maestro-v3.0.0/maestro-v3.0.0.csv"), "utf-8")))
    rows = [r for r in rows if r["split"] == "test" and float(r["duration"]) > start + length]
    rows.sort(key=lambda r: (r["year"], r["audio_filename"]))
    picks = [rows[int(i * len(rows) / count)] for i in range(count)]
    os.makedirs(dest, exist_ok=True)
    for r in picks:
        name = os.path.basename(r["audio_filename"])[:-4]
        raw = z.read("maestro-v3.0.0/" + r["audio_filename"])
        with wave.open(io.BytesIO(raw)) as w:
            rate, ch, width = w.getframerate(), w.getnchannels(), w.getsampwidth()
            w.setpos(int(start * rate))
            frames = w.readframes(int(length * rate))
        with wave.open(os.path.join(dest, name + ".wav"), "wb") as o:
            o.setnchannels(ch); o.setsampwidth(width); o.setframerate(rate); o.writeframes(frames)
        notes = [[round(s - start, 4), round(min(e, start + length) - start, 4), n] for s, e, n, v in notes_of(z.read("maestro-v3.0.0/" + r["midi_filename"]))
                 if e > start and s < start + length]
        notes = [[max(0.0, a), b, n] for a, b, n in notes]
        notes.sort()
        with open(os.path.join(dest, name + ".json"), "w") as f:
            json.dump(notes, f)
    print("maestro:", len(picks), "excerpts")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--gmd"); ap.add_argument("--gmd-per-drummer", type=int, default=2)
    ap.add_argument("--maestro"); ap.add_argument("--maestro-count", type=int, default=10)
    a = ap.parse_args()
    if a.gmd: gmd(a.gmd, a.gmd_per_drummer)
    if a.maestro: maestro(a.maestro, a.maestro_count)
