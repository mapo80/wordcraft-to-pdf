package com.wordcraftpdf;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * Result of a PDF conversion, including diagnostics.
 *
 * <p>Check {@link #isSuccess()} to know whether the conversion succeeded. Diagnostics may be
 * present even on success (e.g. font substitution warnings).</p>
 *
 * <p>For buffer conversions, {@link #getData()} holds the PDF bytes.</p>
 */
public class ConversionResult {

    private final boolean success;
    private final String errorMessage;
    private final WordCraftPdfErrorCode errorCode;
    private final List<ConversionDiagnostic> diagnostics;
    private final byte[] data;
    private final int pageCount;

    ConversionResult(
            boolean success,
            String errorMessage,
            WordCraftPdfErrorCode errorCode,
            List<ConversionDiagnostic> diagnostics,
            byte[] data,
            int pageCount) {
        this.success = success;
        this.errorMessage = errorMessage;
        this.errorCode = errorCode;
        this.diagnostics = diagnostics == null || diagnostics.isEmpty()
                ? Collections.<ConversionDiagnostic>emptyList()
                : Collections.unmodifiableList(new ArrayList<ConversionDiagnostic>(diagnostics));
        this.data = data;
        this.pageCount = pageCount;
    }

    /** Whether the conversion completed successfully. */
    public boolean isSuccess() {
        return success;
    }

    /** Error message if the conversion failed. Null on success. */
    public String getErrorMessage() {
        return errorMessage;
    }

    /** Error code if the conversion failed. Null on success. */
    public WordCraftPdfErrorCode getErrorCode() {
        return errorCode;
    }

    /** Diagnostics reported by the conversion. Never null; may be non-empty on success. */
    public List<ConversionDiagnostic> getDiagnostics() {
        return diagnostics;
    }

    /** Whether any font diagnostic (e.g. a substitution) was reported. */
    public boolean hasFontWarnings() {
        for (ConversionDiagnostic d : diagnostics) {
            if (d.getCategory() == DiagnosticCategory.FONT) {
                return true;
            }
        }
        return false;
    }

    /** Number of pages written to the PDF. 0 when the conversion failed. */
    public int getPageCount() {
        return pageCount;
    }

    /**
     * PDF bytes of a buffer conversion. Null for file and stream conversions, and on failure.
     * The array is not copied: it is owned by the caller from now on.
     */
    public byte[] getData() {
        return data;
    }

    /**
     * Throw a {@link WordCraftPdfException} if the conversion failed; return this result
     * otherwise, for fluent chaining.
     */
    public ConversionResult throwIfFailed() {
        if (!success) {
            throw new WordCraftPdfException(
                    errorMessage != null ? errorMessage : "Conversion failed",
                    errorCode != null ? errorCode : WordCraftPdfErrorCode.UNKNOWN);
        }
        return this;
    }

    @Override
    public String toString() {
        return success
                ? "ConversionResult[success, pages=" + pageCount + ", diagnostics=" + diagnostics.size()
                        + (data != null ? ", bytes=" + data.length : "") + "]"
                : "ConversionResult[failed, " + errorCode + ": " + errorMessage + "]";
    }

    // --- Factory methods ---

    /** A successful file or stream conversion (no data). */
    public static ConversionResult ok(List<ConversionDiagnostic> diagnostics) {
        return ok(0, diagnostics);
    }

    /** A successful file or stream conversion (no data) of {@code pageCount} pages. */
    public static ConversionResult ok(int pageCount, List<ConversionDiagnostic> diagnostics) {
        return new ConversionResult(true, null, null, diagnostics, null, pageCount);
    }

    /** A successful buffer conversion with its PDF bytes. */
    public static ConversionResult ok(byte[] data, List<ConversionDiagnostic> diagnostics) {
        return ok(data, 0, diagnostics);
    }

    /** A successful buffer conversion of {@code pageCount} pages with its PDF bytes. */
    public static ConversionResult ok(byte[] data, int pageCount, List<ConversionDiagnostic> diagnostics) {
        return new ConversionResult(true, null, null, diagnostics, data, pageCount);
    }

    /** A failed conversion. */
    public static ConversionResult fail(
            String errorMessage,
            WordCraftPdfErrorCode errorCode,
            List<ConversionDiagnostic> diagnostics) {
        return new ConversionResult(false,
                errorMessage != null ? errorMessage : "Conversion failed",
                errorCode != null ? errorCode : WordCraftPdfErrorCode.UNKNOWN,
                diagnostics, null, 0);
    }
}
