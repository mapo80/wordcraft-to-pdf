/*
 * wcpdf.h — WordCraftPdf C API
 *
 * Converts word-processing documents (DOCX, DOC, ODT, RTF, HTML, Markdown, plain text) to PDF
 * with the WordCraft engine, in process. Pure Rust underneath: no LibreOffice, no external
 * runtime.
 *
 * Thread safety: conversions may run concurrently on several threads. Error messages and
 * diagnostics are per calling thread.
 *
 * Strings are UTF-8 (paths included, on every platform).
 *
 * Usage:
 *   const char* fonts[] = { "/opt/fonts" };
 *   WcPdfHandle h = wcpdf_init(fonts, 1);
 *   WcPdfError err = wcpdf_convert_file(h, "in.docx", "out.pdf", WCPDF_FORMAT_UNKNOWN, NULL);
 *   if (err != WCPDF_OK) fprintf(stderr, "%s\n", wcpdf_get_error_message());
 *   wcpdf_destroy(h);
 */

#ifndef WCPDF_H
#define WCPDF_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
  #define WCPDF_API __declspec(dllimport)
#else
  #define WCPDF_API
#endif

/* Opaque handle. */
typedef struct WcPdfInstance* WcPdfHandle;

/* Error codes (shared with the worker protocol and the SDKs). */
typedef enum {
    WCPDF_OK                      = 0,
    WCPDF_ERROR_INIT_FAILED       = 1,
    WCPDF_ERROR_LOAD_FAILED       = 2,
    WCPDF_ERROR_EXPORT_FAILED     = 3,
    WCPDF_ERROR_INVALID_FORMAT    = 4,
    WCPDF_ERROR_FILE_NOT_FOUND    = 5,
    WCPDF_ERROR_OUT_OF_MEMORY     = 6,
    WCPDF_ERROR_PERMISSION_DENIED = 7,
    WCPDF_ERROR_NOT_INIT          = 9,
    WCPDF_ERROR_INVALID_ARGUMENT  = 10,
    WCPDF_ERROR_PASSWORD_REQUIRED = 11,
    WCPDF_ERROR_WRONG_PASSWORD    = 12,
    WCPDF_ERROR_UNKNOWN           = 99
} WcPdfError;

/* Input format. UNKNOWN: from the extension (files) or the content (buffers: DOCX, DOC, ODT, RTF). */
typedef enum {
    WCPDF_FORMAT_UNKNOWN  = 0,
    WCPDF_FORMAT_DOCX     = 1,
    WCPDF_FORMAT_DOC      = 2,
    WCPDF_FORMAT_ODT      = 3,
    WCPDF_FORMAT_RTF      = 4,
    WCPDF_FORMAT_HTML     = 5,
    WCPDF_FORMAT_MARKDOWN = 6,
    WCPDF_FORMAT_TEXT     = 7
} WcPdfFormat;

/* Conversion options. A zero-initialised struct (or NULL) means the defaults. */
typedef struct {
    const char* page_range;       /* 1-based, e.g. "1-3,5,8-" (NULL = all pages) */
    const char* password;         /* encrypted documents (NULL = none) */
    int         tagged_pdf;       /* 1 = write a structure tree (tagged PDF) */
    int         update_fields;    /* 1 = refresh TOC, page references... before export */
    int         include_markup;   /* 1 = show tracked changes as markup */
    int         include_comments; /* 1 = with include_markup, comments in balloons */
    const char* title;            /* PDF title (NULL = document property) */
    const char* author;           /* PDF author (NULL = document property) */
} WcPdfOptions;

/**
 * Initialise the library. Fonts in font_dirs (and their subfolders) are used ahead of the
 * installed fonts by every later conversion in the process.
 *
 * @param font_dirs       Array of folder paths, or NULL.
 * @param font_dir_count  Number of entries in font_dirs.
 * @return A handle (never NULL unless out of memory). Folders or fonts that could not be
 *         used are reported by wcpdf_get_diagnostics_json().
 */
WCPDF_API WcPdfHandle wcpdf_init(const char* const* font_dirs, size_t font_dir_count);

/** Release a handle. Safe with NULL. */
WCPDF_API void wcpdf_destroy(WcPdfHandle handle);

/**
 * Convert the document at input_path and write the PDF to output_path (replaced atomically).
 */
WCPDF_API WcPdfError wcpdf_convert_file(
    WcPdfHandle handle,
    const char* input_path,
    const char* output_path,
    WcPdfFormat format_hint,
    const WcPdfOptions* options
);

/**
 * Convert a document in memory. On success *output_data receives the PDF, to be released with
 * wcpdf_free_buffer().
 */
WCPDF_API WcPdfError wcpdf_convert_buffer(
    WcPdfHandle handle,
    const uint8_t* input_data,
    size_t input_size,
    WcPdfFormat format_hint,
    const WcPdfOptions* options,
    uint8_t** output_data,
    size_t* output_size
);

/** Release a buffer from wcpdf_convert_buffer(). Safe with NULL. */
WCPDF_API void wcpdf_free_buffer(uint8_t* buffer);

/**
 * The last error message on this thread ("" if the last call succeeded). Valid until the next
 * WordCraftPdf call on the same thread.
 */
WCPDF_API const char* wcpdf_get_error_message(void);

/**
 * Diagnostics of the last call on this thread, as a JSON array of
 * {"severity","category","message","font"?,"substituted_with"?}. Valid until the next
 * WordCraftPdf call on the same thread.
 */
WCPDF_API const char* wcpdf_get_diagnostics_json(void);

/** Page count of the last successful conversion on this thread. */
WCPDF_API size_t wcpdf_get_page_count(void);

/** "WordCraftPdf X.Y.Z (WordCraft vA.B.C)". Static. */
WCPDF_API const char* wcpdf_version(void);

#ifdef __cplusplus
}
#endif

#endif /* WCPDF_H */
