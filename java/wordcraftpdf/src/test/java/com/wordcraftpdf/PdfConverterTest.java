package com.wordcraftpdf;

import com.wordcraftpdf.testing.FakeWorkers;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;
import org.junit.jupiter.api.io.TempDir;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.*;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

/** Public API behaviour (validation, modes, options, lifecycle) against the fake worker. */
@Timeout(value = 120, unit = TimeUnit.SECONDS)
class PdfConverterTest {

    @TempDir
    static Path scripts;

    @TempDir
    Path tmp;

    private static String fake;

    @BeforeAll
    static void createScripts() throws Exception {
        assumeTrue(FakeWorkers.supported(), "fake workers are shell scripts");
        fake = FakeWorkers.script(scripts, "normal");
    }

    private static PdfConverter converter() throws FileNotFoundException {
        return PdfConverter.create(PdfConverterOptions.builder().workerPath(fake).maxWorkers(2).build());
    }

    private static String diagnostic(ConversionResult r, String prefix) {
        for (ConversionDiagnostic d : r.getDiagnostics()) {
            if (d.getMessage().startsWith(prefix)) return d.getMessage().substring(prefix.length());
        }
        return null;
    }

    // ---- Creation ----

    @Test
    void create_failsWhenWorkerPathDoesNotExist() {
        assertThrows(FileNotFoundException.class, () -> PdfConverter.create(
                PdfConverterOptions.builder().workerPath(tmp.resolve("missing").toString()).build()));
    }

    @Test
    void create_withWarmUpFailureThrows() throws Exception {
        String broken = FakeWorkers.script(scripts, "init-error");
        WordCraftPdfException e = assertThrows(WordCraftPdfException.class, () -> PdfConverter.create(
                PdfConverterOptions.builder().workerPath(broken).maxWorkers(2).warmUp(true).build()));
        assertEquals(WordCraftPdfErrorCode.INIT_FAILED, e.getErrorCode());
    }

    @Test
    void create_withWarmUpAndFontDirectories() throws Exception {
        try (PdfConverter c = PdfConverter.create(PdfConverterOptions.builder()
                .workerPath(fake).maxWorkers(1).warmUp(true)
                .fontDirectories(Arrays.asList("relative/fonts")).build())) {
            assertEquals("FakeWorker 1.0", c.getVersion());
            // Font folders are sent as absolute paths.
            assertEquals(1, c.getStartupDiagnostics().size());
            String reported = c.getStartupDiagnostics().get(0).getMessage();
            assertTrue(reported.startsWith("font folder /"), reported);
            assertTrue(reported.endsWith("relative/fonts"), reported);
        }
    }

    // ---- File mode ----

    @Test
    void file_convertsAndDetectsFormatFromExtension() throws Exception {
        Path in = tmp.resolve("in.ODT");
        Files.write(in, "odt content".getBytes(StandardCharsets.UTF_8));
        Path out = tmp.resolve("out.pdf");
        try (PdfConverter c = converter()) {
            ConversionResult r = c.convert(in.toString(), out.toString());
            assertTrue(r.isSuccess(), r.getErrorMessage());
            assertNull(r.getData());
            assertEquals(1, r.getPageCount());
            assertEquals("3", diagnostic(r, "format="));
            assertEquals("%PDF-FAKE\nodt content", new String(Files.readAllBytes(out), StandardCharsets.UTF_8));
        }
    }

    @Test
    void file_explicitFormatOverridesExtension() throws Exception {
        Path in = tmp.resolve("noext");
        Files.write(in, "x".getBytes(StandardCharsets.UTF_8));
        try (PdfConverter c = converter()) {
            ConversionResult r = c.convert(in.toString(), tmp.resolve("o.pdf").toString(), DocumentFormat.MARKDOWN, null);
            assertTrue(r.isSuccess(), r.getErrorMessage());
            assertEquals("6", diagnostic(r, "format="));
        }
    }

    @Test
    void file_optionsReachTheWorkerAsSnakeCase() throws Exception {
        Path in = tmp.resolve("in.docx");
        Files.write(in, "x".getBytes(StandardCharsets.UTF_8));
        ConversionOptions opts = ConversionOptions.builder()
                .pageRange("1-2").password("päss").taggedPdf(true).updateFields(true)
                .includeMarkup(true).includeComments(true).author("Renée \"R\"").build();
        try (PdfConverter c = converter()) {
            ConversionResult r = c.convert(in.toString(), tmp.resolve("o.pdf").toString(), opts);
            assertTrue(r.isSuccess(), r.getErrorMessage());
            assertEquals("{\"page_range\":\"1-2\",\"password\":\"päss\",\"tagged_pdf\":true,\"update_fields\":true,"
                    + "\"include_markup\":true,\"include_comments\":true,\"author\":\"Renée \\\"R\\\"\"}",
                    diagnostic(r, "options="));
        }
    }

