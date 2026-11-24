# Tasks: how work is planned, claimed and merged

The scope of NeoBuzz Live is frozen (decided 2026-11-24): nothing new is added until everything in scope is done, unless the project owner overrides that in writing. This page is how two kinds of agents (Claude workers and Antigravity) build that scope in parallel without drifting. The rules every agent follows are in [AGENTS.md](../../AGENTS.md).

## The task list is GitHub issues
- **Every task in scope is one open issue labelled `scope`.** The issue body is the task's brief: what to build, the gates, the **Allowed paths** block, and what it depends on. If something has no scope issue, it is out of scope.
- **Owner labels:** `owner:claude` (Claude builds it) or `owner:antigravity` (Antigravity builds it). Every task starts as `owner:claude`. Only Claude moves a task to Antigravity, and only when the project owner asks for it.
- **Kind labels:** `kind:machine`, `kind:engine`, `kind:ux`, `kind:cleanup`, `kind:docs`, `kind:audit`.
- **Status:** an issue is open until the wave that contains it merges (the wave's pull request closes it). Progress and reports are comments on the issue.
- Only Claude creates, edits or closes scope issues, and only from the project owner's decisions.

## Who merges
Only integration waves merge into `main`. Claude builds `integrate/wave-N` from finished task branches, runs Fork CI, the full package and the Mac gallery on it, reviews the screenshots and reports, and merges it. Branch protection on `main` requires the `wave-gate` check, which passes only for `integrate/wave-N` branches, so no other branch can be merged by anyone.

## Checks on task branches
- `scope-guard` (`.github/workflows/scope-guard.yml`) runs on every push to an `ag/` branch: the branch must be named `ag/<issue>-<slug>`, the issue must be open and labelled `scope` and `owner:antigravity`, and the diff against `main` may touch only the issue's Allowed paths plus the shared registration files in [`.github/scope/shared-paths.txt`](../../.github/scope/shared-paths.txt).
- The normal gates: Fork CI, the package (quick on task branches), the Mac gallery when the UI changes, `tools/machine_catalog.py` with 0 problems, the guide tests, and the task's own gates.
- Sound: every new instrument or effect, and every sound change, comes with a listening pack the project owner approves before it merges (AGENTS.md §5).

## Offloading a task to Antigravity (when the project owner asks)
1. Claude picks a self-contained task (a new machine in its own folder is the best fit), writes its full brief into the issue, checks its Allowed paths block, and moves it from `owner:claude` to `owner:antigravity`. The issue gives the branch name: `ag/<issue>-<slug>`.
2. The project owner starts **two agents** in Antigravity's Agent Manager, in the repository folder: the workhorse on **Neon (High effort)** and the advisor on **Carbon (High effort)**, each with its prompt below (replace `N` and the branch name). They work together through the issue's comments (AGENTS.md §6).
3. When the `REPORT` comment is in, Claude reviews the branch like any worker's, asks for fixes in the issue if needed, and puts it in the next wave.

### Workhorse prompt (model: Neon, High effort)

> You are the workhorse for task issue #N of github.com/khuzul2/neo-buzz (NeoBuzz Live). Another agent, the advisor, reviews your plan and your result through the issue.
>
> 1. Setup: `git fetch origin`, `git switch -c BRANCH origin/main` (BRANCH is the branch the issue names), `git submodule update --init --recursive`. Read AGENTS.md completely, then docs/tasks/README.md, then the issue with all its comments: `gh issue view N -R khuzul2/neo-buzz --comments`. The issue is your brief; AGENTS.md is your rulebook.
> 2. Post a comment starting `SETUP CHECK` with the branch name and the output of `dotnet --list-sdks`, `gh auth status`, `git submodule status | head -5`, and a build of the test project the issue names. If anything fails, say what and stop.
> 3. Post a comment starting `PLAN READY` (design, parameters with units and ranges, editor layout, presets, the files you will add or change, how you will test each gate). Then poll the issue every 3 minutes until a comment starting `ADVISOR PLAN REVIEW` appears. Apply every required change, or reply in the issue if you disagree, before you write code.
> 4. Build exactly what the issue asks, following AGENTS.md: change only the issue's Allowed paths and the files in .github/scope/shared-paths.txt; no features the issue does not ask for. Commit in small steps and push to origin BRANCH; keep the `scope-guard` check green.
> 5. Run the issue's tests locally, then CI on your branch only, as AGENTS.md §5 and the issue say, and wait for the results. Read your Mac gallery screenshots right after the gallery run. Make the listening pack in listening-packs/N/ when the issue asks for one, with the click detector at 0.
> 6. Post a comment starting `FINAL READY` (head commit, CI run ids and results, screenshots read, pack and click results). Poll until `ADVISOR FINAL REVIEW` appears; fix every required item and post `FINAL READY` again until the verdict is OK.
> 7. Post the `REPORT` comment (AGENTS.md §2) and stop.
>
> Never open a pull request, merge, push to main or to another branch, delete a branch, `git stash` or `git reset --hard`. If anything in the issue is unclear or seems wrong, ask in the issue and wait for an answer.

### Advisor prompt (model: Carbon, High effort)

> You are the advisor for task issue #N of github.com/khuzul2/neo-buzz (NeoBuzz Live). You review; you never write code, commit or push.
>
> 1. Read AGENTS.md completely, docs/tasks/README.md, and the issue with all its comments: `gh issue view N -R khuzul2/neo-buzz --comments`.
> 2. Poll the issue every 3 minutes. When a comment starting `PLAN READY` appears, review it against the issue and AGENTS.md: nothing in the brief missing and nothing added; sound DSP and real units; clean room and no trademarked names; audio-thread rules (no allocation, no locks, smoothing, denormals); a test for every gate. Post a comment starting `ADVISOR PLAN REVIEW` with `Verdict: OK` or `Verdict: CHANGES REQUIRED`, numbered required changes, then suggestions.
> 3. When a comment starting `FINAL READY` appears: `git fetch origin` and review `git diff origin/main...origin/BRANCH` and the CI runs it names (`gh run view <id> -R khuzul2/neo-buzz`). Check: only allowed paths changed; every gate has a test that passes; CI green; screenshots read; listening pack and click results present when asked; code quality. Post a comment starting `ADVISOR FINAL REVIEW` with the verdict and numbered required fixes. Review again after each new `FINAL READY`.
> 4. Stop after a final review with `Verdict: OK`.

## Antigravity setup (once, on the project owner's computer)
- Install Antigravity, the .NET 10 SDK, git and the GitHub CLI; sign in to GitHub.
- `git clone --recurse-submodules https://github.com/khuzul2/neo-buzz.git` and open the folder in Antigravity. It reads AGENTS.md automatically.
- Antigravity pushes as the project owner's GitHub account, so its commits carry that name; its commit messages end with its own attribution.
