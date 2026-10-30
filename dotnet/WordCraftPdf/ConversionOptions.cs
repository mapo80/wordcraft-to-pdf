namespace WordCraftPdf;

/// <summary>
/// Options for a single conversion. Every option defaults to off / unset; they map one-to-one
/// to the <c>options</c> object of the worker protocol.
/// </summary>
public sealed class ConversionOptions
{
    /// <summary>
    /// 1-based pages to export, e.g. <c>"1-3,5,8-"</c> (<c>8-</c> = page 8 to the end).
    /// Null = all pages. An invalid range fails with <see cref="WordCraftPdfErrorCode.InvalidArgument"/>.
    /// </summary>
    public string? PageRange { get; init; }

    /// <summary>Password of an encrypted document. Null = none.</summary>
    public string? Password { get; init; }

    /// <summary>Write a structure tree (tagged PDF, for accessibility).</summary>
    public bool TaggedPdf { get; init; }

    /// <summary>Refresh fields (table of contents, page references…) before export.</summary>
    public bool UpdateFields { get; init; }

    /// <summary>Show tracked changes as markup instead of the final text.</summary>
    public bool IncludeMarkup { get; init; }

    /// <summary>With <see cref="IncludeMarkup"/>, show comments in balloons beside each page.</summary>
    public bool IncludeComments { get; init; }

    /// <summary>PDF title. Null = the document's own title.</summary>
    public string? Title { get; init; }

    /// <summary>PDF author. Null = the document's own author.</summary>
    public string? Author { get; init; }
}
