//! WordCraftPdf C API (`libwcpdf`): the functions declared in `include/wcpdf.h`.
//!
//! Every entry point catches panics: unwinding across the FFI boundary is undefined behaviour.
#![allow(unsafe_code)]

use std::cell::RefCell;
use std::ffi::{CStr, CString, c_char, c_int};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;

use wcpdf_core::{ConvertOptions, Diagnostic, Error, ErrorCode, Format};

/// Opaque handle. The engine's state is process-wide, so the handle carries none; it keeps the
/// API shape (init / destroy) callers expect and room to grow.
pub struct WcPdfInstance {
    _private: u8,
}

/// Mirror of `WcPdfOptions` in wcpdf.h.
#[repr(C)]
pub struct WcPdfOptions {
    page_range: *const c_char,
    password: *const c_char,
    tagged_pdf: c_int,
    update_fields: c_int,
    include_markup: c_int,
    include_comments: c_int,
    title: *const c_char,
    author: *const c_char,
}

const OK: c_int = 0;

thread_local! {
    static LAST_ERROR: RefCell<CString> = RefCell::new(CString::default());
    static LAST_DIAGNOSTICS: RefCell<CString> = RefCell::new(c"[]".to_owned());
    static LAST_PAGE_COUNT: RefCell<usize> = const { RefCell::new(0) };
}

fn cstring(s: &str) -> CString {
    // Interior NULs can't cross into C; drop them rather than lose the whole message.
    CString::new(s.replace('\0', "")).unwrap_or_default()
}

fn set_outcome(error: Option<&Error>, diagnostics: &[Diagnostic], page_count: usize) {
    let message = error.map(|e| e.message.as_str()).unwrap_or("");
    LAST_ERROR.with(|c| *c.borrow_mut() = cstring(message));
    let json = serde_json::to_string(diagnostics).unwrap_or_else(|_| "[]".into());
    LAST_DIAGNOSTICS.with(|c| *c.borrow_mut() = cstring(&json));
    LAST_PAGE_COUNT.with(|c| *c.borrow_mut() = page_count);
}

fn fail(e: &Error) -> c_int {
    set_outcome(Some(e), &[], 0);
    e.code as c_int
}

/// Run an entry point, turning a panic into `WCPDF_ERROR_UNKNOWN`.
fn ffi(f: impl FnOnce() -> c_int) -> c_int {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or_else(|_| fail(&Error::new(ErrorCode::Unknown, "internal error (panic)")))
}

/// A borrowed C string as UTF-8; `None` for NULL.
///
/// # Safety
/// `p` is NULL or a valid NUL-terminated string.
unsafe fn opt_str<'a>(p: *const c_char, what: &str) -> Result<Option<&'a str>, Error> {
    if p.is_null() {
        return Ok(None);
    }
    // SAFETY: the caller guarantees a valid NUL-terminated string.
    let s = unsafe { CStr::from_ptr(p) };
    s.to_str().map(Some).map_err(|_| Error::new(ErrorCode::InvalidArgument, format!("{what} is not valid UTF-8")))
}

/// # Safety
/// `p` is NULL or a valid NUL-terminated string.
unsafe fn req_str<'a>(p: *const c_char, what: &str) -> Result<&'a str, Error> {
    // SAFETY: forwarded from the caller.
    unsafe { opt_str(p, what) }?.ok_or_else(|| Error::new(ErrorCode::InvalidArgument, format!("{what} is required")))
}

/// # Safety
/// `o` is NULL or points to a valid `WcPdfOptions` whose strings are NULL or valid.
unsafe fn options(o: *const WcPdfOptions) -> Result<ConvertOptions, Error> {
    if o.is_null() {
        return Ok(ConvertOptions::default());
    }
    // SAFETY: the caller guarantees a valid struct.
    let o = unsafe { &*o };
    let owned = |p, what| -> Result<Option<String>, Error> {
        // SAFETY: the struct's strings are NULL or valid, as the caller guarantees.
        Ok(unsafe { opt_str(p, what) }?.filter(|s| !s.is_empty()).map(str::to_string))
    };
    Ok(ConvertOptions {
        page_range: owned(o.page_range, "page_range")?,
        password: owned(o.password, "password")?,
        tagged_pdf: o.tagged_pdf != 0,
        update_fields: o.update_fields != 0,
        include_markup: o.include_markup != 0,
        include_comments: o.include_comments != 0,
        title: owned(o.title, "title")?,
        author: owned(o.author, "author")?,
    })
}

fn format(v: c_int) -> Result<Format, Error> {
    Format::from_i32(v).ok_or_else(|| Error::new(ErrorCode::InvalidFormat, format!("unknown format hint {v}")))
}

/// # Safety
/// `font_dirs` is NULL or points to `font_dir_count` pointers, each NULL or a valid string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wcpdf_init(font_dirs: *const *const c_char, font_dir_count: usize) -> *mut WcPdfInstance {
    let mut diagnostics = Vec::new();
    let r = catch_unwind(AssertUnwindSafe(|| {
        let mut dirs = Vec::new();
        if !font_dirs.is_null() {
            for i in 0..font_dir_count {
                // SAFETY: the caller guarantees `font_dir_count` readable entries.
                let p = unsafe { *font_dirs.add(i) };
                // SAFETY: each entry is NULL or a valid string.
                if let Ok(Some(d)) = unsafe { opt_str(p, "font_dirs") } {
                    dirs.push(PathBuf::from(d));
                }
            }
        }
        diagnostics = wcpdf_core::add_font_dirs(&dirs);
    }));
    if r.is_err() {
        diagnostics.clear();
    }
    set_outcome(None, &diagnostics, 0);
    Box::into_raw(Box::new(WcPdfInstance { _private: 0 }))
}

