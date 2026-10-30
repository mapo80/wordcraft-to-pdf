use crate::{Error, ErrorCode};

/// Input formats. The values are part of the wire format: never renumber them.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i32)]
pub enum Format {
    /// Detect from the file extension (file conversion) or the content (buffer conversion).
    Unknown = 0,
    Docx = 1,
    Doc = 2,
    Odt = 3,
    Rtf = 4,
    Html = 5,
    Markdown = 6,
    Text = 7,
}

impl Format {
    pub fn from_i32(v: i32) -> Option<Format> {
        Some(match v {
            0 => Format::Unknown,
            1 => Format::Docx,
            2 => Format::Doc,
            3 => Format::Odt,
            4 => Format::Rtf,
            5 => Format::Html,
            6 => Format::Markdown,
            7 => Format::Text,
            _ => return None,
        })
    }

    /// The extension WordCraft picks its reader by.
    pub(crate) fn extension(self) -> &'static str {
        match self {
            Format::Unknown | Format::Docx => "docx",
            Format::Doc => "doc",
            Format::Odt => "odt",
            Format::Rtf => "rtf",
            Format::Html => "html",
            Format::Markdown => "md",
            Format::Text => "txt",
        }
    }

    /// The format of a file name's extension (case-insensitive), `None` when it isn't one we
    /// convert. Word's macro-enabled and template packages read as DOCX.
    pub fn from_extension(name: &str) -> Option<Format> {
        let file = name.rsplit(['/', '\\']).next().unwrap_or(name);
        let (_, ext) = file.rsplit_once('.')?;
        Some(match ext.to_ascii_lowercase().as_str() {
            "docx" | "docm" | "dotx" | "dotm" => Format::Docx,
            "doc" | "dot" => Format::Doc,
            "odt" => Format::Odt,
            "rtf" => Format::Rtf,
            "html" | "htm" => Format::Html,
            "md" | "markdown" => Format::Markdown,
            "txt" => Format::Text,
            _ => return None,
        })
    }

    /// The format of a document from its first bytes, for the formats with a signature.
    pub fn sniff(bytes: &[u8]) -> Option<Format> {
        const ZIP: &[u8] = b"PK\x03\x04";
        const OLE: &[u8] = &[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        if bytes.starts_with(ZIP) {
            // An ODF package names its type in a stored `mimetype` entry right after the header.
            let head = bytes.get(..128).unwrap_or(bytes);
            let odt = head.windows(39).any(|w| w == b"application/vnd.oasis.opendocument.text");
            return Some(if odt { Format::Odt } else { Format::Docx });
        }
        if bytes.starts_with(OLE) {
            return Some(Format::Doc);
        }
        if bytes.starts_with(b"{\\rtf") {
            return Some(Format::Rtf);
        }
        None
    }

    /// `hint`, or else the format sniffed from `bytes`.
    pub(crate) fn resolve_for_bytes(hint: Format, bytes: &[u8]) -> Result<Format, Error> {
        match hint {
            Format::Unknown => Format::sniff(bytes).ok_or_else(|| {
                Error::new(ErrorCode::InvalidFormat, "could not detect the document format: pass a format hint")
            }),
            f => Ok(f),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn extensions_map_to_formats() {
        assert_eq!(Format::from_extension("/a/b/Report.DOCX"), Some(Format::Docx));
        assert_eq!(Format::from_extension("C:\\x\\t.dotm"), Some(Format::Docx));
        assert_eq!(Format::from_extension("legacy.doc"), Some(Format::Doc));
        assert_eq!(Format::from_extension("notes.markdown"), Some(Format::Markdown));
        assert_eq!(Format::from_extension("sheet.xlsx"), None);
        assert_eq!(Format::from_extension("no_extension"), None);
        assert_eq!(Format::from_extension("dir.docx/file"), None);
    }

    #[test]
    fn signatures_are_sniffed() {
        assert_eq!(Format::sniff(b"PK\x03\x04rest"), Some(Format::Docx));
        assert_eq!(Format::sniff(b"{\\rtf1\\ansi"), Some(Format::Rtf));
        assert_eq!(Format::sniff(&[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0]), Some(Format::Doc));
        assert_eq!(Format::sniff(b"hello"), None);
        let mut odt = b"PK\x03\x04".to_vec();
        odt.extend_from_slice(&[0; 26]);
        odt.extend_from_slice(b"mimetypeapplication/vnd.oasis.opendocument.text");
        assert_eq!(Format::sniff(&odt), Some(Format::Odt));
    }
}
