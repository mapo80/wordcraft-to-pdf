package com.wordcraftpdf;

/**
 * A non-fatal observation reported by a conversion, such as a font substitution.
 */
public final class ConversionDiagnostic {

    private final DiagnosticSeverity severity;
    private final DiagnosticCategory category;
    private final String message;
    private final String fontName;
    private final String substitutedWith;

    public ConversionDiagnostic(
            DiagnosticSeverity severity,
            DiagnosticCategory category,
            String message,
            String fontName,
            String substitutedWith) {
        this.severity = severity != null ? severity : DiagnosticSeverity.INFO;
        this.category = category != null ? category : DiagnosticCategory.GENERAL;
        this.message = message != null ? message : "";
        this.fontName = fontName;
        this.substitutedWith = substitutedWith;
    }

    /** Severity level (INFO or WARNING). */
    public DiagnosticSeverity getSeverity() {
        return severity;
    }

    /** Category (FONT or GENERAL). */
    public DiagnosticCategory getCategory() {
        return category;
    }

    /** Human-readable message. Never null. */
    public String getMessage() {
        return message;
    }

    /** The font the document asked for (font diagnostics). Null otherwise. */
    public String getFontName() {
        return fontName;
    }

    /** The font used instead (font substitutions). Null otherwise. */
    public String getSubstitutedWith() {
        return substitutedWith;
    }

    @Override
    public String toString() {
        return "[" + severity + ":" + category + "] " + message;
    }
}
