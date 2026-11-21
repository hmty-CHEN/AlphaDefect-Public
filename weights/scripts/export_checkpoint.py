"""Export training checkpoints into clean, shareable weight files.

Usage: python -I export_checkpoint.py <spec.json> <out_dir> <manifest.json>

spec.json is a list of {"name", "src", "notes", optional "config_from"}. For each entry we
load the checkpoint, drop the optimizer state, EMA copy and machine-specific settings (paths,
LAN addresses, token files), check that the weights load into PolicyValueNet with the repo's
own load_state(), and write

    {"format": "sts2-defect-rl/ckpt-1", "version", "obs_format", "vocab_n", "total_steps",
     "character", "config": {"model": {...}, <training hyperparameters>}, "state": {...}, "export": {...}}

which is the layout tools/ability_test.py, python/play_live.py and sts2rl/evaluate.py already
read. Every file loads with torch.load(..., weights_only=True). Weights stay float32: greedy
evaluation is sensitive to tiny numeric changes (see docs/09_plateau_and_churn.md), so we do
not quantize.

Options per spec entry (added for weights-v2, 2026-11-11):
  "state_key": "ema"      export the EMA copy instead of the raw weights; entries the EMA does not
                          track (integer buffers) come from the raw state, as the learner does when
                          it publishes EMA weights for evaluation
  "obs_format", "vocab_n", "total_steps", "version"
                          override fields the source file does not record (e.g. distilled students)
  "teachers"              text describing the distillation teachers (stored in export.pool_distill)
  "kind": "co_heads"      export the combat-outcome heads (sts2rl.model.CombatOutcomeHeads) stored
                          under "co" in a learner checkpoint, as format "alphadefect/co-heads-1";
                          "trunk" names the exported network file whose embedding g they read, and
                          "calibration" is stored as given
Options added for weights-v5 (2026-11-17, game v0.113.x):
  "weights": "ema"        label for export.weights when the source file's "state" already holds EMA
                          weights (checkpoints packaged for live play swap the EMA copy into "state")
  content_names, catalog_version
                          copied from the source when present. v0.113.x checkpoints need them:
                          load_state(net, state, content_names) aligns the content-prior columns by
                          feature name when the content catalog changes between game versions
Each exported tensor is compared with its source after a reload (bit-exact) before the file
is listed in the manifest.
"""
import hashlib, json, os, re, sys
from pathlib import Path

import torch

REPO = Path(os.environ.get("EXPORT_REPO") or Path(__file__).resolve().parents[2])
sys.path.insert(0, str(REPO / "python"))
from sts2rl.model import ModelConfig, PolicyValueNet, load_state  # noqa: E402

DROP_KEYS = {"run_dir", "bridge_bind", "bridge_port", "bridge_token_file", "wp_init", "bv_model",
             "combat_anchor_ckpt", "eval_seeds", "co_init", "skip_pot_from", "skip_from"}
IP = re.compile(r"\b\d{1,3}(\.\d{1,3}){3}\b")


def clean_config(cfg: dict) -> dict:
    out = {}
    for k, v in cfg.items():
        if k in DROP_KEYS:
            out[k] = None
        elif isinstance(v, str) and ("/root" in v or "/home" in v or IP.search(v)):
            out[k] = None
        else:
            out[k] = v
    return out


