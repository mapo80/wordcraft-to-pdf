package com.wordcraftpdf;

import java.util.Locale;

/**
 * Severity level of a {@link ConversionDiagnostic}.
 */
public enum DiagnosticSeverity {
    INFO,
    WARNING;

    /** Map the protocol's {@code "info"} / {@code "warning"}; anything else is {@link #INFO}. */
    public static DiagnosticSeverity fromString(String s) {
        if (s != null && "warning".equals(s.toLowerCase(Locale.ROOT))) {
            return WARNING;
        }
        return INFO;
    }
}
