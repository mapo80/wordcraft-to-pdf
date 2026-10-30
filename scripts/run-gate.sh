#!/bin/bash
# run-gate.sh — WordCraftPdf release gate for one platform's artifact directory.
#
# Steps (each under a hard timeout; any failure fails the gate):
#   [1] Artifact layout, build-metadata.json, dynamic-dependency allowlist (ldd / otool / objdump)
#   [2] C API smoke test          tests/test.sh            (file + buffer mode, error paths)
#   [3] Worker protocol smoke     tests/worker_smoke.py    (docs/protocol.md + throughput)
#   [4] .NET SDK tests            dotnet test dotnet/WordCraftPdf.sln   (optional)
#   [5] Java SDK tests            mvn -f java/pom.xml verify            (optional)
#
# Usage:
#   ./scripts/run-gate.sh [artifact-dir]          # default: output/
#
# Environment:
#   GATE_TIMEOUT_C          seconds for step 2 (default 300)
#   GATE_TIMEOUT_WORKER     seconds for step 3 (default 600)
#   GATE_TIMEOUT_DOTNET     seconds for step 4 (default 1200)
#   GATE_TIMEOUT_JAVA       seconds for step 5 (default 1200)
#   GATE_ITERATIONS         throughput passes over the fixtures (default 5; 0 = skip)
#   GATE_MIN_THROUGHPUT     fail below this many conversions/sec (default 0 = report only)
#   GATE_ENABLE_DOTNET      auto|0|1 (default auto: run when dotnet and the solution exist)
#   GATE_ENABLE_JAVA        auto|0|1 (default auto: run when mvn and java/pom.xml exist)
#   GATE_DOTNET_FRAMEWORK   target framework passed with -f (default: all in the solution)
#   GATE_CHECK_DEPS         1 (default) = enforce the dynamic-dependency allowlist; 0 = report
#   GATE_STRICT_METADATA    1 = invalid/mismatched build-metadata.json fails the gate
#                           (default 0 = warning; CI sets 1)
#   GATE_REPORT_DIR         logs and throughput.json (default: tests/output/gate)
#   PYTHON                  Python 3 interpreter (default: python3, then python)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

ARTIFACT_DIR="${1:-$PROJECT_DIR/output}"
ARTIFACT_DIR="$(cd "$ARTIFACT_DIR" 2>/dev/null && pwd)" || {
    echo "ERROR: artifact directory not found: ${1:-$PROJECT_DIR/output}"
    exit 1
}

GATE_TIMEOUT_C="${GATE_TIMEOUT_C:-300}"
GATE_TIMEOUT_WORKER="${GATE_TIMEOUT_WORKER:-600}"
GATE_TIMEOUT_DOTNET="${GATE_TIMEOUT_DOTNET:-1200}"
GATE_TIMEOUT_JAVA="${GATE_TIMEOUT_JAVA:-1200}"
GATE_ITERATIONS="${GATE_ITERATIONS:-5}"
GATE_MIN_THROUGHPUT="${GATE_MIN_THROUGHPUT:-0}"
GATE_ENABLE_DOTNET="${GATE_ENABLE_DOTNET:-auto}"
GATE_ENABLE_JAVA="${GATE_ENABLE_JAVA:-auto}"
GATE_DOTNET_FRAMEWORK="${GATE_DOTNET_FRAMEWORK:-}"
GATE_CHECK_DEPS="${GATE_CHECK_DEPS:-1}"
GATE_STRICT_METADATA="${GATE_STRICT_METADATA:-0}"
GATE_REPORT_DIR="${GATE_REPORT_DIR:-$PROJECT_DIR/tests/output/gate}"

for v in GATE_ENABLE_DOTNET GATE_ENABLE_JAVA; do
    case "${!v}" in auto|0|1) ;; *) echo "ERROR: $v must be auto, 0 or 1 (got '${!v}')"; exit 1 ;; esac
done
for v in GATE_CHECK_DEPS GATE_STRICT_METADATA; do
    case "${!v}" in 0|1) ;; *) echo "ERROR: $v must be 0 or 1 (got '${!v}')"; exit 1 ;; esac
done

case "$(uname -s)" in
    CYGWIN*|MINGW*|MSYS*) PLATFORM="windows"; EXE=".exe" ;;
    Darwin)               PLATFORM="macos";   EXE="" ;;
    *)                    PLATFORM="linux";   EXE="" ;;
esac
WORKER="$ARTIFACT_DIR/bin/wcpdf_worker$EXE"
case "$PLATFORM" in
    windows) LIB_FILE="$ARTIFACT_DIR/lib/wcpdf.dll" ;;
    macos)   LIB_FILE="$ARTIFACT_DIR/lib/libwcpdf.dylib" ;;
    *)       LIB_FILE="$ARTIFACT_DIR/lib/libwcpdf.so" ;;
