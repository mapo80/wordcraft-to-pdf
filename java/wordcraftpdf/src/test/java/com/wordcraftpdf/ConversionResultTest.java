package com.wordcraftpdf;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

class ConversionResultTest {

    @Test
    void ok_isSuccess() {
        ConversionResult r = ConversionResult.ok(null);
        assertTrue(r.isSuccess());
        assertNull(r.getErrorMessage());
        assertNull(r.getErrorCode());
        assertNull(r.getData());
        assertEquals(0, r.getPageCount());
        assertTrue(r.getDiagnostics().isEmpty());
    }

    @Test
    void ok_withPageCount() {
        ConversionResult r = ConversionResult.ok(14, null);
        assertTrue(r.isSuccess());
        assertEquals(14, r.getPageCount());
        assertNull(r.getData());
    }

    @Test
    void ok_withDataIsNotCopied() {
        byte[] data = {1, 2, 3};
        ConversionResult r = ConversionResult.ok(data, 3, null);
        assertSame(data, r.getData());
        assertEquals(3, r.getPageCount());
        assertSame(data, ConversionResult.ok(data, null).getData());
    }

    @Test
    void fail_isNotSuccess() {
        ConversionResult r = ConversionResult.fail("went wrong", WordCraftPdfErrorCode.EXPORT_FAILED, null);
        assertFalse(r.isSuccess());
        assertEquals("went wrong", r.getErrorMessage());
        assertEquals(WordCraftPdfErrorCode.EXPORT_FAILED, r.getErrorCode());
        assertNull(r.getData());
        assertEquals(0, r.getPageCount());
    }

    @Test
    void fail_defaultsNullMessageAndCode() {
        ConversionResult r = ConversionResult.fail(null, null, null);
        assertEquals("Conversion failed", r.getErrorMessage());
        assertEquals(WordCraftPdfErrorCode.UNKNOWN, r.getErrorCode());
    }

    @Test
    void throwIfFailed_throwsWithCode() {
        ConversionResult r = ConversionResult.fail("crashed", WordCraftPdfErrorCode.WORKER_CRASHED, null);
        WordCraftPdfException e = assertThrows(WordCraftPdfException.class, r::throwIfFailed);
        assertEquals("crashed", e.getMessage());
        assertEquals(WordCraftPdfErrorCode.WORKER_CRASHED, e.getErrorCode());
    }

    @Test
    void throwIfFailed_returnsThisOnSuccess() {
        ConversionResult r = ConversionResult.ok(null);
        assertSame(r, r.throwIfFailed());
    }

    @Test
    void hasFontWarnings() {
        ConversionDiagnostic font = new ConversionDiagnostic(DiagnosticSeverity.WARNING, DiagnosticCategory.FONT,
                "font \"Calibri\" is not available; substituted with \"Carlito\"", "Calibri", "Carlito");
        ConversionDiagnostic general = new ConversionDiagnostic(DiagnosticSeverity.INFO, DiagnosticCategory.GENERAL,
                "something", null, null);
        assertTrue(ConversionResult.ok(Arrays.asList(general, font)).hasFontWarnings());
        assertFalse(ConversionResult.ok(Collections.singletonList(general)).hasFontWarnings());
        assertFalse(ConversionResult.ok(null).hasFontWarnings());
    }

    @Test
    void diagnostics_areUnmodifiableAndDefensivelyCopied() {
        List<ConversionDiagnostic> list = new ArrayList<ConversionDiagnostic>();
        list.add(new ConversionDiagnostic(DiagnosticSeverity.WARNING, DiagnosticCategory.FONT, "w", "Arial", null));
        ConversionResult r = ConversionResult.ok(list);
        list.clear();
        assertEquals(1, r.getDiagnostics().size());
        assertThrows(UnsupportedOperationException.class, () -> r.getDiagnostics().add(null));
    }

    @Test
    void toString_describesResult() {
        assertTrue(ConversionResult.ok(new byte[5], 2, null).toString().contains("bytes=5"));
        assertTrue(ConversionResult.fail("x", WordCraftPdfErrorCode.TIMEOUT, null).toString().contains("TIMEOUT"));
    }

    // ---- Diagnostics ----

    @Test
    void diagnostic_accessorsAndToString() {
        ConversionDiagnostic d = new ConversionDiagnostic(DiagnosticSeverity.WARNING, DiagnosticCategory.FONT,
                "Font not found", "Calibri", "Carlito");
        assertEquals("[WARNING:FONT] Font not found", d.toString());
        assertEquals("Calibri", d.getFontName());
        assertEquals("Carlito", d.getSubstitutedWith());
    }

    @Test
    void diagnostic_nullsBecomeDefaults() {
        ConversionDiagnostic d = new ConversionDiagnostic(null, null, null, null, null);
        assertEquals(DiagnosticSeverity.INFO, d.getSeverity());
        assertEquals(DiagnosticCategory.GENERAL, d.getCategory());
        assertEquals("", d.getMessage());
    }

    @Test
    void diagnosticSeverity_fromString() {
        assertEquals(DiagnosticSeverity.WARNING, DiagnosticSeverity.fromString("warning"));
        assertEquals(DiagnosticSeverity.WARNING, DiagnosticSeverity.fromString("WARNING"));
        assertEquals(DiagnosticSeverity.INFO, DiagnosticSeverity.fromString("info"));
        assertEquals(DiagnosticSeverity.INFO, DiagnosticSeverity.fromString("other"));
        assertEquals(DiagnosticSeverity.INFO, DiagnosticSeverity.fromString(null));
    }

