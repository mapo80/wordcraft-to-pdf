package com.wordcraftpdf.internal;

import com.google.gson.JsonObject;
import com.wordcraftpdf.ConversionDiagnostic;
import com.wordcraftpdf.ConversionOptions;
import com.wordcraftpdf.ConversionResult;
import com.wordcraftpdf.WordCraftPdfErrorCode;
import com.wordcraftpdf.WordCraftPdfException;
import com.wordcraftpdf.internal.Protocol.ProtocolException;
import com.wordcraftpdf.internal.Protocol.ResultHeader;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Future;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * One {@code wcpdf_worker} process: start-up handshake, request/response exchange, crash and
 * timeout handling. One conversion at a time: the pool never hands a worker to two callers.
 *
 * <p>Pipe I/O runs on a thread of the shared I/O executor while the caller waits with a timeout;
 * reads on a process pipe can't be interrupted, so a timeout or an interrupt kills the process,
 * which ends the blocked read.</p>
 */
public final class WorkerProcess {

    /** stdin carries whole documents in buffer mode. */
    private static final int STDIN_BUFFER = 1024 * 1024;
    private static final int STDOUT_BUFFER = 256 * 1024;
    /** How long to wait for a dead worker's exit code and last stderr output. */
    private static final long EXIT_WAIT_MILLIS = 2000;

    private final String workerPath;
    private final Process process;
    private final OutputStream stdin;
    private final InputStream stdout;
    private final StderrTail stderr = new StderrTail(StderrTail.DEFAULT_CAPACITY);
    private final Thread stderrThread;
    private final ExecutorService io;
    private final AtomicInteger conversionCount = new AtomicInteger();

    private volatile String version;
    private volatile List<ConversionDiagnostic> startupDiagnostics = Collections.emptyList();
    /** The process can no longer be used (dead, killed, or its stream is out of step). */
    private volatile boolean broken;
    private volatile boolean closing;
    /** Why the SDK killed the process, for the message of the conversion that was running. */
    private volatile String killReason;

    private WorkerProcess(String workerPath, Process process, ExecutorService io) {
        this.workerPath = workerPath;
        this.process = process;
        this.io = io;
        this.stdin = new BufferedOutputStream(process.getOutputStream(), STDIN_BUFFER);
        this.stdout = new BufferedInputStream(process.getInputStream(), STDOUT_BUFFER);
        this.stderrThread = stderr.drain(process.getErrorStream(), "wcpdf-worker-stderr");
    }

    /**
     * Start a worker and complete the {@code init} handshake.
     *
     * @throws WordCraftPdfException with {@link WordCraftPdfErrorCode#INIT_FAILED} if the process
     *                               can't be started or doesn't answer {@code ready}.
     */
    public static WorkerProcess start(String workerPath, List<String> fontPaths, long startupTimeoutMillis,
                                      ExecutorService io) {
        Process process;
        try {
            ProcessBuilder pb = new ProcessBuilder(workerPath);
            pb.redirectErrorStream(false);
            process = pb.start();
        } catch (IOException e) {
            throw new WordCraftPdfException("Failed to start worker '" + workerPath + "': " + e.getMessage(),
                    WordCraftPdfErrorCode.INIT_FAILED, e);
        }
        WorkerProcess worker = new WorkerProcess(workerPath, process, io);
        try {
            worker.handshake(fontPaths, startupTimeoutMillis);
        } catch (RuntimeException e) {
            worker.kill(null);
            throw e;
        }
        return worker;
    }

