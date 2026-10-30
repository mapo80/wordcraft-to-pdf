#!/bin/bash
# build.sh — Main WordCraftPdf build script
# Clones WordCraft source at the tag in WORDCRAFT_VERSION, applies patches, builds the C API and
# the worker against it, and assembles the artifacts in output/.
#
# Environment:
#   WC_SRC_DIR        WordCraft checkout (default: wc-src/). Linked to wc-src/ when elsewhere.
#   OUTPUT_DIR        Artifact directory (default: output/).
#   TARGET            Rust target triple to cross-compile for (default: the host).
#   NPROC             Parallel compile jobs (default: CPU count).
#   WITH_CRAFT_FONTS  1 (default) = embed WordCraft's craft-fonts (CJK, Arabic…) so documents
#                     render without installed fonts; 0 = rely on system fonts only.
#   CRAFT_FONTS_DIR   Use this craft-fonts checkout instead of cloning the pinned one.
#   CLEAN_BUILD       1 = remove target/ and the output before building.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
WC_VERSION="$(tr -d '[:space:]' < "$PROJECT_DIR/WORDCRAFT_VERSION")"
WC_REPO="${WC_REPO:-https://github.com/storytold/wordcraft.git}"
CRAFT_FONTS_REPO="${CRAFT_FONTS_REPO:-https://github.com/storytold/craft-fonts.git}"
to_abs_path() {
    case "$1" in
        /*) printf '%s\n' "$1" ;;
        *) printf '%s\n' "$PROJECT_DIR/$1" ;;
    esac
}

WC_SRC_DIR="$(to_abs_path "${WC_SRC_DIR:-$PROJECT_DIR/wc-src}")"
WC_LINK="$PROJECT_DIR/wc-src"
OUTPUT_DIR="$(to_abs_path "${OUTPUT_DIR:-$PROJECT_DIR/output}")"
NPROC="${NPROC:-$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4)}"
TARGET="${TARGET:-}"
WITH_CRAFT_FONTS="${WITH_CRAFT_FONTS:-1}"
CLEAN_BUILD="${CLEAN_BUILD:-0}"

for flag in WITH_CRAFT_FONTS CLEAN_BUILD; do
    case "${!flag}" in
        0|1) ;;
        *)
            echo "ERROR: $flag must be 0 or 1 (got '${!flag}')."
            exit 1
            ;;
    esac
done

case "$(uname -s)" in
    CYGWIN*|MINGW*|MSYS*) PLATFORM="windows" ;;
    Darwin)               PLATFORM="macos" ;;
    *)                    PLATFORM="linux" ;;
esac

for tool in git cargo rustc; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "ERROR: $tool not found in PATH."
        exit 1
    fi
done

echo "============================================"
echo " WordCraftPdf Build"
echo " WordCraft:    $WC_VERSION"
echo " Platform:     $PLATFORM${TARGET:+ (target $TARGET)}"
echo " Source dir:   $WC_SRC_DIR"
echo " Output dir:   $OUTPUT_DIR"
echo " Parallelism:  $NPROC"
echo " craft-fonts:  $([ "$WITH_CRAFT_FONTS" = "1" ] && echo embedded || echo 'off (system fonts only)')"
echo " Rust:         $(rustc --version)"
if [ "$CLEAN_BUILD" = "1" ]; then
echo " Clean build:  enabled (target/ and output reset)"
fi
echo "============================================"
echo ""

# -----------------------------------------------------------
# Step 1: Clone upstream source (shallow, at the pinned tag)
# -----------------------------------------------------------
if [ ! -e "$WC_SRC_DIR/.git" ]; then
    echo ">>> Step 1: Cloning WordCraft $WC_VERSION..."
    git clone --depth 1 --branch "$WC_VERSION" "$WC_REPO" "$WC_SRC_DIR"
    if [ ! -f "$WC_SRC_DIR/Cargo.toml" ] || [ ! -d "$WC_SRC_DIR/crates/engine" ]; then
        echo "ERROR: git clone failed — $WC_SRC_DIR is incomplete"
        exit 1
    fi
else
    echo ">>> Step 1: Source already exists at $WC_SRC_DIR (skipping clone)"
    CURRENT_TAG="$(git -C "$WC_SRC_DIR" describe --tags --exact-match 2>/dev/null || echo 'unknown')"
    if [ "$CURRENT_TAG" != "$WC_VERSION" ]; then
        echo "    WARNING: Existing source is at $CURRENT_TAG, expected $WC_VERSION"
        echo "    Delete $WC_SRC_DIR to re-clone, or set WC_SRC_DIR to a different path."
    fi
fi

# Cargo finds the engine at wc-src/ (path dependencies in Cargo.toml).
if [ "$WC_SRC_DIR" != "$WC_LINK" ]; then
    if [ -e "$WC_LINK" ] && [ ! -L "$WC_LINK" ]; then
        echo "ERROR: $WC_LINK exists and is not a link; remove it or unset WC_SRC_DIR."
        exit 1
    fi
    ln -sfn "$WC_SRC_DIR" "$WC_LINK"
    echo "    Linked $WC_LINK -> $WC_SRC_DIR"
fi
WC_COMMIT="$(git -C "$WC_SRC_DIR" rev-parse HEAD)"
echo "    Commit: $WC_COMMIT"
echo ""

if [ "$CLEAN_BUILD" = "1" ]; then
    echo ">>> Step 1.5: Cleaning previous build outputs..."
    rm -rf "$PROJECT_DIR/target" "$OUTPUT_DIR"
    echo "    Removed target/ and $OUTPUT_DIR"
    echo ""
fi

# -----------------------------------------------------------
# Step 2: Apply WordCraftPdf patches
# -----------------------------------------------------------
echo ">>> Step 2: Applying patches..."
"$SCRIPT_DIR/apply-patches.sh" "$WC_SRC_DIR"
echo ""

# -----------------------------------------------------------
# Step 3: Align the workspace with the WordCraft checkout
# -----------------------------------------------------------
echo ">>> Step 3: Aligning dependencies with WordCraft $WC_VERSION..."
# Patches in a dependency's workspace don't apply to ours, so Cargo.toml repeats WordCraft's
# required Krilla patch; it must match the checkout.
krilla_patch() { grep -E '^krilla[[:space:]]*=' "$1" | head -1 | tr -d '[:space:]'; }
WC_KRILLA="$(krilla_patch "$WC_SRC_DIR/Cargo.toml")"
OUR_KRILLA="$(krilla_patch "$PROJECT_DIR/Cargo.toml")"
if [ -z "$WC_KRILLA" ]; then
    echo "    WARNING: WordCraft no longer patches krilla; review [patch.crates-io] in Cargo.toml."
elif [ "$WC_KRILLA" != "$OUR_KRILLA" ]; then
    echo "ERROR: the krilla patch in Cargo.toml differs from WordCraft $WC_VERSION's:"
    echo "       WordCraft: $WC_KRILLA"
    echo "       ours:      $OUR_KRILLA"
    echo "       Run scripts/bump-wordcraft-version.sh $WC_VERSION to resync."
    exit 1
fi
# Build with exactly the dependency versions WordCraft locked and tested at this tag; Cargo
# adds our own crates to the copy.
cp "$WC_SRC_DIR/Cargo.lock" "$PROJECT_DIR/Cargo.lock"
echo "    Krilla patch matches; Cargo.lock copied from WordCraft"
echo ""

# -----------------------------------------------------------
# Step 4: craft-fonts (fonts embedded in the engine)
# -----------------------------------------------------------
CRAFT_FONTS_REF=""
if [ "$WITH_CRAFT_FONTS" = "1" ]; then
    echo ">>> Step 4: Preparing craft-fonts..."
    if [ -n "${CRAFT_FONTS_DIR:-}" ]; then
        CRAFT_FONTS_DIR="$(to_abs_path "$CRAFT_FONTS_DIR")"
        echo "    Using CRAFT_FONTS_DIR=$CRAFT_FONTS_DIR"
    else
        # The commit WordCraft's own release builds embed.
        CRAFT_FONTS_REF="$(grep -hE 'craft-fonts-ref:' "$WC_SRC_DIR/.github/workflows/release.yml" 2>/dev/null \
            | head -1 | sed -E 's/.*craft-fonts-ref:[[:space:]]*//' | tr -d "[:space:]\"'")"
        if [ -z "$CRAFT_FONTS_REF" ]; then
            echo "ERROR: no craft-fonts-ref in $WC_SRC_DIR/.github/workflows/release.yml."
            echo "       Set CRAFT_FONTS_DIR to a checkout, or WITH_CRAFT_FONTS=0."
            exit 1
        fi
        CRAFT_FONTS_DIR="$PROJECT_DIR/craft-fonts-src"
        if [ "$(git -C "$CRAFT_FONTS_DIR" rev-parse HEAD 2>/dev/null || true)" != "$CRAFT_FONTS_REF" ]; then
            echo "    Fetching craft-fonts $CRAFT_FONTS_REF..."
            rm -rf "$CRAFT_FONTS_DIR"
            git init -q "$CRAFT_FONTS_DIR"
            git -C "$CRAFT_FONTS_DIR" fetch -q --depth 1 "$CRAFT_FONTS_REPO" "$CRAFT_FONTS_REF"
            git -C "$CRAFT_FONTS_DIR" checkout -q FETCH_HEAD
        else
            echo "    craft-fonts already at $CRAFT_FONTS_REF"
        fi
    fi
    if [ ! -f "$CRAFT_FONTS_DIR/fonts/manifest.txt" ]; then
        echo "ERROR: $CRAFT_FONTS_DIR/fonts/manifest.txt not found."
        exit 1
    fi
    export CRAFT_FONTS_DIR
    # Fail the build rather than ship a worker missing the CJK faces.
    export CRAFT_FONTS_REQUIRED=1
else
    echo ">>> Step 4: Skipping craft-fonts (WITH_CRAFT_FONTS=0)"
    unset CRAFT_FONTS_DIR CRAFT_FONTS_REQUIRED
fi
echo ""

# -----------------------------------------------------------
# Step 5: Build
# -----------------------------------------------------------
echo ">>> Step 5: Building the C API and the worker (release)..."
CARGO_ARGS=(build --release -p wcpdf-capi -p wcpdf-worker -j "$NPROC")
[ -n "$TARGET" ] && CARGO_ARGS+=(--target "$TARGET")
(cd "$PROJECT_DIR" && cargo "${CARGO_ARGS[@]}")
BUILD_DIR="$PROJECT_DIR/target/${TARGET:+$TARGET/}release"
echo ""

# -----------------------------------------------------------
# Step 6: Assemble artifacts
# -----------------------------------------------------------
echo ">>> Step 6: Assembling artifacts..."
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR/bin" "$OUTPUT_DIR/lib" "$OUTPUT_DIR/include" "$OUTPUT_DIR/licenses/wordcraft"
case "$PLATFORM" in
    windows)
        cp "$BUILD_DIR/wcpdf_worker.exe" "$OUTPUT_DIR/bin/"
        cp "$BUILD_DIR/wcpdf.dll" "$OUTPUT_DIR/lib/"
        cp "$BUILD_DIR/wcpdf.dll.lib" "$OUTPUT_DIR/lib/wcpdf.lib" 2>/dev/null || true
        cp "$BUILD_DIR/wcpdf.lib" "$OUTPUT_DIR/lib/wcpdf_static.lib" 2>/dev/null || true
        ;;
    macos)
        cp "$BUILD_DIR/wcpdf_worker" "$OUTPUT_DIR/bin/"
        cp "$BUILD_DIR/libwcpdf.dylib" "$BUILD_DIR/libwcpdf.a" "$OUTPUT_DIR/lib/"
        # Let consumers find the library through their own rpath.
        install_name_tool -id @rpath/libwcpdf.dylib "$OUTPUT_DIR/lib/libwcpdf.dylib"
        ;;
    *)
        cp "$BUILD_DIR/wcpdf_worker" "$OUTPUT_DIR/bin/"
        cp "$BUILD_DIR/libwcpdf.so" "$BUILD_DIR/libwcpdf.a" "$OUTPUT_DIR/lib/"
        ;;
