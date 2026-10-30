package com.wordcraftpdf;

import com.wordcraftpdf.internal.WorkerLocator;
import com.wordcraftpdf.testing.FakeWorkers;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;
import org.junit.jupiter.api.io.TempDir;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileNotFoundException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.stream.Collectors;
import java.util.stream.Stream;

import static org.junit.jupiter.api.Assertions.*;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

/**
 * Tests against the real {@code wcpdf_worker}. Skipped when the worker isn't built
 * ({@code output/bin/wcpdf_worker}, or wherever {@link WorkerLocator} finds one).
 */
@Timeout(value = 180, unit = TimeUnit.SECONDS)
class IntegrationTest {

    private static Path root;
    private static Path fixtures;
    private static String worker;
    private static PdfConverter shared;

    @TempDir
    Path tmp;

    @BeforeAll
    static void findWorker() throws Exception {
        root = Paths.get(System.getProperty("wcpdf.project.root", "../..")).toAbsolutePath().normalize();
        fixtures = root.resolve("tests/fixtures");
        Path built = root.resolve("output/bin").resolve(WorkerLocator.executableName());
        if (Files.isRegularFile(built)) {
            worker = built.toString();
        } else {
            try {
                worker = WorkerLocator.findWorkerExecutable(null);
            } catch (FileNotFoundException e) {
                worker = null;
            }
        }
        assumeTrue(worker != null, "wcpdf_worker not found: integration tests skipped");
        assumeTrue(Files.isDirectory(fixtures), "fixtures not found at " + fixtures);
        shared = PdfConverter.create(options().maxWorkers(2).build());
    }

    @AfterAll
    static void closeShared() {
        if (shared != null) shared.close();
    }

    private static PdfConverterOptions.Builder options() {
        return PdfConverterOptions.builder().workerPath(worker);
    }

    static Stream<String> fixtureNames() throws Exception {
        Path dir = Paths.get(System.getProperty("wcpdf.project.root", "../..")).toAbsolutePath().normalize()
                .resolve("tests/fixtures");
        if (!Files.isDirectory(dir)) return Stream.of("missing-fixtures");
        try (Stream<Path> s = Files.list(dir)) {
            List<String> names = s.map(p -> p.getFileName().toString())
                    .filter(n -> n.toLowerCase(Locale.ROOT).endsWith(".docx"))
                    .sorted()
                    .collect(Collectors.toList());
            return names.stream();
        }
    }

    private static void assertPdf(byte[] pdf) {
        assertNotNull(pdf);
        assertTrue(pdf.length > 100, "PDF too small: " + pdf.length);
        assertEquals("%PDF-", new String(pdf, 0, 5, StandardCharsets.US_ASCII));
        String tail = new String(pdf, Math.max(0, pdf.length - 64), Math.min(64, pdf.length), StandardCharsets.ISO_8859_1);
        assertTrue(tail.contains("%%EOF"), "PDF does not end with %%EOF");
    }

    private static void assertOk(ConversionResult r) {
        assertTrue(r.isSuccess(), () -> "expected success, got " + r.getErrorCode() + ": " + r.getErrorMessage());
    }

    /** A plain-text document of {@code lines} lines: about 2 s per 100k lines to convert. */
    private static byte[] bigText(int lines) {
        StringBuilder sb = new StringBuilder(lines * 64);
        for (int i = 0; i < lines; i++) {
            sb.append("Line ").append(i).append(" lorem ipsum dolor sit amet, consectetur adipiscing elit.\n");
        }
        return sb.toString().getBytes(StandardCharsets.UTF_8);
    }

    // ---- Every fixture, every mode ----

    @ParameterizedTest
    @MethodSource("fixtureNames")
    void fixture_fileMode(String name) throws Exception {
        Path out = tmp.resolve(name + ".pdf");
        ConversionResult r = shared.convert(fixtures.resolve(name).toString(), out.toString());
        assertOk(r);
        assertTrue(r.getPageCount() > 0);
        assertNull(r.getData());
        assertPdf(Files.readAllBytes(out));
    }

    @ParameterizedTest
    @MethodSource("fixtureNames")
    void fixture_bytesMode(String name) throws Exception {
        byte[] doc = Files.readAllBytes(fixtures.resolve(name));
        ConversionResult explicit = shared.convert(doc, DocumentFormat.DOCX);
        assertOk(explicit);
        assertPdf(explicit.getData());
        assertTrue(explicit.getPageCount() > 0);

        ConversionResult sniffed = shared.convert(doc);
        assertOk(sniffed);
        assertPdf(sniffed.getData());
        assertEquals(explicit.getPageCount(), sniffed.getPageCount());
    }

