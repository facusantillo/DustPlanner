#!/usr/bin/env python3
"""Generate deckstring test vectors with python-hearthstone's reference implementation.

Writes tests/DustPlanner.Core.Tests/Fixtures/deckstrings.json and cloud/kick-worker/test/fixtures/deckstrings.json. The C# codec must decode every
vector to the same cards/heroes/format/sideboards and re-encode it to the same canonical string.
Card ids are arbitrary dbfIds: no card data is involved.

Usage: python tools/gen_deckstring_vectors.py
"""
from __future__ import annotations

import json
import random
from pathlib import Path

from hearthstone.deckstrings import write_deckstring
from hearthstone.enums import FormatType

ROOT = Path(__file__).resolve().parent.parent
# Two consumers of the same vectors: the C# codec and the browser decoder of the public deck page (K6).
OUTS = [
    ROOT / "tests" / "DustPlanner.Core.Tests" / "Fixtures" / "deckstrings.json",
    ROOT / "cloud" / "kick-worker" / "test" / "fixtures" / "deckstrings.json",
]

ETC = 90749  # E.T.C., Band Manager
ZILLIAX = 102983  # Zilliax Deluxe 3000 (dbfId as of 2024; only used as an owner id here)


def vector(name: str, cards, heroes, fmt: FormatType, sideboards=None, note: str = "") -> dict:
    code = write_deckstring(cards, heroes, fmt, sideboards or [])
    return {
        "name": name,
        "deckstring": code,
        "format": int(fmt),
        "heroes": sorted(heroes),
        "cards": [[d, c] for d, c in sorted(cards)],
        "sideboards": [[d, c, o] for d, c, o in sorted(sideboards or [], key=lambda x: (x[2], x[0]))],
        "note": note,
    }


