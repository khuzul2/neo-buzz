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
1. Claude picks a self-contained task (a new machine in its own folder is the best fit), checks its Allowed paths block, and moves the issue from `owner:claude` to `owner:antigravity`.
2. The project owner opens the repository in Antigravity and gives it this prompt, with the issue number:

   > Do GitHub issue #N of khuzul2/neo-buzz. Read AGENTS.md and the issue first and follow them exactly. Work on a branch named ag/N-<short-slug> from the latest origin/main. Build and run the tests the issue names locally, push the branch, run the CI the issue names on that branch only, and post your report as a comment on the issue. Do not open a pull request, do not merge, and do not change anything outside the issue's Allowed paths. If anything in the issue is unclear or seems wrong, ask in the issue instead of guessing.

3. When the report is in, Claude reviews the branch like any worker's, asks for fixes in the issue if needed, and puts it in the next wave.

## Antigravity setup (once, on the project owner's computer)
- Install Antigravity, the .NET 10 SDK, git and the GitHub CLI; sign in to GitHub.
- `git clone --recurse-submodules https://github.com/khuzul2/neo-buzz.git` and open the folder in Antigravity. It reads AGENTS.md automatically.
- Antigravity pushes as the project owner's GitHub account, so its commits carry that name; its commit messages end with its own attribution.
