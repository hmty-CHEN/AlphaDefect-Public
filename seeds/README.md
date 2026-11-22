# Seed pools

All pools except P2-future were drawn with `tools/seed_pool.py`: 10 characters, each drawn independently and uniformly with Python's `secrets` from the alphabet `0123456789ABCDEFGHJKLMNPQRSTUVWXYZ` (about 51 bits per seed), all distinct. Once used, a pool is never changed.

| File | Seeds | Drawn | Used for | SHA-256 |
|---|---|---|---|---|
| `W1.json` | 10,000 | 2026-11-02 18:27:30 +0100 | Table 3: Regent, Silent and Ironclad (first 1,000; also used to choose those networks); Defect (entries 6,901–8,400); Appendix B: the students against their starting checkpoints on the same seeds | `8c365e70030ad2e4…` |
| `W1_6901-8400.txt` | 1,500 | subset of W1 | the Defect's 1,500 seeds in Table 3, one per line | `b226195d8269cd92…` |
| `P2-future.json` | 3,000 | 2026-11-16 (the file records only the date and that the seeds overlap neither W1 nor the repository's other pools; the drawing procedure is not recorded) | Table 3: Fool (greedy and search; also used to choose the network and search setting) | `83a5dcbe6106372a…` |
| `R1.json` | 1,000 | 2026-11-21 03:50:35 +0100 | Ironclad search row of Table 3 and its paired greedy runs; not used for any choice | `4c28371e41ebd6c4…` |
| `P1.json` | 1,000 (384 marked `eval`) | 2026-11-01 23:03:01 +0100 | routine evaluation during training: the `eval` list (384 seeds) or its first 192 | `b3d41ab974bb148c…` |
| `P1_eval_first384.txt` | 384 | the `eval` list of P1 | routine evaluation of the Regent, Silent and Ironclad | `e4f1999801310304…` |
| `P1_eval_first192.txt` | 192 | first 192 of the `eval` list | routine evaluation of the Fool and Necrobinder | `036cf618f492fae4…` |
| `RV1.json` | 5,000 | 2026-11-21 18:37:30 +0100, committed before any result | Fresh seeds, not used for any choice. First 1,000: Table 4, the Necrobinder's row of Table 3, the Defect on v0.113.1 (Section 3.2), Table 7 and the students against their starting checkpoints (Appendix B); first 3,000: the Fool's greedy run of Table 4 and the Silent's network on v0.113.1 (Appendix E) | `11a1a4f3964dbfed…` |
| `AB1.json` | 1,000 | 2026-11-21 19:03:03 +0100 | final evaluation of the training ablation (not yet run) | `92152e2f4348caaa…` |

Training games use seeds of a different form (`R<actor>_<integer>`), so no pool seed is played during training.