    private void handshake(List<String> fontPaths, long timeoutMillis) {
        final byte[] init = Protocol.toJsonBytes(Protocol.initRequest(fontPaths));
        Future<byte[]> future;
        try {
            future = io.submit(new Callable<byte[]>() {
                @Override
                public byte[] call() throws IOException {
                    Protocol.writeFrame(stdin, init);
                    stdin.flush();
                    return Protocol.readFrame(stdout);
                }
            });
        } catch (RejectedExecutionException e) {
            throw new WordCraftPdfException("The converter has been closed", WordCraftPdfErrorCode.INIT_FAILED);
        }

        byte[] response;
        try {
            response = future.get(timeoutMillis, TimeUnit.MILLISECONDS);
        } catch (TimeoutException e) {
            future.cancel(true);
            throw new WordCraftPdfException("Worker did not start within " + timeoutMillis + " ms"
                    + stderrSuffix(), WordCraftPdfErrorCode.INIT_FAILED);
        } catch (InterruptedException e) {
            future.cancel(true);
            Thread.currentThread().interrupt();
            throw new WordCraftPdfException("Interrupted while starting a worker", WordCraftPdfErrorCode.INIT_FAILED, e);
        } catch (ExecutionException e) {
            Throwable cause = e.getCause();
            if (cause instanceof ProtocolException) {
                throw new WordCraftPdfException("Invalid start-up response from the worker: " + cause.getMessage(),
                        WordCraftPdfErrorCode.INIT_FAILED, cause);
            }
            // Writing failed: the process died before reading the request.
            throw new WordCraftPdfException(exitDescription("during start-up"), WordCraftPdfErrorCode.INIT_FAILED, cause);
        }

        if (response == null) {
            throw new WordCraftPdfException(exitDescription("during start-up"), WordCraftPdfErrorCode.INIT_FAILED);
        }
        JsonObject root;
        try {
            root = Protocol.parseObject(response);
        } catch (ProtocolException e) {
            throw new WordCraftPdfException("Invalid start-up response from the worker: " + e.getMessage(),
                    WordCraftPdfErrorCode.INIT_FAILED, e);
        }
        String type = Protocol.getString(root, "type");
        if ("ready".equals(type)) {
            version = Protocol.getString(root, "version");
            startupDiagnostics = Collections.unmodifiableList(Protocol.parseDiagnostics(root.get("diagnostics")));
            return;
        }
        if ("error".equals(type)) {
            String message = Protocol.getString(root, "message");
            throw new WordCraftPdfException("Worker initialization failed: " + (message != null ? message : "unspecified"),
                    WordCraftPdfErrorCode.INIT_FAILED);
        }
        throw new WordCraftPdfException("Unexpected start-up response type '" + type + "'",
                WordCraftPdfErrorCode.INIT_FAILED);
    }

    // ------------------------------------------------------------------------------------------
    // Conversions

    /** File mode: the worker reads {@code input} and writes {@code output}. */
    public ConversionResult convertFile(final long id, String input, String output, int format,
                                        ConversionOptions options, long timeoutMillis) {
        final byte[] header = Protocol.toJsonBytes(Protocol.convertRequest(id, input, output, format, options));
        return exchange(new Callable<ConversionResult>() {
            @Override
            public ConversionResult call() throws IOException {
                Protocol.writeFrame(stdin, header);
                stdin.flush();
                return readResult("result", id);
            }
        }, timeoutMillis);
    }

    /** Buffer mode: {@code data[0, length)} is sent as one binary frame, the PDF comes back as one. */
    public ConversionResult convertBuffer(final long id, final byte[] data, final int length, int format,
                                          ConversionOptions options, long timeoutMillis) {
        final byte[] header = Protocol.toJsonBytes(Protocol.convertBufferRequest(id, format, length, options));
        return exchange(new Callable<ConversionResult>() {
            @Override
            public ConversionResult call() throws IOException {
                Protocol.writeFrame(stdin, header);
                Protocol.writeFrame(stdin, data, 0, length);
                stdin.flush();
                return readResult("buffer_result", id);
            }
        }, timeoutMillis);
    }

    private ConversionResult readResult(String type, long id) throws IOException {
        byte[] frame = Protocol.readFrame(stdout);
        if (frame == null) {
            return crashed();
        }
        ResultHeader h = Protocol.parseResult(Protocol.parseObject(frame), type, id);
        conversionCount.incrementAndGet();
        if (!h.success) {
            return ConversionResult.fail(h.errorMessage, h.errorCode, h.diagnostics);
        }
        if (h.dataSize < 0) {
            return ConversionResult.ok(h.pageCount, h.diagnostics);
        }
        byte[] pdf = Protocol.readFrame(stdout, h.dataSize);
        if (pdf == null) {
            return crashed();
        }
        return ConversionResult.ok(pdf, h.pageCount, h.diagnostics);
    }

