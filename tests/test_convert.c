/*
 * test_convert.c — WordCraftPdf C API smoke test
 *
 * Links libwcpdf through wcpdf.h and checks, for every fixture given on the command line:
 *   - file mode   (wcpdf_convert_file, format from the extension)
 *   - buffer mode (wcpdf_convert_buffer, format sniffed from the content)
 * Each PDF must start with "%PDF-" and report a non-zero page count; buffer and file mode must
 * agree on the page count. Diagnostics JSON is printed for each conversion.
 *
 * Then the error paths and options:
 *   - missing file                         -> WCPDF_ERROR_FILE_NOT_FOUND
 *   - unsupported extension (.xlsx)        -> WCPDF_ERROR_INVALID_FORMAT
 *   - garbage bytes with FORMAT_UNKNOWN    -> WCPDF_ERROR_INVALID_FORMAT
 *   - bad page ranges ("999999", "0", "x") -> WCPDF_ERROR_INVALID_ARGUMENT
 *   - empty buffer, NULL handle            -> INVALID_ARGUMENT / NOT_INIT
 *   - page_range "1" -> exactly one page; tagged PDF + title/author succeed
 *   - text / Markdown / HTML (explicit hint) and RTF (sniffed) buffers convert
 *   - concurrent buffer conversions on several threads (POSIX only)
 *
 * With --fonts DIR the folder is passed to wcpdf_init; fixtures whose name contains "barcode"
 * must then NOT report the barcode font as substituted.
 *
 * Build (see tests/test.sh):
 *   cc -o test_convert tests/test_convert.c -Ioutput/include -Loutput/lib -lwcpdf \
 *      -Wl,-rpath,output/lib
 *
 * Run:
 *   ./test_convert --out /tmp/wcpdf-test [--fonts tests/fixtures/fonts] tests/fixtures/<name>.docx...
 *
 * Exit status: 0 when every check passed, 1 otherwise.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <time.h>

#ifndef _WIN32
#include <pthread.h>
#endif

#include "wcpdf.h"

#ifdef _WIN32
#define PATH_SEP '\\'
#else
#define PATH_SEP '/'
#endif

static int g_failures = 0;
static int g_checks = 0;

#define CHECK(cond, ...)                                  \
    do {                                                  \
        g_checks++;                                       \
        if (!(cond)) {                                    \
            g_failures++;                                 \
            fprintf(stderr, "  FAIL: " __VA_ARGS__);      \
            fprintf(stderr, "\n");                        \
        }                                                 \
    } while (0)

static double now_ms(void) {
#ifdef TIME_UTC
    struct timespec ts;
    if (timespec_get(&ts, TIME_UTC) == TIME_UTC) {
        return (double)ts.tv_sec * 1000.0 + (double)ts.tv_nsec / 1e6;
    }
#endif
    return (double)clock() * 1000.0 / CLOCKS_PER_SEC;
}

static const char* base_name(const char* path) {
    const char* b = path;
    for (const char* p = path; *p; p++) {
        if (*p == '/' || *p == '\\') b = p + 1;
    }
    return b;
}

static unsigned char* read_file(const char* path, size_t* size) {
    FILE* f = fopen(path, "rb");
    if (!f) return NULL;
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return NULL; }
    long sz = ftell(f);
    if (sz < 0 || fseek(f, 0, SEEK_SET) != 0) { fclose(f); return NULL; }
    unsigned char* buf = (unsigned char*)malloc((size_t)sz + 1);
    if (!buf) { fclose(f); return NULL; }
    size_t n = fread(buf, 1, (size_t)sz, f);
    fclose(f);
    if (n != (size_t)sz) { free(buf); return NULL; }
    *size = n;
    return buf;
}

static int write_file(const char* path, const void* data, size_t size) {
    FILE* f = fopen(path, "wb");
    if (!f) return 0;
    size_t n = fwrite(data, 1, size, f);
    fclose(f);
    return n == size;
}

static int has_pdf_header(const unsigned char* data, size_t size) {
    return size >= 5 && memcmp(data, "%PDF-", 5) == 0;
}

/* Case-insensitive substring search. */
static int contains_ci(const char* hay, const char* needle) {
    size_t n = strlen(needle);
    for (; *hay; hay++) {
        size_t i = 0;
        while (i < n && hay[i] && tolower((unsigned char)hay[i]) == tolower((unsigned char)needle[i])) i++;
        if (i == n) return 1;
    }
    return 0;
}

