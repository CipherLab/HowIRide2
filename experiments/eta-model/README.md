# ETA model experiment

Can a gradient-boosted correction to `v1` beat it on days it hasn't seen? A throwaway-grade experiment, kept so it can be rerun as data builds up. Nothing here runs in production.

```sh
# A snapshot of crossings.db (SQLite backup API, not a file copy), plus data/static and data/raw/metro-tripupdates from the Pi.
dotnet build experiments/eta-model/Dump -c Release
Dump/bin/Release/net10.0/Dump <data> <crossings.db> 20261104 <csv-dir>/20261104.csv   # per service day; they run fine in parallel

uv venv && uv pip install lightgbm polars numpy pyarrow
python train.py <csv-dir> report.json     # about an hour on 24 cores; edit the folds in main() for new days
python checks.py <csv-dir>                # which inputs the gain comes from
```

`Dump` samples predictions on a fixed 5-minute clock, the way a rider sees them. Don't train on the backtest's "exactly N minutes before the bus left" rows: the error equals the true lead minus the shown lead, and the true lead would take only six values, so a model could score well just by rounding.

## Results, 2026-11-05

All leads from 1 to 33 minutes, full output in `results-2026-11-05.txt`.

| Held-out | Median error against v1 | p90 error | Early rate |
|---|---|---|---|
| Tue Nov 3 | -24% | -35% | +41% |
| Wed Nov 4 | -25% | -37% | +49% |
| Thu Nov 5 morning | -28% | -42% | +37% |
| Weekend, trained on weekdays only | -16% | -35% | +154% |

The point model misses the bar set beforehand, at least 10% fewer early departures, because `v1`'s early bias is part of what protects riders from it.

A 5% "be there by" model did better on weekdays: the same missed-bus rate as a fixed margin by lead, with 30-40% less waiting (10 minutes out, 2.3 minutes against 3.3). On the unfamiliar weekend it missed 7-12% of buses against a 5% target, while the margin stayed at 3.6-5.2%.

More days of the same kind didn't help after two (median 60 s with one day, 58 s with two to five). Variety is what's missing.

## Before trusting a future result

- Test on days the model hasn't seen, including a day type or weather it hasn't seen, with Metro as the control.
- Run it live as a shadow before anyone sees it. These checks can't rule out every leak.
- Rows exist only for trips that really departed after the prediction, so cancelled trips and buses that had just left aren't scored, for any predictor.