    @ParameterizedTest
    @MethodSource("fixtureNames")
    void fixture_streamMode(String name) throws Exception {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try (InputStream in = Files.newInputStream(fixtures.resolve(name))) {
            ConversionResult r = shared.convert(in, out, DocumentFormat.UNKNOWN);
            assertOk(r);
            assertNull(r.getData());
            assertTrue(r.getPageCount() > 0);
        }
        assertPdf(out.toByteArray());
    }

    @Test
    void versionAndWarmUp() throws Exception {
        Set<Long> before = pids(workerChildren());
        try (PdfConverter c = PdfConverter.create(options().maxWorkers(2).warmUp(true).build())) {
            assertNotNull(c.getVersion());
            assertTrue(c.getVersion().startsWith("WordCraftPdf"), c.getVersion());
            Set<Long> started = pids(workerChildren());
            started.removeAll(before);
            assertEquals(2, started.size(), "warmUp starts maxWorkers workers");
        }
    }

    // ---- Formats and options ----

    @Test
    void otherFormatsInBufferMode() {
        ConversionResult text = shared.convert("Hello, world.\nSecond line.".getBytes(StandardCharsets.UTF_8), DocumentFormat.TEXT);
        assertOk(text);
        assertPdf(text.getData());

        ConversionResult md = shared.convert("# Title\n\nSome *markdown* text.\n".getBytes(StandardCharsets.UTF_8),
                DocumentFormat.MARKDOWN);
        assertOk(md);
        assertPdf(md.getData());

        ConversionResult html = shared.convert("<html><body><h1>Title</h1><p>Para</p></body></html>"
                .getBytes(StandardCharsets.UTF_8), DocumentFormat.HTML);
        assertOk(html);
        assertPdf(html.getData());

        ConversionResult rtf = shared.convert("{\\rtf1\\ansi Hello RTF\\par}".getBytes(StandardCharsets.US_ASCII));
        assertOk(rtf);
        assertPdf(rtf.getData());
    }

    @Test
    void pageRange() throws Exception {
        byte[] doc = bigText(200); // several pages
        ConversionResult all = shared.convert(doc, DocumentFormat.TEXT);
        assertOk(all);
        int total = all.getPageCount();
        assertTrue(total >= 3, "need at least 3 pages, got " + total);

        ConversionResult first = shared.convert(doc, DocumentFormat.TEXT, ConversionOptions.builder().pageRange("1").build());
        assertOk(first);
        assertEquals(1, first.getPageCount());
        assertTrue(first.getData().length < all.getData().length);

        ConversionResult some = shared.convert(doc, DocumentFormat.TEXT, ConversionOptions.builder().pageRange("1,3-").build());
        assertOk(some);
        assertEquals(total - 1, some.getPageCount());

        Path out = tmp.resolve("range.pdf");
        Path in = tmp.resolve("range.txt");
        Files.write(in, doc);
        ConversionResult file = shared.convert(in.toString(), out.toString(), ConversionOptions.builder().pageRange("2-3").build());
        assertOk(file);
        assertEquals(2, file.getPageCount());

        ConversionResult invalid = shared.convert(doc, DocumentFormat.TEXT, ConversionOptions.builder().pageRange("0").build());
        assertEquals(WordCraftPdfErrorCode.INVALID_ARGUMENT, invalid.getErrorCode());
        ConversionResult beyond = shared.convert(doc, DocumentFormat.TEXT,
                ConversionOptions.builder().pageRange(String.valueOf(total + 1)).build());
        assertEquals(WordCraftPdfErrorCode.INVALID_ARGUMENT, beyond.getErrorCode());
    }

    @Test
    void allOptionsAreAccepted() throws Exception {
        byte[] doc = Files.readAllBytes(fixtures.resolve("rich_formatting.docx"));
        ConversionResult r = shared.convert(doc, DocumentFormat.DOCX, ConversionOptions.builder()
                .taggedPdf(true).updateFields(true).includeMarkup(true).includeComments(true)
                .title("Tïtle \"quoted\" 😀").author("Renée").build());
        assertOk(r);
        assertPdf(r.getData());
    }

    // ---- Invalid input ----

