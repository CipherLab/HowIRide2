"""Can a gradient-boosted correction beat v1 on days it hasn't seen?

Usage: train.py <dir of per-day CSVs from Dump> [out.json]

Trains on earlier service days and tests on later ones. Everything is scored on the same rows, grouped by the lead
v1 was showing the rider. Sampling is on a fixed clock (see Dump/Program.cs), so the true lead isn't a usable signal.

The bar: at least 10% lower early rate and p90 than v1, with no worse a median, on every held-out day, and better than
the simple baselines. See README.md.
"""
import json
import sys
from pathlib import Path

import lightgbm as lgb
import numpy as np
import polars as pl

LEADS = {"2": (60, 180), "5": (240, 360), "10": (540, 660), "15": (810, 990), "20": (1080, 1320), "30": (1620, 1980)}

# Known when the prediction is made. Never the true lead: that's the answer. Names, not ids, are picked per variant below.
BASE = ["lead_v1", "stop_frac", "started", "stops_left", "dist_left", "since_last", "delay_now", "pace", "local_second", "day_type", "route_type",
        "direction", "v0_gap", "sched_gap", "is_weekend"]
HEADWAY = ["prev_gap", "prev_delay", "prev_sched_gap", "expected_gap", "route_delay_15m", "route_seen_15m"]
METRO = ["metro_gap", "metro_lead", "has_metro"]
IDS = ["route_cat"]

PARAMS = dict(learning_rate=0.05, num_leaves=63, min_data_in_leaf=500, feature_fraction=0.8, bagging_fraction=0.7, bagging_freq=1, lambda_l2=10.0,
              cat_smooth=50, max_cat_threshold=32, verbosity=-1, num_threads=20, seed=7)
ROUNDS = 400


COLUMNS = ["date", "trip", "route", "route_type", "direction", "stop", "seq", "q", "actual", "v1", "v0", "metro", "sched", "day_type", "local_second", "lead_v1", "stop_frac",
           "started", "stops_left", "dist_left", "since_last", "delay_now", "pace", "prev_gap", "prev_delay", "prev_sched_gap", "route_delay_15m", "route_seen_15m", "target_is_rail"]
SCHEMA = {c: pl.String if c in ("date", "trip", "route", "stop") else pl.Float64 for c in COLUMNS}


def load(csv_dir: Path) -> pl.DataFrame:
    frame = pl.concat([pl.scan_csv(p, schema=SCHEMA) for p in sorted(csv_dir.glob("*.csv"))])
    frame = frame.with_columns(
        (pl.col("actual") - pl.col("v1")).alias("residual"),
        (pl.col("actual") - pl.col("q")).alias("true_lead"),
        (pl.col("v0") - pl.col("v1")).alias("v0_gap"),
        (pl.col("sched") - pl.col("v1")).alias("sched_gap"),
        (pl.col("metro") - pl.col("v1")).alias("metro_gap"),
        (pl.col("metro") - pl.col("q")).alias("metro_lead"),
        pl.col("metro").is_not_null().cast(pl.Int8).alias("has_metro"),
        (pl.col("v1") - (pl.col("q") - pl.col("prev_gap"))).alias("expected_gap"),
    )
    df = frame.collect(engine="streaming")
    routes = {r: i for i, r in enumerate(sorted(df["route"].unique().to_list()))}
    return df.with_columns(
        pl.col("date").str.to_date("%Y%m%d").dt.weekday().is_in([6, 7]).cast(pl.Int8).alias("is_weekend"),
        df["route"].replace_strict(routes, return_dtype=pl.Int32).alias("route_cat"),
    )


def score(actual: np.ndarray, predicted: np.ndarray) -> dict:
    err = predicted - actual  # Positive: the bus left before we said.
    abs_err = np.abs(err)
    return {"n": int(len(err)), "median": float(np.median(abs_err)), "p90": float(np.quantile(abs_err, 0.9)), "early": float(np.mean(err > 60)), "bias": float(np.mean(err))}


def lead_buckets(lead: np.ndarray):
    for name, (lo, hi) in LEADS.items():
        yield name, (lead >= lo) & (lead < hi)


def fit(train: pl.DataFrame, features: list[str], objective: str = "l1", alpha: float | None = None, rounds: int = ROUNDS):
    params = dict(PARAMS, objective=objective)
    if alpha is not None:
        params["alpha"] = alpha
    categorical = [f for f in features if f == "route_cat"]
    data = lgb.Dataset(train.select(features).to_numpy(), label=train["residual"].to_numpy(), feature_name=features, categorical_feature=categorical, free_raw_data=True)
    return lgb.train(params, data, num_boost_round=rounds)


