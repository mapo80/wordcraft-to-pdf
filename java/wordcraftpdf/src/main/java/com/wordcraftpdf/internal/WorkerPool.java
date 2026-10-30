package com.wordcraftpdf.internal;

import com.wordcraftpdf.ConversionDiagnostic;
import com.wordcraftpdf.ConversionResult;
import com.wordcraftpdf.WordCraftPdfErrorCode;
import com.wordcraftpdf.WordCraftPdfException;

import java.io.Closeable;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentLinkedDeque;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.Semaphore;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Thread-safe pool of {@code wcpdf_worker} processes.
 *
 * <ul>
 *   <li>A conversion always goes to an <b>idle</b> worker; a new worker is started only when every
 *       running one is busy, up to {@code maxWorkers}. Callers beyond that wait for the first worker
 *       to become free (never behind one particular busy worker).</li>
 *   <li>A worker that crashed, timed out, was interrupted or broke the protocol is discarded; the
 *       next conversion gets a healthy one (or a new one).</li>
 *   <li>Workers are recycled after {@code maxConversionsPerWorker} conversions (0 = never).</li>
 *   <li>All threads are daemon threads: the pool never keeps the JVM alive, and each worker exits
 *       by itself when its stdin closes, so workers die with the JVM.</li>
 * </ul>
 */
public final class WorkerPool implements Closeable {

    /** Time given to workers to exit after {@code quit} before they are killed. */
    static final long CLOSE_GRACE_MILLIS = 5000;

    /** Converts on a worker it is given; returns the result, never throws. */
    public interface Job {
        ConversionResult run(WorkerProcess worker, long requestId);
    }

    private final String workerPath;
    private final List<String> fontPaths;
    private final int maxWorkers;
    private final int maxConversionsPerWorker;
    private final long startupTimeoutMillis;
    private final long closeGraceMillis;

    /** One permit per worker slot: a caller holding a permit owns an idle worker or may start one. */
    private final Semaphore permits;
    /** Idle workers, most recently used first (its memory is warm). */
    private final ConcurrentLinkedDeque<WorkerProcess> idle = new ConcurrentLinkedDeque<WorkerProcess>();
    /** Every live worker, idle or busy. */
    private final Set<WorkerProcess> live = Collections.newSetFromMap(new ConcurrentHashMap<WorkerProcess, Boolean>());
    private final AtomicLong requestIds = new AtomicLong();
    private final ExecutorService executor;

    private volatile boolean closed;
    private volatile String version;
    private volatile List<ConversionDiagnostic> startupDiagnostics = Collections.emptyList();

    public WorkerPool(String workerPath, List<String> fontPaths, int maxWorkers, int maxConversionsPerWorker,
                      long startupTimeoutMillis) {
        this(workerPath, fontPaths, maxWorkers, maxConversionsPerWorker, startupTimeoutMillis, CLOSE_GRACE_MILLIS);
    }

    WorkerPool(String workerPath, List<String> fontPaths, int maxWorkers, int maxConversionsPerWorker,
               long startupTimeoutMillis, long closeGraceMillis) {
        if (maxWorkers < 1) throw new IllegalArgumentException("maxWorkers must be at least 1");
        this.workerPath = workerPath;
        this.fontPaths = fontPaths;
        this.maxWorkers = maxWorkers;
        this.maxConversionsPerWorker = maxConversionsPerWorker;
        this.startupTimeoutMillis = startupTimeoutMillis;
        this.closeGraceMillis = closeGraceMillis;
        this.permits = new Semaphore(maxWorkers, true);
        this.executor = Executors.newCachedThreadPool(new ThreadFactory() {
            private final AtomicInteger count = new AtomicInteger();

            @Override
            public Thread newThread(Runnable r) {
                Thread t = new Thread(r, "wcpdf-io-" + count.incrementAndGet());
                t.setDaemon(true);
                return t;
            }
        });
    }

    /** Daemon executor for pipe I/O, also used by the converter's async API. */
    public ExecutorService executor() {
        return executor;
    }

    /** Engine version reported by the first worker started; null before any worker started. */
    public String getVersion() {
        return version;
    }

    /** Diagnostics of the first worker's start-up (font folders or files that couldn't be used). */
    public List<ConversionDiagnostic> getStartupDiagnostics() {
        return startupDiagnostics;
    }

    /** Number of live worker processes (idle or busy). */
    public int liveWorkers() {
        return live.size();
    }

    /** Number of idle worker processes. */
    public int idleWorkers() {
        return idle.size();
    }

