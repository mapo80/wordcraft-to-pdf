#!/bin/bash
# test.sh — WordCraftPdf C API smoke test
#
# Compiles tests/test_convert.c against an artifact directory (output/ by default) and runs it
# on every tests/fixtures/*.docx, in file and buffer mode, with tests/fixtures/fonts passed to
# wcpdf_init. Exits non-zero on any failure.
#
# Usage:
#   ./tests/test.sh                      # artifacts in output/
#   WCPDF_DIR=/opt/wcpdf ./tests/test.sh # another artifact directory
#
# Environment:
#   WCPDF_DIR        Artifact directory with include/ and lib/ (default: output/)
#   WCPDF_TEST_OUT   Where PDFs and the test binary go (default: tests/output/)
#   CC               C compiler (default: cc; on Windows cl, then gcc, then clang)
#
# Linux / macOS: links -lwcpdf with an rpath to the artifact's lib/.
# Windows (Git Bash): builds with MSVC cl (run inside a Developer environment, e.g.
# ilammy/msvc-dev-cmd in CI) against wcpdf.lib, or MinGW gcc / clang straight against
# wcpdf.dll, and copies the DLL next to the test executable.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
FIXTURES_DIR="$SCRIPT_DIR/fixtures"

WCPDF_DIR="${WCPDF_DIR:-$PROJECT_DIR/output}"
WCPDF_DIR="$(cd "$WCPDF_DIR" 2>/dev/null && pwd)" || {
    echo "ERROR: artifact directory not found: ${WCPDF_DIR}"
    echo "Run ./scripts/build.sh first, or set WCPDF_DIR=/path/to/artifacts"
    exit 1
}
TEST_OUT="${WCPDF_TEST_OUT:-$SCRIPT_DIR/output}"
mkdir -p "$TEST_OUT"
TEST_OUT="$(cd "$TEST_OUT" && pwd)"

case "$(uname -s)" in
    CYGWIN*|MINGW*|MSYS*) PLATFORM="windows" ;;
    Darwin)               PLATFORM="macos" ;;
    *)                    PLATFORM="linux" ;;
esac

# Windows-native tools want C:/... paths, not /c/...
native_path() {
    if [ "$PLATFORM" = "windows" ] && command -v cygpath >/dev/null 2>&1; then
        cygpath -m "$1"
    else
        printf '%s\n' "$1"
    fi
}

echo "=== WordCraftPdf C API test ==="
echo "Platform:  $PLATFORM"
echo "Artifacts: $WCPDF_DIR"
echo "Output:    $TEST_OUT"
echo ""

# -----------------------------------------------------------
# [1/3] Check artifacts
# -----------------------------------------------------------
echo "[1/3] Checking artifacts..."
case "$PLATFORM" in
    windows) LIB_FILE="$WCPDF_DIR/lib/wcpdf.dll"; WORKER="$WCPDF_DIR/bin/wcpdf_worker.exe" ;;
    macos)   LIB_FILE="$WCPDF_DIR/lib/libwcpdf.dylib"; WORKER="$WCPDF_DIR/bin/wcpdf_worker" ;;
    *)       LIB_FILE="$WCPDF_DIR/lib/libwcpdf.so"; WORKER="$WCPDF_DIR/bin/wcpdf_worker" ;;
esac
missing=0
for f in "$WCPDF_DIR/include/wcpdf.h" "$LIB_FILE" "$WORKER"; do
    if [ -f "$f" ]; then
        printf '  %-28s OK (%s KB)\n' "${f#"$WCPDF_DIR"/}" "$(( $(wc -c < "$f") / 1024 ))"
    else
        echo "  ${f#"$WCPDF_DIR"/}: MISSING"
        missing=1
    fi
done
for f in licenses/wordcraft build-metadata.json; do
    [ -e "$WCPDF_DIR/$f" ] && echo "  $f: OK" || echo "  $f: missing (warning)"
done
[ "$missing" -eq 0 ] || { echo "ERROR: required artifacts missing in $WCPDF_DIR"; exit 1; }