/// # Safety
/// `handle` is NULL or a handle from `wcpdf_init` not yet destroyed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wcpdf_destroy(handle: *mut WcPdfInstance) {
    if !handle.is_null() {
        // SAFETY: the caller hands back a handle `wcpdf_init` boxed, once.
        drop(unsafe { Box::from_raw(handle) });
    }
}

/// # Safety
/// Pointers as documented in wcpdf.h.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wcpdf_convert_file(
    handle: *mut WcPdfInstance,
    input_path: *const c_char,
    output_path: *const c_char,
    format_hint: c_int,
    opts: *const WcPdfOptions,
) -> c_int {
    ffi(|| {
        let r = (|| {
            if handle.is_null() {
                return Err(Error::new(ErrorCode::NotInitialized, "not initialised: call wcpdf_init first"));
            }
            // SAFETY: valid strings and options, as the caller guarantees.
            let (input, output, opts) = unsafe { (req_str(input_path, "input_path")?, req_str(output_path, "output_path")?, options(opts)?) };
            wcpdf_core::convert_file(input.as_ref(), output.as_ref(), format(format_hint)?, &opts)
        })();
        match r {
            Ok(c) => {
                set_outcome(None, &c.diagnostics, c.page_count);
                OK
            }
            Err(e) => fail(&e),
        }
    })
}

/// # Safety
/// Pointers as documented in wcpdf.h.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wcpdf_convert_buffer(
    handle: *mut WcPdfInstance,
    input_data: *const u8,
    input_size: usize,
    format_hint: c_int,
    opts: *const WcPdfOptions,
    output_data: *mut *mut u8,
    output_size: *mut usize,
) -> c_int {
    ffi(|| {
        if output_data.is_null() || output_size.is_null() {
            return fail(&Error::new(ErrorCode::InvalidArgument, "output_data and output_size are required"));
        }
        // SAFETY: both checked non-NULL above; the caller guarantees they are writable.
        unsafe {
            *output_data = std::ptr::null_mut();
            *output_size = 0;
        }
        let r = (|| {
            if handle.is_null() {
                return Err(Error::new(ErrorCode::NotInitialized, "not initialised: call wcpdf_init first"));
            }
            if input_data.is_null() || input_size == 0 {
                return Err(Error::new(ErrorCode::InvalidArgument, "input_data and input_size are required"));
            }
            // SAFETY: the caller guarantees `input_size` readable bytes at `input_data`.
            let input = unsafe { std::slice::from_raw_parts(input_data, input_size) };
            // SAFETY: valid options, as the caller guarantees.
            let opts = unsafe { options(opts)? };
            wcpdf_core::convert_bytes(input, format(format_hint)?, &opts)
        })();
        match r {
            Ok(c) => {
                // malloc'd so that wcpdf_free_buffer (free) releases it, whatever allocator Rust uses.
                // SAFETY: plain allocation; checked for NULL before use.
                let buf = unsafe { libc::malloc(c.pdf.len().max(1)) }.cast::<u8>();
                if buf.is_null() {
                    return fail(&Error::new(ErrorCode::OutOfMemory, "out of memory for the PDF buffer"));
                }
                // SAFETY: `buf` holds at least `c.pdf.len()` bytes and doesn't overlap `c.pdf`;
                // the output pointers were checked above.
                unsafe {
                    std::ptr::copy_nonoverlapping(c.pdf.as_ptr(), buf, c.pdf.len());
                    *output_data = buf;
                    *output_size = c.pdf.len();
                }
                set_outcome(None, &c.diagnostics, c.page_count);
                OK
            }
            Err(e) => fail(&e),
        }
    })
}

/// # Safety
/// `buffer` is NULL or a buffer from `wcpdf_convert_buffer`, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wcpdf_free_buffer(buffer: *mut u8) {
    // SAFETY: the buffer came from libc::malloc in wcpdf_convert_buffer (free(NULL) is a no-op).
    unsafe { libc::free(buffer.cast()) }
}

#[unsafe(no_mangle)]
pub extern "C" fn wcpdf_get_error_message() -> *const c_char {
    LAST_ERROR.with(|c| c.borrow().as_ptr())
}

#[unsafe(no_mangle)]
pub extern "C" fn wcpdf_get_diagnostics_json() -> *const c_char {
    LAST_DIAGNOSTICS.with(|c| c.borrow().as_ptr())
}

#[unsafe(no_mangle)]
pub extern "C" fn wcpdf_get_page_count() -> usize {
    LAST_PAGE_COUNT.with(|c| *c.borrow())
}

#[unsafe(no_mangle)]
pub extern "C" fn wcpdf_version() -> *const c_char {
    static VERSION: std::sync::OnceLock<CString> = std::sync::OnceLock::new();
    VERSION.get_or_init(|| cstring(&wcpdf_core::version())).as_ptr()
}