    @Test
    void invalidInput() throws Exception {
        byte[] garbage = "this is definitely not a document".getBytes(StandardCharsets.US_ASCII);

        ConversionResult sniffed = shared.convert(garbage);
        assertFalse(sniffed.isSuccess());
        assertEquals(WordCraftPdfErrorCode.INVALID_FORMAT, sniffed.getErrorCode());

        ConversionResult asDocx = shared.convert(garbage, DocumentFormat.DOCX);
        assertFalse(asDocx.isSuccess());
        assertNotNull(asDocx.getErrorMessage());

        byte[] truncated = java.util.Arrays.copyOf(Files.readAllBytes(fixtures.resolve("stress_test.docx")), 600);
        ConversionResult broken = shared.convert(truncated, DocumentFormat.DOCX);
        assertFalse(broken.isSuccess());

        Path fake = tmp.resolve("fake.docx");
        Files.write(fake, garbage);
        ConversionResult file = shared.convert(fake.toString(), tmp.resolve("fake.pdf").toString());
        assertFalse(file.isSuccess());
        assertFalse(Files.exists(tmp.resolve("fake.pdf")), "no output on failure");

        assertEquals(WordCraftPdfErrorCode.FILE_NOT_FOUND,
                shared.convert(tmp.resolve("missing.docx").toString(), tmp.resolve("m.pdf").toString()).getErrorCode());

        // Failures don't break the worker.
        assertOk(shared.convert(Files.readAllBytes(fixtures.resolve("stress_test.docx")), DocumentFormat.DOCX));
    }

    // ---- Fonts ----

    @Test
    void fontDirectoriesRemoveBarcodeSubstitution() throws Exception {
        byte[] doc = Files.readAllBytes(fixtures.resolve("barcode_font.docx"));

        ConversionResult without = shared.convert(doc, DocumentFormat.DOCX);
        assertOk(without);
        // Without the folder the barcode font is substituted (unless it is installed system-wide).
        boolean systemHasIt = !hasBarcodeSubstitution(without);

        try (PdfConverter c = PdfConverter.create(options().maxWorkers(1)
                .fontDirectories(Collections.singletonList(fixtures.resolve("fonts").toString())).build())) {
            ConversionResult with = c.convert(doc, DocumentFormat.DOCX);
            assertOk(with);
            assertFalse(hasBarcodeSubstitution(with), "LibreBarcode128 still substituted: " + with.getDiagnostics());
            assertTrue(c.getStartupDiagnostics().isEmpty(), "font folder rejected: " + c.getStartupDiagnostics());
        }
        if (!systemHasIt) {
            assertTrue(hasBarcodeSubstitution(without));
        }
    }

    @Test
    void unusableFontDirectoryIsAStartupDiagnostic() throws Exception {
        try (PdfConverter c = PdfConverter.create(options().maxWorkers(1).warmUp(true)
                .fontDirectories(Collections.singletonList(tmp.resolve("no-such-folder").toString())).build())) {
            assertFalse(c.getStartupDiagnostics().isEmpty());
            assertOk(c.convert(Files.readAllBytes(fixtures.resolve("multi_font.docx")), DocumentFormat.DOCX));
        }
    }

    private static boolean hasBarcodeSubstitution(ConversionResult r) {
        for (ConversionDiagnostic d : r.getDiagnostics()) {
            if (d.getCategory() == DiagnosticCategory.FONT && d.getFontName() != null
                    && d.getFontName().toLowerCase(Locale.ROOT).replace(" ", "").contains("barcode128")) {
                return true;
            }
        }
        return false;
    }

    // ---- Concurrency ----

    @Test
    void concurrentConversions() throws Exception {
        List<Path> docs;
        try (Stream<Path> s = Files.list(fixtures)) {
            docs = s.filter(p -> p.toString().endsWith(".docx")).sorted().collect(Collectors.toList());
        }
        try (PdfConverter c = PdfConverter.create(options().maxWorkers(4).build())) {
            ExecutorService ex = Executors.newFixedThreadPool(16);
            try {
                List<Future<ConversionResult>> fs = new ArrayList<>();
                for (int i = 0; i < 16; i++) {
                    Path doc = docs.get(i % docs.size());
                    final int n = i;
                    fs.add(ex.submit(() -> n % 2 == 0
                            ? c.convert(Files.readAllBytes(doc), DocumentFormat.DOCX)
                            : c.convert(doc.toString(), tmp.resolve("c" + n + ".pdf").toString())));
                }
                for (Future<ConversionResult> f : fs) assertOk(f.get());
            } finally {
                ex.shutdownNow();
            }
            assertTrue(workerChildren().size() >= 1);
        }
    }

    // ---- Isolation ----

