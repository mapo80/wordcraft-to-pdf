#!/bin/bash
# apply-patches.sh — Apply WordCraftPdf modifications to vanilla WordCraft source
#
# Supports two types of patches (in patches/, applied in name order):
#   *.sh     — executed with $1 = WordCraft source dir; must be idempotent
#   *.patch  — applied via git apply; skipped when already applied
#
set -euo pipefail

WC_SRC_DIR="${1:?Usage: apply-patches.sh <wordcraft-src-dir>}"
PATCHES_DIR="$(cd "$(dirname "$0")/../patches" && pwd)"

if [ ! -d "$WC_SRC_DIR" ]; then
    echo "ERROR: WordCraft source directory not found: $WC_SRC_DIR"
    exit 1
fi

WC_SRC_DIR="$(cd "$WC_SRC_DIR" && pwd)"

TOTAL=0
OK=0
FAIL=0

for file in "$PATCHES_DIR"/*.sh "$PATCHES_DIR"/*.patch; do
    [ -f "$file" ] || continue
    NAME="$(basename "$file")"
    TOTAL=$((TOTAL + 1))

    echo "--- [$TOTAL] $NAME"

    case "$file" in
        *.sh)
            if bash "$file" "$WC_SRC_DIR"; then
                echo "    OK"
                OK=$((OK + 1))
            else
                echo "    FAILED"
                FAIL=$((FAIL + 1))
            fi
            ;;
        *.patch)
            if git -C "$WC_SRC_DIR" apply --check "$file" 2>/dev/null; then
                git -C "$WC_SRC_DIR" apply "$file"
                echo "    OK"
                OK=$((OK + 1))
            elif git -C "$WC_SRC_DIR" apply --reverse --check "$file" 2>/dev/null; then
                echo "    OK (already applied)"
                OK=$((OK + 1))
            else
                echo "    FAILED: Patch does not apply cleanly"
                FAIL=$((FAIL + 1))
            fi
            ;;
    esac
done

echo ""
echo "=== Patch Summary ==="
echo "Total:   $TOTAL"
echo "Applied: $OK"
echo "Failed:  $FAIL"

if [ "$FAIL" -gt 0 ]; then
    echo ""
    echo "ERROR: Some patches failed. Fix them before building."
    exit 1
fi

if [ "$TOTAL" -eq 0 ]; then
    echo "NOTE: No patches found in $PATCHES_DIR (vanilla WordCraft)"
fi
