package com.wordcraftpdf;

import com.wordcraftpdf.internal.Protocol;
import com.wordcraftpdf.internal.WorkerLocator;
import com.wordcraftpdf.internal.WorkerPool;
import com.wordcraftpdf.internal.WorkerProcess;

import java.io.Closeable;
import java.io.File;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.RejectedExecutionException;
import java.util.function.Supplier;

/**
 * Converts documents (DOCX, DOC, ODT, RTF, HTML, Markdown, plain text) to PDF with the WordCraft
 * engine.
 *
 * <p><b>Process isolation:</b> conversions run in separate {@code wcpdf_worker} processes that
 * talk to the JVM over pipes only (no JNI). If the engine crashes, hangs or runs out of memory on
 * a document, only its worker dies: the conversion fails with
 * {@link WordCraftPdfErrorCode#WORKER_CRASHED} or {@link WordCraftPdfErrorCode#TIMEOUT} and the
 * worker is replaced for the next conversion. The JVM is never affected.</p>
 *
 * <p><b>Thread safety:</b> all methods are thread-safe. Up to
 * {@link PdfConverterOptions#getMaxWorkers()} conversions run in parallel, each on an idle worker;
 * workers stay alive between conversions.</p>
 *
 * <p><b>Conversion modes:</b></p>
 * <ul>
 *   <li><b>File mode</b>: only paths travel to the worker, which reads and writes the files
 *       itself (the PDF is written atomically). The format follows the file extension.</li>
 *   <li><b>Buffer / stream mode</b>: document bytes go to the worker as one binary frame and the
 *       PDF comes back as one; no temporary files.</li>
 * </ul>
 *
 * <pre>{@code
 * try (PdfConverter converter = PdfConverter.create()) {
 *     ConversionResult result = converter.convert("input.docx", "output.pdf");
 *     result.throwIfFailed();
 *     System.out.println(result.getPageCount() + " pages");
 * }
 * }</pre>
 */
public final class PdfConverter implements Closeable {

    private final WorkerPool pool;
    private final long timeoutMillis;
    private volatile boolean closed;

    private PdfConverter(WorkerPool pool, long timeoutMillis) {
        this.pool = pool;
        this.timeoutMillis = timeoutMillis;
    }

    /**
     * Create a converter with default options.
     *
     * @throws FileNotFoundException if the worker executable can't be found.
     */
    public static PdfConverter create() throws FileNotFoundException {
        return create(PdfConverterOptions.builder().build());
    }

    /**
     * Create a converter.
     *
     * @param options converter configuration; null = defaults.
     * @throws FileNotFoundException if the worker executable can't be found.
     * @throws WordCraftPdfException if {@link PdfConverterOptions#isWarmUp()} is set and a worker
     *                               fails to start.
     */
    public static PdfConverter create(PdfConverterOptions options) throws FileNotFoundException {
        if (options == null) {
            options = PdfConverterOptions.builder().build();
        }
        String workerPath = WorkerLocator.findWorkerExecutable(options.getWorkerPath());

        List<String> fontPaths = null;
        if (options.getFontDirectories() != null && !options.getFontDirectories().isEmpty()) {
            fontPaths = new ArrayList<String>(options.getFontDirectories().size());
            for (String dir : options.getFontDirectories()) {
                fontPaths.add(new File(dir).getAbsolutePath());
            }
        }

        WorkerPool pool = new WorkerPool(workerPath, fontPaths, options.getMaxWorkers(),
                options.getMaxConversionsPerWorker(), options.getStartupTimeoutMillis());
        if (options.isWarmUp()) {
            try {
                pool.warmUp();
            } catch (RuntimeException e) {
                pool.close();
                throw e;
            }
        }
        return new PdfConverter(pool, options.getConversionTimeoutMillis());
    }

    // ---- File mode ----

    /**
     * Convert a document file to a PDF file. The format follows the input's extension.
     *
     * @param inputPath  the document.
     * @param outputPath where to write the PDF (replaced atomically).
     */
    public ConversionResult convert(String inputPath, String outputPath) {
        return convert(inputPath, outputPath, DocumentFormat.UNKNOWN, null);
    }

    /**
     * Convert a document file to a PDF file. The format follows the input's extension.
     *
     * @param options conversion options, or null for defaults.
     */
    public ConversionResult convert(String inputPath, String outputPath, ConversionOptions options) {
        return convert(inputPath, outputPath, DocumentFormat.UNKNOWN, options);
    }

