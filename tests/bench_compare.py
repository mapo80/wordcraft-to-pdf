"""Benchmark a WordCraftPdf or SlimLO worker over their shared framed protocol (macOS).

usage: python3 -I tests/bench_compare.py NAME WORKER FIXTURES OUTDIR ROUNDS [RESOURCE_PATH]

RESOURCE_PATH marks a SlimLO worker: it is sent in `init` and the environment SlimLO's SDK sets
up for its worker is applied. Runs the worker under /usr/bin/time -l (peak RSS), sends init
(font_paths = FIXTURES/fonts), then ROUNDS x every fixture in buffer mode; the first conversion
is reported on its own, the rest give the medians. Writes the last round's PDFs to OUTDIR and a
JSON summary to OUTDIR/summary.json. scripts/compare-slimlo.sh drives it for both releases.
"""
import json
import os
import pathlib
import re
import statistics
import struct
import subprocess
import sys
import time


def send(p, payload):
    p.stdin.write(struct.pack("<I", len(payload)) + payload)
    p.stdin.flush()


def recv(p):
    head = p.stdout.read(4)
    if len(head) < 4:
        raise EOFError("worker closed stdout")
    n = struct.unpack("<I", head)[0]
    return p.stdout.read(n)


def main():
    name, worker, fixtures, outdir, rounds = sys.argv[1:6]
    resource = sys.argv[6] if len(sys.argv) > 6 else None
    fixtures, outdir, rounds = pathlib.Path(fixtures), pathlib.Path(outdir), int(rounds)
    outdir.mkdir(parents=True, exist_ok=True)
    docs = sorted(fixtures.glob("*.docx"))

    env = dict(os.environ)
    if resource:  # what SlimLO's SDK sets up for its worker on macOS
        prog = str(pathlib.Path(resource) / "program")
        env["DYLD_LIBRARY_PATH"] = prog
        env["SAL_LOK_OPTIONS"] = "unipoll"
        env["SAL_LOG"] = "+WARN.vcl.fonts+INFO.vcl+WARN.vcl"
        home = outdir / "home"
        home.mkdir(exist_ok=True)
        env["HOME"] = str(home)

    stderr_path = outdir / "worker.stderr"
    t0 = time.perf_counter()
    with open(stderr_path, "wb") as err:
        p = subprocess.Popen(["/usr/bin/time", "-l", worker], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=err, env=env)
        init = {"type": "init", "font_paths": [str(fixtures / "fonts")]}
        if resource:
            init["resource_path"] = resource
        send(p, json.dumps(init).encode())
        ready = json.loads(recv(p))
        t_ready = (time.perf_counter() - t0) * 1000
        assert ready["type"] == "ready", ready

        lat = {d.name: [] for d in docs}
        first = None
        i = 0
        start = time.perf_counter()
        for rnd in range(rounds):
            for d in docs:
                data = d.read_bytes()
                i += 1
                t = time.perf_counter()
                send(p, json.dumps({"type": "convert_buffer", "id": i, "format": 1, "data_size": len(data)}).encode())
                send(p, data)
                r = json.loads(recv(p))
                if not r["success"]:
                    raise SystemExit(f"{name}: {d.name} failed: {r}")
                pdf = recv(p)
                ms = (time.perf_counter() - t) * 1000
                if first is None:
                    first = ms
                else:
                    lat[d.name].append(ms)
                if rnd == rounds - 1:
                    (outdir / (d.stem + ".pdf")).write_bytes(pdf)
        total = time.perf_counter() - start
        send(p, b'{"type":"quit"}')
        p.wait(timeout=60)

    m = re.search(rb"(\d+)\s+maximum resident set size", stderr_path.read_bytes())
    rss_mb = int(m.group(1)) / 1048576 if m else None
    summary = {
        "name": name,
        "version": ready.get("version"),
        "ready_ms": round(t_ready, 1),
        "first_conversion_ms": round(first, 1),
        "throughput_per_s": round(i / total, 1),
        "peak_rss_mb": round(rss_mb, 1) if rss_mb else None,
        "p50_ms": {k: round(statistics.median(v), 1) for k, v in lat.items()},
        "p95_ms": {k: round(sorted(v)[max(0, int(len(v) * 0.95) - 1)], 1) for k, v in lat.items()},
    }
    (outdir / "summary.json").write_text(json.dumps(summary, indent=2))
    print(json.dumps(summary, indent=2))


main()