    /**
     * Start every worker not running yet, in parallel.
     *
     * @throws WordCraftPdfException if a worker fails to start.
     */
    public void warmUp() {
        if (closed) throw new IllegalStateException("The converter has been closed");
        try {
            permits.acquire(maxWorkers);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new WordCraftPdfException("Interrupted while starting workers", WordCraftPdfErrorCode.INIT_FAILED, e);
        }
        try {
            // Holding every permit: no conversion is running, every live worker is idle.
            for (WorkerProcess w : idle) {
                if (!w.isUsable() && idle.remove(w)) retire(w);
            }
            int missing = maxWorkers - live.size();
            List<Future<WorkerProcess>> starts = new ArrayList<Future<WorkerProcess>>(missing);
            for (int i = 0; i < missing; i++) {
                starts.add(executor.submit(new java.util.concurrent.Callable<WorkerProcess>() {
                    @Override
                    public WorkerProcess call() {
                        return startWorker();
                    }
                }));
            }
            WordCraftPdfException failure = null;
            for (Future<WorkerProcess> f : starts) {
                try {
                    idle.offerFirst(f.get());
                } catch (ExecutionException e) {
                    Throwable cause = e.getCause();
                    if (failure == null) {
                        failure = cause instanceof WordCraftPdfException
                                ? (WordCraftPdfException) cause
                                : new WordCraftPdfException("Failed to start a worker: " + cause,
                                        WordCraftPdfErrorCode.INIT_FAILED, cause);
                    }
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    if (failure == null) {
                        failure = new WordCraftPdfException("Interrupted while starting workers",
                                WordCraftPdfErrorCode.INIT_FAILED, e);
                    }
                }
            }
            if (failure != null) throw failure;
        } finally {
            permits.release(maxWorkers);
        }
    }

    /** Run {@code job} on an idle worker (started if needed), waiting for one if all are busy. */
    public ConversionResult execute(Job job) {
        if (closed) return closedResult();
        try {
            permits.acquire();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return ConversionResult.fail("Interrupted while waiting for a worker", WordCraftPdfErrorCode.UNKNOWN, null);
        }
        WorkerProcess worker = null;
        try {
            if (closed) return closedResult();
            try {
                worker = takeIdleOrStart();
            } catch (WordCraftPdfException e) {
                return ConversionResult.fail(e.getMessage(), e.getErrorCode(), null);
            }
            return job.run(worker, requestIds.incrementAndGet());
        } finally {
            if (worker != null) giveBack(worker);
            permits.release();
        }
    }

    /** Requires a held permit. */
    private WorkerProcess takeIdleOrStart() {
        WorkerProcess w;
        while ((w = idle.pollFirst()) != null) {
            if (w.isUsable()) return w;
            retire(w); // died while idle
        }
        // Every live worker is busy and each holds a permit, so starting one stays within maxWorkers.
        return startWorker();
    }

    private WorkerProcess startWorker() {
        WorkerProcess w = WorkerProcess.start(workerPath, fontPaths, startupTimeoutMillis, executor);
        live.add(w);
        if (version == null) {
            synchronized (this) {
                if (version == null) {
                    startupDiagnostics = w.getStartupDiagnostics();
                    version = w.getVersion();
                }
            }
        }
        if (closed) {
            retire(w);
            throw new WordCraftPdfException("The converter has been closed", WordCraftPdfErrorCode.NOT_INITIALIZED);
        }
        return w;
    }

    private void giveBack(WorkerProcess w) {
        boolean recycle = maxConversionsPerWorker > 0 && w.getConversionCount() >= maxConversionsPerWorker;
        if (closed || recycle || !w.isUsable()) {
            retire(w);
            return;
        }
        idle.offerFirst(w);
        // close() may have drained the idle list just before the offer.
        if (closed && idle.remove(w)) {
            retire(w);
        }
    }

    /** Remove a worker from the pool: kill it if it is unusable, otherwise let it quit gracefully. */
    private void retire(final WorkerProcess w) {
        live.remove(w);
        if (!w.isUsable()) {
            w.kill(null);
            return;
        }
        w.beginClose();
        try {
            // Wait for the exit off the caller's thread; kill it if it doesn't quit in time.
            executor.execute(new Runnable() {
                @Override
                public void run() {
                    w.finishClose(System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(closeGraceMillis));
                }
            });
        } catch (RejectedExecutionException e) {
            // The pool has shut down: the worker already got quit; wait for it here.
            w.finishClose(System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(closeGraceMillis));
        }
    }

    private static ConversionResult closedResult() {
        return ConversionResult.fail("The converter has been closed", WordCraftPdfErrorCode.NOT_INITIALIZED, null);
    }

    /**
     * Stop every worker: idle ones get {@code quit}; running conversions get the grace period to
     * finish. Workers still alive after it are killed.
     */
    @Override
    public void close() {
        if (closed) return;
        closed = true;
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(closeGraceMillis);

        List<WorkerProcess> stopping = new ArrayList<WorkerProcess>();
        WorkerProcess w;
        while ((w = idle.pollFirst()) != null) {
            live.remove(w);
            w.beginClose();
            stopping.add(w);
        }
        // Busy workers: their conversion finishes (or not) within the grace period; giveBack then
        // retires them because the pool is closed.
        stopping.addAll(live);

        boolean interrupted = false;
        for (WorkerProcess p : stopping) {
            try {
                if (!p.awaitExit(deadline - System.nanoTime())) {
                    p.killOnClose();
                }
            } catch (InterruptedException e) {
                interrupted = true;
                p.killOnClose();
            }
        }
        for (WorkerProcess p : live) {
            p.killOnClose();
        }
        // A killed process takes a moment to go away: let close() return only once it has.
        long killDeadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2);
        for (WorkerProcess p : stopping) {
            try {
                p.awaitExit(killDeadline - System.nanoTime());
            } catch (InterruptedException e) {
                interrupted = true;
            }
        }
        executor.shutdown();
        if (interrupted) Thread.currentThread().interrupt();
    }
}
