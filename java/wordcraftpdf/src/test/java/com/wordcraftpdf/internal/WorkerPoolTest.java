package com.wordcraftpdf.internal;

import com.wordcraftpdf.ConversionDiagnostic;
import com.wordcraftpdf.ConversionOptions;
import com.wordcraftpdf.ConversionResult;
import com.wordcraftpdf.WordCraftPdfErrorCode;
import com.wordcraftpdf.WordCraftPdfException;
import com.wordcraftpdf.testing.FakeWorkers;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;
import org.junit.jupiter.api.io.TempDir;

import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.Callable;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.jupiter.api.Assertions.*;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

/** Pool behaviour against {@link com.wordcraftpdf.testing.FakeWorker}: deterministic crashes, hangs and slowness. */
@Timeout(value = 120, unit = TimeUnit.SECONDS)
class WorkerPoolTest {

    private static final long TIMEOUT = 20_000;
    private static final long STARTUP = 30_000;

    @TempDir
    static Path scripts;

    private static String normal;

    private final List<WorkerPool> pools = new ArrayList<>();

    @BeforeAll
    static void createScripts() throws Exception {
        assumeTrue(FakeWorkers.supported(), "fake workers are shell scripts");
        normal = FakeWorkers.script(scripts, "normal");
    }

    @BeforeEach
    void noLeftoverWorkers() {
        // Nothing: each test closes its pools.
    }

    @AfterEach
    void closePools() {
        for (WorkerPool p : pools) p.close();
    }

    private WorkerPool pool(String script, int maxWorkers, int maxConversions) {
        WorkerPool p = new WorkerPool(script, null, maxWorkers, maxConversions, STARTUP, 2000);
        pools.add(p);
        return p;
    }

    private static ConversionResult convert(WorkerPool pool, String command, long timeoutMillis) {
        final byte[] doc = ("doc:" + command).getBytes(StandardCharsets.UTF_8);
        final ConversionOptions opts = command == null ? null : ConversionOptions.builder().title(command).build();
        return pool.execute((worker, id) -> worker.convertBuffer(id, doc, doc.length, 1, opts, timeoutMillis));
    }

    private static ConversionResult convert(WorkerPool pool, String command) {
        return convert(pool, command, TIMEOUT);
    }

    static long pid(ConversionResult r) {
        for (ConversionDiagnostic d : r.getDiagnostics()) {
            if (d.getMessage().startsWith("pid=")) return Long.parseLong(d.getMessage().substring(4));
        }
        throw new AssertionError("no pid diagnostic in " + r + " " + r.getErrorMessage());
    }

    private static void assertExited(long pid) {
        ProcessHandle.of(pid).ifPresent(h -> {
            try {
                h.onExit().get(5, TimeUnit.SECONDS);
            } catch (Exception e) {
                fail("worker " + pid + " still alive");
            }
        });
    }

    private static void assertOk(ConversionResult r) {
        assertTrue(r.isSuccess(), () -> "expected success, got " + r.getErrorCode() + ": " + r.getErrorMessage());
    }

    // ---- Basics ----

    @Test
    void bufferConversionRoundTripsAndReportsVersion() {
        WorkerPool p = pool(normal, 1, 0);
        assertNull(p.getVersion());
        ConversionResult r = convert(p, null);
        assertOk(r);
        assertEquals("%PDF-FAKE\ndoc:null", new String(r.getData(), StandardCharsets.UTF_8));
        assertEquals(1, r.getPageCount());
        assertEquals("FakeWorker 1.0", p.getVersion());
    }

    @Test
    void largeBufferRoundTrip() {
        WorkerPool p = pool(normal, 1, 0);
        byte[] doc = new byte[20 * 1024 * 1024 + 3];
        for (int i = 0; i < doc.length; i++) doc[i] = (byte) (i * 7);
        ConversionResult r = p.execute((w, id) -> w.convertBuffer(id, doc, doc.length, 0, null, TIMEOUT));
        assertOk(r);
        assertEquals(doc.length + 10, r.getData().length);
        assertArrayEquals(doc, Arrays.copyOfRange(r.getData(), 10, r.getData().length));
    }

