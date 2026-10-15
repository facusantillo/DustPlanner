#!/usr/bin/env python3
"""Validate data/format-rotation.json and data/non-craftable.json against python-hearthstone.

Exit code 1 when a Standard year in the JSON differs from hearthstone.utils.STANDARD_SETS, when a
rotation date differs from ZODIAC_ROTATION_DATES, or when a set listed as craftable/non-craftable
contradicts hearthstone.enums.CardSet.craftable. Sets that python-hearthstone does not know yet
(or does not list as craftable yet) produce a warning, not a failure, because the library lags
new expansions by a few days.

Usage: python tools/check_format_rotation.py [--data-dir data]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

try:
    from hearthstone.enums import CardSet, ZodiacYear
    from hearthstone.utils import STANDARD_SETS, ZODIAC_ROTATION_DATES
except ImportError:  # pragma: no cover
    print("python-hearthstone is required: pip install hearthstone", file=sys.stderr)
    sys.exit(2)


def load(path: Path) -> dict:
    with path.open(encoding="utf-8") as fh:
        return json.load(fh)


def check_rotation(data_dir: Path) -> tuple[list[str], list[str]]:
    errors: list[str] = []
    warnings: list[str] = []
    rotation = load(data_dir / "format-rotation.json")
    json_years = {y["name"]: y for y in rotation["years"]}

    for year, sets in STANDARD_SETS.items():
        if year == ZodiacYear.PRE_STANDARD or year.name not in json_years:
            continue
        expected = sorted({s.name for s in sets})
        actual = sorted(set(json_years[year.name]["sets"]))
        if expected != actual:
            missing = sorted(set(expected) - set(actual))
            extra = sorted(set(actual) - set(expected))
            errors.append(f"{year.name}: sets differ. missing={missing} extra={extra}")
        expected_date = ZODIAC_ROTATION_DATES[year].date().isoformat()
        if json_years[year.name]["starts"] != expected_date:
            errors.append(f"{year.name}: starts {json_years[year.name]['starts']} != {expected_date}")

    latest_lib = max((y for y in STANDARD_SETS if y != ZodiacYear.PRE_STANDARD), key=lambda y: ZODIAC_ROTATION_DATES[y])
    latest_json = max(rotation["years"], key=lambda y: y["starts"])
    if latest_lib.name != latest_json["name"]:
        warnings.append(
            f"latest year differs: json={latest_json['name']} python-hearthstone={latest_lib.name} "
            "(update data/format-rotation.json or python-hearthstone)"
        )

    known_names = {s.name for s in CardSet}
    for y in rotation["years"]:
        for s in y["sets"]:
            if s not in known_names:
                warnings.append(f"{y['name']}: set {s} is unknown to python-hearthstone {CardSet.__module__}")
    return errors, warnings


def check_craftability(data_dir: Path) -> tuple[list[str], list[str]]:
    errors: list[str] = []
    warnings: list[str] = []
    rules = load(data_dir / "non-craftable.json")
    lib_craftable = {s.name for s in CardSet if s.craftable}
    for s in rules["craftable_sets"]:
        if s not in {c.name for c in CardSet}:
            warnings.append(f"craftable_sets: {s} unknown to python-hearthstone")
        elif s not in lib_craftable:
            warnings.append(f"craftable_sets: {s} is not marked craftable by python-hearthstone (added by hand?)")
    for s in rules["non_craftable_sets"]:
        if s in lib_craftable:
            errors.append(f"non_craftable_sets: {s} IS craftable according to python-hearthstone")
    for s in sorted(lib_craftable - set(rules["craftable_sets"])):
        warnings.append(f"python-hearthstone marks {s} craftable but data/non-craftable.json does not list it")
    return errors, warnings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--data-dir", default=str(Path(__file__).resolve().parent.parent / "data"))
    args = parser.parse_args()
    data_dir = Path(args.data_dir)

    errors: list[str] = []
    warnings: list[str] = []
    for check in (check_rotation, check_craftability):
        e, w = check(data_dir)
        errors.extend(e)
        warnings.extend(w)

    import hearthstone  # noqa: WPS433  (version for the report)

    print(f"python-hearthstone {getattr(hearthstone, '__version__', '?')}")
    for w in warnings:
        print(f"WARN  {w}")
    for e in errors:
        print(f"ERROR {e}")
    print("OK" if not errors else f"{len(errors)} error(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
