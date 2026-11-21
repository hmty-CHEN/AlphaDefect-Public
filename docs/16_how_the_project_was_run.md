# How the project was run

Knowing how the work was organized helps you read the records correctly.

## People and agents

- **One owner** (CHEN Qiyue) set the goals, chose between options, approved every change to the live training run, and decided when to stop experiments. The owner learned RL by doing this project.
- **AI coding agents** (mostly Claude Code sessions; Codex sessions worked on side tooling, see the `codex/*` branches in [`data/history/branches.txt`](../data/history/branches.txt)) wrote nearly all of the code, ran the experiments, operated the rented machines and wrote the reports, under the owner's instructions. Several sessions often worked in parallel on separate branches and worktrees (e.g. three sessions on the Rust engine, DR #148), with one "main session" merging and numbering the decision log.
- **"External expert reviews"** (seventeen of them, numbered in the decision log as the first, second, … review) were separate agent sessions prompted to act as an outside RL expert, with read-only access to code, logs and data and permission to search the literature. They are not human peer reviews. They found real problems (only ~84% of Act-1 data reached the learner; missing observation fields; an advantage-truncation detail) and also recommended things that did not help.
- **Planning by debate**: the 10-26 plan ([`zh/后续计划.md`](../zh/后续计划.md)) was synthesized from two planning agents with opposed mandates — "raise the clear rate fast" vs "attributable, reversible, statistically sound" — that reviewed each other's drafts.
- **Literature surveys** were agent-compiled; each note marks how every citation was verified ([14](14_research_reports_and_literature.md)).
- The owner also consulted a general chatbot (Gemini) at least twice: on the original technical plan (§10 of [`zh/技术路线.md`](../zh/技术路线.md) records what was taken and rejected) and on long-horizon credit (DR #260, which corrects one of its premises).

## The decision log as the system of record

[`zh/决策记录.md`](../zh/决策记录.md) is the project's memory. Each entry has:

1. a bold one-line decision with the date and, for training changes, the version where it took effect;
2. the owner's instruction **quoted verbatim** (in Chinese, after "用户：");
3. why, what exactly changed (config keys, files, commands), backups taken, and how to roll back;
4. verification and measured results, often with later addenda ("补记") that sometimes correct the original claim.

Because entries quote the owner and the agents wrote the rest, the log separates *who decided* from *who argued what*. Commit messages in the working repository follow the same rule (what changed / numbers before and after / basis); the full log is in [`data/history/git_log_master.txt`](../data/history/git_log_master.txt).

## Rules the agents worked under

Condensed from the project's agent instructions (the verbatim file is not included because it contains unrelated operational text):

- **Large samples or no conclusion.** For engine differential tests: random 200 games (3,479 frames) and starter 200 games (3,601 frames). The same code once read 98.17% agreement on 50 games and 86.26% on 200 — a 15× difference in the disagreement rate — and "fixed" had been declared several times on small samples.
- **Every change must pass the large-sample check** and not fall below the current baseline; otherwise revert the file. No half-finished changes left behind.
- **Confirm the binary was rebuilt** (check the timestamp) before testing; stale binaries caused several false results.
- **Two metrics, not one**: rule equivalence must not regress and live-import fidelity should improve; neither substitutes for the other.
- **Dump the real values** from the C# reference engine (`Observation.cs` probes built into a separate directory) instead of reasoning from comments; restore the file afterwards and never touch another session's probes.
- **Record failures completely**: what was tried, what happened, why it was reverted.
- **Shared worktrees**: never `git reset --hard` a worktree another agent uses (it once wiped two of another session's commits); add only your own paths and hunks; leave unknown uncommitted changes alone.
- Pitfalls written down for the next agent: one diff category often mixes several record types; `grep` counts are not evidence that something is unimplemented; read the assignment, not the doc comment; compare floats NaN-aware; fields are version-gated (formats 17/18/19 each added fields).

## Operating the training run

- Machines: B (RTX 4090) learner, A (RTX 3090) remote collector and evaluator, the owner's workstation for dashboards, solver labelling and analysis ([03](03_system_and_infrastructure.md)).
- Every restart takes a named backup first; every rollback is written as an executable command in the log.
- A watchdog restarts training from the last checkpoint unless a STOP marker exists.
- Guardrails pause training and notify; they never roll back on their own.
- Dashboards: a local HTML dashboard refreshed every few seconds, mirrored for a while to a public page (with replays), later reduced to a lightweight local page (DR #254, tools/dashboard*.py).
- The owner worked across the clock in bursts; the log shows decisions at all hours, and at least one documented hourly watch while the owner rested ([`zh/reports/休息期间巡检记录.md`](../zh/reports/休息期间巡检记录.md)).

## Pace and its consequences

291 decisions in 14 days means a decision roughly every hour of waking time. That pace explains both the project's strengths (a complete record, quick recovery from mistakes, many ideas tested) and its main weakness (bundled launches and short or interrupted experiments; see [11](11_evidence_gaps.md)).

## Language

Code comments, commit messages and most documents are in Chinese. The English documents in `docs/` are written for collaborators; the originals in `zh/` remain the reference. Machine translation of a single decision-log entry or report section works well; [glossary.md](glossary.md) maps the recurring terms.
