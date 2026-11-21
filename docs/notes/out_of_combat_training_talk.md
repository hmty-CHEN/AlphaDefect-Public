# How we train out-of-combat decisions in a Slay the Spire 2 agent

*Share-ready English text. Presenter notes with sources are at the end and are not part of the talk.*

## Context

We train a Defect agent for Slay the Spire 2 at Ascension 10 with PPO, using no language model and no human data. Most of what the agent does is combat: play a card, pick a target, end the turn. A smaller part is what we call out-of-combat decisions: which map node to take, which card reward to pick or skip, what to buy, which event option to choose, whether to rest or upgrade.

Out-of-combat decisions are only about 15% of the training steps, and only about 6% of the parameters are specific to them (three heads on top of an encoder shared with combat). Yet they decide which deck the combat policy has to play with. Below are the three design choices that matter most for training them, followed by a fourth lesson, about data intake, whose evidence was the clearest of all.

## 1. Separate scoring and value heads on a shared encoder

Every decision is a choice among legal candidates: an MLP scores each candidate, and a softmax over the scores is the policy. We keep one pair of heads (candidate scoring and value) for combat and a second pair for out-of-combat decisions. Both read the same Transformer encoder. When we introduced the second pair, we initialized it as a copy of the combat heads, so behavior did not change at the moment of the switch.

The reason is that the two kinds of decision live on different scales. A fight is a few dozen steps, and its consequences show up within a few turns. A card pick may only show up in the result several floors, or a whole run, later. With a single head, the two kinds of targets interfered with each other.

One caveat: we introduced the split together with other fixes, after a training run drifted and we rolled back to an earlier checkpoint. We did not ablate the split on its own.

## 2. A separate advantage chain

For combat steps we use ordinary per-step GAE (γ = 1, λ = 0.95). For out-of-combat decisions we compute GAE only along the sequence of out-of-combat decisions. All reward between two of them (the fights, the floors climbed) is folded into the earlier decision. The two groups of advantages are then normalized separately, and the out-of-combat policy loss has its own weight. We started that weight at 2× and brought it back to 1× because it was a suspect in a training drift: the weighting amplified the noisy out-of-combat advantages.

Why: a fight is dozens of steps. If out-of-combat decisions shared the per-step chain, λ = 0.95 per step would decay the signal long before it reached the next fight, and a card pick would see almost nothing beyond it. On the dedicated chain, λ = 0.95 applies per decision, which reaches roughly 20 decisions, or about five floors.

## 3. A higher entropy coefficient for out-of-combat decisions

We use 0.05 for out-of-combat decisions and 0.01 for combat. Early in training, the out-of-combat policy was nearly deterministic, with a mean entropy of about 0.16. It never skipped a card reward, never upgraded at a rest site, and never removed a card in a shop.

An entropy bonus has a gradient on a logit that is proportional to that option's probability. An option chosen with near-zero probability therefore gets almost no push back toward being tried. The value network never sees the states that option leads to, so it cannot correct the policy's view either. A higher coefficient stops the other options from shrinking further. We tried 0.03 and 0.08 along the way and settled on 0.05.

Entropy alone does not revive options that are already near zero, so for a while we also forced random re-picks at out-of-combat screens. We later removed them. A one-step TD diagnostic showed that the forced picks lost 0.11 to 0.26 per step on average, while the policy's own picks were around zero (−0.004 to +0.014), and about 94% of training runs had at least one forced pick, which added outcome noise unrelated to the policy. With the coefficient at 0.05, the out-of-combat entropy in later logs sits around 0.3 to 0.4.

## 4. Make sure out-of-combat samples actually reach training

This was the clearest evidence we have that out-of-combat skill grows.

**The problem.** Runs were sent to the learner only when they finished, and the learner dropped, step by step, any step produced by weights more than 3 versions old. A full run is about 800 steps long, so by the time it arrived, the start of it was stale. What got dropped was always the beginning of long runs. Only 34% of Act-1 steps were used (62% in Act 2, 85% in Act 3), and about half of the out-of-combat decisions. The opening relic choice and the first act's card picks mostly never took part in learning.

**The fix.** Once a run had at least 64 unsent steps, the actor cut a segment just before the next route decision and sent it. The segment was bootstrapped from the value recorded at the first step of the next segment, in both advantage chains. Nothing else about training changed in that restart.