def sha256(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def same(a: dict, b: dict) -> bool:
    return a.keys() == b.keys() and all(torch.equal(a[k], b[k]) for k in a)


def export_co_heads(e: dict, ck: dict, src: str, out: Path) -> dict:
    co = {k: v.detach().clone() for k, v in ck["co"].items()}
    cfg = ck["config"]
    blob = {"format": "alphadefect/co-heads-1", "version": int(e.get("version", ck["version"])),
            "obs_format": int(e.get("obs_format", ck.get("obs_format") or 0)), "vocab_n": e.get("vocab_n", ck.get("vocab_n")),
            "character": cfg.get("character"), "trunk": e["trunk"],
            "config": {"d": cfg["model"]["d"], "hp_bins": int(co["edges"].numel() - 1) if "edges" in co else 41,
                       "pot_slots": 5, "co_lr": cfg.get("co_lr"),
                       "skip": {k: cfg.get(k) for k in ("skip_easy", "skip_p", "skip_prob", "skip_real_runs", "skip_rooms",
                                                        "skip_blacklist", "skip_hp_ema")}},
            "calibration": e.get("calibration"), "state": co,
            "export": {"name": e["name"], "notes": e.get("notes", ""), "source_file": os.path.basename(src)}}
    dst = out / f"{e['name']}.pt"
    torch.save(blob, dst)
    chk = torch.load(dst, map_location="cpu", weights_only=True)
    assert same(chk["state"], co), "co heads changed on reload"
    return {"file": dst.name, "kind": "co_heads", "version": blob["version"], "obs_format": blob["obs_format"],
            "character": blob["character"], "trunk": blob["trunk"], "params": sum(v.numel() for v in co.values()),
            "bytes": dst.stat().st_size, "sha256": sha256(dst), "notes": e.get("notes", ""),
            **{k: e[k] for k in ("eval", "release", "dr") if k in e}}


def main() -> None:
    spec = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    out = Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
    manifest = []
    cache = {}
    for e in spec:
        src = os.path.expanduser(e["src"])
        ck = torch.load(src, map_location="cpu", weights_only=False)
        if e.get("kind") == "co_heads":
            manifest.append(export_co_heads(e, ck, src, out))
            print(manifest[-1]["file"], manifest[-1]["version"], manifest[-1]["params"], flush=True)
            continue
        if "state" not in ck:            # bare state_dict (evaluation archive): borrow metadata
            base = cache[e["config_from"]]
            ck = {**{k: base[k] for k in ("obs_format", "vocab_n")}, "version": e["version"],
                  "total_steps": None, "config": base["config"], "state": ck}
        cache[e["name"]] = ck
        raw = ck["state"]
        if e.get("state_key", "state") == "ema":
            ema = ck["ema"]
            src_state = {k: (ema[k].to(v.dtype) if k in ema else v) for k, v in raw.items()}
        else:
            src_state = raw
        model_cfg = dict(ck["config"]["model"])
        net = PolicyValueNet(ModelConfig(**model_cfg)).eval()
        load_state(net, src_state, ck.get("content_names"))
        state = {k: v.detach().clone().float() if v.is_floating_point() else v.detach().clone()
                 for k, v in src_state.items()}
        cfg = clean_config({k: v for k, v in ck["config"].items() if k != "model"})
        cfg["model"] = model_cfg
        blob = {"format": "sts2-defect-rl/ckpt-1", "version": int(e.get("version", ck["version"])),
                "obs_format": int(e.get("obs_format", ck.get("obs_format") or 0)),
                "vocab_n": e.get("vocab_n", ck.get("vocab_n")),
                "total_steps": e.get("total_steps", ck.get("total_steps")),
                "character": ck["config"].get("character"), "config": cfg, "state": state,
                "export": {"name": e["name"], "notes": e.get("notes", ""), "source_file": os.path.basename(src),
                           "weights": e.get("weights") or ("ema" if e.get("state_key") == "ema" else "raw")}}
        for k in ("content_names", "catalog_version"):      # v0.113.x: needed to load across content catalogs
            if ck.get(k) is not None:
                blob[k] = list(ck[k]) if k == "content_names" else ck[k]
        if "pool_distill" in ck:
            pdist = dict(ck["pool_distill"])
            pdist["teachers"] = e.get("teachers", "16 uniform-spaced SL-pool snapshots (v130978-v156880)")
            blob["export"]["pool_distill"] = pdist
        dst = out / f"{e['name']}.pt"
        torch.save(blob, dst)
        chk = torch.load(dst, map_location="cpu", weights_only=True)        # must load without pickle code
        assert chk["version"] == blob["version"]
        assert same(chk["state"], {k: v.float() if v.is_floating_point() else v for k, v in src_state.items()}), "weights changed on export"
        n_params = sum(v.numel() for v in state.values())
        manifest.append({"file": dst.name, "version": blob["version"], "obs_format": blob["obs_format"],
                         "vocab_n": blob["vocab_n"], "total_steps": blob["total_steps"], "character": blob["character"],
                         **({"catalog_version": blob["catalog_version"]} if "catalog_version" in blob else {}),
                         "weights": blob["export"]["weights"], "params": n_params,
                         "bytes": dst.stat().st_size, "sha256": sha256(dst),
                         "model_flags": {k: v for k, v in model_cfg.items() if isinstance(v, bool) and v},
                         "arch": {k: model_cfg.get(k) for k in ("d", "layers", "heads", "ff_mult", "vocab_size")},
                         "notes": e.get("notes", ""), **{k: e[k] for k in ("eval", "release", "dr") if k in e}})
        print(dst.name, blob["version"], n_params, flush=True)
    Path(sys.argv[3]).write_text(json.dumps(manifest, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
