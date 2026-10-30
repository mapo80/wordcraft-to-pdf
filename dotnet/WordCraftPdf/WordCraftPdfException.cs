using System;

namespace WordCraftPdf;

/// <summary>
/// Exception thrown when a conversion fails and the caller asked for an exception
/// (<see cref="ConversionResult.ThrowIfFailed"/>), or when workers cannot be started eagerly
/// (<see cref="PdfConverterOptions.WarmUp"/>, <see cref="PdfConverter.WarmUpAsync"/>).
/// </summary>
public class WordCraftPdfException : Exception
{
    /// <summary>The error code associated with the failure.</summary>
    public WordCraftPdfErrorCode ErrorCode { get; }

    /// <summary>Create an exception with a message and an error code.</summary>
    public WordCraftPdfException(string message, WordCraftPdfErrorCode errorCode = WordCraftPdfErrorCode.Unknown)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Create an exception with a message, an error code and the underlying cause.</summary>
    public WordCraftPdfException(string message, WordCraftPdfErrorCode errorCode, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
