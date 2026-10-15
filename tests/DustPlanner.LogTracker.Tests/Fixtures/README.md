# Power.log fixtures

Real logs from HearthSim's public test corpus [hsreplay-test-data](https://github.com/HearthSim/hsreplay-test-data)
(CC0 1.0), renamed for what they exercise:

| File | Origin | Exercises |
|---|---|---|
| `ranked_standard_both_names.power.log` | `hslog-tests/88998_missing_player_hash.power.log` | ranked Standard, both names known, PLAYSTATE lines without the `#1234` discriminator |
| `ranked_wild_unknown_name.power.log` | `hslog-tests/186852_hearthstone_access_eoe.power.log` | ranked Wild, one player initially `UNKNOWN HUMAN PLAYER`, name learned from later lines |
| `casual_inferrable_player.power.log` | `hslog-tests/25770_inferrable_player.power.log` | casual Standard, friendly player inferred from the first revealed hand |

`synthetic_edge_cases.power.log` is hand-written: two games in one file, a concede, a midnight rollover and a game
that never completes. No card data from the game is included.
