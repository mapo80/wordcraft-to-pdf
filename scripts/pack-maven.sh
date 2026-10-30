#!/bin/bash
# pack-maven.sh — Build the WordCraftPdf Java SDK JAR with the native worker bundled
#
# Usage:
#   ./scripts/pack-maven.sh                                  # bundle output/ for the current platform
#   ./scripts/pack-maven.sh osx-arm64=output linux-x64=out-linux-x64
#                                                            # bundle several platforms' build outputs
#   ./scripts/pack-maven.sh --api-only                       # API JAR without a worker
#
#   SKIP_TESTS=1 ./scripts/pack-maven.sh                     # don't run the tests
#
# Each build output folder is what scripts/build.sh produces: bin/wcpdf_worker[.exe] and licenses/.
# Platform ids: osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64, win-arm64.
#
# Produces:
#   java/wordcraftpdf/target/wordcraftpdf-<version>.jar
#     native/<platform>/wcpdf_worker[.exe]          the worker (extracted at run time)
#     native/<platform>/wcpdf_worker[.exe].sha256   its SHA-256 (cache key and integrity check)
#     native/<platform>/build-metadata.json         when present in the build output
#     META-INF/wordcraftpdf/licenses/...            third-party licenses of the worker
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
JAVA_DIR="$ROOT_DIR/java"

if [ ! -f "$JAVA_DIR/pom.xml" ]; then
    echo "ERROR: $JAVA_DIR/pom.xml not found"
    exit 1
fi

current_platform() {
    local os arch
    case "$(uname -s)" in
        Darwin) os=osx ;;
        Linux) os=linux ;;
        MINGW*|MSYS*|CYGWIN*) os=win ;;
        *) echo "ERROR: unsupported OS $(uname -s)" >&2; exit 1 ;;
    esac
    case "$(uname -m)" in
        arm64|aarch64) arch=arm64 ;;
        x86_64|amd64) arch=x64 ;;
        *) echo "ERROR: unsupported architecture $(uname -m)" >&2; exit 1 ;;
    esac
    echo "$os-$arch"
}

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | cut -d' ' -f1
    else
        shasum -a 256 "$1" | cut -d' ' -f1
    fi
}

API_ONLY=0
BUNDLES=()
for arg in "$@"; do
    case "$arg" in
        --api-only) API_ONLY=1 ;;
        -h|--help) sed -n '2,23p' "$0"; exit 0 ;;
        *=*) BUNDLES+=("$arg") ;;
        *) echo "ERROR: unexpected argument '$arg' (expected <platform>=<build-output-dir>)"; exit 1 ;;
    esac
done
if [ "$API_ONLY" -eq 0 ] && [ ${#BUNDLES[@]} -eq 0 ]; then
    BUNDLES=("$(current_platform)=$ROOT_DIR/output")
fi

STAGE="$(mktemp -d "${TMPDIR:-/tmp}/wcpdf-maven.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT

echo "=== Building WordCraftPdf Java SDK ==="
echo ""

for bundle in ${BUNDLES[@]+"${BUNDLES[@]}"}; do
    platform="${bundle%%=*}"
    out_dir="${bundle#*=}"
    case "$platform" in
        win-*) exe=wcpdf_worker.exe ;;
        *) exe=wcpdf_worker ;;
    esac
    worker="$out_dir/bin/$exe"
    if [ ! -f "$worker" ]; then
        echo "ERROR: $worker not found (run scripts/build.sh first, or pass --api-only)"
        exit 1
    fi
    dest="$STAGE/native/$platform"
    mkdir -p "$dest"
    cp "$worker" "$dest/$exe"
    echo "$(sha256_of "$dest/$exe")  $exe" > "$dest/$exe.sha256"
    if [ -f "$out_dir/build-metadata.json" ]; then
        cp "$out_dir/build-metadata.json" "$dest/build-metadata.json"
    fi
    if [ -d "$out_dir/licenses" ]; then
        mkdir -p "$STAGE/META-INF/wordcraftpdf/licenses"
        cp -R "$out_dir/licenses/." "$STAGE/META-INF/wordcraftpdf/licenses/"
    else
        echo "WARNING: $out_dir/licenses not found; the JAR will not carry the worker's licenses"
    fi
    echo "Bundling $platform: $worker ($(du -h "$worker" | cut -f1))"
done
echo ""

MVN_ARGS=(clean package -pl wordcraftpdf -am "-Dwcpdf.native.dir=$STAGE")
if [ "${SKIP_TESTS:-0}" = "1" ]; then
    MVN_ARGS+=(-DskipTests)
fi

cd "$JAVA_DIR"
mvn "${MVN_ARGS[@]}"

VERSION="$(sed -n 's:.*<version>\(.*\)</version>.*:\1:p' "$JAVA_DIR/pom.xml" | head -1)"
JAR="$JAVA_DIR/wordcraftpdf/target/wordcraftpdf-$VERSION.jar"

echo ""
echo "=== Package built ==="
ls -la "$JAR"
echo ""
echo "Bundled native content:"
unzip -l "$JAR" | awk '$4 ~ /^native\// {print "  " $4 " (" $1 " bytes)"}'

echo ""
echo "=== Maven coordinates ==="
echo "  <dependency>"
echo "    <groupId>com.wordcraftpdf</groupId>"
echo "    <artifactId>wordcraftpdf</artifactId>"
echo "    <version>$VERSION</version>"
echo "  </dependency>"