    private ConversionResult exchange(Callable<ConversionResult> task, long timeoutMillis) {
        if (!isUsable()) {
            return ConversionResult.fail("The worker process is not running", WordCraftPdfErrorCode.WORKER_CRASHED, null);
        }
        Future<ConversionResult> future;
        try {
            future = io.submit(task);
        } catch (RejectedExecutionException e) {
            return ConversionResult.fail("The converter has been closed", WordCraftPdfErrorCode.NOT_INITIALIZED, null);
        }
        try {
            return future.get(timeoutMillis, TimeUnit.MILLISECONDS);
        } catch (TimeoutException e) {
            kill("timeout");
            future.cancel(true);
            return ConversionResult.fail("Conversion timed out after " + timeoutMillis
                    + " ms; the worker process was stopped", WordCraftPdfErrorCode.TIMEOUT, null);
        } catch (InterruptedException e) {
            kill("interrupted");
            future.cancel(true);
            Thread.currentThread().interrupt();
            return ConversionResult.fail("Conversion interrupted; the worker process was stopped",
                    WordCraftPdfErrorCode.UNKNOWN, null);
        } catch (ExecutionException e) {
            Throwable cause = e.getCause();
            if (cause instanceof ProtocolException) {
                kill("protocol error");
                return ConversionResult.fail("Protocol error: " + cause.getMessage(), WordCraftPdfErrorCode.UNKNOWN, null);
            }
            if (cause instanceof IOException) {
                // A failed write (broken pipe): the process died before reading the request.
                return crashed();
            }
            kill("internal error");
            return ConversionResult.fail("Conversion error: " + cause, WordCraftPdfErrorCode.UNKNOWN, null);
        }
    }

    /** The result for a worker whose output ended (it exited, or was killed). */
    private ConversionResult crashed() {
        broken = true;
        String reason = killReason;
        if ("closed".equals(reason)) {
            return ConversionResult.fail("The converter was closed during the conversion",
                    WordCraftPdfErrorCode.WORKER_CRASHED, null);
        }
        return ConversionResult.fail(exitDescription("during the conversion"), WordCraftPdfErrorCode.WORKER_CRASHED, null);
    }

    /** "The worker process exited ... (exit code N): stderr tail", killing it if it is still alive. */
    private String exitDescription(String when) {
        broken = true;
        String exit = "unknown";
        try {
            if (process.waitFor(EXIT_WAIT_MILLIS, TimeUnit.MILLISECONDS)) {
                exit = String.valueOf(process.exitValue());
            } else {
                ProcessKiller.destroyTree(process);
            }
            // Let the drainer collect the last words the process wrote before dying.
            stderrThread.join(500);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
        return "The worker process exited " + when + " (exit code " + exit + ")" + stderrSuffix();
    }

    private String stderrSuffix() {
        String tail = stderr.toString();
        return tail.isEmpty() ? "" : ". Worker stderr: " + tail;
    }

    // ------------------------------------------------------------------------------------------
    // State and lifecycle

    /** Whether the worker can take another conversion. Safe to call on a closed worker. */
    public boolean isUsable() {
        return !broken && !closing && process.isAlive();
    }

    public int getConversionCount() {
        return conversionCount.get();
    }

    public String getVersion() {
        return version;
    }

    public List<ConversionDiagnostic> getStartupDiagnostics() {
        return startupDiagnostics;
    }

    public String getWorkerPath() {
        return workerPath;
    }

    /** The bounded tail of the worker's stderr. */
    public StderrTail getStderr() {
        return stderr;
    }

    /**
     * Ask the worker to exit: send {@code quit} and close stdin. Only call when no conversion is
     * running on it. Never blocks for long and never throws.
     */
    public void beginClose() {
        if (closing) return;
        closing = true;
        if (!process.isAlive()) return;
        try {
            Protocol.writeJsonFrame(stdin, Protocol.quitRequest());
            stdin.flush();
        } catch (IOException ignored) {
            // Already gone.
        }
        try {
            stdin.close();
        } catch (IOException ignored) {
            // Already gone.
        }
    }

    /** Wait until {@code deadlineNanos} (System.nanoTime) for the process to exit; kill it otherwise. */
    public void finishClose(long deadlineNanos) {
        try {
            long remaining = deadlineNanos - System.nanoTime();
            if (remaining <= 0 || !process.waitFor(remaining, TimeUnit.NANOSECONDS)) {
                kill("closed");
            }
        } catch (InterruptedException e) {
            kill("closed");
            Thread.currentThread().interrupt();
        }
    }

    /** Graceful close: {@code quit}, then kill if the process hasn't exited within {@code graceMillis}. */
    public void close(long graceMillis) {
        beginClose();
        finishClose(System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(graceMillis));
    }

    /** Wait up to {@code timeoutNanos} for the process to exit. */
    boolean awaitExit(long timeoutNanos) throws InterruptedException {
        return timeoutNanos > 0 ? process.waitFor(timeoutNanos, TimeUnit.NANOSECONDS) : !process.isAlive();
    }

    /** Kill the process (and its descendants) now. Never throws. */
    public void kill(String reason) {
        broken = true;
        if (reason != null && killReason == null) {
            killReason = reason;
        }
        ProcessKiller.destroyTree(process);
    }

    /** Kill the process now because the converter is closing. */
    void killOnClose() {
        kill("closed");
    }

    boolean isAlive() {
        return process.isAlive();
    }
}