esac

native_path() {
    if [ "$PLATFORM" = "windows" ] && command -v cygpath >/dev/null 2>&1; then
        cygpath -m "$1"
    else
        printf '%s\n' "$1"
    fi
}

PYTHON="${PYTHON:-}"
if [ -z "$PYTHON" ]; then
    for p in python3 python; do
        if command -v "$p" >/dev/null 2>&1 && "$p" -c 'import sys; sys.exit(sys.version_info[0] != 3)' 2>/dev/null; then
            PYTHON="$p"; break
        fi
    done
fi
[ -n "$PYTHON" ] || { echo "ERROR: Python 3 is required (set PYTHON=...)"; exit 1; }

mkdir -p "$GATE_REPORT_DIR"
GATE_REPORT_DIR="$(cd "$GATE_REPORT_DIR" && pwd)"

# Run "$@" with a hard timeout, killing the whole process tree on expiry. Exit 124 on timeout.
run_with_timeout() {
    local seconds="$1"
    shift
    "$PYTHON" -I -c '
import os, signal, subprocess, sys
timeout, cmd = float(sys.argv[1]), sys.argv[2:]
if os.name == "nt":
    proc = subprocess.Popen(cmd, creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
else:
    proc = subprocess.Popen(cmd, start_new_session=True)
try:
    sys.exit(proc.wait(timeout=timeout))
except subprocess.TimeoutExpired:
    if os.name == "nt":
        subprocess.call(["taskkill", "/F", "/T", "/PID", str(proc.pid)])
    else:
        for sig in (signal.SIGTERM, signal.SIGKILL):
            try:
                os.killpg(proc.pid, sig)
            except ProcessLookupError:
                break
            try:
                proc.wait(timeout=3)
                break
            except subprocess.TimeoutExpired:
                pass
    sys.exit(124)
except KeyboardInterrupt:
    proc.kill()
    sys.exit(130)
' "$seconds" "$@"
}

cleanup_orphans() {
    # Workers started by this gate (by path), should a step have been killed mid-conversion.
    if command -v pkill >/dev/null 2>&1; then
        pkill -f "$ARTIFACT_DIR/bin/wcpdf_worker" 2>/dev/null || true
    fi
}
trap cleanup_orphans EXIT

# Run one step: name, timeout, log file, command... Fails the gate on error or timeout.
run_step() {
    local name="$1" seconds="$2" log="$3"
    shift 3
    local rc
    set +e
    run_with_timeout "$seconds" "$@" 2>&1 | tee "$log"
    rc=${PIPESTATUS[0]}
    set -e
    if [ "$rc" -eq 124 ]; then
        echo "FAIL: $name timed out after ${seconds}s (log: $log)"
        exit 124
    elif [ "$rc" -ne 0 ]; then
        echo "FAIL: $name (exit $rc, log: $log)"
        exit "$rc"
    fi
    echo "PASS: $name"
    echo ""
}

echo "=== WordCraftPdf Gate ==="
echo "Platform:   $PLATFORM"
echo "Artifacts:  $ARTIFACT_DIR"
echo "Reports:    $GATE_REPORT_DIR"
echo "Python:     $PYTHON ($("$PYTHON" -c 'import platform; print(platform.python_version())'))"
echo "Timeouts:   C ${GATE_TIMEOUT_C}s, worker ${GATE_TIMEOUT_WORKER}s, .NET ${GATE_TIMEOUT_DOTNET}s, Java ${GATE_TIMEOUT_JAVA}s"
echo ""
cleanup_orphans

# -----------------------------------------------------------
# [1/5] Artifact layout, metadata, dynamic dependencies
# -----------------------------------------------------------
echo "[1/5] Artifact checks"
for f in "$WORKER" "$LIB_FILE" "$ARTIFACT_DIR/include/wcpdf.h"; do
    [ -f "$f" ] || { echo "FAIL: missing ${f#"$ARTIFACT_DIR"/}"; exit 1; }
done
[ -d "$ARTIFACT_DIR/licenses/wordcraft" ] || { echo "FAIL: missing licenses/wordcraft"; exit 1; }
echo "  layout: OK"

metadata_problem=""
if [ ! -f "$ARTIFACT_DIR/build-metadata.json" ]; then
    metadata_problem="build-metadata.json missing"
else
    metadata_problem="$("$PYTHON" -I -c '
import json, sys
try:
    m = json.load(open(sys.argv[1], encoding="utf-8"))
except Exception as e:
    print("build-metadata.json is not valid JSON: %s" % e); sys.exit()
want = open(sys.argv[2]).read().strip()
if m.get("wordcraft_version") != want:
    print("wordcraft_version %r != WORDCRAFT_VERSION %r" % (m.get("wordcraft_version"), want))
' "$(native_path "$ARTIFACT_DIR/build-metadata.json")" "$(native_path "$PROJECT_DIR/WORDCRAFT_VERSION")")"
fi
if [ -n "$metadata_problem" ]; then
    if [ "$GATE_STRICT_METADATA" = "1" ]; then
        echo "FAIL: $metadata_problem"
        exit 1
    fi
    echo "  WARN: $metadata_problem (GATE_STRICT_METADATA=1 makes this fatal)"
else
    echo "  build-metadata.json: OK"
fi

# Only the C runtime / OS libraries may be linked dynamically: everything else (fonts, shaping,
# PDF writing) is compiled in. Anything outside the allowlist would have to be installed on the
# target machine.
deps_of() {
    case "$PLATFORM" in
        linux)
            ldd "$1" 2>&1 | awk '/=>/ {print $1; next} /statically linked/ {print "(static)"; next} {print $1}'
            ;;
        macos)
            otool -L "$1" | tail -n +2 | awk '{print $1}'
            ;;
        windows)
            if command -v objdump >/dev/null 2>&1; then
                objdump -p "$1" | awk '/DLL Name:/ {print $3}'
            elif command -v dumpbin >/dev/null 2>&1; then
                dumpbin -nologo -dependents "$1" | awk '/\.dll$/ {print $1}'
            fi
            ;;
    esac
}
allowed_dep() {
    local d="$1"
    case "$PLATFORM" in
        linux)
            case "$d" in
                linux-vdso.so.*|linux-gate.so.*|/lib*/ld-linux*|/lib/ld-*|ld-linux*|libc.so.6|libm.so.6|\
                libgcc_s.so.1|libpthread.so.0|libdl.so.2|librt.so.1|"(static)") return 0 ;;
            esac ;;
        macos)
            case "$d" in
                /usr/lib/libSystem.B.dylib|/usr/lib/libiconv.2.dylib|/usr/lib/libc++.1.dylib|\
                /usr/lib/libobjc.A.dylib|/usr/lib/libresolv.9.dylib|/System/Library/Frameworks/*|\
                @rpath/libwcpdf.dylib) return 0 ;;
            esac ;;
        windows)
            case "$(printf '%s' "$d" | tr '[:upper:]' '[:lower:]')" in
                kernel32.dll|ntdll.dll|advapi32.dll|bcrypt.dll|bcryptprimitives.dll|userenv.dll|ws2_32.dll|\
                ole32.dll|oleaut32.dll|shell32.dll|secur32.dll|crypt32.dll|synchronization.dll|\
                api-ms-win-*.dll|ucrtbase.dll|vcruntime140.dll|vcruntime140_1.dll|msvcrt.dll) return 0 ;;
            esac ;;
    esac
    return 1
}
deps_failed=0
for bin in "$WORKER" "$LIB_FILE"; do
    deps="$(deps_of "$bin" || true)"
    if [ -z "$deps" ]; then
        echo "  deps $(basename "$bin"): (no inspection tool available, skipped)"
        continue
    fi
    echo "  deps $(basename "$bin"): $(echo "$deps" | tr '\n' ' ')"
    while IFS= read -r d; do
        [ -z "$d" ] && continue
        [ "$PLATFORM" = "linux" ] && case "$d" in "$bin"*|*:) continue ;; esac
        if ! allowed_dep "$d"; then
            echo "    unexpected dynamic dependency: $d"
            deps_failed=1
        fi
        case "$(printf '%s' "$d" | tr '[:upper:]' '[:lower:]')" in
            vcruntime140*.dll) echo "    note: $d needs the VC++ redistributable (link with +crt-static to drop it)" ;;
        esac
    done <<< "$deps"
done
if [ "$deps_failed" -ne 0 ]; then
    if [ "$GATE_CHECK_DEPS" = "1" ]; then
        echo "FAIL: dynamic dependencies outside the allowlist"
        exit 1
    fi
    echo "  WARN: dependency allowlist violations (GATE_CHECK_DEPS=0)"
fi
echo "PASS: artifact checks"
echo ""

# -----------------------------------------------------------
# [2/5] C API smoke test
# -----------------------------------------------------------
echo "[2/5] C API smoke test (tests/test.sh)"
run_step "C API smoke test" "$GATE_TIMEOUT_C" "$GATE_REPORT_DIR/c-smoke.log" \
    env WCPDF_DIR="$ARTIFACT_DIR" WCPDF_TEST_OUT="$GATE_REPORT_DIR/c-smoke" bash "$PROJECT_DIR/tests/test.sh"

# -----------------------------------------------------------
# [3/5] Worker protocol smoke + throughput
# -----------------------------------------------------------
echo "[3/5] Worker protocol smoke + throughput (tests/worker_smoke.py)"
run_step "worker protocol smoke" "$GATE_TIMEOUT_WORKER" "$GATE_REPORT_DIR/worker-smoke.log" \
    "$PYTHON" -I "$(native_path "$PROJECT_DIR/tests/worker_smoke.py")" \
        --worker "$(native_path "$WORKER")" \
        --fixtures "$(native_path "$PROJECT_DIR/tests/fixtures")" \
        --fonts "$(native_path "$PROJECT_DIR/tests/fixtures/fonts")" \
        --iterations "$GATE_ITERATIONS" \
        --min-throughput "$GATE_MIN_THROUGHPUT" \
        --json-out "$(native_path "$GATE_REPORT_DIR/throughput.json")"

# -----------------------------------------------------------
# [4/5] .NET SDK
# -----------------------------------------------------------
# The SDK tests find the worker through these variables (see dotnet/ and java/).
WCPDF_WORKER_PATH="$(native_path "$WORKER")"
WCPDF_NATIVE_DIR="$(native_path "$ARTIFACT_DIR")"
export WCPDF_WORKER_PATH WCPDF_NATIVE_DIR

enabled() {  # enabled <auto|0|1> <available 0|1> <name>
    case "$1" in
        1) [ "$2" -eq 1 ] || { echo "FAIL: $3 gate requested but its toolchain or project is missing"; exit 1; }; return 0 ;;
        0) return 1 ;;
        auto) [ "$2" -eq 1 ] ;;
    esac
}

DOTNET_SLN="$PROJECT_DIR/dotnet/WordCraftPdf.sln"
[ -f "$DOTNET_SLN" ] || DOTNET_SLN="$PROJECT_DIR/dotnet/WordCraftPdf.slnx"
DOTNET_AVAILABLE=0
command -v dotnet >/dev/null 2>&1 && [ -f "$DOTNET_SLN" ] && DOTNET_AVAILABLE=1
if enabled "$GATE_ENABLE_DOTNET" "$DOTNET_AVAILABLE" ".NET"; then
    echo "[4/5] .NET SDK tests"
    DOTNET_ARGS=(test "$(native_path "$DOTNET_SLN")" -c Release --nologo --verbosity minimal)
    [ -n "$GATE_DOTNET_FRAMEWORK" ] && DOTNET_ARGS+=(-f "$GATE_DOTNET_FRAMEWORK")
    run_step ".NET SDK tests" "$GATE_TIMEOUT_DOTNET" "$GATE_REPORT_DIR/dotnet.log" dotnet "${DOTNET_ARGS[@]}"
else
    echo "[4/5] .NET SDK tests skipped (GATE_ENABLE_DOTNET=$GATE_ENABLE_DOTNET, available=$DOTNET_AVAILABLE)"
    echo ""
fi

# -----------------------------------------------------------
# [5/5] Java SDK
# -----------------------------------------------------------
JAVA_POM="$PROJECT_DIR/java/pom.xml"
[ -f "$JAVA_POM" ] || JAVA_POM="$PROJECT_DIR/java/wordcraftpdf/pom.xml"
JAVA_AVAILABLE=0
command -v mvn >/dev/null 2>&1 && command -v java >/dev/null 2>&1 && [ -f "$JAVA_POM" ] && JAVA_AVAILABLE=1
if enabled "$GATE_ENABLE_JAVA" "$JAVA_AVAILABLE" "Java"; then
    echo "[5/5] Java SDK tests"
    run_step "Java SDK tests" "$GATE_TIMEOUT_JAVA" "$GATE_REPORT_DIR/java.log" \
        mvn -B -ntp -f "$(native_path "$JAVA_POM")" verify \
            "-Dwcpdf.worker.path=$WCPDF_WORKER_PATH" "-Dwcpdf.native.dir=$WCPDF_NATIVE_DIR"
else
    echo "[5/5] Java SDK tests skipped (GATE_ENABLE_JAVA=$GATE_ENABLE_JAVA, available=$JAVA_AVAILABLE)"
    echo ""
fi

if [ -f "$GATE_REPORT_DIR/throughput.json" ]; then
    "$PYTHON" -I -c '
import json, sys
t = json.load(open(sys.argv[1]))
print("Throughput: %(conversions)d conversions, %(conversions_per_sec).1f conv/s, %(pages_per_sec).0f pages/s, "
      "p50 %(p50_ms).1f ms, p95 %(p95_ms).1f ms" % t)
' "$(native_path "$GATE_REPORT_DIR/throughput.json")"
fi
echo "=== Gate PASSED ==="
