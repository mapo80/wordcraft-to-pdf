namespace WordCraftPdf;

/// <summary>
/// A diagnostic reported by the worker: a font substitution or another non-fatal observation
/// about a conversion (or, in <see cref="PdfConverter.StartupDiagnostics"/>, about a font
/// folder that couldn't be used).
/// </summary>
public sealed class ConversionDiagnostic
{
    /// <summary>Create a diagnostic.</summary>
    public ConversionDiagnostic(
        DiagnosticSeverity severity,
        DiagnosticCategory category,
        string message,
        string? fontName = null,
        string? substitutedWith = null)
    {
        Severity = severity;
        Category = category;
        Message = message;
        FontName = fontName;
        SubstitutedWith = substitutedWith;
    }

    /// <summary>Severity level (Info or Warning).</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Category (Font or General).</summary>
    public DiagnosticCategory Category { get; }

    /// <summary>Human-readable diagnostic message.</summary>
    public string Message { get; }

    /// <summary>The font the document asked for, on font diagnostics. Null otherwise.</summary>
    public string? FontName { get; }

    /// <summary>The font used instead, on font substitutions. Null otherwise.</summary>
    public string? SubstitutedWith { get; }

    /// <inheritdoc />
    public override string ToString() => $"[{Severity}:{Category}] {Message}";
}