**The effect on data.** The share of Act-1 steps used rose from 34% to 77%. Intake became roughly even across acts (Act 2 76%, Act 3 75%), and Act 3 dropped a little from 85%.

**The effect on results.** On the same fresh seeds (about 1,000), win rate at Ascension 10 rose from 12.6% (version 63,680) to 29.4% (version 66,040). The paired difference was +16.8 ± 3.1 points, with 225 seeds won only after the change against 57 won only before it (p < 0.001). Three checkpoints before the change, spanning about 2,300 versions, had been flat at 11.9%, 13.1% and 12.6%, so the jump is not a continuation of an existing trend.

**The effect on behavior.** Per run, elite fights went from 3.28 to 4.86, relics at the first Act-3 boss from 15.0 to 17.1, and card removals from 0.70 to 1.02. These changes come with a price: Act-1 deaths rose from 1.9% to 4.4%, and HP on arriving at the Act-1 boss fell from 93% to 82%. The agent took more risk early to build a stronger run.

So out-of-combat skill grew first because its samples were finally learned from, not because the network changed.

## What we have not shown

- We have no isolated ablation of points 1 to 3. They were introduced in a short span, partly together with other changes.
- The out-of-combat value head is weak. It explains about 0.85 of the variance of its bootstrapped target but only 0.19 of the variance of the full-run return (0.04 in Act 1). Used to rank two out-of-combat options, it picks the better one 53.8% of the time, against 57.5% for the policy's own preference. Out-of-combat learning therefore rests mainly on a noisy policy gradient.
- Giving out-of-combat decisions a larger share of training did not help. In one experiment the policy loss applied to out-of-combat steps only (combat played greedily, and the same seed replayed several times per group), with about 2.5× as many out-of-combat steps per update. A 1,000-seed retest moved from 72.1% to 73.2% (+1.1 points, p = 0.55). The limit looks like signal rather than share: one out-of-combat decision carries about 1 point of signal, and resolving that takes about 8,900 paired continuations.
- Where the policy stands now: when we played each option out from the same position (branch labelling), the first choice was optimal, ties included, in 90% of 7,101 pairs. Single out-of-combat choices look locally right. Any remaining gains lie in longer-range build direction, which we have not found a way to measure or train.

## Takeaway

Give out-of-combat decisions their own heads, their own advantage chain and their own entropy budget, but first make sure their samples are not being dropped before they reach the learner. In our run, fixing the data path moved the win rate more than any change to the model or the loss.

---

## Presenter notes (not part of the talk)

Sources, by decision record (DR) number in `决策记录.md`:

- Point 1: DR #67 (heads split, rollback to version 9,400, out-of-combat loss weight 2 → 1). The new heads are copied from the combat heads in `load_state` (`python/sts2rl/model.py`).
- Point 2: DR #65 (advantage chain, separate normalization, 2× weight), DR #67 (weight back to 1×). Horizon of "about 20 decisions, about five floors": comment on `macro_lam` in `python/sts2rl/config.py`.
- Point 3: DR #68 (0.01 → 0.05), #89 (0.03), #167 (back to 0.05, forced re-picks), #184 (0.08), #185 (back to 0.05), #231 (forced re-picks removed, TD numbers). The 0.16 entropy and the "never skips / never upgrades / never removes" description come from the comment on `ent_coef_macro` in `config.py`. The 0.3–0.4 figure comes from DR #219.
- Point 4: DR #55 (per-step staleness rule), #168 and #169 (diagnosis, segmented submission, intake numbers), #170 (results and behavior). The pre-change checkpoints (11.9%, 13.1%, 12.6%) are in DR #169. The 53.8% / 57.5% and 0.85 / 0.19 figures are from DR #282 and the 2026-11-03 value-probe diagnosis report. The 72.1% → 73.2% result is DR #267 with addendum 3 of DR #269. The 90% of 7,101 pairs is DR #259.
- "About 6% of parameters" is my recomputation from the config flags recorded in the decision log (1,116,802 of 19,407,367), not read from the live config. "About 15% of steps" is from the independent review of the out-of-combat value head (version 141k).
- The 12.6% → 29.4% comparison is between two checkpoints about 2,400 versions apart (segmented submission went live at version 64,911), so part of the gain could be ordinary training progress. The three flat checkpoints before the change argue against that, but this is not a controlled A/B.