/*
 * True when the diagnostics JSON reports a substitution for a font whose name contains
 * `needle` (spaces ignored, so "LibreBarcode128" matches "Libre Barcode 128"). Looks at every
 * "font":"..." value of an entry that has "substituted_with".
 */
static int reports_substitution(const char* json, const char* needle) {
    const char* key = "\"font\":\"";
    for (const char* p = strstr(json, key); p; p = strstr(p + 1, key)) {
        const char* v = p + strlen(key);
        const char* end = strchr(v, '"');
        if (!end) break;
        /* The entry ends at the next '}' (font names don't contain braces). */
        const char* entry_end = strchr(end, '}');
        char name[512];
        size_t k = 0;
        for (const char* q = v; q < end && k + 1 < sizeof name; q++) {
            if (*q != ' ') name[k++] = *q;
        }
        name[k] = '\0';
        char compact[128];
        size_t m = 0;
        for (const char* q = needle; *q && m + 1 < sizeof compact; q++) {
            if (*q != ' ') compact[m++] = *q;
        }
        compact[m] = '\0';
        if (contains_ci(name, compact)) {
            const char* sub = strstr(end, "\"substituted_with\"");
            if (sub && (!entry_end || sub < entry_end)) return 1;
        }
    }
    return 0;
}

static const char* error_name(WcPdfError e) {
    switch (e) {
        case WCPDF_OK: return "OK";
        case WCPDF_ERROR_INIT_FAILED: return "INIT_FAILED";
        case WCPDF_ERROR_LOAD_FAILED: return "LOAD_FAILED";
        case WCPDF_ERROR_EXPORT_FAILED: return "EXPORT_FAILED";
        case WCPDF_ERROR_INVALID_FORMAT: return "INVALID_FORMAT";
        case WCPDF_ERROR_FILE_NOT_FOUND: return "FILE_NOT_FOUND";
        case WCPDF_ERROR_OUT_OF_MEMORY: return "OUT_OF_MEMORY";
        case WCPDF_ERROR_PERMISSION_DENIED: return "PERMISSION_DENIED";
        case WCPDF_ERROR_NOT_INIT: return "NOT_INIT";
        case WCPDF_ERROR_INVALID_ARGUMENT: return "INVALID_ARGUMENT";
        case WCPDF_ERROR_PASSWORD_REQUIRED: return "PASSWORD_REQUIRED";
        case WCPDF_ERROR_WRONG_PASSWORD: return "WRONG_PASSWORD";
        case WCPDF_ERROR_UNKNOWN: return "UNKNOWN";
    }
    return "?";
}

static void expect_error(const char* what, WcPdfError got, WcPdfError want) {
    const char* msg = wcpdf_get_error_message();
    printf("  %-44s -> %s (%s)\n", what, error_name(got), msg ? msg : "(null)");
    CHECK(got == want, "%s: expected %s, got %s", what, error_name(want), error_name(got));
    CHECK(msg != NULL && msg[0] != '\0', "%s: empty error message", what);
}

/* ------------------------------------------------------------------------------------------ */