FIXTURES=()
for f in "$FIXTURES_DIR"/*.docx; do
    [ -f "$f" ] && FIXTURES+=("$(native_path "$f")")
done
if [ "${#FIXTURES[@]}" -eq 0 ]; then
    echo "ERROR: no fixtures in $FIXTURES_DIR"
    exit 1
fi
echo "  fixtures: ${#FIXTURES[@]}"
echo ""

# -----------------------------------------------------------
# [2/3] Compile the test program
# -----------------------------------------------------------
echo "[2/3] Compiling test_convert.c..."
SRC="$(native_path "$SCRIPT_DIR/test_convert.c")"
INC="$(native_path "$WCPDF_DIR/include")"
if [ "$PLATFORM" = "windows" ]; then
    TEST_BIN="$TEST_OUT/test_convert.exe"
    CC="${CC:-}"
    if [ -z "$CC" ]; then
        for c in cl gcc clang; do
            if command -v "$c" >/dev/null 2>&1; then CC="$c"; break; fi
        done
    fi
    [ -n "$CC" ] || { echo "ERROR: no C compiler (cl, gcc or clang) in PATH"; exit 1; }
    case "$(basename "$CC")" in
        cl|cl.exe)
            # Dash-style switches: Git Bash would rewrite /nologo-style ones as paths.
            (cd "$TEST_OUT" && "$CC" -nologo -W3 -O2 -D_CRT_SECURE_NO_WARNINGS -I"$INC" "$SRC" \
                "$(native_path "$WCPDF_DIR/lib/wcpdf.lib")" -Fe"$(native_path "$TEST_BIN")")
            ;;
        *)
            "$CC" -std=c11 -O2 -Wall -D__USE_MINGW_ANSI_STDIO=1 -o "$TEST_BIN" "$SRC" -I"$INC" \
                "$(native_path "$LIB_FILE")"
            ;;
    esac
    # The loader looks next to the executable first.
    cp "$LIB_FILE" "$TEST_OUT/"
else
    TEST_BIN="$TEST_OUT/test_convert"
    CC="${CC:-cc}"
    "$CC" -std=c11 -O2 -Wall -Wextra -D_POSIX_C_SOURCE=200809L -o "$TEST_BIN" "$SRC" \
        -I"$INC" -L"$WCPDF_DIR/lib" -lwcpdf -pthread \
        -Wl,-rpath,"$WCPDF_DIR/lib"
    # Fallback in case a toolchain drops the rpath.
    if [ "$PLATFORM" = "macos" ]; then
        export DYLD_LIBRARY_PATH="$WCPDF_DIR/lib${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
    else
        export LD_LIBRARY_PATH="$WCPDF_DIR/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    fi
fi
echo "  OK ($CC)"
echo ""

# -----------------------------------------------------------
# [3/3] Run
# -----------------------------------------------------------
echo "[3/3] Running conversions..."
rm -f "$TEST_OUT"/*.pdf
set +e
"$TEST_BIN" --out "$(native_path "$TEST_OUT")" --fonts "$(native_path "$FIXTURES_DIR/fonts")" "${FIXTURES[@]}"
RC=$?
set -e
echo ""

if [ "$RC" -ne 0 ]; then
    echo "=== FAILED (test_convert exit $RC) ==="
    exit "$RC"
fi

# Independent check of what landed on disk: one PDF per fixture, each with the %PDF- magic.
bad=0
for f in "$FIXTURES_DIR"/*.docx; do
    pdf="$TEST_OUT/$(basename "$f").pdf"
    if [ ! -s "$pdf" ] || [ "$(head -c 5 "$pdf")" != "%PDF-" ]; then
        echo "FAIL: $pdf missing or not a PDF"
        bad=1
    fi
done
[ "$bad" -eq 0 ] || { echo "=== FAILED ==="; exit 1; }

echo "=== SUCCESS: ${#FIXTURES[@]} fixtures converted (PDFs in $TEST_OUT) ==="
