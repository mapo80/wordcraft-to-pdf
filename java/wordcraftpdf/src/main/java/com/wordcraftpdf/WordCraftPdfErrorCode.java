package com.wordcraftpdf;

/**
 * Why a conversion failed. Values 1-99 are the worker protocol's {@code error_code}s
 * (see docs/protocol.md); 100 and above are reported by the SDK itself.
 */
public enum WordCraftPdfErrorCode {
    INIT_FAILED(1),
    LOAD_FAILED(2),
    EXPORT_FAILED(3),
    INVALID_FORMAT(4),
    FILE_NOT_FOUND(5),
    OUT_OF_MEMORY(6),
    PERMISSION_DENIED(7),
    NOT_INITIALIZED(9),
    INVALID_ARGUMENT(10),
    PASSWORD_REQUIRED(11),
    WRONG_PASSWORD(12),
    /** Unexpected failure, including engine panics caught by the worker. */
    UNKNOWN(99),
    /** The worker process died during the conversion; it is replaced for the next one. */
    WORKER_CRASHED(100),
    /** The conversion exceeded the configured timeout; its worker was killed and is replaced. */
    TIMEOUT(101);

    private final int value;

    WordCraftPdfErrorCode(int value) {
        this.value = value;
    }

    /** The numeric code used on the wire. */
    public int getValue() {
        return value;
    }

    /** The code for {@code value}, or {@link #UNKNOWN} for a value this SDK does not know. */
    public static WordCraftPdfErrorCode fromValue(int value) {
        for (WordCraftPdfErrorCode c : values()) {
            if (c.value == value) return c;
        }
        return UNKNOWN;
    }
}