static size_t convert_fixture(WcPdfHandle h, const char* input, const char* out_dir, int fonts_given) {
    const char* name = base_name(input);
    char out_path[4096];
    snprintf(out_path, sizeof out_path, "%s%c%s.pdf", out_dir, PATH_SEP, name);
    int is_barcode = contains_ci(name, "barcode");

    printf("--- %s\n", name);

    /* File mode, format from the extension. */
    double t0 = now_ms();
    WcPdfError err = wcpdf_convert_file(h, input, out_path, WCPDF_FORMAT_UNKNOWN, NULL);
    double t_file = now_ms() - t0;
    size_t pages_file = 0;
    if (err != WCPDF_OK) {
        CHECK(0, "%s: file mode returned %s: %s", name, error_name(err), wcpdf_get_error_message());
    } else {
        pages_file = wcpdf_get_page_count();
        const char* diag = wcpdf_get_diagnostics_json();
        const char* msg = wcpdf_get_error_message();
        size_t size = 0;
        unsigned char* pdf = read_file(out_path, &size);
        printf("  file:   %zu page(s), %zu bytes, %.1f ms\n", pages_file, size, t_file);
        printf("  diagnostics: %s\n", diag ? diag : "(null)");
        CHECK(pdf != NULL, "%s: output not written: %s", name, out_path);
        CHECK(pdf && has_pdf_header(pdf, size), "%s: file output lacks %%PDF- header", name);
        CHECK(pages_file > 0, "%s: file mode page count is 0", name);
        CHECK(msg != NULL && msg[0] == '\0', "%s: error message not cleared after success", name);
        CHECK(diag != NULL && diag[0] == '[', "%s: diagnostics is not a JSON array", name);
        if (is_barcode && fonts_given && diag) {
            CHECK(!reports_substitution(diag, "LibreBarcode128") && !reports_substitution(diag, "Libre Barcode"),
                  "%s: barcode font reported as substituted although --fonts was given", name);
        }
        free(pdf);
    }

    /* Buffer mode, format sniffed. */
    size_t in_size = 0;
    unsigned char* in = read_file(input, &in_size);
    CHECK(in != NULL, "%s: cannot read fixture", name);
    if (!in) return pages_file;
    uint8_t* out = NULL;
    size_t out_size = 0;
    t0 = now_ms();
    err = wcpdf_convert_buffer(h, in, in_size, WCPDF_FORMAT_UNKNOWN, NULL, &out, &out_size);
    double t_buf = now_ms() - t0;
    if (err != WCPDF_OK) {
        CHECK(0, "%s: buffer mode returned %s: %s", name, error_name(err), wcpdf_get_error_message());
    } else {
        size_t pages_buf = wcpdf_get_page_count();
        printf("  buffer: %zu page(s), %zu bytes, %.1f ms\n", pages_buf, out_size, t_buf);
        CHECK(out != NULL && has_pdf_header(out, out_size), "%s: buffer output lacks %%PDF- header", name);
        CHECK(pages_buf > 0, "%s: buffer mode page count is 0", name);
        CHECK(pages_file == 0 || pages_buf == pages_file,
              "%s: page count differs between file (%zu) and buffer (%zu) mode", name, pages_file, pages_buf);
        if (is_barcode && fonts_given) {
            const char* diag = wcpdf_get_diagnostics_json();
            CHECK(diag && !reports_substitution(diag, "LibreBarcode128") && !reports_substitution(diag, "Libre Barcode"),
                  "%s: barcode font reported as substituted (buffer mode)", name);
        }
    }
    wcpdf_free_buffer(out);
    free(in);
    return pages_file;
}

static void convert_text_buffer(WcPdfHandle h, const char* label, const char* text, WcPdfFormat fmt) {
    uint8_t* out = NULL;
    size_t out_size = 0;
    WcPdfError err = wcpdf_convert_buffer(h, (const uint8_t*)text, strlen(text), fmt, NULL, &out, &out_size);
    if (err != WCPDF_OK) {
        CHECK(0, "%s buffer returned %s: %s", label, error_name(err), wcpdf_get_error_message());
    } else {
        printf("  %-10s %zu page(s), %zu bytes\n", label, wcpdf_get_page_count(), out_size);
        CHECK(has_pdf_header(out, out_size), "%s: output lacks %%PDF- header", label);
        CHECK(wcpdf_get_page_count() > 0, "%s: page count is 0", label);
    }
    wcpdf_free_buffer(out);
}

#ifndef _WIN32
typedef struct {
    WcPdfHandle handle;
    const unsigned char* data;
    size_t size;
    int iterations;
    int failures;
} ThreadJob;

static void* thread_main(void* arg) {
    ThreadJob* job = (ThreadJob*)arg;
    for (int i = 0; i < job->iterations; i++) {
        uint8_t* out = NULL;
        size_t out_size = 0;
        WcPdfError err = wcpdf_convert_buffer(job->handle, job->data, job->size, WCPDF_FORMAT_DOCX, NULL, &out, &out_size);
        if (err != WCPDF_OK || !has_pdf_header(out, out_size) || wcpdf_get_page_count() == 0) {
            job->failures++;
        }
        wcpdf_free_buffer(out);
    }
    return NULL;
}
#endif

