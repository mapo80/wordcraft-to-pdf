#!/usr/bin/env python3
"""worker_smoke.py — protocol smoke test and throughput check for wcpdf_worker.

Speaks docs/protocol.md to the worker over stdin/stdout (4-byte little-endian length + payload
frames) and checks: convert before init (error 9), init (ready + version), convert and
convert_buffer of every fixture, page ranges, error codes (file not found, invalid format,
invalid argument), malformed / unknown messages, a refused buffer request staying in step, quit
and EOF shutdown. Then a throughput run converts every fixture N times in buffer mode and
reports conversions/sec and p50/p95 latency.

Standard library only; run with `python3 -I`:

    python3 -I tests/worker_smoke.py --worker output/bin/wcpdf_worker \
        --fixtures tests/fixtures --fonts tests/fixtures/fonts --iterations 5

Exit status 0 when every check passed, 1 otherwise (2 for usage errors).
"""

import argparse
import json
import os
import queue
import statistics
import struct
import subprocess
import sys
import tempfile
import threading
import time

FORMAT_UNKNOWN, FORMAT_DOCX, FORMAT_TEXT = 0, 1, 7
E_LOAD_FAILED, E_INVALID_FORMAT, E_FILE_NOT_FOUND = 2, 4, 5
E_NOT_INIT, E_INVALID_ARGUMENT = 9, 10


class Failure(Exception):
    pass


