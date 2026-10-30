package com.wordcraftpdf;

/**
 * Exception thrown by WordCraftPdf operations, e.g. when a worker cannot be started or by
 * {@link ConversionResult#throwIfFailed()}.
 */
public class WordCraftPdfException extends RuntimeException {

    private static final long serialVersionUID = 1L;

    private final WordCraftPdfErrorCode errorCode;

    public WordCraftPdfException(String message, WordCraftPdfErrorCode errorCode) {
        super(message);
        this.errorCode = errorCode;
    }

    public WordCraftPdfException(String message, WordCraftPdfErrorCode errorCode, Throwable cause) {
        super(message, cause);
        this.errorCode = errorCode;
    }

    public WordCraftPdfErrorCode getErrorCode() {
        return errorCode;
    }
}
