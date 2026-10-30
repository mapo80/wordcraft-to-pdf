# Patches

Modifications applied to the vanilla WordCraft checkout (`wc-src/`) by
`scripts/apply-patches.sh`, in name order, before every build:

- `NNN-name.sh` — run with the WordCraft source directory as `$1`; must be idempotent.
- `NNN-name.patch` — applied with `git apply`; skipped when already applied.

WordCraft builds as-is today, so there are none. Prefer fixing things upstream; add a patch
only for what can't wait for a WordCraft release, and drop it once the release lands.