def main() -> None:
    rng = random.Random(20261015)
    vectors = []

    vectors.append(vector("minimal x2 only", [(1, 2), (2, 2), (3, 2), (4, 2)], [7], FormatType.FT_STANDARD))
    vectors.append(vector("x1 only, multi-byte varints", [(8, 1), (179, 1), (2009, 1)], [7], FormatType.FT_STANDARD))
    vectors.append(vector("xN section with 3 and 4 copies", [(10, 3), (20, 4), (30, 1), (40, 2)], [31], FormatType.FT_WILD))
    vectors.append(vector("large dbfIds (3 and 4 byte varints)", [(16384, 2), (2097152, 1), (123456789, 2)], [930], FormatType.FT_STANDARD))
    vectors.append(vector("classic format", [(100, 2), (101, 2)], [637], FormatType.FT_CLASSIC))
    vectors.append(vector("twist format", [(100, 2), (101, 1)], [637], FormatType.FT_TWIST))
    vectors.append(
        vector(
            "E.T.C. sideboard with three x1 entries",
            [(ETC, 1), (102223, 2), (69566, 2), (71781, 1)],
            [930],
            FormatType.FT_STANDARD,
            [(76984, 1, ETC), (78079, 1, ETC), (69616, 1, ETC)],
            "shape of the python-hearthstone TEST_SIDEBOARD_DECKSTRING",
        )
    )
    vectors.append(
        vector(
            "Zilliax modules plus E.T.C. band, mixed counts",
            [(ETC, 1), (ZILLIAX, 1), (500, 2), (501, 2)],
            [7],
            FormatType.FT_STANDARD,
            [(600, 1, ZILLIAX), (601, 1, ZILLIAX), (700, 2, ETC), (701, 3, ETC)],
            "two owners, x1/x2/xN sideboard sections",
        )
    )

    for i in range(12):
        n = rng.randint(1, 20)
        ids = rng.sample(range(1, 200000), n)
        cards = [(d, rng.choice([1, 1, 2, 2, 2, 3])) for d in ids]
        side = []
        if rng.random() < 0.5:
            owners = rng.sample(ids, min(2, len(ids)))
            for o in owners:
                for d in rng.sample(range(200000, 300000), rng.randint(1, 3)):
                    side.append((d, rng.choice([1, 1, 2, 3]), o))
        vectors.append(vector(f"random {i}", cards, [rng.randint(1, 100000)], rng.choice([FormatType.FT_WILD, FormatType.FT_STANDARD]), side))

    # Real strings from python-hearthstone's test-suite (hand-verified card lists there).
    vectors.append(
        {
            "name": "python-hearthstone TEST_DECKSTRING (hunter, no sideboard marker)",
            "deckstring": "AAEBAR8GhwS7BfixAqHCAtnCAubCAgyoArUD6wftCf4Mzq4CubQC6rsC7LsCicMCjsMCk8MCAAA=",
            "format": 1,
            "heroes": [31],
            "cards": sorted(
                [[40426, 2], [41353, 2], [39160, 1], [41358, 2], [41363, 2], [519, 1], [39481, 2], [41318, 1], [437, 2],
                 [1662, 2], [41249, 1], [296, 2], [40428, 2], [1003, 2], [38734, 2], [41305, 1], [699, 1], [1261, 2]]
            ),
            "sideboards": [],
            "note": "trailing 00 00 bytes: xN=0 then no-sideboard marker",
        }
    )
    vectors.append(
        {
            "name": "python-hearthstone TEST_DECKSTRING_PRE_SIDEBOARD (legacy, ends after xN)",
            "deckstring": "AAEBAR8G+LEChwTmwgKhwgLZwgK7BQzquwKJwwKOwwKTwwK5tAK1A/4MqALsuwLrB86uAu0JAA==",
            "format": 1,
            "heroes": [31],
            "cards": sorted(
                [[40426, 2], [41353, 2], [39160, 1], [41358, 2], [41363, 2], [519, 1], [39481, 2], [41318, 1], [437, 2],
                 [1662, 2], [41249, 1], [296, 2], [40428, 2], [1003, 2], [38734, 2], [41305, 1], [699, 1], [1261, 2]]
            ),
            "sideboards": [],
            "note": "pre-sideboard client output; not canonical (unsorted), so only decode is compared",
            "canonical": False,
        }
    )
    vectors.append(
        {
            "name": "python-hearthstone TEST_SIDEBOARD_DECKSTRING (priest with E.T.C.)",
            "deckstring": "AAEBAZCaBgjlsASotgSX7wTvkQXipAX9xAXPxgXGxwUQvp8EobYElrcE+dsEuNwEutwE9vAEhoMFopkF4KQFlMQFu8QFu8cFuJ4Gz54G0Z4GAAED8J8E/cQFuNkE/cQF/+EE/cQFAAA=",
            "format": 1,
            "heroes": [101648],
            "cards": sorted(
                [[102223, 2], [69566, 2], [102200, 2], [71781, 1], [77305, 2], [86626, 1], [91078, 1], [102225, 2], [90644, 2],
                 [91067, 2], [82310, 2], [77368, 2], [90959, 1], [85154, 2], [79767, 1], [86624, 2], [79990, 2], [90749, 1],
                 [72598, 2], [77370, 2], [90683, 2], [84207, 1], [72488, 1], [72481, 2]]
            ),
            "sideboards": [[69616, 1, 90749], [76984, 1, 90749], [78079, 1, 90749]],
            "note": "hero dbfId and format (Wild) taken from the string itself with python-hearthstone",
        }
    )

    invalid = [
        {"name": "empty", "deckstring": "", "reason": "empty"},
        {"name": "not base64", "deckstring": "###not-base64###", "reason": "base64"},
        {"name": "bad reserved byte", "deckstring": "AQEBAQABAgAAAA==", "reason": "reserved byte"},
        {"name": "unsupported version", "deckstring": "AAICAQABAgAAAA==", "reason": "version"},
        {"name": "truncated", "deckstring": "AAECAQcA", "reason": "truncated"},
    ]

    text = json.dumps({"vectors": vectors, "invalid": invalid}, indent=2) + "\n"
    for out in OUTS:
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(text, encoding="utf-8")
        print(f"wrote {len(vectors)} vectors and {len(invalid)} invalid cases to {out}")


if __name__ == "__main__":
    main()
