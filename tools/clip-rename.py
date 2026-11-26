#!/usr/bin/env python3
"""Apply tools/clip-rename-map.txt (old identifier -> new identifier) to C# source.

CU5 (docs/specs/clips.md section 9): the patterns became clips in the code. This is the tool that did it, and the one that
brings an upstream ReBuzz patch into this fork's names before `git am -3` (FORK.md, "Cherry-picks").

    python3 tools/clip-rename.py FILE...            rewrite C# files in place
    python3 tools/clip-rename.py --patch < up.patch > ours.patch
                                                    rewrite the added and context lines of a unified diff

What it changes: identifier tokens of C# code (also inside the holes of interpolated strings), listed whole in the map.
What it leaves alone: string and character literals, comments and preprocessor lines, so a name that a saved file, a
machine's reflection lookup or a wire protocol stores never changes by accident. (In a patch each line is read on its
own, so a line inside a block comment that starts on an earlier line is read as code: check such hunks.)

    --check FILE...     print the identifiers of the map that are still in the files (exit 1 if any)
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MAP_FILE = os.path.join(HERE, "clip-rename-map.txt")
IDENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")


def load_map(path=MAP_FILE):
    m = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.split("#", 1)[0].strip()
            if not line:
                continue
            parts = line.split()
            if len(parts) != 2:
                raise SystemExit("bad map line: " + line)
            m[parts[0]] = parts[1]
    return m


def rename_code(text, m, hits=None):
    """Return text with the identifier tokens of C# code renamed by the map m."""
    out = []
    i = 0
    n = len(text)
    # A stack of interpolation contexts: each entry is the brace depth at which the hole started and the string kind.
    stack = []
    brace = 0

    def ident_at(j):
        mo = IDENT.match(text, j)
        return mo

    def read_string(j, verbatim, interp, raw_quotes=0):
        """Copy a string body starting after the opening quote. Returns (new index, ended_in_hole)."""
        nonlocal out
        k = j
        while k < n:
            c = text[k]
            if raw_quotes:
                if text.startswith('"' * raw_quotes, k):
                    out.append(text[j:k + raw_quotes])
                    return k + raw_quotes, False
                if interp and c == "{":
                    out.append(text[j:k + 1])
                    stack.append((brace, verbatim, True, raw_quotes))
                    return k + 1, True
                k += 1
                continue
            if verbatim:
                if c == '"':
                    if k + 1 < n and text[k + 1] == '"':
                        k += 2
                        continue
                    out.append(text[j:k + 1])
                    return k + 1, False
            else:
                if c == "\\":
                    k += 2
                    continue
                if c == '"':
                    out.append(text[j:k + 1])
                    return k + 1, False
                if c == "\n":  # unterminated: stop at the line end
                    out.append(text[j:k])
                    return k, False
            if interp and c == "{":
                if k + 1 < n and text[k + 1] == "{":
                    k += 2
                    continue
                out.append(text[j:k + 1])
                stack.append((brace, verbatim, False, 0))
                return k + 1, True
            k += 1
        out.append(text[j:])
        return n, False

    def resume_string(j, ctx):
        _, verbatim, _, raw = ctx
        return read_string(j, verbatim, True, raw)

    while i < n:
        c = text[i]
        # inside an interpolation hole, a closing brace at the hole's depth returns to the string
        if c == "{":
            brace += 1
            out.append(c)
            i += 1
            continue
        if c == "}":
            if stack and stack[-1][0] == brace:
                ctx = stack.pop()
                out.append(c)
                i += 1
                i, _ = resume_string(i, ctx)
                continue
            brace -= 1
            out.append(c)
            i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(text[i:j])
            i = j
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(text[i:j])
            i = j
            continue
        if c == "#" and (i == 0 or text[i - 1] == "\n" or text[:i].rsplit("\n", 1)[-1].strip() == ""):
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(text[i:j])
            i = j
            continue
        if c == "'":
            # a character literal: 'x', '\'' or 'A'
            j = i + 1
            if j < n and text[j] == "\\":
                j += 2
            else:
                j += 1
            while j < n and text[j] != "'" and text[j] != "\n":
                j += 1
            j = min(n, j + 1)
            out.append(text[i:j])
            i = j
            continue
        if c == '"' or ((c == "$" or c == "@") and i + 1 < n and text[i + 1] in '"$@'):
            # a string: find the prefix
            j = i
            verbatim = interp = False
            dollars = 0
            while j < n and text[j] in "$@":
                if text[j] == "$":
                    interp = True
                    dollars += 1
                else:
                    verbatim = True
                j += 1
            if j < n and text[j] == '"':
                q = 0
                while j + q < n and text[j + q] == '"':
                    q += 1
                if q >= 3:
                    out.append(text[i:j + q])
                    i, _ = read_string(j + q, False, interp, q)
                    continue
                out.append(text[i:j + 1])
                i, _ = read_string(j + 1, verbatim, interp)
                continue
            # not a string after all ("@name" verbatim identifier)
        mo = ident_at(i) if (c.isalpha() or c == "_") else None
        if mo:
            word = mo.group(0)
            prev = text[i - 1] if i else ""
            if word in m and prev != "$":
                out.append(m[word])
                if hits is not None:
                    hits[word] = hits.get(word, 0) + 1
            else:
                out.append(word)
            i = mo.end()
            continue
        if c.isdigit():
            j = i
            while j < n and (text[j].isalnum() or text[j] in "._"):
                j += 1
            out.append(text[i:j])
            i = j
            continue
        out.append(c)
        i += 1
    return "".join(out)


def read_file(path):
    with open(path, "rb") as f:
        raw = f.read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw[3:].decode("utf-8") if bom else raw.decode("utf-8")
    return text, bom


def write_file(path, text, bom):
    data = text.encode("utf-8")
    with open(path, "wb") as f:
        f.write((b"\xef\xbb\xbf" if bom else b"") + data)


def patch_main(m):
    out = []
    for line in sys.stdin.read().split("\n"):
        if line[:1] in (" ", "+") and not line.startswith("+++"):
            out.append(line[0] + rename_code(line[1:], m))
        else:
            out.append(line)
    sys.stdout.write("\n".join(out))


def main(argv):
    m = load_map()
    if argv and argv[0] == "--patch":
        patch_main(m)
        return 0
    if argv and argv[0] == "--check":
        left = 0
        for p in argv[1:]:
            text, _ = read_file(p)
            hits = {}
            rename_code(text, m, hits)
            for k, v in sorted(hits.items()):
                print(f"{p}: {k} x{v}")
                left += v
        return 1 if left else 0
    total = {}
    for p in argv:
        text, bom = read_file(p)
        new = rename_code(text, m, total)
        if new != text:
            write_file(p, new, bom)
    print(f"{sum(total.values())} identifier tokens renamed in {len(argv)} files", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