    @Test
    void diagnosticCategory_fromString() {
        assertEquals(DiagnosticCategory.FONT, DiagnosticCategory.fromString("font"));
        assertEquals(DiagnosticCategory.FONT, DiagnosticCategory.fromString("Font"));
        assertEquals(DiagnosticCategory.GENERAL, DiagnosticCategory.fromString("general"));
        assertEquals(DiagnosticCategory.GENERAL, DiagnosticCategory.fromString("layout"));
        assertEquals(DiagnosticCategory.GENERAL, DiagnosticCategory.fromString(null));
    }

    // ---- Codes ----

    @Test
    void errorCode_valuesMatchProtocol() {
        assertEquals(1, WordCraftPdfErrorCode.INIT_FAILED.getValue());
        assertEquals(2, WordCraftPdfErrorCode.LOAD_FAILED.getValue());
        assertEquals(3, WordCraftPdfErrorCode.EXPORT_FAILED.getValue());
        assertEquals(4, WordCraftPdfErrorCode.INVALID_FORMAT.getValue());
        assertEquals(5, WordCraftPdfErrorCode.FILE_NOT_FOUND.getValue());
        assertEquals(6, WordCraftPdfErrorCode.OUT_OF_MEMORY.getValue());
        assertEquals(7, WordCraftPdfErrorCode.PERMISSION_DENIED.getValue());
        assertEquals(9, WordCraftPdfErrorCode.NOT_INITIALIZED.getValue());
        assertEquals(10, WordCraftPdfErrorCode.INVALID_ARGUMENT.getValue());
        assertEquals(11, WordCraftPdfErrorCode.PASSWORD_REQUIRED.getValue());
        assertEquals(12, WordCraftPdfErrorCode.WRONG_PASSWORD.getValue());
        assertEquals(99, WordCraftPdfErrorCode.UNKNOWN.getValue());
        assertEquals(100, WordCraftPdfErrorCode.WORKER_CRASHED.getValue());
        assertEquals(101, WordCraftPdfErrorCode.TIMEOUT.getValue());
    }

    @Test
    void errorCode_fromValue() {
        for (WordCraftPdfErrorCode c : WordCraftPdfErrorCode.values()) {
            assertEquals(c, WordCraftPdfErrorCode.fromValue(c.getValue()));
        }
        assertEquals(WordCraftPdfErrorCode.UNKNOWN, WordCraftPdfErrorCode.fromValue(0));
        assertEquals(WordCraftPdfErrorCode.UNKNOWN, WordCraftPdfErrorCode.fromValue(8));
        assertEquals(WordCraftPdfErrorCode.UNKNOWN, WordCraftPdfErrorCode.fromValue(-1));
    }

    @Test
    void documentFormat_valuesMatchProtocol() {
        assertEquals(0, DocumentFormat.UNKNOWN.getValue());
        assertEquals(1, DocumentFormat.DOCX.getValue());
        assertEquals(2, DocumentFormat.DOC.getValue());
        assertEquals(3, DocumentFormat.ODT.getValue());
        assertEquals(4, DocumentFormat.RTF.getValue());
        assertEquals(5, DocumentFormat.HTML.getValue());
        assertEquals(6, DocumentFormat.MARKDOWN.getValue());
        assertEquals(7, DocumentFormat.TEXT.getValue());
        for (DocumentFormat f : DocumentFormat.values()) {
            assertEquals(f, DocumentFormat.fromValue(f.getValue()));
        }
        assertEquals(DocumentFormat.UNKNOWN, DocumentFormat.fromValue(42));
    }

    @Test
    void documentFormat_fromExtension() {
        assertEquals(DocumentFormat.DOCX, DocumentFormat.fromExtension("/a/b/Report.DOCX"));
        assertEquals(DocumentFormat.DOCX, DocumentFormat.fromExtension("C:\\x\\t.dotm"));
        assertEquals(DocumentFormat.DOCX, DocumentFormat.fromExtension("m.docm"));
        assertEquals(DocumentFormat.DOCX, DocumentFormat.fromExtension("t.dotx"));
        assertEquals(DocumentFormat.DOC, DocumentFormat.fromExtension("legacy.doc"));
        assertEquals(DocumentFormat.DOC, DocumentFormat.fromExtension("legacy.dot"));
        assertEquals(DocumentFormat.ODT, DocumentFormat.fromExtension("x.odt"));
        assertEquals(DocumentFormat.RTF, DocumentFormat.fromExtension("x.rtf"));
        assertEquals(DocumentFormat.HTML, DocumentFormat.fromExtension("x.html"));
        assertEquals(DocumentFormat.HTML, DocumentFormat.fromExtension("x.HTM"));
        assertEquals(DocumentFormat.MARKDOWN, DocumentFormat.fromExtension("notes.markdown"));
        assertEquals(DocumentFormat.MARKDOWN, DocumentFormat.fromExtension("README.md"));
        assertEquals(DocumentFormat.TEXT, DocumentFormat.fromExtension("a.txt"));
        assertEquals(DocumentFormat.UNKNOWN, DocumentFormat.fromExtension("sheet.xlsx"));
        assertEquals(DocumentFormat.UNKNOWN, DocumentFormat.fromExtension("no_extension"));
        assertEquals(DocumentFormat.UNKNOWN, DocumentFormat.fromExtension("dir.docx/file"));
        assertEquals(DocumentFormat.UNKNOWN, DocumentFormat.fromExtension("trailing."));
        assertEquals(DocumentFormat.UNKNOWN, DocumentFormat.fromExtension(null));
    }

    @Test
    void exception_carriesCodeAndCause() {
        RuntimeException cause = new RuntimeException("root");
        WordCraftPdfException e = new WordCraftPdfException("msg", WordCraftPdfErrorCode.LOAD_FAILED, cause);
        assertEquals("msg", e.getMessage());
        assertEquals(WordCraftPdfErrorCode.LOAD_FAILED, e.getErrorCode());
        assertSame(cause, e.getCause());
    }
}
