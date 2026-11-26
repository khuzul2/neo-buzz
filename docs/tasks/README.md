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
1. Claude picks a self-contained task (a new machine in its own folder is the best fit), writes its full brief into the issue, checks its Allowed paths block, moves it from `owner:claude` to `owner:antigravity` and adds the label `ag:ready`. The issue gives the branch name: `ag/<issue>-<slug>`.
2. Two Antigravity agents run all the time, each in a loop that waits for its turn on GitHub: the **workhorse** on **Neon (High effort)** and the **advisor** on **Carbon (High effort)**. The project owner starts each once with its standing prompt below, and again only when one stops (tokens out, or its conversation ends); it picks up from the labels.
3. At `ag:done` Claude makes the listening page for the project owner and puts the branch in the next wave. If the owner wants changes, Claude posts a `COORDINATOR NOTE` with numbered fixes and sets `ag:fix`.

### The loop: one label says whose turn it is
Each task issue carries exactly one `ag:*` label.

| Label | Whose turn | What to do, then which label to set |
|---|---|---|
| `ag:ready` | workhorse | Read the issue, then post `SETUP CHECK` and `PLAN READY`. Set `ag:plan-review`. |
| `ag:plan-review` | advisor | Post `ADVISOR PLAN REVIEW`. Set `ag:go` if the verdict is OK, else `ag:plan-fix`. |
| `ag:plan-fix` | workhorse | Revise the plan and post `PLAN READY` again. Set `ag:plan-review`. |
| `ag:go` | workhorse | Implement; CI once (AGENTS.md §5); read the shots; build the listening pack. Post `FINAL READY`. Set `ag:review`. |
| `ag:review` | advisor | Post `ADVISOR FINAL REVIEW` from `git diff origin/main...origin/<branch>` only. Set `ag:approved` if OK, else `ag:fix`. |
| `ag:fix` | workhorse | Apply the numbered required fixes in the latest `ADVISOR FINAL REVIEW` or `COORDINATOR NOTE`; CI once. Post `FINAL READY`. Set `ag:review`. |
| `ag:approved` | workhorse | Post `REPORT` (AGENTS.md §2). Set `ag:done`. |
| `ag:done` | Claude | Listening page, then an integration wave; or a `COORDINATOR NOTE` and `ag:fix`. |

Change a label: `tools/ag-wait.sh swap <issue> <old label> <new label>`.

Between tasks each agent runs `tools/ag-wait.sh workhorse` (or `advisor`) and nothing else. It checks GitHub every 5 minutes and spends no tokens while it waits; when it prints `TASK <issue> <label>`, the agent does that step and runs it again. If the tool stops it before it prints, the agent runs it again.

Token rules: read the issue body once per task, then only the comments newer than your own last one (`gh api "repos/khuzul2/neo-buzz/issues/N/comments?per_page=100" --jq '.[-5:][]|.body'`); grep CI logs, never read them whole; one task at a time; the advisor never builds or runs anything. `COORDINATOR NOTE` comments come from Claude: they are binding and override older instructions on that issue.

### Workhorse standing prompt (model: Neon, High effort)

> You are the NeoBuzz Live workhorse. Read AGENTS.md and the loop in docs/tasks/README.md, then run `tools/ag-wait.sh workhorse` and follow the loop forever: when it prints `TASK <n> <label>`, do that step on issue n as the table says (for `ag:ready`: `git fetch origin`, `git switch -c <branch the issue names> origin/main`, `git submodule update --init --recursive` first), set the next label, then run the wait command again. Never open a pull request, merge, push to main or another branch, delete a branch, `git stash` or `git reset --hard`. If the issue is unclear, ask in it and leave the label as it is. Never stop unless I tell you.

### Advisor standing prompt (model: Carbon, High effort)

> You are the NeoBuzz Live advisor. Read AGENTS.md and the loop in docs/tasks/README.md, then run `tools/ag-wait.sh advisor` and follow the loop forever: when it prints `TASK <n> <label>`, post the review the table says for issue n, set the next label, then run the wait command again. Review a plan against the issue and AGENTS.md (nothing missing, nothing added, sound DSP in real units, clean room, no trademarked names, audio-thread rules, a test for every gate), and a result from `git diff origin/main...origin/<branch>` and the CI runs it names. You never write code, build, commit or push. Never stop unless I tell you.

## Antigravity setup (once, on the project owner's computer)
- Install Antigravity, the .NET 10 SDK, git and the GitHub CLI; sign in to GitHub.
- `git clone --recurse-submodules https://github.com/khuzul2/neo-buzz.git` and open the folder in Antigravity. It reads AGENTS.md automatically.
- Antigravity pushes as the project owner's GitHub account, so its commits carry that name; its commit messages end with its own attribution.