    /**
     * Convert a document file to a PDF file.
     *
     * @param format the input format, or {@link DocumentFormat#UNKNOWN} to use the extension.
     * @param options conversion options, or null for defaults.
     */
    public ConversionResult convert(String inputPath, String outputPath, DocumentFormat format,
                                    ConversionOptions options) {
        checkOpen();
        if (inputPath == null || inputPath.isEmpty()) {
            throw new IllegalArgumentException("inputPath must not be null or empty");
        }
        if (outputPath == null || outputPath.isEmpty()) {
            throw new IllegalArgumentException("outputPath must not be null or empty");
        }
        final File input = new File(inputPath).getAbsoluteFile();
        final File output = new File(outputPath).getAbsoluteFile();
        if (!input.exists()) {
            return ConversionResult.fail("Input file not found: " + input.getPath(),
                    WordCraftPdfErrorCode.FILE_NOT_FOUND, null);
        }
        final DocumentFormat resolved = format == null || format == DocumentFormat.UNKNOWN
                ? DocumentFormat.fromExtension(input.getName())
                : format;
        if (resolved == DocumentFormat.UNKNOWN) {
            return ConversionResult.fail("Unsupported file type: " + input.getName()
                            + ". Supported extensions: .docx .docm .dotx .dotm .doc .dot .odt .rtf .html .htm .md .markdown .txt",
                    WordCraftPdfErrorCode.INVALID_FORMAT, null);
        }
        final ConversionOptions opts = options;
        return pool.execute(new WorkerPool.Job() {
            @Override
            public ConversionResult run(WorkerProcess worker, long requestId) {
                return worker.convertFile(requestId, input.getPath(), output.getPath(), resolved.getValue(),
                        opts, timeoutMillis);
            }
        });
    }

    // ---- Buffer mode ----

    /** Convert in-memory document bytes to PDF bytes, detecting the format from the content. */
    public ConversionResult convert(byte[] input) {
        return convert(input, DocumentFormat.UNKNOWN, null);
    }

    /**
     * Convert in-memory document bytes to PDF bytes ({@link ConversionResult#getData()}).
     *
     * @param format the input format; {@link DocumentFormat#UNKNOWN} detects DOCX, DOC, ODT and RTF
     *               from the content (HTML, Markdown and text need an explicit format).
     */
    public ConversionResult convert(byte[] input, DocumentFormat format) {
        return convert(input, format, null);
    }

    /**
     * Convert in-memory document bytes to PDF bytes ({@link ConversionResult#getData()}).
     *
     * @param format  the input format; {@link DocumentFormat#UNKNOWN} detects it from the content.
     * @param options conversion options, or null for defaults.
     */
    public ConversionResult convert(byte[] input, DocumentFormat format, ConversionOptions options) {
        checkOpen();
        if (input == null) {
            throw new IllegalArgumentException("input must not be null");
        }
        return convertBuffer(input, input.length, format, options);
    }

    // ---- Stream mode ----

    /**
     * Convert a document stream to PDF, written to {@code output}. Neither stream is closed.
     *
     * @param format the input format; {@link DocumentFormat#UNKNOWN} detects it from the content.
     */
    public ConversionResult convert(InputStream input, OutputStream output, DocumentFormat format) {
        return convert(input, output, format, null);
    }

    /**
     * Convert a document stream to PDF, written to {@code output}. Neither stream is closed.
     * The returned result carries no data (it went to {@code output}).
     *
     * @param format  the input format; {@link DocumentFormat#UNKNOWN} detects it from the content.
     * @param options conversion options, or null for defaults.
     */
    public ConversionResult convert(InputStream input, OutputStream output, DocumentFormat format,
                                    ConversionOptions options) {
        checkOpen();
        if (input == null) throw new IllegalArgumentException("input must not be null");
        if (output == null) throw new IllegalArgumentException("output must not be null");

        byte[] buffer;
        int length;
        try {
            buffer = new byte[initialCapacity(input)];
            length = 0;
            int n;
            while ((n = input.read(buffer, length, buffer.length - length)) >= 0) {
                length += n;
                if (length == buffer.length) {
                    if (length > Protocol.MAX_FRAME) {
                        return tooLarge(length);
                    }
                    long grown = Math.min((long) buffer.length * 2, (long) Protocol.MAX_FRAME + 1);
                    buffer = Arrays.copyOf(buffer, (int) grown);
                }
            }
        } catch (IOException e) {
            return ConversionResult.fail("Failed to read the input stream: " + e.getMessage(),
                    WordCraftPdfErrorCode.INVALID_ARGUMENT, null);
        }

        ConversionResult result = convertBuffer(buffer, length, format, options);
        if (!result.isSuccess()) {
            return result;
        }
        try {
            output.write(result.getData());
            output.flush();
        } catch (IOException e) {
            return ConversionResult.fail("Failed to write the PDF to the output stream: " + e.getMessage(),
                    WordCraftPdfErrorCode.EXPORT_FAILED, result.getDiagnostics());
        }
        return ConversionResult.ok(result.getPageCount(), result.getDiagnostics());
    }

