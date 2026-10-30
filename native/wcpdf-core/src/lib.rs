//! WordCraftPdf core: word-processing documents (DOCX first) to PDF through the WordCraft engine,
//! in pure, safe Rust. The C API (`wcpdf-capi`) and the out-of-process worker (`wcpdf-worker`)
//! are thin layers over this crate.
//!
//! Conversions are independent and may run concurrently on several threads; the only shared
//! state is WordCraft's process-wide font database, which [`add_font_dirs`] extends.
#![forbid(unsafe_code)]

mod diagnostics;
mod error;
mod format;
mod options;

use std::path::{Path, PathBuf};

use wordcraft_layout::{LayoutCache, LayoutOptions, NumeralMode};

pub use diagnostics::{Category, Diagnostic, Severity};
pub use error::{Error, ErrorCode};
pub use format::Format;
pub use options::ConvertOptions;

/// This library's version.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// Largest input document read, in bytes.
pub const MAX_INPUT_BYTES: u64 = 512 * 1024 * 1024;

/// `"WordCraftPdf X.Y.Z (WordCraft vA.B.C)"`.
pub fn version() -> String {
    format!("WordCraftPdf {VERSION} (WordCraft {})", include_str!("../../../WORDCRAFT_VERSION").trim())
}

/// A successful conversion.
#[derive(Debug)]
pub struct Conversion {
    /// The PDF (empty for [`convert_file`], which writes it to disk).
    pub pdf: Vec<u8>,
    /// Pages in the PDF.
    pub page_count: usize,
    pub diagnostics: Vec<Diagnostic>,
}

/// Font folders to deepest scan, and font files per call.
const MAX_FONT_DIR_DEPTH: usize = 8;

/// Make the fonts in `dirs` (and their subfolders) available to every later conversion in this
/// process, ahead of the installed fonts. Returns a warning for each folder or file that couldn't
/// be used.
pub fn add_font_dirs<P: AsRef<Path>>(dirs: &[P]) -> Vec<Diagnostic> {
    let db = wordcraft_fonts::FontDb::global();
    let mut out = Vec::new();
    let mut stack: Vec<(PathBuf, usize)> = dirs.iter().map(|d| (d.as_ref().to_path_buf(), 0)).collect();
    while let Some((dir, depth)) = stack.pop() {
        let entries = match std::fs::read_dir(&dir) {
            Ok(e) => e,
            Err(e) => {
                out.push(Diagnostic::general(Severity::Warning, &format!("font folder {}: {e}", dir.display())));
                continue;
            }
        };
        if depth < MAX_FONT_DIR_DEPTH {
            stack.extend(entries.flatten().map(|e| e.path()).filter(|p| p.is_dir()).map(|p| (p, depth + 1)));
        }
        let (_, skipped) = db.load_user_fonts(&dir);
        out.extend(skipped.into_iter().map(|(file, why)| {
            Diagnostic::general(Severity::Warning, &format!("font {}: {why}", dir.join(file).display()))
        }));
    }
    out
}

/// Pay the one-time costs up front — the system font scan, and a tiny conversion through the
/// whole pipeline — so the first real conversion runs at full speed. Call after
/// [`add_font_dirs`].
pub fn warm_up() {
    let _ = wordcraft_fonts::FontDb::global().load_system_fonts();
    let _ = convert_bytes(b"WordCraftPdf warm-up", Format::Text, &ConvertOptions::default());
}

/// Convert a document held in memory. `format` may be [`Format::Unknown`] for the formats with a
/// signature (DOCX, DOC, ODT, RTF).
pub fn convert_bytes(input: &[u8], format: Format, opts: &ConvertOptions) -> Result<Conversion, Error> {
    if input.is_empty() {
        return Err(Error::new(ErrorCode::InvalidArgument, "the input document is empty"));
    }
    let format = Format::resolve_for_bytes(format, input)?;
    guarded(|| {
        let name = format!("input.{}", format.extension());
        let (doc, _) = wordcraft_engine::io::open_bytes_in(&name, input, opts.password.as_deref(), &Default::default())
            .map_err(load_error)?;
        export(doc, opts)
    })
}

