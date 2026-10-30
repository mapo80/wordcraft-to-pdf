namespace WordCraftPdf;

/// <summary>
/// Input document format. Values match the <c>format</c> codes of the worker protocol
/// (docs/protocol.md): never renumber them.
/// </summary>
public enum DocumentFormat
{
    /// <summary>
    /// Detect the format: from the file extension (file conversions) or from the content
    /// (buffer and stream conversions; DOCX, DOC, ODT and RTF have a signature, HTML,
    /// Markdown and plain text need an explicit format).
    /// </summary>
    Unknown = 0,
    /// <summary>Office Open XML word processing document (.docx, .docm, .dotx, .dotm).</summary>
    Docx = 1,
    /// <summary>Word 97-2003 binary document (.doc, .dot).</summary>
    Doc = 2,
    /// <summary>OpenDocument text (.odt).</summary>
    Odt = 3,
    /// <summary>Rich Text Format (.rtf).</summary>
    Rtf = 4,
    /// <summary>HTML (.html, .htm).</summary>
    Html = 5,
    /// <summary>Markdown (.md, .markdown).</summary>
    Markdown = 6,
    /// <summary>Plain text (.txt).</summary>
    Text = 7
}

/// <summary>
/// Error codes of a failed conversion. Values 1-99 are the <c>error_code</c> values of the
/// worker protocol (docs/protocol.md); values from 100 are produced by the SDK itself.
/// </summary>
public enum WordCraftPdfErrorCode
{
    /// <summary>The worker could not be started or initialised.</summary>
    InitFailed = 1,
    /// <summary>The document could not be loaded (corrupt or unsupported content).</summary>
    LoadFailed = 2,
    /// <summary>The PDF could not be produced.</summary>
    ExportFailed = 3,
    /// <summary>The format is unknown, unsupported, or could not be detected.</summary>
    InvalidFormat = 4,
    /// <summary>The input file does not exist.</summary>
    FileNotFound = 5,
    /// <summary>The worker ran out of memory.</summary>
    OutOfMemory = 6,
    /// <summary>A file could not be read or written because of its permissions.</summary>
    PermissionDenied = 7,
    /// <summary>A conversion was sent to a worker before <c>init</c> (SDK bug).</summary>
    NotInitialized = 9,
    /// <summary>An argument or option is invalid (e.g. a malformed page range, empty input).</summary>
    InvalidArgument = 10,
    /// <summary>The document is encrypted and no password was given.</summary>
    PasswordRequired = 11,
    /// <summary>The password given does not open the document.</summary>
    WrongPassword = 12,
    /// <summary>Unexpected error, including engine panics caught by the worker.</summary>
    Unknown = 99,
    /// <summary>
    /// The worker process died during the conversion (abort, stack overflow, killed). The worker
    /// is replaced automatically; the next conversion runs on a fresh one.
    /// </summary>
    WorkerCrashed = 100,
    /// <summary>
    /// The conversion exceeded <see cref="PdfConverterOptions.ConversionTimeout"/>. The worker
    /// was killed and is replaced automatically.
    /// </summary>
    Timeout = 101
}

/// <summary>Severity level of a conversion diagnostic.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational.</summary>
    Info = 0,
    /// <summary>Something that may affect the output (e.g. a substituted font).</summary>
    Warning = 1
}

/// <summary>Category of a conversion diagnostic.</summary>
public enum DiagnosticCategory
{
    /// <summary>Anything that isn't about fonts.</summary>
    General = 0,
    /// <summary>Font availability and substitution.</summary>
    Font = 1
}