def lead_table(train: pl.DataFrame, q: float) -> tuple[np.ndarray, np.ndarray]:
    """v1's residual quantile by lead: the simplest possible correction, the bar any model has to clear."""
    edges = np.arange(0, 2160, 120.0)
    bins = np.digitize(train["lead_v1"].to_numpy(), edges)
    residual = train["residual"].to_numpy()
    values = np.array([np.quantile(residual[bins == b], q) if (bins == b).sum() > 100 else 0.0 for b in range(len(edges) + 2)])
    return edges, values


def apply_lead_table(table, lead: np.ndarray) -> np.ndarray:
    edges, values = table
    return values[np.digitize(lead, edges)]


def bootstrap(actual, base, candidate, routes, metric, draws=200, seed=11) -> tuple[float, float]:
    """Change in a metric (candidate vs base, %) resampling whole routes, since a route's buses share their errors."""
    rng = np.random.default_rng(seed)
    unique = np.unique(routes)
    by_route = {r: np.flatnonzero(routes == r) for r in unique}
    changes = []
    for _ in range(draws):
        rows = np.concatenate([by_route[r] for r in rng.choice(unique, len(unique))])
        a, b = score(actual[rows], base[rows])[metric], score(actual[rows], candidate[rows])[metric]
        changes.append(100 * (b - a) / a)
    return float(np.quantile(changes, 0.025)), float(np.quantile(changes, 0.975))


def evaluate(name: str, test: pl.DataFrame, predictions: dict[str, np.ndarray], report: dict) -> None:
    """One table per held-out window, on rows every predictor has an answer for."""
    actual = test["actual"].to_numpy()
    lead = test["lead_v1"].to_numpy()
    routes = test["route"].to_numpy()
    ok = np.all([~np.isnan(p) for p in predictions.values()], axis=0)
    print(f"\n=== {name}: {ok.sum():,} predictions with an answer from all ({100 * ok.mean():.0f}% of {len(ok):,})")
    print(f"{'lead':>5} {'predictor':16} {'n':>8} {'median':>7} {'p90':>6} {'early':>7} {'bias':>6}")
    window = {}
    for bucket, mask in lead_buckets(lead):
        mask = mask & ok
        row = {}
        for predictor, p in predictions.items():
            s = score(actual[mask], p[mask])
            row[predictor] = s
            print(f"{bucket:>5} {predictor:16} {s['n']:>8,} {s['median']:>7.0f} {s['p90']:>6.0f} {100 * s['early']:>6.1f}% {s['bias']:>6.0f}")
        window[bucket] = row
    mask = ok & (lead >= 60) & (lead < 1980)
    print(f"  all leads 1-33 min: change against v1, with a 95% range resampling whole routes")
    summary = {}
    for predictor, p in predictions.items():
        if predictor == "v1":
            continue
        changes = {}
        for metric in ("median", "p90", "early"):
            a, b = score(actual[mask], predictions["v1"][mask])[metric], score(actual[mask], p[mask])[metric]
            low, high = bootstrap(actual[mask], predictions["v1"][mask], p[mask], routes[mask], metric)
            changes[metric] = {"change_pct": 100 * (b - a) / a, "low": low, "high": high}
        summary[predictor] = changes
        text = "  ".join(f"{m} {c['change_pct']:+5.1f}% [{c['low']:+.1f}, {c['high']:+.1f}]" for m, c in changes.items())
        print(f"  {predictor:16} {text}")
    report[name] = {"by_lead": window, "summary": summary, "rows": int(ok.sum())}


def be_by(train: pl.DataFrame, test: pl.DataFrame, features: list[str], report: dict, name: str) -> None:
    """A safe "be there by" time: same miss rate, less waiting? Compared with v1 minus a margin that depends only on lead."""
    target = 0.05
    table = lead_table(train, target)
    simple = test["v1"].to_numpy() + apply_lead_table(table, test["lead_v1"].to_numpy())
    model = fit(train, features, objective="quantile", alpha=target, rounds=250)
    learned = test["v1"].to_numpy() + model.predict(test.select(features).to_numpy())
    actual = test["actual"].to_numpy()
    lead = test["lead_v1"].to_numpy()
    print(f"\n  be-there-by at 5% target, {name}: share of buses gone first, and minutes waiting at the stop")
    results = {}
    for bucket, mask in lead_buckets(lead):
        row = {}
        for label, times in (("lead margin", simple), ("model", learned)):
            row[label] = {"missed": float(np.mean(actual[mask] < times[mask])), "wait_min": float(np.mean(np.maximum(actual[mask] - times[mask], 0)) / 60)}
        results[bucket] = row
        a, b = row["lead margin"], row["model"]
        print(f"  {bucket:>5}  margin: missed {100 * a['missed']:4.1f}% wait {a['wait_min']:4.1f}   model: missed {100 * b['missed']:4.1f}% wait {b['wait_min']:4.1f}")
    report[f"{name} be-by"] = results


