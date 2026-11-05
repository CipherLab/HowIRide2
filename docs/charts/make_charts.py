"""Draws the README's charts from scores.csv, in light and dark versions.

Usage: python3 docs/charts/make_charts.py    (needs matplotlib)

scores.csv is a snapshot of the bus rows of /api/scores: the latest full-day scoring run of each service date.
Replace it, adjust the dates below, and run this again to update the charts.
"""
import csv
from collections import defaultdict
from datetime import datetime
from pathlib import Path

import matplotlib

matplotlib.use("svg")
import matplotlib.pyplot as plt

HERE = Path(__file__).parent
LATEST_DAY = "20261104"
DAYS = ["20261028", "20261029", "20261030", "20261031", "20261101", "20261102", "20261103", "20261104"]
WEEKEND = ("20261031", "20261101")
HORIZONS = [2, 5, 10, 15, 20, 30]

THEMES = {
    "light": dict(surface="#fcfcfb", text="#0b0b0b", muted="#52514e", grid="#e6e5e1", band="#f1f0ec", ours="#2a78d6", metro="#eb6834", timetable="#1baf7a"),
    "dark": dict(surface="#1a1a19", text="#f0efec", muted="#c3c2b7", grid="#333331", band="#262625", ours="#3987e5", metro="#d95926", timetable="#199e70"),
}
LABELS = {"ours": "This project", "metro": "Metro Transit", "timetable": "Timetable"}

plt.rcParams.update({"svg.fonttype": "path", "font.family": ["Source Sans 3", "DejaVu Sans"], "font.size": 13, "axes.unicode_minus": False})


def load():
    scores = defaultdict(dict)
    with open(HERE / "scores.csv", newline="") as f:
        for row in csv.DictReader(f):
            scores[(row["service_date"], row["predictor"], int(row["horizon_min"]))] = row
    return scores


def style(ax, t):
    ax.set_facecolor(t["surface"])
    ax.grid(axis="y", color=t["grid"], linewidth=0.8)
    ax.set_axisbelow(True)
    for side in ("top", "right", "left"):
        ax.spines[side].set_visible(False)
    ax.spines["bottom"].set_color(t["grid"])
    ax.tick_params(colors=t["muted"], length=0, labelsize=12)
    ax.set_ylim(bottom=0)


def draw(ax, xs, ys, name, t):
    ax.plot(xs, ys, color=t[name], linewidth=1.5, marker="o", markersize=7, markeredgecolor=t["surface"], markeredgewidth=1.2, clip_on=False, zorder=3 if name == "ours" else 2)


def end_labels(ax, x, ends, t, unit=""):
    """Names and final values beside the last points, nudged apart so they don't collide."""
    low, high = ax.get_ylim()
    gap = (high - low) * 0.1
    placed = []
    for name, value in sorted(ends.items(), key=lambda kv: kv[1]):
        y = max(value, placed[-1] + gap) if placed else value
        placed.append(y)
        ax.annotate(f"{LABELS[name]} {value:.0f}{unit}", (x, y), xytext=(10, 0), textcoords="offset points", va="center", color=t["text"], fontsize=11.5, annotation_clip=False)


def legend(fig, t):
    handles = [plt.Line2D([], [], color=t[n], linewidth=1.5, marker="o", markersize=7, markeredgecolor=t["surface"]) for n in LABELS]
    fig.legend(handles, LABELS.values(), loc="upper left", bbox_to_anchor=(0.015, 1.0), ncols=3, frameon=False, labelcolor=t["text"], fontsize=12.5, handletextpad=0.4, columnspacing=1.4)


def by_horizon(scores, t, path):
    """How the day's misses grow with how far ahead the prediction was."""
    series = {"ours": "live-v1", "metro": "metro-tripupdates", "timetable": "schedule"}
    panels = [("median_abs_s", "Typical miss: median, seconds", 1, ""), ("p90_abs_s", "Bad miss: 90th percentile, seconds", 1, ""), ("early_rate", "Bus left over 60 s early, %", 100, "%")]
    fig, axes = plt.subplots(1, 3, figsize=(12.5, 4.2), facecolor=t["surface"])
    for ax, (column, title, scale, unit) in zip(axes, panels):
        ends = {}
        for name, predictor in series.items():
            ys = [float(scores[(LATEST_DAY, predictor, h)][column]) * scale for h in HORIZONS]
            draw(ax, HORIZONS, ys, name, t)
            ends[name] = ys[-1]
        style(ax, t)
        ax.set_title(title, loc="left", color=t["text"], fontsize=13, pad=10)
        ax.set_xticks([2, 10, 20, 30])
        ax.set_xlim(0, 30.5)
        ax.set_xlabel("Minutes ahead", color=t["muted"], fontsize=12)
        end_labels(ax, 30, ends, t, unit)
    legend(fig, t)
    fig.subplots_adjust(left=0.035, right=0.885, top=0.76, bottom=0.14, wspace=0.72)
    fig.savefig(path, facecolor=t["surface"])
    plt.close(fig)


def by_day(scores, t, path):
    """The typical miss on each service day so far, replayed with only earlier days' history."""
    series = {"ours": "v1-pace", "metro": "metro-tripupdates", "timetable": "schedule"}
    names = [datetime.strptime(d, "%Y%m%d").strftime("%b %-d") for d in DAYS]
    fig, axes = plt.subplots(1, 2, figsize=(12.5, 4.2), facecolor=t["surface"])
    for ax, horizon in zip(axes, (10, 30)):
        ax.axvspan(DAYS.index(WEEKEND[0]) - 0.5, DAYS.index(WEEKEND[1]) + 0.5, color=t["band"], linewidth=0, zorder=0)
        ends = {}
        for name, predictor in series.items():
            ys = [float(scores[(d, predictor, horizon)]["median_abs_s"]) for d in DAYS]
            draw(ax, range(len(DAYS)), ys, name, t)
            ends[name] = ys[-1]
        style(ax, t)
        ax.set_title(f"Typical miss {horizon} minutes ahead: median, seconds", loc="left", color=t["text"], fontsize=13, pad=10)
        ax.set_xticks(range(len(DAYS)), [n if i % 2 == 0 else n.split()[1] for i, n in enumerate(names)])
        ax.set_xlim(-0.5, len(DAYS) - 0.5)
        ax.text(DAYS.index(WEEKEND[0]) + 0.5, ax.get_ylim()[1] * 0.04, "Weekend", ha="center", color=t["muted"], fontsize=11.5)
        end_labels(ax, len(DAYS) - 1, ends, t)
    legend(fig, t)
    fig.subplots_adjust(left=0.035, right=0.885, top=0.76, bottom=0.1, wspace=0.42)
    fig.savefig(path, facecolor=t["surface"])
    plt.close(fig)


scores = load()
for mode, theme in THEMES.items():
    by_horizon(scores, theme, HERE / f"by-horizon-{mode}.svg")
    by_day(scores, theme, HERE / f"by-day-{mode}.svg")