    @Test
    void killedWorkerMidConversionIsReportedAndReplaced() throws Exception {
        byte[] big = bigText(100_000);
        byte[] small = Files.readAllBytes(fixtures.resolve("stress_test.docx"));
        Set<Long> others = pids(workerChildren());
        try (PdfConverter c = PdfConverter.create(options().maxWorkers(1).warmUp(true).build())) {
            Set<Long> before = pids(workerChildren());
            ConversionResult crashed = null;
            for (int attempt = 0; attempt < 5 && crashed == null; attempt++) {
                ExecutorService ex = Executors.newSingleThreadExecutor();
                try {
                    Future<ConversionResult> f = ex.submit(() -> c.convert(big, DocumentFormat.TEXT));
                    Thread.sleep(400);
                    for (ProcessHandle h : workerChildren()) {
                        if (!others.contains(h.pid())) h.destroyForcibly(); // only this converter's worker
                    }
                    ConversionResult r = f.get(60, TimeUnit.SECONDS);
                    if (!r.isSuccess()) crashed = r;
                } finally {
                    ex.shutdownNow();
                }
            }
            assertNotNull(crashed, "could not kill the worker mid-conversion");
            assertEquals(WordCraftPdfErrorCode.WORKER_CRASHED, crashed.getErrorCode());
            assertTrue(crashed.getErrorMessage().contains("exited"), crashed.getErrorMessage());

            // The JVM is fine and the next conversion gets a new worker.
            ConversionResult next = c.convert(small, DocumentFormat.DOCX);
            assertOk(next);
            assertPdf(next.getData());
            Set<Long> after = pids(workerChildren());
            after.removeAll(before);
            assertFalse(after.isEmpty(), "a new worker should have been started");
        }
    }

    @Test
    void timeoutKillsWorkerAndNextConversionSucceeds() throws Exception {
        byte[] big = bigText(200_000);
        byte[] small = Files.readAllBytes(fixtures.resolve("stress_test.docx"));
        try (PdfConverter c = PdfConverter.create(options().maxWorkers(1).warmUp(true)
                .conversionTimeout(1000, TimeUnit.MILLISECONDS).build())) {
            long start = System.nanoTime();
            ConversionResult r = c.convert(big, DocumentFormat.TEXT);
            long ms = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start);
            assertEquals(WordCraftPdfErrorCode.TIMEOUT, r.getErrorCode(), String.valueOf(r));
            assertTrue(ms < 5000, "timeout took " + ms + " ms");

            ConversionResult next = c.convert(small, DocumentFormat.DOCX);
            assertOk(next);
            assertPdf(next.getData());
        }
        try (PdfConverter c = PdfConverter.create(options().maxWorkers(1)
                .conversionTimeout(1, TimeUnit.MILLISECONDS).build())) {
            // Start-up is not counted in the conversion timeout; 1 ms is too short for any conversion.
            assertEquals(WordCraftPdfErrorCode.TIMEOUT, c.convert(small, DocumentFormat.DOCX).getErrorCode());
        }
    }

    @Test
    void recycling() throws Exception {
        byte[] doc = Files.readAllBytes(fixtures.resolve("unicode_text.docx"));
        try (PdfConverter c = PdfConverter.create(options().maxWorkers(1).maxConversionsPerWorker(2).build())) {
            for (int i = 0; i < 5; i++) {
                ConversionResult r = c.convert(doc, DocumentFormat.DOCX);
                assertOk(r);
                assertPdf(r.getData());
            }
        }
    }

    @Test
    void closeStopsWorkers() throws Exception {
        Set<Long> before = pids(workerChildren());
        PdfConverter c = PdfConverter.create(options().maxWorkers(2).warmUp(true).build());
        Set<Long> started = pids(workerChildren());
        started.removeAll(before);
        assertEquals(2, started.size());
        c.close();
        for (long pid : started) {
            ProcessHandle.of(pid).ifPresent(h -> {
                try {
                    h.onExit().get(10, TimeUnit.SECONDS);
                } catch (Exception e) {
                    fail("worker " + pid + " did not exit");
                }
            });
        }
    }

    // ---- helpers ----

    private static List<ProcessHandle> workerChildren() {
        String name = new File(worker).getName();
        List<ProcessHandle> list = new ArrayList<>();
        ProcessHandle.current().children().forEach(h -> {
            if (h.isAlive() && FakeWorkers.commandLine(h).contains(name)) list.add(h);
        });
        return list;
    }

    private static Set<Long> pids(List<ProcessHandle> handles) {
        Set<Long> s = new HashSet<>();
        for (ProcessHandle h : handles) s.add(h.pid());
        return s;
    }
}
