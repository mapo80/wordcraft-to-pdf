use std::cell::RefCell;
use std::collections::BTreeSet;

use serde::Serialize;
use wordcraft_doc::Document;

/// Most diagnostics reported for one conversion; later ones are dropped.
const MAX_DIAGNOSTICS: usize = 256;
/// Longest diagnostic message kept, in characters.
const MAX_MESSAGE_CHARS: usize = 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Severity {
    Info,
    Warning,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Category {
    Font,
    General,
}

/// Something worth knowing about a conversion that still succeeded.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct Diagnostic {
    pub severity: Severity,
    pub category: Category,
    pub message: String,
    /// The font the document asked for (font diagnostics).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub font: Option<String>,
    /// The font used instead (font substitutions).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub substituted_with: Option<String>,
}

impl Diagnostic {
    pub(crate) fn general(severity: Severity, message: &str) -> Self {
        Diagnostic { severity, category: Category::General, message: message.chars().take(MAX_MESSAGE_CHARS).collect(), font: None, substituted_with: None }
    }
}

/// A warning for each font the document asks for that isn't available, naming its substitute.
pub(crate) fn font_substitutions(doc: &Document) -> Vec<Diagnostic> {
    let mut families = BTreeSet::new();
    // The document model is serialisable; walking its JSON finds every font reference (runs,
    // styles, defaults, symbols) without depending on the shape of WordCraft's internal types.
    if let Ok(json) = serde_json::to_value(doc) {
        collect_fonts(&json, &mut families, 0);
    }
    families
        .into_iter()
        .filter_map(|family| {
            let resolved = wordcraft_fonts::word::resolve(&family, false, false);
            resolved.substituted.then(|| {
                let used = resolved.face.get().family.clone();
                Diagnostic {
                    severity: Severity::Warning,
                    category: Category::Font,
                    message: format!("font \"{family}\" is not available; substituted with \"{used}\""),
                    font: Some(family),
                    substituted_with: Some(used),
                }
            })
        })
        .take(MAX_DIAGNOSTICS)
        .collect()
}

const MAX_DEPTH: usize = 256;
const MAX_FAMILIES: usize = 512;

fn collect_fonts(v: &serde_json::Value, out: &mut BTreeSet<String>, depth: usize) {
    if depth > MAX_DEPTH || out.len() >= MAX_FAMILIES {
        return;
    }
    match v {
        serde_json::Value::Object(map) => {
            for (key, value) in map {
                if let serde_json::Value::String(name) = value
                    && (key == "font" || key.ends_with("Font"))
                {
                    let name = name.trim();
                    // `+mj-lt`-style names are theme references, resolved elsewhere.
                    if !name.is_empty() && !name.starts_with('+') && name.len() <= 256 {
                        out.insert(name.to_string());
                    }
                } else {
                    collect_fonts(value, out, depth + 1);
                }
            }
        }
        serde_json::Value::Array(items) => items.iter().for_each(|i| collect_fonts(i, out, depth + 1)),
        _ => {}
    }
}

// ---------------------------------------------------------------------------------------------
// Log capture: WordCraft reports what an export had to leave out (unreadable pictures, fonts it
// couldn't embed…) through the `log` crate. While a conversion runs on a thread, its warnings
// are collected as general diagnostics.

thread_local! {
    static CAPTURED: RefCell<Option<Vec<Diagnostic>>> = const { RefCell::new(None) };
}

struct CaptureLogger;

static LOGGER: CaptureLogger = CaptureLogger;

impl log::Log for CaptureLogger {
    fn enabled(&self, metadata: &log::Metadata) -> bool {
        metadata.level() <= log::Level::Warn
    }

    fn log(&self, record: &log::Record) {
        if !self.enabled(record.metadata()) {
            return;
        }
        let severity = if record.level() == log::Level::Error { Severity::Warning } else { Severity::Info };
        // try_borrow_mut: a logger must never panic, even if something logs while we push.
        CAPTURED.with(|c| {
            if let Ok(mut c) = c.try_borrow_mut()
                && let Some(v) = c.as_mut()
                && v.len() < MAX_DIAGNOSTICS
            {
                v.push(Diagnostic::general(severity, &record.args().to_string()));
            }
        });
    }

    fn flush(&self) {}
}

/// Route `log` records to the capture (once per process; a no-op when the host program
/// already installed its own logger).
pub(crate) fn install_logger() {
    static ONCE: std::sync::Once = std::sync::Once::new();
    ONCE.call_once(|| {
        if log::set_logger(&LOGGER).is_ok() {
            log::set_max_level(log::LevelFilter::Warn);
        }
    });
}

/// Run `f`, returning what it logged on this thread as diagnostics.
pub(crate) fn capture<R>(f: impl FnOnce() -> R) -> (R, Vec<Diagnostic>) {
    install_logger();
    CAPTURED.with(|c| {
        if let Ok(mut c) = c.try_borrow_mut() {
            *c = Some(Vec::new());
        }
    });
    let r = f();
    let logged = CAPTURED.with(|c| c.try_borrow_mut().ok().and_then(|mut c| c.take())).unwrap_or_default();
    (r, logged)
}