    @Test
    void sliceOfBufferIsSent() {
        WorkerPool p = pool(normal, 1, 0);
        byte[] doc = "abcdefXXXX".getBytes(StandardCharsets.US_ASCII);
        ConversionResult r = p.execute((w, id) -> w.convertBuffer(id, doc, 6, 0, null, TIMEOUT));
        assertOk(r);
        assertEquals("%PDF-FAKE\nabcdef", new String(r.getData(), StandardCharsets.US_ASCII));
    }

    @Test
    void workerFailureIsReportedAndWorkerKept() {
        WorkerPool p = pool(normal, 1, 0);
        long first = pid(convert(p, null));
        ConversionResult r = convert(p, "fail");
        assertFalse(r.isSuccess());
        assertEquals(WordCraftPdfErrorCode.LOAD_FAILED, r.getErrorCode());
        assertEquals("simulated load failure", r.getErrorMessage());
        assertEquals(first, pid(convert(p, null)), "a failed conversion must not replace a healthy worker");
    }

    // ---- Dispatch ----

    @Test
    void workersStartLazilyUpToMax() throws Exception {
        WorkerPool p = pool(normal, 3, 0);
        assertEquals(0, p.liveWorkers());
        assertOk(convert(p, null));
        assertEquals(1, p.liveWorkers());
        // Sequential conversions reuse the idle worker instead of starting new ones.
        Set<Long> pids = new HashSet<>();
        for (int i = 0; i < 5; i++) pids.add(pid(convert(p, null)));
        assertEquals(1, pids.size());
        assertEquals(1, p.liveWorkers());

        // Six concurrent slow conversions: at most three workers.
        ExecutorService ex = Executors.newFixedThreadPool(6);
        try {
            List<Future<ConversionResult>> fs = new ArrayList<>();
            for (int i = 0; i < 6; i++) fs.add(ex.submit(() -> convert(p, "slow:300")));
            Set<Long> concurrentPids = new HashSet<>();
            for (Future<ConversionResult> f : fs) {
                ConversionResult r = f.get();
                assertOk(r);
                concurrentPids.add(pid(r));
            }
            assertEquals(3, concurrentPids.size());
            assertEquals(3, p.liveWorkers());
        } finally {
            ex.shutdownNow();
        }
    }

    @Test
    void dispatchesToIdleWorkerNotBehindBusyOne() throws Exception {
        WorkerPool p = pool(normal, 2, 0);
        p.warmUp();
        assertEquals(2, p.liveWorkers());
        assertEquals(2, p.idleWorkers());

        ExecutorService ex = Executors.newSingleThreadExecutor();
        try {
            Future<ConversionResult> slow = ex.submit(() -> convert(p, "slow:3000"));
            // Wait until the slow conversion holds a worker.
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10);
            while (p.idleWorkers() != 1 && System.nanoTime() < deadline) Thread.sleep(5);
            assertEquals(1, p.idleWorkers());

            long start = System.nanoTime();
            Set<Long> fastPids = new HashSet<>();
            for (int i = 0; i < 6; i++) {
                ConversionResult r = convert(p, null);
                assertOk(r);
                fastPids.add(pid(r));
            }
            long elapsedMs = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start);
            assertFalse(slow.isDone(), "the fast conversions must not have waited for the slow one");
            assertTrue(elapsedMs < 2500, "fast conversions took " + elapsedMs + " ms");
            assertEquals(1, fastPids.size(), "all fast conversions ran on the one idle worker");