    @Test
    void file_missingInputIsFileNotFound() throws Exception {
        try (PdfConverter c = converter()) {
            ConversionResult r = c.convert(tmp.resolve("nope.docx").toString(), tmp.resolve("o.pdf").toString());
            assertEquals(WordCraftPdfErrorCode.FILE_NOT_FOUND, r.getErrorCode());
        }
    }

    @Test
    void file_unsupportedExtensionIsInvalidFormat() throws Exception {
        Path in = tmp.resolve("sheet.xlsx");
        Files.write(in, new byte[] {1});
        try (PdfConverter c = converter()) {
            ConversionResult r = c.convert(in.toString(), tmp.resolve("o.pdf").toString());
            assertEquals(WordCraftPdfErrorCode.INVALID_FORMAT, r.getErrorCode());
        }
    }

    @Test
    void file_rejectsNullOrEmptyPaths() throws Exception {
        try (PdfConverter c = converter()) {
            assertThrows(IllegalArgumentException.class, () -> c.convert((String) null, "o.pdf"));
            assertThrows(IllegalArgumentException.class, () -> c.convert("", "o.pdf"));
            assertThrows(IllegalArgumentException.class, () -> c.convert("i.docx", null));
            assertThrows(IllegalArgumentException.class, () -> c.convert("i.docx", ""));
        }
    }

    // ---- Buffer mode ----

    @Test
    void bytes_convertWithUnknownFormat() throws Exception {
        byte[] doc = "PK\u0003\u0004 docx".getBytes(StandardCharsets.ISO_8859_1);
        try (PdfConverter c = converter()) {
            ConversionResult r = c.convert(doc);
            assertTrue(r.isSuccess(), r.getErrorMessage());
            assertEquals("0", diagnostic(r, "format="));
            byte[] expected = new byte[10 + doc.length];
            System.arraycopy("%PDF-FAKE\n".getBytes(StandardCharsets.US_ASCII), 0, expected, 0, 10);
            System.arraycopy(doc, 0, expected, 10, doc.length);
            assertArrayEquals(expected, r.getData());
        }
    }

    @Test
    void bytes_explicitFormatAndNullFormat() throws Exception {
        try (PdfConverter c = converter()) {
            assertEquals("5", diagnostic(c.convert("<p>x</p>".getBytes(StandardCharsets.UTF_8), DocumentFormat.HTML), "format="));
            assertEquals("0", diagnostic(c.convert("x".getBytes(StandardCharsets.UTF_8), null), "format="));
        }
    }

    @Test
    void bytes_emptyIsInvalidArgument() throws Exception {
        try (PdfConverter c = converter()) {
            ConversionResult r = c.convert(new byte[0], DocumentFormat.DOCX);
            assertEquals(WordCraftPdfErrorCode.INVALID_ARGUMENT, r.getErrorCode());
        }
    }

    @Test
    void bytes_nullThrows() throws Exception {
        try (PdfConverter c = converter()) {
            assertThrows(IllegalArgumentException.class, () -> c.convert((byte[]) null, DocumentFormat.DOCX));
        }
    }

    // ---- Stream mode ----

    @Test
    void stream_writesPdfToOutputAndReturnsNoData() throws Exception {
        byte[] doc = new byte[300_000];
        for (int i = 0; i < doc.length; i++) doc[i] = (byte) i;
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try (PdfConverter c = converter()) {
            // A stream that returns few bytes per read and lies about available().
            InputStream in = new ByteArrayInputStream(doc) {
                @Override
                public synchronized int read(byte[] b, int off, int len) {
                    return super.read(b, off, Math.min(len, 777));
                }

                @Override
                public synchronized int available() {
                    return 10;
                }
            };
            ConversionResult r = c.convert(in, out, DocumentFormat.DOCX);
            assertTrue(r.isSuccess(), r.getErrorMessage());
            assertNull(r.getData());
            assertEquals(1, r.getPageCount());
        }
        byte[] pdf = out.toByteArray();
        assertEquals(10 + doc.length, pdf.length);
        assertArrayEquals(doc, Arrays.copyOfRange(pdf, 10, pdf.length));
    }