def main() -> None:
    csv_dir = Path(sys.argv[1])
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else None
    df = load(csv_dir).sort("q")
    print(f"{len(df):,} predictions")
    print(df.group_by("date").agg(pl.len().alias("n"), pl.col("has_metro").mean().alias("metro"), pl.col("residual").abs().median().alias("v1_med")).sort("date"))

    # The check that the sampling did its job: true lead must not take a few discrete values given the shown lead.
    print("true lead values, sample:", np.unique(np.round(df["true_lead"].head(200_000).to_numpy() / 10)).size, "distinct (rounded to 10 s)")

    report = {}
    full = BASE + HEADWAY + METRO + IDS
    folds = [
        ("Tue Nov 3 (trained Oct 29-Nov 2)", ["20261029", "20261030", "20261031", "20261101", "20261102"], ["20261103"]),
        ("Wed Nov 4 (trained Oct 29-Nov 3)", ["20261029", "20261030", "20261031", "20261101", "20261102", "20261103"], ["20261104"]),
        ("Thu Nov 5 morning (trained Oct 29-Nov 4)", ["20261029", "20261030", "20261031", "20261101", "20261102", "20261103", "20261104"], ["20261105"]),
        ("Weekend, trained on weekdays only: something unfamiliar", ["20261028", "20261029", "20261030"], ["20261031", "20261101"]),
    ]
    importance = None
    for name, train_days, test_days in folds:
        train = df.filter(pl.col("date").is_in(train_days) & (pl.col("lead_v1") < 1980))
        test = df.filter(pl.col("date").is_in(test_days))
        v1 = test["v1"].to_numpy()
        metro = test["metro"].to_numpy().astype(float)
        models = {}
        for label, features in (("gbm no metro", BASE + HEADWAY + IDS), ("gbm", full)):
            model = fit(train, features)
            models[label] = v1 + model.predict(test.select(features).to_numpy())
            if label == "gbm":
                gain = model.feature_importance("gain")
                importance = sorted(zip(features, (100 * gain / gain.sum()).round(1).tolist()), key=lambda x: -x[1])
        evaluate(name, test, {
            "v1": v1,
            "metro": metro,
            "v1/metro average": np.where(np.isnan(metro), v1, (v1 + metro) / 2),
            "v1 + lead shift": v1 + apply_lead_table(lead_table(train, 0.5), test["lead_v1"].to_numpy()),
            **models,
        }, report)
        print("  importance:", importance[:10])
        be_by(train, test, full, report, name)

    # Does more history help? Same test day, growing training set: the question for when to try again.
    print("\n=== learning curve, testing Wed Nov 4, no-metro and full model, all leads")
    test = df.filter((pl.col("date") == "20261104") & (pl.col("lead_v1") >= 60) & (pl.col("lead_v1") < 1980))
    usable = ~np.isnan(test["metro"].to_numpy().astype(float))
    curve = {}
    days = ["20261103", "20261102", "20261101", "20261031", "20261030", "20261029"]
    base = score(test["actual"].to_numpy()[usable], test["v1"].to_numpy()[usable])
    print(f"  v1: median {base['median']:.0f} p90 {base['p90']:.0f} early {100 * base['early']:.1f}%")
    for count in (1, 2, 3, 4, 5, 6):
        train = df.filter(pl.col("date").is_in(days[:count]) & (pl.col("lead_v1") < 1980))
        model = fit(train, full)
        p = test["v1"].to_numpy() + model.predict(test.select(full).to_numpy())
        s = score(test["actual"].to_numpy()[usable], p[usable])
        curve[count] = s
        print(f"  {count} day(s): median {s['median']:.0f} p90 {s['p90']:.0f} early {100 * s['early']:.1f}%")
    report["learning curve"] = {"v1": base, "gbm": curve}

    if out:
        out.write_text(json.dumps(report, indent=1))


if __name__ == "__main__":
    main()
