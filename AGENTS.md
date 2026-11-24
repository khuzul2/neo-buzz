# Rules for coding agents working on NeoBuzz Live

Every agent that changes this repository follows these rules: Claude workers, Antigravity agents and any other. They are the same for everyone. When a task's brief and this file disagree, ask in the task's issue; don't guess.

## 1. Scope: only what a task issue says
- The scope is frozen. The only work allowed is an **open GitHub issue labelled `scope`** whose owner label names you: `owner:antigravity` for Antigravity, `owner:claude` for Claude workers. No issue, no work.
- Do exactly what the issue's brief says. An idea, a fix outside the task or a "while I'm here" change goes into a comment on the issue, never into code.
- Change only the paths the issue lists under **Allowed paths**, plus the shared registration files in `.github/scope/shared-paths.txt` when the task adds a machine. The `scope-guard` check enforces this for `ag/` branches.
- Never push to `main`, never merge into `main`, never open a pull request into `main`. Only integration waves (`integrate/wave-N`) merge into `main`; the `wave-gate` check refuses anything else.

## 2. Branches, commits, reports
- Antigravity: branch **`ag/<issue number>-<short-slug>`** from the latest `origin/main`. Claude workers: the branch the brief names.
- Merge `origin/main` into your branch before your final test runs; resolve conflicts keeping both sides; regenerate `docs/agent-coverage.md` with `python3 tools/agent-coverage.py` instead of hand-merging it.
- Never `git stash`, never `git reset --hard`, never rewrite pushed history, never delete branches.
- Commit messages say what changed and why, in plain English. End them with the attribution your own tool provides.
- When done: push the branch and post a **report as a comment on the issue**: branch and head commit, CI run ids and results, what was built, decisions you made, gates met or not (with numbers), limits, the screenshots you checked, `git diff --stat origin/main...HEAD`.

## 3. Product rules
- **Clean break:** NeoBuzz Live opens and saves only `.nbl` songs and runs only Neo machines and the internal ones. No code for old Buzz songs, formats or machines.
- **Clean room:** new machines and models come from published literature and our own DSP. Never copy code, parameter tables or presets from another product or legacy machine, and don't open their sources.
- **No trademarked names** in UI text, machine names, preset names, guides or docs: no brands, product models or artists. Name sounds by what they are.
- **Real units, versioned state:** parameters in real units (dB, Hz, ms, %, cents) through `Params.def` and `tools/gen-machine-params.py`; presets in `Presets.txt` through `tools/gen-machine-presets.py`; every machine's state is versioned, and new parameters are appended so saved songs, presets, MIDI maps and automation load and sound the same.
- **Every effect has Mix** (dry/wet, latency-compensated) **and Amount** (effect strength, 0–200 %, 100 % = the settings as they are, 0 % = bit-exact dry), through the shared helper. Only meters and analysers, which pass audio unchanged, are exempt.
- **Presets are clearly audible** at first listen on full-scale material and clearly different from each other. Subtle is a bug.
- **Audio thread:** 0 bytes allocated per buffer, no locks the UI can hold, no denormals, all parameters smoothed (no zipper noise above −60 dB under automation).
- **Licences:** training data CC BY or CC0 only; never commit model weights, third-party audio, rendered audio, features or evaluation data.
- Keep each file's BOM and line endings as they are.

## 4. A new machine needs all of this
Follow the newest complete Neo machine as the pattern (see `docs/specs/machines.md`): the project folder under `Machines/`, the Portable and Studio projects, the solution entry, the manifest (`.machine.json`), the machine-profile and catalog entries (`python3 tools/machine_catalog.py` must report 0 problems), factory presets, the editor in the house style (Mac gallery shots, light and dark), the agent guide in `ReBuzz.Agent/Guides/machines/` within its token budget (and an agent-eval task if the self-test asks), the guide test lists, unit tests for every gate in the brief, a row in `docs/specs/machines.md` and in `FORK.md`.

## 5. Tests and CI
- Build with `-m:1` if the machine is small; run the filtered tests of what you changed locally, then CI is the full check.
- CI runs only on your own branch: Fork CI (`fork-ci.yml`), the package (`fork-package.yml`, `quick=true` on task branches; the full package runs on the wave) and, when the UI changed, the Mac gallery (`fork-mac.yml` with `gallery=true`). Rerun a job lost to runner capacity once and say so; don't rerun real failures, fix them.
- Gallery screenshots: the branch `ci-artifacts-mac` holds the latest gallery run only. Read your shots right after your run (`git show origin/ci-artifacts-mac:headless/<file>` and `headless-light/`) and check them yourself.
- **Listening packs** (every new instrument or effect, and every sound change): renders of every preset on the brief's sources, 48 kHz WAV, kept local and never committed, with a README. Run a click detector (second difference against its local mean) on every render: 0 clicks unless the sound is meant to click. Blind A/B packs put each version in slot A in exactly half the items (±1), chosen by a seeded shuffle. The user approves the sound before the machine merges.

## 6. Safety
- Never kill processes by name, only ones you started, by id. Don't work around a failing safety check, proxy or permission refusal: report it in the issue.
- No paid APIs or services without the user's approval in the issue.