esac
cp "$PROJECT_DIR/native/wcpdf-capi/include/wcpdf.h" "$OUTPUT_DIR/include/"
# The engine is MIT OR Apache-2.0: ship its licence texts and notices with the binaries.
for f in LICENSE-MIT LICENSE-APACHE NOTICE ATTRIBUTION.md; do
    [ -f "$WC_SRC_DIR/$f" ] && cp "$WC_SRC_DIR/$f" "$OUTPUT_DIR/licenses/wordcraft/"
done
if [ "$WITH_CRAFT_FONTS" = "1" ]; then
    # Each font folder has its own licence (OFL.txt…): keep the folder layout so none
    # overwrites another, alongside the manifest that maps fonts to licences.
    (cd "$CRAFT_FONTS_DIR" && find . -maxdepth 3 -type f \
        \( -iname 'LICENSE*' -o -iname 'OFL*' -o -iname 'COPYING*' -o -iname 'ATTRIBUTION*' -o -name manifest.txt \) \
        -not -path './.git/*') | while IFS= read -r f; do
        mkdir -p "$OUTPUT_DIR/licenses/craft-fonts/$(dirname "$f")"
        cp "$CRAFT_FONTS_DIR/$f" "$OUTPUT_DIR/licenses/craft-fonts/$f"
    done
fi
echo "    Done"
echo ""

# -----------------------------------------------------------
# Step 7: Build metadata + size report
# -----------------------------------------------------------
echo ">>> Step 7: Writing build metadata..."
"$SCRIPT_DIR/write-build-metadata.sh" "$OUTPUT_DIR" "$WC_VERSION" "$WC_COMMIT" "${CRAFT_FONTS_REF:-${CRAFT_FONTS_DIR:-none}}" "${TARGET:-$(rustc -vV | sed -n 's/^host: //p')}"
echo ""

echo "============================================"
echo " Build complete!"
echo " Output: $OUTPUT_DIR"
echo "============================================"
echo ""
du -sh "$OUTPUT_DIR" 2>/dev/null || true
