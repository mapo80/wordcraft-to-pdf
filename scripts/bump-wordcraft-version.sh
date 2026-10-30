#!/bin/bash
# bump-wordcraft-version.sh — Move WordCraftPdf to another WordCraft release tag.
# Updates WORDCRAFT_VERSION and the Krilla patch in Cargo.toml (which must match WordCraft's).
# Usage: bump-wordcraft-version.sh v0.13.0
set -euo pipefail

NEW="${1:?Usage: bump-wordcraft-version.sh <wordcraft-tag, e.g. v0.13.0>}"
PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
WC_REPO="${WC_REPO:-https://github.com/storytold/wordcraft.git}"
OLD="$(tr -d '[:space:]' < "$PROJECT_DIR/WORDCRAFT_VERSION")"

if ! git ls-remote --exit-code --tags "$WC_REPO" "refs/tags/$NEW" >/dev/null; then
    echo "ERROR: tag $NEW not found in $WC_REPO"
    exit 1
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
git clone -q --depth 1 --branch "$NEW" --no-checkout "$WC_REPO" "$TMP/wc"
NEW_KRILLA="$(git -C "$TMP/wc" show HEAD:Cargo.toml | grep -E '^krilla[[:space:]]*=' | head -1 || true)"

printf '%s\n' "$NEW" > "$PROJECT_DIR/WORDCRAFT_VERSION"
if [ -n "$NEW_KRILLA" ]; then
    # Replace our krilla line with WordCraft's, verbatim.
    awk -v line="$NEW_KRILLA" '/^krilla[[:space:]]*=/{print line; next} {print}' "$PROJECT_DIR/Cargo.toml" > "$TMP/Cargo.toml"
    mv "$TMP/Cargo.toml" "$PROJECT_DIR/Cargo.toml"
else
    echo "WARNING: WordCraft $NEW no longer patches krilla; review [patch.crates-io] in Cargo.toml."
fi

echo "WordCraft: $OLD -> $NEW"
echo "Krilla:    ${NEW_KRILLA:-<none>}"
echo ""
echo "Next: delete wc-src/ (or set WC_SRC_DIR), run scripts/build.sh, then scripts/run-gate.sh."
