#!/bin/bash
# write-build-metadata.sh — Record what an output/ directory was built from, and its sizes.
# Usage: write-build-metadata.sh <output-dir> <wordcraft-tag> <wordcraft-commit> <craft-fonts> <target>
set -euo pipefail

OUTPUT_DIR="${1:?output dir}"
WC_VERSION="${2:?wordcraft tag}"
WC_COMMIT="${3:?wordcraft commit}"
CRAFT_FONTS="${4:?craft-fonts ref or dir}"
TARGET="${5:?target triple}"
PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"

size_of() { [ -f "$1" ] && wc -c < "$1" | tr -d '[:space:]' || echo 0; }

files_json=""
total=0
while IFS= read -r f; do
    rel="${f#"$OUTPUT_DIR"/}"
    s="$(size_of "$f")"
    total=$((total + s))
    files_json="$files_json${files_json:+,}
    {\"path\": \"$rel\", \"bytes\": $s}"
done < <(find "$OUTPUT_DIR/bin" "$OUTPUT_DIR/lib" -type f | sort)

cat > "$OUTPUT_DIR/build-metadata.json" <<EOF
{
  "wordcraftpdf_version": "$(sed -n 's/^version = "\(.*\)"/\1/p' "$PROJECT_DIR/Cargo.toml" | head -1)",
  "wordcraftpdf_commit": "$(git -C "$PROJECT_DIR" rev-parse --verify -q HEAD 2>/dev/null || echo uncommitted)",
  "wordcraft_version": "$WC_VERSION",
  "wordcraft_commit": "$WC_COMMIT",
  "craft_fonts": "$CRAFT_FONTS",
  "target": "$TARGET",
  "rustc": "$(rustc --version)",
  "built_at": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "total_bytes": $total,
  "files": [$files_json
  ]
}
EOF

echo "    Wrote $OUTPUT_DIR/build-metadata.json"
find "$OUTPUT_DIR/bin" "$OUTPUT_DIR/lib" -type f | sort | while IFS= read -r f; do
    printf '    %-32s %8s KB\n' "${f#"$OUTPUT_DIR"/}" "$(( $(size_of "$f") / 1024 ))"
done
