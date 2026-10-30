using System;
using System.Collections.Generic;

namespace WordCraftPdf;

/// <summary>
/// Result of a PDF conversion, including diagnostics.
/// Check <see cref="Success"/> or use the implicit bool conversion. Diagnostics may be present
/// even on success (e.g. font substitution warnings).
/// </summary>
public class ConversionResult
{
    private static readonly IReadOnlyList<ConversionDiagnostic> EmptyDiagnostics =
        Array.Empty<ConversionDiagnostic>();

    internal ConversionResult(
        bool success,
        string? errorMessage,
        WordCraftPdfErrorCode? errorCode,
        IReadOnlyList<ConversionDiagnostic>? diagnostics,
        int pageCount)
    {
        Success = success;
        ErrorMessage = errorMessage;
        ErrorCode = errorCode;
        Diagnostics = diagnostics ?? EmptyDiagnostics;
        PageCount = pageCount;
    }

    /// <summary>Whether the conversion completed successfully.</summary>
    public bool Success { get; }

    /// <summary>Error message if the conversion failed. Null on success.</summary>
    public string? ErrorMessage { get; }

    /// <summary>Error code if the conversion failed. Null on success.</summary>
    public WordCraftPdfErrorCode? ErrorCode { get; }

    /// <summary>Number of pages in the PDF (after <see cref="ConversionOptions.PageRange"/>). 0 on failure.</summary>
    public int PageCount { get; }

    /// <summary>
    /// Diagnostics collected during the conversion (font substitutions…).
    /// May be non-empty on success: warnings do not prevent conversion.
    /// </summary>
    public IReadOnlyList<ConversionDiagnostic> Diagnostics { get; }

    /// <summary>Whether any font diagnostic was reported.</summary>
    public bool HasFontWarnings
    {
        get
        {
            for (int i = 0; i < Diagnostics.Count; i++)
            {
                if (Diagnostics[i].Category == DiagnosticCategory.Font)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Implicit bool conversion: true if the conversion succeeded.</summary>
    public static implicit operator bool(ConversionResult result) => result.Success;

    /// <summary>
    /// Throw a <see cref="WordCraftPdfException"/> if the conversion failed.
    /// Returns this result for fluent chaining on success.
    /// </summary>
    public ConversionResult ThrowIfFailed()
    {
        if (!Success)
            throw new WordCraftPdfException(
                ErrorMessage ?? "Conversion failed",
                ErrorCode ?? WordCraftPdfErrorCode.Unknown);
        return this;
    }

    internal static ConversionResult Ok(IReadOnlyList<ConversionDiagnostic>? diagnostics, int pageCount) =>
        new(true, null, null, diagnostics, pageCount);

    internal static ConversionResult Fail(
        string errorMessage,
        WordCraftPdfErrorCode errorCode,
        IReadOnlyList<ConversionDiagnostic>? diagnostics) =>
        new(false, errorMessage, errorCode, diagnostics, 0);
}

/// <summary>
/// Conversion result that also carries output data (buffer conversions).
/// </summary>
public sealed class ConversionResult<T> : ConversionResult
{
    internal ConversionResult(
        bool success,
        string? errorMessage,
        WordCraftPdfErrorCode? errorCode,
        IReadOnlyList<ConversionDiagnostic>? diagnostics,
        int pageCount,
        T? data)
        : base(success, errorMessage, errorCode, diagnostics, pageCount)
    {
        Data = data;
    }

    /// <summary>Output data (the PDF bytes). Null if the conversion failed.</summary>
    public T? Data { get; }

    /// <summary>
    /// Throw a <see cref="WordCraftPdfException"/> if the conversion failed.
    /// Returns this result for fluent chaining on success.
    /// </summary>
    public new ConversionResult<T> ThrowIfFailed()
    {
        base.ThrowIfFailed();
        return this;
    }

    /// <summary>Convert to a base ConversionResult (drops the data).</summary>
    internal ConversionResult AsBase() => Success
        ? ConversionResult.Ok(Diagnostics, PageCount)
        : ConversionResult.Fail(ErrorMessage!, ErrorCode!.Value, Diagnostics);

    internal static ConversionResult<T> Ok(
        T data,
        IReadOnlyList<ConversionDiagnostic>? diagnostics,
        int pageCount) =>
        new(true, null, null, diagnostics, pageCount, data);

    internal static new ConversionResult<T> Fail(
        string errorMessage,
        WordCraftPdfErrorCode errorCode,
        IReadOnlyList<ConversionDiagnostic>? diagnostics) =>
        new(false, errorMessage, errorCode, diagnostics, 0, default);
}