    @Test
    void stream_emptyInputIsInvalidArgument() throws Exception {
        try (PdfConverter c = converter()) {
            ConversionResult r = c.convert(new ByteArrayInputStream(new byte[0]), new ByteArrayOutputStream(), DocumentFormat.UNKNOWN);
            assertEquals(WordCraftPdfErrorCode.INVALID_ARGUMENT, r.getErrorCode());
        }
    }

    @Test
    void stream_readFailureIsReported() throws Exception {
        try (PdfConverter c = converter()) {
            InputStream failing = new InputStream() {
                @Override
                public int read() throws IOException {
                    throw new IOException("disk on fire");
                }
            };
            ConversionResult r = c.convert(failing, new ByteArrayOutputStream(), DocumentFormat.DOCX);
            assertFalse(r.isSuccess());
            assertTrue(r.getErrorMessage().contains("disk on fire"));
        }
    }

    @Test
    void stream_writeFailureIsReported() throws Exception {
        try (PdfConverter c = converter()) {
            OutputStream failing = new OutputStream() {
                @Override
                public void write(int b) throws IOException {
                    throw new IOException("no space");
                }

                @Override
                public void write(byte[] b, int off, int len) throws IOException {
                    throw new IOException("no space");
                }
            };
            ConversionResult r = c.convert(new ByteArrayInputStream(new byte[] {1}), failing, DocumentFormat.DOCX);
            assertFalse(r.isSuccess());
            assertTrue(r.getErrorMessage().contains("no space"));
        }
    }

    @Test
    void stream_rejectsNullStreams() throws Exception {
        try (PdfConverter c = converter()) {
            assertThrows(IllegalArgumentException.class, () -> c.convert(null, new ByteArrayOutputStream(), DocumentFormat.DOCX));
            assertThrows(IllegalArgumentException.class, () -> c.convert(new ByteArrayInputStream(new byte[1]), null, DocumentFormat.DOCX));
        }
    }

    // ---- Async ----

    @Test
    void async_fileAndBytes() throws Exception {
        Path in = tmp.resolve("a.rtf");
        Files.write(in, "{\\rtf1}".getBytes(StandardCharsets.US_ASCII));
        try (PdfConverter c = converter()) {
            CompletableFuture<ConversionResult> f1 = c.convertAsync(in.toString(), tmp.resolve("a.pdf").toString());
            CompletableFuture<ConversionResult> f2 = c.convertAsync("x".getBytes(StandardCharsets.UTF_8), DocumentFormat.TEXT);
            ConversionResult r1 = f1.get(30, TimeUnit.SECONDS);
            ConversionResult r2 = f2.get(30, TimeUnit.SECONDS);
            assertTrue(r1.isSuccess(), r1.getErrorMessage());
            assertEquals("4", diagnostic(r1, "format="));
            assertTrue(r2.isSuccess(), r2.getErrorMessage());
            assertEquals("7", diagnostic(r2, "format="));
        }
    }

    // ---- Lifecycle ----

    @Test
    void closedConverterThrows() throws Exception {
        PdfConverter c = converter();
        assertTrue(c.convert(new byte[] {1}, DocumentFormat.DOCX).isSuccess());
        c.close();
        c.close(); // idempotent
        assertThrows(IllegalStateException.class, () -> c.convert(new byte[] {1}, DocumentFormat.DOCX));
        assertThrows(IllegalStateException.class, () -> c.convert("a.docx", "b.pdf"));
        assertThrows(IllegalStateException.class, () -> c.convertAsync(new byte[] {1}, DocumentFormat.DOCX));
        assertThrows(IllegalStateException.class, c::warmUp);
    }

    @Test
    void crashAndTimeoutThroughPublicApi() throws Exception {
        try (PdfConverter c = PdfConverter.create(PdfConverterOptions.builder()
                .workerPath(fake).maxWorkers(1).conversionTimeout(1500, TimeUnit.MILLISECONDS).build())) {
            byte[] doc = {1, 2, 3};
            ConversionResult crash = c.convert(doc, DocumentFormat.DOCX, ConversionOptions.builder().title("crash").build());
            assertEquals(WordCraftPdfErrorCode.WORKER_CRASHED, crash.getErrorCode());
            WordCraftPdfException e = assertThrows(WordCraftPdfException.class, crash::throwIfFailed);
            assertEquals(WordCraftPdfErrorCode.WORKER_CRASHED, e.getErrorCode());

            ConversionResult timeout = c.convert(doc, DocumentFormat.DOCX, ConversionOptions.builder().title("hang").build());
            assertEquals(WordCraftPdfErrorCode.TIMEOUT, timeout.getErrorCode());

            assertTrue(c.convert(doc, DocumentFormat.DOCX).isSuccess());
        }
    }
}