/// Convert the document at `input` and write the PDF to `output` (replaced atomically). With
/// [`Format::Unknown`] the format follows `input`'s extension.
pub fn convert_file(input: &Path, output: &Path, format: Format, opts: &ConvertOptions) -> Result<Conversion, Error> {
    let format = match format {
        Format::Unknown => Format::from_extension(&input.to_string_lossy()).ok_or_else(|| {
            Error::new(ErrorCode::InvalidFormat, format!("{}: unsupported file type", input.display()))
        })?,
        f => f,
    };
    let meta = std::fs::metadata(input).map_err(|e| Error::io(input, &e, ErrorCode::LoadFailed))?;
    if !meta.is_file() {
        return Err(Error::new(ErrorCode::InvalidArgument, format!("{}: not a file", input.display())));
    }
    if meta.len() > MAX_INPUT_BYTES {
        return Err(Error::new(ErrorCode::InvalidArgument, format!("{}: larger than {MAX_INPUT_BYTES} bytes", input.display())));
    }
    let mut conversion = if format == Format::Html {
        // From its path, so pictures referenced relative to the page are found.
        guarded(|| {
            let (doc, _) = wordcraft_engine::io::open_path_in(input, opts.password.as_deref(), &Default::default())
                .map_err(load_error)?;
            export(doc, opts)
        })?
    } else {
        let bytes = std::fs::read(input).map_err(|e| Error::io(input, &e, ErrorCode::LoadFailed))?;
        convert_bytes(&bytes, format, opts)?
    };
    write_atomically(output, &conversion.pdf)?;
    conversion.pdf = Vec::new();
    Ok(conversion)
}

/// Run a conversion step, turning a panic that escapes WordCraft into an error and collecting
/// what WordCraft logged as diagnostics.
fn guarded(f: impl FnOnce() -> Result<Conversion, Error>) -> Result<Conversion, Error> {
    let (r, logged) = diagnostics::capture(|| std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)));
    match r {
        Ok(Ok(mut c)) => {
            c.diagnostics.extend(logged);
            Ok(c)
        }
        Ok(Err(e)) => Err(e),
        Err(panic) => {
            let what = panic
                .downcast_ref::<&str>()
                .map(|s| (*s).to_string())
                .or_else(|| panic.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "unknown cause".into());
            Err(Error::new(ErrorCode::Unknown, format!("internal error in the conversion engine: {what}")))
        }
    }
}

fn load_error(e: String) -> Error {
    if wordcraft_engine::io::needs_password(&e) {
        Error::new(ErrorCode::PasswordRequired, e)
    } else if wordcraft_engine::io::wrong_password(&e) {
        Error::new(ErrorCode::WrongPassword, e)
    } else {
        Error::new(ErrorCode::LoadFailed, e)
    }
}

fn export(doc: wordcraft_doc::Document, opts: &ConvertOptions) -> Result<Conversion, Error> {
    let mut session = wordcraft_engine::Session::new(doc);
    if opts.update_fields {
        session
            .run("references.updateFields", &serde_json::json!({}))
            .map_err(|e| Error::new(ErrorCode::ExportFailed, format!("updating fields: {e}")))?;
    }
    let doc = &session.doc;
    let mut diagnostics = diagnostics::font_substitutions(doc);

    // The same layout wordcraft_pdf::export makes, done here to know the page count first.
    let markup = opts.include_markup;
    let lo = LayoutOptions { hide_deleted: !markup, comment_balloons: markup && opts.include_comments, numeral: NumeralMode::Arabic, ..Default::default() };
    let lay = wordcraft_layout::layout(doc, &mut LayoutCache::new(), &lo);
    let total = lay.pages.len();
    let pages = opts.page_range.as_deref().map(|r| options::parse_page_range(r, total)).transpose()?;
    let page_count = pages.as_ref().map_or(total, Vec::len);

    let pdf_opts = wordcraft_pdf::PdfOptions {
        pages,
        title: opts.title.clone(),
        author: opts.author.clone(),
        tagged: opts.tagged_pdf,
        include_markup: markup,
        comments: markup && opts.include_comments,
        ..Default::default()
    };
    let pdf = wordcraft_pdf::export_layout(doc, &lay, &pdf_opts).map_err(|e| Error::new(ErrorCode::ExportFailed, e.to_string()))?;
    if total == 0 {
        diagnostics.push(Diagnostic::general(Severity::Warning, "the document has no pages"));
    }
    Ok(Conversion { pdf, page_count, diagnostics })
}

/// Write `bytes` to a temporary file beside `path`, then rename it over `path`, so a reader never
/// sees a half-written PDF and a failed conversion leaves an existing file untouched.
fn write_atomically(path: &Path, bytes: &[u8]) -> Result<(), Error> {
    static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    let n = COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| "output.pdf".into());
    let tmp = path.with_file_name(format!(".{name}.{}.{n}.tmp", std::process::id()));
    let r = std::fs::write(&tmp, bytes).and_then(|()| std::fs::rename(&tmp, path));
    if let Err(e) = r {
        let _ = std::fs::remove_file(&tmp);
        return Err(Error::io(path, &e, ErrorCode::ExportFailed));
    }
    Ok(())
}