            ConversionResult slowResult = slow.get();
            assertOk(slowResult);
            assertFalse(fastPids.contains(pid(slowResult)));
        } finally {
            ex.shutdownNow();
        }
    }

    @Test
    void warmUpStartsAllWorkersInParallel() {
        WorkerPool p = pool(normal, 4, 0);
        long start = System.nanoTime();
        p.warmUp();
        long ms = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start);
        assertEquals(4, p.liveWorkers());
        assertEquals(4, p.idleWorkers());
        assertEquals("FakeWorker 1.0", p.getVersion());
        // Warming up again starts nothing new.
        p.warmUp();
        assertEquals(4, p.liveWorkers());
        assertTrue(ms < 20_000, "warm-up took " + ms + " ms");
    }

    @Test
    void concurrentConversionsAllSucceed() throws Exception {
        WorkerPool p = pool(normal, 4, 0);
        ExecutorService ex = Executors.newFixedThreadPool(16);
        try {
            List<Future<ConversionResult>> fs = new ArrayList<>();
            for (int i = 0; i < 16; i++) {
                final int n = i;
                fs.add(ex.submit(() -> {
                    byte[] doc = ("document " + n).getBytes(StandardCharsets.UTF_8);
                    ConversionResult r = p.execute((w, id) -> w.convertBuffer(id, doc, doc.length, 1, null, TIMEOUT));
                    assertEquals("%PDF-FAKE\ndocument " + n, new String(r.getData(), StandardCharsets.UTF_8));
                    return r;
                }));
            }
            for (Future<ConversionResult> f : fs) assertOk(f.get());
            assertTrue(p.liveWorkers() <= 4);
        } finally {
            ex.shutdownNow();
        }
    }

    // ---- Failures ----

    @Test
    void crashIsReportedAndWorkerReplaced() {
        WorkerPool p = pool(normal, 1, 0);
        long before = pid(convert(p, null));
        ConversionResult crash = convert(p, "crash");
        assertFalse(crash.isSuccess());
        assertEquals(WordCraftPdfErrorCode.WORKER_CRASHED, crash.getErrorCode());
        assertTrue(crash.getErrorMessage().contains("exit code 134"), crash.getErrorMessage());
        assertTrue(crash.getErrorMessage().contains("fake: simulated crash"), crash.getErrorMessage());
        assertEquals(0, p.liveWorkers());

        ConversionResult next = convert(p, null);
        assertOk(next);
        assertNotEquals(before, pid(next));
    }

    @Test
    void timeoutKillsWorkerAndDescendants() throws Exception {
        WorkerPool p = pool(normal, 1, 0);
        long before = pid(convert(p, null));
        ExecutorService ex = Executors.newSingleThreadExecutor();
        ConversionResult r;
        long ms;
        try {
            long start = System.nanoTime();
            Future<ConversionResult> f = ex.submit(() -> convert(p, "spawn-child-and-hang", 3000));
            // The worker's child must exist before the timeout, or the check below proves nothing.
            long seen = System.nanoTime() + TimeUnit.SECONDS.toNanos(3);
            while (FakeWorkers.descendantsMatching("sleep 600").isEmpty() && System.nanoTime() < seen) {
                Thread.sleep(10);
            }
            assertFalse(FakeWorkers.descendantsMatching("sleep 600").isEmpty(), "the worker's child never appeared");
            r = f.get(20, TimeUnit.SECONDS);
            ms = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start);
        } finally {
            ex.shutdownNow();
        }
        assertEquals(WordCraftPdfErrorCode.TIMEOUT, r.getErrorCode());
        assertTrue(ms < 10_000, "timeout took " + ms + " ms");

        // The hung worker and the process it started are gone.
        ProcessHandle worker = ProcessHandle.of(before).orElse(null);
        if (worker != null) assertTrue(worker.onExit().get(5, TimeUnit.SECONDS) != null);
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (!FakeWorkers.descendantsMatching("sleep 600").isEmpty() && System.nanoTime() < deadline) {
            Thread.sleep(20);
        }
        assertTrue(FakeWorkers.descendantsMatching("sleep 600").isEmpty(), "child of the worker survived");

        ConversionResult next = convert(p, null);
        assertOk(next);
        assertNotEquals(before, pid(next));
    }

    @Test
    void interruptKillsWorkerAndRestoresFlag() throws Exception {
        WorkerPool p = pool(normal, 1, 0);
        long before = pid(convert(p, null));
        AtomicReference<ConversionResult> result = new AtomicReference<>();
        AtomicReference<Boolean> flag = new AtomicReference<>();
        Thread t = new Thread(() -> {
            result.set(convert(p, "hang", 60_000));
            flag.set(Thread.currentThread().isInterrupted());
        });
        t.start();
        Thread.sleep(500);
        t.interrupt();
        t.join(10_000);
        assertFalse(t.isAlive());
        assertFalse(result.get().isSuccess());
        assertTrue(result.get().getErrorMessage().contains("interrupted"), result.get().getErrorMessage());
        assertTrue(flag.get(), "the interrupt flag must be restored");
        assertTrue(ProcessHandle.of(before).map(h -> {
            try {
                h.onExit().get(5, TimeUnit.SECONDS);
                return true;
            } catch (Exception e) {
                return false;
            }
        }).orElse(true));

        assertOk(convert(p, null));
    }

    @Test
    void interruptedWhileWaitingForWorker() throws Exception {
        WorkerPool p = pool(normal, 1, 0);
        Thread.currentThread().interrupt();
        ConversionResult r = convert(p, null);
        assertTrue(Thread.interrupted(), "flag restored");
        assertFalse(r.isSuccess());
        assertOk(convert(p, null));
    }

    @Test
    void garbageResponseDiscardsWorker() {
        WorkerPool p = pool(normal, 1, 0);
        long before = pid(convert(p, null));
        ConversionResult r = convert(p, "garbage");
        assertEquals(WordCraftPdfErrorCode.UNKNOWN, r.getErrorCode());
        assertTrue(r.getErrorMessage().startsWith("Protocol error"), r.getErrorMessage());
        ConversionResult next = convert(p, null);
        assertOk(next);
        assertNotEquals(before, pid(next));
    }

    @Test
    void mismatchedIdDiscardsWorker() {
        WorkerPool p = pool(normal, 1, 0);
        long before = pid(convert(p, null));
        ConversionResult r = convert(p, "wrong-id");
        assertEquals(WordCraftPdfErrorCode.UNKNOWN, r.getErrorCode());
        assertTrue(r.getErrorMessage().contains("does not match"), r.getErrorMessage());
        assertNotEquals(before, pid(convert(p, null)));
    }

    @Test
    void workerKilledWhileIdleIsReplacedTransparently() throws Exception {
        WorkerPool p = pool(normal, 1, 0);
        long before = pid(convert(p, null));
        ProcessHandle h = ProcessHandle.of(before).orElseThrow(AssertionError::new);
        h.destroyForcibly();
        h.onExit().get(5, TimeUnit.SECONDS);
        ConversionResult r = convert(p, null);
        assertOk(r);
        assertNotEquals(before, pid(r));
    }

    @Test
    void stderrFloodDoesNotBlockWorker() {
        WorkerPool p = pool(normal, 1, 0);
        // 8 MiB of stderr: far beyond any pipe buffer.
        ConversionResult r = convert(p, "stderr-flood:" + (8 * 1024 * 1024));
        assertOk(r);
        assertOk(convert(p, null));
    }

    // ---- Recycling ----

    @Test
    void recyclingAfterMaxConversions() {
        WorkerPool p = pool(normal, 1, 2);
        List<Long> pids = new ArrayList<>();
        for (int i = 0; i < 5; i++) {
            ConversionResult r = convert(p, null);
            assertOk(r);
            pids.add(pid(r));
        }
        assertEquals(pids.get(0), pids.get(1));
        assertNotEquals(pids.get(1), pids.get(2));
        assertEquals(pids.get(2), pids.get(3));
        assertNotEquals(pids.get(3), pids.get(4));
        assertEquals(1, p.liveWorkers());
    }

    @Test
    void recyclingWithConcurrency() throws Exception {
        WorkerPool p = pool(normal, 3, 2);
        ExecutorService ex = Executors.newFixedThreadPool(8);
        try {
            List<Future<ConversionResult>> fs = new ArrayList<>();
            for (int i = 0; i < 24; i++) fs.add(ex.submit((Callable<ConversionResult>) () -> convert(p, null)));
            for (Future<ConversionResult> f : fs) assertOk(f.get());
            assertTrue(p.liveWorkers() <= 3);
        } finally {
            ex.shutdownNow();
        }
    }

    // ---- Start-up failures ----

    @Test
    void initErrorIsInitFailed() throws Exception {
        WorkerPool p = pool(FakeWorkers.script(scripts, "init-error"), 1, 0);
        ConversionResult r = convert(p, null);
        assertEquals(WordCraftPdfErrorCode.INIT_FAILED, r.getErrorCode());
        assertTrue(r.getErrorMessage().contains("fake init failure"), r.getErrorMessage());
        WordCraftPdfException e = assertThrows(WordCraftPdfException.class, p::warmUp);
        assertEquals(WordCraftPdfErrorCode.INIT_FAILED, e.getErrorCode());
        assertEquals(0, p.liveWorkers());
    }

    @Test
    void exitDuringStartupReportsExitCodeAndStderr() throws Exception {
        WorkerPool p = pool(FakeWorkers.script(scripts, "exit-on-start"), 1, 0);
        ConversionResult r = convert(p, null);
        assertEquals(WordCraftPdfErrorCode.INIT_FAILED, r.getErrorCode());
        assertTrue(r.getErrorMessage().contains("exit code 7"), r.getErrorMessage());
        assertTrue(r.getErrorMessage().contains("cannot load libwordcraft.so"), r.getErrorMessage());
    }

    @Test
    void missingExecutableIsInitFailed() {
        WorkerPool p = pool(scripts.resolve("does-not-exist").toString(), 1, 0);
        ConversionResult r = convert(p, null);
        assertEquals(WordCraftPdfErrorCode.INIT_FAILED, r.getErrorCode());
    }

    @Test
    void startupTimeout() throws Exception {
        // A worker that never answers init.
        java.nio.file.Path silent = scripts.resolve("silent_worker");
        java.nio.file.Files.write(silent, "#!/bin/sh\nexec sleep 600\n".getBytes(StandardCharsets.US_ASCII));
        assertTrue(silent.toFile().setExecutable(true));
        WorkerPool p = new WorkerPool(silent.toString(), null, 1, 0, 500, 1000);
        pools.add(p);
        ConversionResult r = convert(p, null);
        assertEquals(WordCraftPdfErrorCode.INIT_FAILED, r.getErrorCode());
        assertTrue(r.getErrorMessage().contains("did not start"), r.getErrorMessage());
    }

    // ---- Close ----

    @Test
    void closeStopsIdleWorkers() throws Exception {
        WorkerPool p = pool(normal, 2, 0);
        p.warmUp();
        List<Long> pids = new ArrayList<>();
        ExecutorService ex = Executors.newFixedThreadPool(2);
        try {
            CountDownLatch go = new CountDownLatch(1);
            List<Future<ConversionResult>> fs = new ArrayList<>();
            for (int i = 0; i < 2; i++) fs.add(ex.submit(() -> {
                go.await();
                return convert(p, "slow:200");
            }));
            go.countDown();
            for (Future<ConversionResult> f : fs) pids.add(pid(f.get()));
        } finally {
            ex.shutdownNow();
        }
        p.close();
        for (long pid : pids) assertExited(pid);
        ConversionResult after = convert(p, null);
        assertEquals(WordCraftPdfErrorCode.NOT_INITIALIZED, after.getErrorCode());
    }

    @Test
    void closeKillsWorkerThatDoesNotFinish() throws Exception {
        WorkerPool p = pool(normal, 1, 0);
        long before = pid(convert(p, null));
        ExecutorService ex = Executors.newSingleThreadExecutor();
        try {
            Future<ConversionResult> hung = ex.submit(() -> convert(p, "hang", 60_000));
            while (p.idleWorkers() != 0) Thread.sleep(5);
            Thread.sleep(200);
            long start = System.nanoTime();
            p.close(); // grace: 2 s in this test
            long ms = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start);
            assertTrue(ms < 8000, "close took " + ms + " ms");
            ConversionResult r = hung.get(10, TimeUnit.SECONDS);
            assertEquals(WordCraftPdfErrorCode.WORKER_CRASHED, r.getErrorCode());
            assertTrue(r.getErrorMessage().contains("closed"), r.getErrorMessage());
            assertExited(before);
        } finally {
            ex.shutdownNow();
        }
    }

    @Test
    void ioThreadsAreDaemons() throws Exception {
        WorkerPool p = pool(normal, 1, 0);
        assertOk(convert(p, null));
        Thread[] threads = new Thread[Thread.activeCount() * 2 + 16];
        int n = Thread.enumerate(threads);
        boolean sawIo = false;
        for (int i = 0; i < n; i++) {
            String name = threads[i].getName();
            if (name.startsWith("wcpdf-")) {
                sawIo = true;
                assertTrue(threads[i].isDaemon(), name + " is not a daemon thread");
            }
        }
        assertTrue(sawIo);
    }
}
