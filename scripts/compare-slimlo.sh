#!/bin/bash
# compare-slimlo.sh — Benchmark the published WordCraftPdf and SlimLO workers side by side (macOS).
#
# Downloads both releases' macOS workers, prepares SlimLO's the way its NuGet .targets does
# (recreating the symlinks NuGet drops), then runs tests/bench_compare.py on tests/fixtures for
# each, alternating, RUNS times. Results: <out>/{wordcraftpdf,slimlo}-<n>/summary.json + PDFs.
#
# Usage: scripts/compare-slimlo.sh [out-dir]
# Env:   WCPDF_VERSION (default 0.1.0), SLIMLO_VERSION (default 0.1.10), ROUNDS (20), RUNS (2)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT="${1:-$PROJECT_DIR/tests/output/compare}"
WCPDF_VERSION="${WCPDF_VERSION:-0.1.0}"
SLIMLO_VERSION="${SLIMLO_VERSION:-0.1.10}"
ROUNDS="${ROUNDS:-20}"
RUNS="${RUNS:-2}"

if [ "$(uname -s)" != "Darwin" ]; then
    echo "ERROR: macOS only (the SlimLO preparation mirrors its macOS .targets)."
    exit 1
fi
case "$(uname -m)" in
    arm64) RID="osx-arm64" ;;
    *)     RID="osx-x64" ;;
esac
for tool in gh python3 unzip tar otool; do
    command -v "$tool" >/dev/null 2>&1 || { echo "ERROR: $tool not found"; exit 1; }
done

DL="$OUT/downloads"
mkdir -p "$DL"

echo ">>> WordCraftPdf v$WCPDF_VERSION ($RID)"
WC_ARCHIVE="wordcraftpdf-native-$RID-$WCPDF_VERSION"
[ -d "$DL/$WC_ARCHIVE" ] || {
    gh release download "v$WCPDF_VERSION" -R mapo80/wordcraft-to-pdf -p "$WC_ARCHIVE.tar.gz" -D "$DL" --clobber
    tar xzf "$DL/$WC_ARCHIVE.tar.gz" -C "$DL"
}
WC_WORKER="$DL/$WC_ARCHIVE/bin/wcpdf_worker"

echo ">>> SlimLO v$SLIMLO_VERSION ($RID)"
LO_PKG="SlimLO.NativeAssets.macOS.$SLIMLO_VERSION"
LO_DIR="$DL/$LO_PKG"
[ -d "$LO_DIR" ] || {
    gh release download "v$SLIMLO_VERSION" -R mapo80/libreoffice-to-pdf -p "$LO_PKG.nupkg" -D "$DL" --clobber
    unzip -q "$DL/$LO_PKG.nupkg" -d "$LO_DIR"
}
LO_RES="$LO_DIR/slimlo/$RID"
chmod +x "$LO_RES/program/slimlo_worker"
# What SlimLO.NativeAssets.macOS.targets does after copying the files.
(cd "$LO_RES"
 [ -e Resources ] || ln -sf program Resources
 [ -e Frameworks ] || ln -sf program Frameworks
 cd program
 for f in libslimlo.*.*.*.dylib; do
     [ -f "$f" ] || continue
     soname="$(otool -D "$f" | tail -1 | xargs basename)"
     [ "$soname" != "$f" ] && ln -sf "$f" "$soname"
     base="${soname%%.*}.dylib"
     [ "$base" != "$soname" ] && ln -sf "$soname" "$base"
 done
 for d in registry filter config palette fonts liblangtag; do
     [ -d "../share/$d" ] && [ ! -e "$d" ] && ln -sfn "../share/$d" "$d"
 done
 true)

FIXTURES="$PROJECT_DIR/tests/fixtures"
for run in $(seq 1 "$RUNS"); do
    echo ">>> Run $run/$RUNS ($ROUNDS rounds each)"
    python3 -I "$PROJECT_DIR/tests/bench_compare.py" wordcraftpdf "$WC_WORKER" "$FIXTURES" "$OUT/wordcraftpdf-$run" "$ROUNDS" >/dev/null
    python3 -I "$PROJECT_DIR/tests/bench_compare.py" slimlo "$LO_RES/program/slimlo_worker" "$FIXTURES" "$OUT/slimlo-$run" "$ROUNDS" "$LO_RES" >/dev/null
done

python3 -I - "$OUT" "$RUNS" <<'EOF'
import json, pathlib, sys
out, runs = pathlib.Path(sys.argv[1]), int(sys.argv[2])
rows = []
for run in range(1, runs + 1):
    for name in ("wordcraftpdf", "slimlo"):
        s = json.loads((out / f"{name}-{run}" / "summary.json").read_text())
        rows.append(s)
        print(f"{name:13s} run {run}: ready {s['ready_ms']:6.1f} ms  first {s['first_conversion_ms']:6.1f} ms  "
              f"{s['throughput_per_s']:6.1f} conv/s  peak {s['peak_rss_mb']} MB")
wc, lo = rows[-2], rows[-1]
print(f"\n{'document':24s} {'WordCraftPdf':>12s} {'SlimLO':>10s} {'speed-up':>9s}")
for doc, ms in wc["p50_ms"].items():
    print(f"{doc:24s} {ms:10.1f}ms {lo['p50_ms'][doc]:8.1f}ms {lo['p50_ms'][doc] / ms:8.1f}x")
EOF