int main(int argc, char** argv) {
    const char* out_dir = NULL;
    const char* fonts_dir = NULL;
    const char* fixtures[256];
    int n_fixtures = 0;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--out") == 0 && i + 1 < argc) {
            out_dir = argv[++i];
        } else if (strcmp(argv[i], "--fonts") == 0 && i + 1 < argc) {
            fonts_dir = argv[++i];
        } else if (n_fixtures < (int)(sizeof fixtures / sizeof fixtures[0])) {
            fixtures[n_fixtures++] = argv[i];
        }
    }
    if (!out_dir || n_fixtures == 0) {
        fprintf(stderr, "Usage: %s --out <dir> [--fonts <dir>] <fixture.docx>...\n", argv[0]);
        return 2;
    }

    printf("=== WordCraftPdf C API test ===\n");
    printf("Version:  %s\n", wcpdf_version());
    printf("Fixtures: %d\n", n_fixtures);
    printf("Fonts:    %s\n", fonts_dir ? fonts_dir : "(none)");
    printf("Output:   %s\n\n", out_dir);

    CHECK(strstr(wcpdf_version(), "WordCraftPdf") != NULL, "unexpected version string");

    /* Safe-with-NULL contracts. */
    wcpdf_destroy(NULL);
    wcpdf_free_buffer(NULL);

    /* ---- Init ---- */
    printf("[1/5] wcpdf_init\n");
    const char* dirs[1] = { fonts_dir };
    double t0 = now_ms();
    WcPdfHandle h = wcpdf_init(fonts_dir ? dirs : NULL, fonts_dir ? 1 : 0);
    double t_init = now_ms() - t0;
    if (!h) {
        fprintf(stderr, "FAIL: wcpdf_init returned NULL: %s\n", wcpdf_get_error_message());
        return 1;
    }
    const char* init_diag = wcpdf_get_diagnostics_json();
    printf("  OK (%.1f ms), diagnostics: %s\n\n", t_init, init_diag ? init_diag : "(null)");
    CHECK(init_diag && strcmp(init_diag, "[]") == 0, "wcpdf_init reported problems with the font folder: %s",
          init_diag ? init_diag : "(null)");

    /* ---- Fixtures ---- */
    printf("[2/5] Fixtures (file + buffer mode)\n");
    size_t first_pages = 0;
    for (int i = 0; i < n_fixtures; i++) {
        size_t p = convert_fixture(h, fixtures[i], out_dir, fonts_dir != NULL);
        if (i == 0) first_pages = p;
    }
    printf("\n");

    /* ---- Options ---- */
    printf("[3/5] Options\n");
    {
        char out_path[4096];
        snprintf(out_path, sizeof out_path, "%s%cpage-range.pdf", out_dir, PATH_SEP);
        WcPdfOptions o;
        memset(&o, 0, sizeof o);
        o.page_range = "1";
        WcPdfError err = wcpdf_convert_file(h, fixtures[0], out_path, WCPDF_FORMAT_UNKNOWN, &o);
        CHECK(err == WCPDF_OK, "page_range \"1\" returned %s: %s", error_name(err), wcpdf_get_error_message());
        if (err == WCPDF_OK) {
            printf("  page_range \"1\": %zu page(s) (document has %zu)\n", wcpdf_get_page_count(), first_pages);
            CHECK(wcpdf_get_page_count() == 1, "page_range \"1\" produced %zu pages", wcpdf_get_page_count());
        }

        memset(&o, 0, sizeof o);
        o.tagged_pdf = 1;
        o.title = "WordCraftPdf smoke test";
        o.author = "test_convert.c";
        snprintf(out_path, sizeof out_path, "%s%ctagged.pdf", out_dir, PATH_SEP);
        err = wcpdf_convert_file(h, fixtures[0], out_path, WCPDF_FORMAT_DOCX, &o);
        CHECK(err == WCPDF_OK, "tagged/title/author returned %s: %s", error_name(err), wcpdf_get_error_message());
        if (err == WCPDF_OK) printf("  tagged_pdf + title/author: OK\n");

        convert_text_buffer(h, "text", "Hello from WordCraftPdf.\n\nSecond paragraph.", WCPDF_FORMAT_TEXT);
        convert_text_buffer(h, "markdown", "# Title\n\nSome *emphasis* and a list:\n\n- one\n- two\n", WCPDF_FORMAT_MARKDOWN);
        convert_text_buffer(h, "html", "<html><body><h1>Title</h1><p>Paragraph <b>bold</b>.</p></body></html>", WCPDF_FORMAT_HTML);
        convert_text_buffer(h, "rtf(sniff)", "{\\rtf1\\ansi\\deff0 {\\fonttbl {\\f0 Arial;}} \\f0 Hello RTF \\b bold\\b0 .\\par }", WCPDF_FORMAT_UNKNOWN);
    }
    printf("\n");

    /* ---- Error paths ---- */
    printf("[4/5] Error paths\n");
    {
        char missing[4096], xlsx[4096], out_path[4096];
        snprintf(missing, sizeof missing, "%s%cdoes-not-exist.docx", out_dir, PATH_SEP);
        snprintf(xlsx, sizeof xlsx, "%s%csheet.xlsx", out_dir, PATH_SEP);
        snprintf(out_path, sizeof out_path, "%s%cerror.pdf", out_dir, PATH_SEP);
        remove(missing);
        remove(out_path);
        write_file(xlsx, "PK\x03\x04 not really a spreadsheet", 30);

        expect_error("missing file", wcpdf_convert_file(h, missing, out_path, WCPDF_FORMAT_UNKNOWN, NULL),
                     WCPDF_ERROR_FILE_NOT_FOUND);
        expect_error("unsupported extension (.xlsx)", wcpdf_convert_file(h, xlsx, out_path, WCPDF_FORMAT_UNKNOWN, NULL),
                     WCPDF_ERROR_INVALID_FORMAT);

        const char* bad_ranges[] = { "999999", "0", "x", "3-1" };
        for (size_t i = 0; i < sizeof bad_ranges / sizeof bad_ranges[0]; i++) {
            WcPdfOptions o;
            memset(&o, 0, sizeof o);
            o.page_range = bad_ranges[i];
            char label[64];
            snprintf(label, sizeof label, "page_range \"%s\"", bad_ranges[i]);
            /* "3-1" is only backwards when the document has >= 3 pages; otherwise page 3 is out of range. */
            expect_error(label, wcpdf_convert_file(h, fixtures[0], out_path, WCPDF_FORMAT_UNKNOWN, &o),
                         WCPDF_ERROR_INVALID_ARGUMENT);
        }

        static const unsigned char garbage[] = "this is not a document \x01\x02\x03\xff\xfe";
        uint8_t* out = NULL;
        size_t out_size = 0;
        expect_error("garbage bytes, FORMAT_UNKNOWN",
                     wcpdf_convert_buffer(h, garbage, sizeof garbage - 1, WCPDF_FORMAT_UNKNOWN, NULL, &out, &out_size),
                     WCPDF_ERROR_INVALID_FORMAT);
        CHECK(out == NULL && out_size == 0, "failed buffer conversion left output set");
        expect_error("empty buffer",
                     wcpdf_convert_buffer(h, garbage, 0, WCPDF_FORMAT_DOCX, NULL, &out, &out_size),
                     WCPDF_ERROR_INVALID_ARGUMENT);
        expect_error("NULL handle",
                     wcpdf_convert_file(NULL, fixtures[0], out_path, WCPDF_FORMAT_UNKNOWN, NULL),
                     WCPDF_ERROR_NOT_INIT);
        expect_error("format hint out of range",
                     wcpdf_convert_buffer(h, garbage, sizeof garbage - 1, (WcPdfFormat)42, NULL, &out, &out_size),
                     WCPDF_ERROR_INVALID_FORMAT);

        /* Failed conversions must not leave a PDF behind. */
        FILE* f = fopen(out_path, "rb");
        CHECK(f == NULL, "a failed conversion wrote %s", out_path);
        if (f) fclose(f);
        remove(xlsx);
    }
    printf("\n");

    /* ---- Concurrency ---- */
    printf("[5/5] Concurrent buffer conversions\n");
