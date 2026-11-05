"""Is train.py's gain real, and where does it come from? Usage: checks.py <dir of per-day CSVs>

Trains on Oct 29-Nov 3 (a 25% sample of trips, for speed) and tests on Nov 4, on the same rows as train.py.
"""
import sys
from pathlib import Path

import numpy as np
import polars as pl

import train as t

TRAIN = ["20261029", "20261030", "20261031", "20261101", "20261102", "20261103"]
ROUNDS = 150


def line(label: str, actual: np.ndarray, predicted: np.ndarray, base: dict | None = None) -> dict:
    s = t.score(actual, predicted)
    change = "" if base is None else f"  median {100 * (s['median'] / base['median'] - 1):+5.1f}%  p90 {100 * (s['p90'] / base['p90'] - 1):+5.1f}%  early {100 * (s['early'] / base['early'] - 1):+5.1f}%"
    print(f"{label:48} median {s['median']:4.0f}  p90 {s['p90']:4.0f}  early {100 * s['early']:4.1f}%  bias {s['bias']:5.0f}{change}", flush=True)
    return s


def main() -> None:
    df = t.load(Path(sys.argv[1]))
    usable = pl.col("lead_v1").is_between(60, 1980, closed="left") & pl.col("metro").is_not_null()
    test = df.filter((pl.col("date") == "20261104") & usable)
    all_train = df.filter(pl.col("date").is_in(TRAIN) & (pl.col("lead_v1") < 1980))
    # Whole trips, so one trip's rows don't land on both sides of any comparison.
    train = all_train.filter(pl.col("trip").hash(seed=3) % 4 == 0)
    print(f"train {len(train):,} of {len(all_train):,}; test {len(test):,}")

    actual, v1 = test["actual"].to_numpy(), test["v1"].to_numpy()
    sched = test["sched"].to_numpy().astype(float)
    base = line("v1", actual, v1)
    line("metro", actual, test["metro"].to_numpy().astype(float), base)

    # A rule, no learning: a bus doesn't leave before its timetable time.
    held = np.where(np.isnan(sched), v1, np.maximum(v1, sched))
    line("rule: never before the timetable", actual, held, base)
    started = test["started"].to_numpy() == 1
    line("rule: same, only once the trip has started", actual, np.where(started, held, v1), base)

    groups = {
        "lead and route only": ["lead_v1", "route_cat"],
        "+ timetable (sched_gap)": ["lead_v1", "route_cat", "sched_gap"],
        "+ where the bus is": ["lead_v1", "route_cat", "sched_gap", "started", "stops_left", "dist_left", "since_last", "delay_now", "stop_frac"],
        "all but Metro": t.BASE + t.HEADWAY + t.IDS,
        "all": t.BASE + t.HEADWAY + t.METRO + t.IDS,
        "all but the timetable": [f for f in t.BASE + t.HEADWAY + t.METRO + t.IDS if f != "sched_gap"],
    }
    for label, features in groups.items():
        model = t.fit(train, features, rounds=ROUNDS)
        line(label, actual, v1 + model.predict(test.select(features).to_numpy()), base)

    # Scramble the training answers: the inputs no longer say anything, so all a model can learn is one overall shift.
    # That's the floor for the models above. It can't prove the inputs are free of leaks; only a live shadow run can.
    rng = np.random.default_rng(5)
    scrambled = train.with_columns(pl.Series("residual", rng.permutation(train["residual"].to_numpy())))
    features = t.BASE + t.HEADWAY + t.METRO + t.IDS
    model = t.fit(scrambled, features, rounds=ROUNDS)
    line("scrambled answers (overall shift only)", actual, v1 + model.predict(test.select(features).to_numpy()), base)

    # Where the improvement is: buses already seen on this trip, or trips not started.
    model = t.fit(train, features, rounds=ROUNDS)
    p = v1 + model.predict(test.select(features).to_numpy())
    for label, mask in (("already on the trip", started), ("trip not started", ~started)):
        print(f"\n{label}: {100 * mask.mean():.0f}% of predictions")
        b = line("  v1", actual[mask], v1[mask])
        line("  metro", actual[mask], test["metro"].to_numpy().astype(float)[mask], b)
        line("  all", actual[mask], p[mask], b)


if __name__ == "__main__":
    main()