class Worker:
    """One wcpdf_worker process. Frames are read on a thread so every wait has a deadline."""

    def __init__(self, path, timeout):
        self.timeout = timeout
        self.proc = subprocess.Popen(
            [path], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE
        )
        self.frames = queue.Queue()
        self.stderr_tail = []
        threading.Thread(target=self._read_frames, daemon=True).start()
        threading.Thread(target=self._drain_stderr, daemon=True).start()

    def _read_exact(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.proc.stdout.read(n - len(buf))
            if not chunk:
                return None
            buf += chunk
        return buf

    def _read_frames(self):
        while True:
            head = self._read_exact(4)
            if head is None:
                self.frames.put(None)
                return
            (n,) = struct.unpack("<I", head)
            body = self._read_exact(n) if n else b""
            if body is None:
                self.frames.put(None)
                return
            self.frames.put(body)

    def _drain_stderr(self):
        for line in self.proc.stderr:
            self.stderr_tail = (self.stderr_tail + [line.decode("utf-8", "replace").rstrip()])[-20:]

    def send_raw(self, payload):
        self.proc.stdin.write(struct.pack("<I", len(payload)) + payload)
        self.proc.stdin.flush()

    def send(self, msg, data=None):
        self.send_raw(json.dumps(msg).encode("utf-8"))
        if data is not None:
            self.send_raw(data)

    def recv_frame(self, timeout=None):
        try:
            frame = self.frames.get(timeout=timeout or self.timeout)
        except queue.Empty:
            raise Failure("no response within %ss" % (timeout or self.timeout))
        if frame is None:
            raise Failure("worker closed stdout (exit %s); stderr: %s" % (self.proc.poll(), self.stderr_tail))
        return frame

    def recv(self, timeout=None):
        return json.loads(self.recv_frame(timeout).decode("utf-8"))

    def request(self, msg, data=None, timeout=None):
        self.send(msg, data)
        resp = self.recv(timeout)
        pdf = None
        if resp.get("type") == "buffer_result" and resp.get("success"):
            pdf = self.recv_frame(timeout)
        return resp, pdf

    def close(self, quit_msg=True):
        try:
            if quit_msg:
                self.send({"type": "quit"})
            self.proc.stdin.close()
        except OSError:
            pass
        try:
            return self.proc.wait(timeout=self.timeout)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait()
            return None


class Checks:
    def __init__(self):
        self.passed = 0
        self.failed = 0

    def check(self, cond, what):
        if cond:
            self.passed += 1
        else:
            self.failed += 1
            print("  FAIL: %s" % what)
        return cond


def barcode_substituted(diagnostics):
    for d in diagnostics or []:
        font = (d.get("font") or "").replace(" ", "").lower()
        if "barcode" in font and d.get("substituted_with"):
            return True
    return False


def percentile(values, p):
    s = sorted(values)
    if not s:
        return 0.0
    k = (len(s) - 1) * p / 100.0
    lo, hi = int(k), min(int(k) + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (k - lo)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--worker", required=True)
    ap.add_argument("--fixtures", required=True, help="folder with *.docx fixtures")
    ap.add_argument("--fonts", help="font folder passed in init")
    ap.add_argument("--iterations", type=int, default=5, help="throughput passes over the fixtures (0 = skip)")
    ap.add_argument("--timeout", type=float, default=120.0, help="seconds to wait for any one response")
    ap.add_argument("--min-throughput", type=float, default=0.0, help="fail below this many conversions/sec")
    ap.add_argument("--json-out", help="write the throughput summary here")
    args = ap.parse_args()

    worker_path = os.path.abspath(args.worker)
    if not os.path.isfile(worker_path):
        print("ERROR: worker not found: %s" % worker_path)
        return 2
    fixtures = sorted(
        os.path.join(os.path.abspath(args.fixtures), f)
        for f in os.listdir(args.fixtures)
        if f.lower().endswith(".docx")
    )
    if not fixtures:
        print("ERROR: no .docx fixtures in %s" % args.fixtures)
        return 2
    fonts = [os.path.abspath(args.fonts)] if args.fonts else []

    c = Checks()
    tmp = tempfile.mkdtemp(prefix="wcpdf-worker-smoke.")
    print("=== wcpdf_worker protocol smoke test ===")
    print("Worker:   %s" % worker_path)
    print("Fixtures: %d" % len(fixtures))
    print("Fonts:    %s" % (fonts or "(none)"))
    print("Scratch:  %s" % tmp)
    print("")

    w = Worker(worker_path, args.timeout)
    try:
        # ---- before init ----
        print("[1/6] Lifecycle")
        r, _ = w.request({"type": "convert", "id": 1, "input": fixtures[0], "output": os.path.join(tmp, "x.pdf")})
        c.check(r.get("type") == "result" and r.get("id") == 1 and r.get("success") is False
                and r.get("error_code") == E_NOT_INIT, "convert before init -> error 9, got %r" % r)

        t0 = time.monotonic()
        r = w.request({"type": "init", "font_paths": fonts}, timeout=max(args.timeout, 300))[0]
        init_ms = (time.monotonic() - t0) * 1000
        c.check(r.get("type") == "ready", "init -> ready, got %r" % r)
        c.check("WordCraftPdf" in (r.get("version") or ""), "ready.version, got %r" % r.get("version"))
        c.check(r.get("diagnostics") == [], "init diagnostics should be empty, got %r" % r.get("diagnostics"))
        print("  ready in %.0f ms: %s" % (init_ms, r.get("version")))

        # ---- file + buffer conversions ----
        print("[2/6] convert / convert_buffer")
        next_id = 100
        for path in fixtures:
            name = os.path.basename(path)
            out = os.path.join(tmp, name + ".pdf")
            next_id += 1
            r, _ = w.request({"type": "convert", "id": next_id, "input": path, "output": out, "format": FORMAT_UNKNOWN})
            ok = c.check(r.get("type") == "result" and r.get("id") == next_id and r.get("success") is True,
                         "%s: convert failed: %r" % (name, r))
            pages_file = r.get("page_count") or 0
            if ok:
                with open(out, "rb") as f:
                    head = f.read(5)
                c.check(head == b"%PDF-", "%s: output is not a PDF" % name)
                c.check(pages_file > 0, "%s: page_count 0" % name)
                if "barcode" in name.lower() and fonts:
                    c.check(not barcode_substituted(r.get("diagnostics")),
                            "%s: barcode font substituted despite font_paths: %r" % (name, r.get("diagnostics")))

            with open(path, "rb") as f:
                data = f.read()
            next_id += 1
            r, pdf = w.request({"type": "convert_buffer", "id": next_id, "format": FORMAT_UNKNOWN,
                                "data_size": len(data), "options": {}}, data)
            ok = c.check(r.get("type") == "buffer_result" and r.get("id") == next_id and r.get("success") is True,
                         "%s: convert_buffer failed: %r" % (name, r))
            if ok:
                c.check(pdf is not None and pdf[:5] == b"%PDF-", "%s: buffer PDF lacks %%PDF-" % name)
                c.check(r.get("data_size") == len(pdf or b""), "%s: data_size %r != frame %d"
                        % (name, r.get("data_size"), len(pdf or b"")))
                c.check(r.get("page_count") == pages_file, "%s: page_count file %r vs buffer %r"
                        % (name, pages_file, r.get("page_count")))
            print("  %-24s %3d page(s), %d diagnostic(s)" % (name, pages_file, len(r.get("diagnostics") or [])))

        # ---- options ----
        print("[3/6] Options")
        multi = max(fixtures, key=os.path.getsize)
        r, _ = w.request({"type": "convert", "id": 200, "input": multi, "output": os.path.join(tmp, "range.pdf"),
                          "options": {"page_range": "1"}})
        c.check(r.get("success") is True and r.get("page_count") == 1, "page_range \"1\": %r" % r)
        r, pdf = w.request({"type": "convert_buffer", "id": 201, "format": FORMAT_TEXT, "data_size": 11,
                            "options": {"tagged_pdf": True, "title": "Smoke", "author": "worker_smoke.py"}},
                           b"Hello world")
        c.check(r.get("success") is True and pdf is not None and pdf[:5] == b"%PDF-", "text + tagged/title: %r" % r)
        print("  page_range, tagged_pdf/title/author, text format: done")

        # ---- errors ----
        print("[4/6] Error paths")
        cases = [
            ("missing file", {"type": "convert", "id": 300, "input": os.path.join(tmp, "nope.docx"),
                              "output": os.path.join(tmp, "nope.pdf")}, None, E_FILE_NOT_FOUND),
            ("unsupported extension", {"type": "convert", "id": 301, "input": os.path.join(tmp, "sheet.xlsx"),
                                       "output": os.path.join(tmp, "sheet.pdf")}, None, E_INVALID_FORMAT),
            ("bad page range", {"type": "convert", "id": 302, "input": fixtures[0], "output": os.path.join(tmp, "r.pdf"),
                                "options": {"page_range": "999999"}}, None, E_INVALID_ARGUMENT),
            ("garbage buffer", {"type": "convert_buffer", "id": 303, "format": FORMAT_UNKNOWN, "data_size": 12},
             b"not a doc!\x00\xff", E_INVALID_FORMAT),
            ("data_size mismatch", {"type": "convert_buffer", "id": 304, "format": FORMAT_DOCX, "data_size": 99},
             b"short", E_INVALID_ARGUMENT),
            ("missing output path", {"type": "convert", "id": 305, "input": fixtures[0]}, None, E_INVALID_ARGUMENT),
            ("unknown format code", {"type": "convert", "id": 306, "input": fixtures[0],
                                     "output": os.path.join(tmp, "f.pdf"), "format": 42}, None, E_INVALID_FORMAT),
        ]
        for label, msg, data, want in cases:
            r, _ = w.request(msg, data)
            c.check(r.get("success") is False and r.get("error_code") == want and r.get("id") == msg["id"]
                    and r.get("error_message"), "%s: expected error %d, got %r" % (label, want, r))
            print("  %-22s -> %s %s" % (label, r.get("error_code"), r.get("error_message")))
        for label, raw in [("malformed JSON", b"{not json"), ("unknown type", b'{"type":"bogus"}')]:
            w.send_raw(raw)
            r = w.recv()
            c.check(r.get("type") == "error" and r.get("message"), "%s -> error, got %r" % (label, r))
            print("  %-22s -> error: %s" % (label, r.get("message")))
        # The worker is still in step after all of the above.
        r, pdf = w.request({"type": "convert_buffer", "id": 399, "format": FORMAT_TEXT, "data_size": 2}, b"ok")
        c.check(r.get("success") is True and r.get("id") == 399, "worker out of step after errors: %r" % r)

        # ---- throughput ----
        summary = None
        if args.iterations > 0:
            print("[5/6] Throughput (%d pass(es) x %d fixtures, buffer mode)" % (args.iterations, len(fixtures)))
            docs = []
            for p in fixtures:
                with open(p, "rb") as f:
                    docs.append((os.path.basename(p), f.read()))
            latencies, per_doc, pages = [], {}, 0
            t_start = time.monotonic()
            for i in range(args.iterations):
                for name, data in docs:
                    next_id += 1
                    t0 = time.monotonic()
                    r, pdf = w.request({"type": "convert_buffer", "id": next_id, "format": FORMAT_UNKNOWN,
                                        "data_size": len(data)}, data)
                    ms = (time.monotonic() - t0) * 1000
                    if not c.check(r.get("success") is True and pdf and pdf[:5] == b"%PDF-",
                                   "throughput %s pass %d: %r" % (name, i, r)):
                        continue
                    latencies.append(ms)
                    per_doc.setdefault(name, []).append(ms)
                    pages += r.get("page_count") or 0
            elapsed = time.monotonic() - t_start
            rate = len(latencies) / elapsed if elapsed > 0 else 0.0
            summary = {
                "conversions": len(latencies),
                "pages": pages,
                "seconds": round(elapsed, 3),
                "conversions_per_sec": round(rate, 2),
                "pages_per_sec": round(pages / elapsed, 1) if elapsed > 0 else 0.0,
                "p50_ms": round(percentile(latencies, 50), 1),
                "p95_ms": round(percentile(latencies, 95), 1),
                "max_ms": round(max(latencies), 1) if latencies else 0.0,
                "per_fixture_median_ms": {k: round(statistics.median(v), 1) for k, v in sorted(per_doc.items())},
            }
            for k, v in summary["per_fixture_median_ms"].items():
                print("  %-24s median %7.1f ms" % (k, v))
            print("  %d conversions (%d pages) in %.2fs: %.1f conv/s, %.0f pages/s, p50 %.1f ms, p95 %.1f ms, max %.1f ms"
                  % (summary["conversions"], pages, elapsed, rate, summary["pages_per_sec"],
                     summary["p50_ms"], summary["p95_ms"], summary["max_ms"]))
            if args.min_throughput > 0:
                c.check(rate >= args.min_throughput, "throughput %.2f conv/s below --min-throughput %.2f"
                        % (rate, args.min_throughput))
            if args.json_out:
                with open(args.json_out, "w") as f:
                    json.dump(summary, f, indent=2)
        else:
            print("[5/6] Throughput skipped (--iterations 0)")

        # ---- shutdown ----
        print("[6/6] Shutdown")
        rc = w.close(quit_msg=True)
        c.check(rc == 0, "quit -> exit 0, got %r (stderr: %s)" % (rc, w.stderr_tail))
        print("  quit: exit %s" % rc)

        # A second worker: EOF on stdin is a clean shutdown too. Without font_paths the barcode
        # font should be substituted (informational: a system may have it installed).
        w2 = Worker(worker_path, args.timeout)
        r = w2.request({"type": "init"}, timeout=max(args.timeout, 300))[0]
        c.check(r.get("type") == "ready", "second worker init: %r" % r)
        barcode = [p for p in fixtures if "barcode" in os.path.basename(p).lower()]
        if barcode:
            r, _ = w2.request({"type": "convert", "id": 1, "input": barcode[0], "output": os.path.join(tmp, "nb.pdf")})
            note = "substituted (expected without font_paths)" if barcode_substituted(r.get("diagnostics")) \
                else "NOT substituted (installed on this system?)"
            print("  control: barcode font without font_paths: %s" % note)
        rc = w2.close(quit_msg=False)
        c.check(rc == 0, "EOF -> exit 0, got %r" % rc)
        print("  EOF:  exit %s" % rc)
    except Failure as e:
        c.check(False, str(e))
    finally:
        if w.proc.poll() is None:
            w.proc.kill()

    print("")
    if c.failed:
        print("=== WORKER SMOKE FAILED: %d of %d checks ===" % (c.failed, c.passed + c.failed))
        return 1
    print("=== WORKER SMOKE PASSED: %d checks ===" % c.passed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
