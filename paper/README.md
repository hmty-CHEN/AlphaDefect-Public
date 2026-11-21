# Figures and tables of the paper

Scripts that regenerate the paper's figures and its card-and-relic tables from the data in this repository. Run them from anywhere with Python 3, NumPy and Matplotlib; outputs go to `paper/figures/` and `paper/tables/`.

| Script | Output (paper) | Inputs |
|---|---|---|
| `scripts/make_headline.py` | `figures/headline.pdf` (Figure 1) | win rates of Table 3 and Section 3.5, written in the script with their sources |
| `scripts/make_learning_curves.py` | `figures/learning_curves.pdf` (Figure 3) | `data/training/learner_log_*.csv.gz`, `data/evaluation/routine_eval_*.jsonl.gz`, `data/evaluation/seed_outcomes_capability_tests.csv.gz` (the Defect, the Regent, the Silent); `paper/data/curves_v0113_raw.json` and `lr_v0113_raw.json` (the Fool, the Ironclad, the Necrobinder) |
| `scripts/choice_stats.py` | `figures/card_picks.pdf` (Figure 4), `data/choice_stats.json`; prints the numbers of Appendix C | routine evaluations as above; `paper/data/choices_v0113_raw.json`; `paper/data/codex/` (players); `paper/data/vocab.json` |
| `scripts/make_choices_tex.py` | `tables/appendix_choices_tables.tex` (Tables 6–8) | `paper/data/choice_stats.json` |
| `scripts/paper_stats.py` | prints Wilson intervals, paired differences and exact McNemar tests | per-seed result files (see `results/`) |

## Data in `paper/data/`

- `curves_v0113_raw.json`, `lr_v0113_raw.json`, `deaths_v0113_raw.json`, `choices_v0113_raw.json`: read-only exports (2026-11-21) of the learner logs and routine evaluations of the three v0.113.1 training runs (`runs/<line>/log.jsonl` and `runs/<line>-eval/eval.jsonl` on the training machine). `deaths_v0113_raw.json` gives the loss breakdown of Section 3.4 for these characters.
- `codex/`: Spire Codex card and relic metrics for single-player Ascension 10 runs on the same game versions, from its public API (`/api/runs/metrics/cards` and `/relics`, bracket `solo:a10:<version>`), retrieved 2026-11-21 (`fetched_utc.txt`). Third-party data; see [NOTICE.md](../NOTICE.md).
- `vocab.json`: the vocabulary used to name cards and relics in the evaluation records.

Section 3.4's agent baseline on the solver's positions is in `results/solver_baseline/`.

The figures produced here were compared with the paper's: the appendix tables come out byte-identical, and the printed summary numbers match the text.