    // ---- Async variants (run on the converter's daemon threads) ----

    /** Asynchronously convert a document file to a PDF file. */
    public CompletableFuture<ConversionResult> convertAsync(String inputPath, String outputPath) {
        return convertAsync(inputPath, outputPath, null);
    }

    /** Asynchronously convert a document file to a PDF file with options. */
    public CompletableFuture<ConversionResult> convertAsync(final String inputPath, final String outputPath,
                                                            final ConversionOptions options) {
        return async(new Supplier<ConversionResult>() {
            @Override
            public ConversionResult get() {
                return convert(inputPath, outputPath, options);
            }
        });
    }

    /** Asynchronously convert in-memory document bytes to PDF bytes. */
    public CompletableFuture<ConversionResult> convertAsync(byte[] input, DocumentFormat format) {
        return convertAsync(input, format, null);
    }

    /** Asynchronously convert in-memory document bytes to PDF bytes with options. */
    public CompletableFuture<ConversionResult> convertAsync(final byte[] input, final DocumentFormat format,
                                                            final ConversionOptions options) {
        return async(new Supplier<ConversionResult>() {
            @Override
            public ConversionResult get() {
                return convert(input, format, options);
            }
        });
    }

    // ---- Lifecycle and information ----

    /** Start every worker not running yet (in parallel), so the next conversions don't wait for one. */
    public void warmUp() {
        checkOpen();
        pool.warmUp();
    }

    /** Engine version reported by the workers, e.g. "WordCraftPdf 0.1.0 (WordCraft v0.12.0)"; null before a worker started. */
    public String getVersion() {
        return pool.getVersion();
    }

    /** Font folders or files a worker couldn't use at start-up. Empty before a worker started. */
    public List<ConversionDiagnostic> getStartupDiagnostics() {
        return pool.getStartupDiagnostics();
    }

    /**
     * Stop the workers. Idle workers exit at once; running conversions get a few seconds to finish
     * before their workers are killed.
     */
    @Override
    public void close() {
        if (closed) return;
        closed = true;
        pool.close();
    }

    // ---- Internal helpers ----

    private ConversionResult convertBuffer(final byte[] data, final int length, DocumentFormat format,
                                           final ConversionOptions options) {
        if (length == 0) {
            return ConversionResult.fail("Input data is empty", WordCraftPdfErrorCode.INVALID_ARGUMENT, null);
        }
        if (length > Protocol.MAX_FRAME) {
            return tooLarge(length);
        }
        final int formatValue = (format == null ? DocumentFormat.UNKNOWN : format).getValue();
        return pool.execute(new WorkerPool.Job() {
            @Override
            public ConversionResult run(WorkerProcess worker, long requestId) {
                return worker.convertBuffer(requestId, data, length, formatValue, options, timeoutMillis);
            }
        });
    }

    private static ConversionResult tooLarge(long length) {
        return ConversionResult.fail("Input of " + (length > Protocol.MAX_FRAME ? "more than " + Protocol.MAX_FRAME : length)
                        + " bytes exceeds the " + Protocol.MAX_FRAME + "-byte limit",
                WordCraftPdfErrorCode.INVALID_ARGUMENT, null);
    }

    private static int initialCapacity(InputStream in) {
        int available;
        try {
            available = in.available();
        } catch (IOException e) {
            available = 0;
        }
        // +1 so a stream whose size equals available() is read without growing the buffer.
        return Math.max(64 * 1024, Math.min(available, Protocol.MAX_FRAME) + 1);
    }

    private CompletableFuture<ConversionResult> async(Supplier<ConversionResult> work) {
        checkOpen();
        try {
            return CompletableFuture.supplyAsync(work, pool.executor());
        } catch (RejectedExecutionException e) {
            throw new IllegalStateException("PdfConverter has been closed", e);
        }
    }

    private void checkOpen() {
        if (closed) {
            throw new IllegalStateException("PdfConverter has been closed");
        }
    }
}
