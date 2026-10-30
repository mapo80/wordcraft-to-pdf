package com.wordcraftpdf;

import java.util.Locale;

/**
 * Category of a {@link ConversionDiagnostic}.
 */
public enum DiagnosticCategory {
    /** Anything that is not about fonts (e.g. content the export had to leave out). */
    GENERAL,
    /** A font the document asks for is unavailable or could not be used. */
    FONT;

    /** Map the protocol's {@code "font"} / {@code "general"}; anything else is {@link #GENERAL}. */
    public static DiagnosticCategory fromString(String s) {
        if (s != null && "font".equals(s.toLowerCase(Locale.ROOT))) {
            return FONT;
        }
        return GENERAL;
    }
}