#ifndef _WIN32
    {
        enum { THREADS = 4, ITERATIONS = 3 };
        size_t size = 0;
        unsigned char* data = read_file(fixtures[0], &size);
        CHECK(data != NULL, "cannot read %s", fixtures[0]);
        if (data) {
            pthread_t threads[THREADS];
            ThreadJob jobs[THREADS];
            t0 = now_ms();
            for (int i = 0; i < THREADS; i++) {
                jobs[i].handle = h;
                jobs[i].data = data;
                jobs[i].size = size;
                jobs[i].iterations = ITERATIONS;
                jobs[i].failures = 0;
                pthread_create(&threads[i], NULL, thread_main, &jobs[i]);
            }
            int failures = 0;
            for (int i = 0; i < THREADS; i++) {
                pthread_join(threads[i], NULL);
                failures += jobs[i].failures;
            }
            double elapsed = now_ms() - t0;
            printf("  %d threads x %d conversions: %d failure(s), %.1f ms\n", THREADS, ITERATIONS, failures, elapsed);
            CHECK(failures == 0, "%d concurrent conversion(s) failed", failures);
            free(data);
        }
    }
#else
    printf("  skipped on Windows\n");
#endif
    printf("\n");

    wcpdf_destroy(h);

    if (g_failures > 0) {
        printf("=== FAILED: %d of %d checks ===\n", g_failures, g_checks);
        return 1;
    }
    printf("=== ALL %d CHECKS PASSED ===\n", g_checks);
    return 0;
}
