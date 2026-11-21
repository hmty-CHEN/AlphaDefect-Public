# Incidents and rollbacks

Every rollback of the main line, with what caused it and what changed afterwards. Rollback commands, backup file names and the overwritten version ranges are in the decision log entries cited.

| Date | Event | Response | DR |
|---|---|---|---|
| 10-25 | Deck-building training drifted: a deck-score head paid reward, out-of-combat advantages were normalized separately and weighted ×2, Act-1 practice was halved. Training mean floor 18.4 → 13.75, evaluation ~21 → 12.4 floors. | Rolled back to v9,400 (the earliest surviving backup; no pre-change checkpoint existed). Redesign: separate out-of-combat heads, deck score diagnostic only, out-of-combat loss weight 2 → 1. Backups every 30 minutes and top-5 evaluation checkpoints archived from then on. Root cause never isolated. | #67 |
| 10-28 | A group of trials (policy collects gold with a phase-in, skip penalties, HP shaping off) | Rolled back to v59,098; trials withdrawn | #160, #162 |
| 10-29 | All parameters NaN from v68,160 | Rolled back to v67,920; learner skips non-finite-gradient minibatches; NaN batches archived for offline analysis, which could not reproduce it (DR #176) | #173 |
| 10-29 evening | Stacked changes: map entropy 0.1, entropy changes, win-probability potential, progress potentials | **Four rollbacks to v80,200** in a little over three hours, the strongest full backup at the time. Post-mortem: the baseline was a cherry-picked peak; several changes mixed; nothing measured cleanly. Old rewards and combat entropy restored; Act-3 chain practice kept. | #186, #190–#192 |
| 11-01 | LR 1.65e-4 trial: KL up, more clipping, no gain | Reverted after 736 versions; weights back to v136,503 | #228–#229 |
| 11-03 | Cross-segment out-of-combat advantages + λ 0.98 + targeted exploration off (three at once) | Reverted after ~200 versions; weights back to v158,635; training paused to collect interventional data | #256–#257 |
| 11-04 → 11-05 | Out-of-combat attention expansion regressed (73.3% → 69.6%) | Kept running at the owner's choice; removed when the main line switched to student e | #262, #274 |
| 11-05 | Continued PPO from student e drifted down (~77% → ~74%) | Restarted from student e with lower LR, then without anchor, then per-fight bootstrapping; none recovered student e's level | #275, #278, #281, #283 |

## Operating rules that came out of these

- Take a "pre-restart" backup before every launch; name it with version, date and reason (`v80966-20261030-0030-回退前`).
- Write each rollback as an executable command in the decision log, with the overwritten range.
- Guardrails pause and report; they **never roll back automatically**.
- Only change one thing per launch — a rule that was broken often, always with a note saying so ([11](11_evidence_gaps.md)).
- Never `git reset --hard` a worktree another agent is using; it once wiped two commits of another session ([16](16_how_the_project_was_run.md)).
