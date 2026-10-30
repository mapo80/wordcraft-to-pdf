use serde::Deserialize;

use crate::{Error, ErrorCode};

/// Conversion options. Every field defaults to off / unset.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(default)]
pub struct ConvertOptions {
    /// 1-based pages to export, e.g. `"1-3,5,8-"` (`8-` = page 8 to the end). `None` = all.
    pub page_range: Option<String>,
    /// Password of an encrypted document.
    pub password: Option<String>,
    /// Write a structure tree (tagged PDF, best effort).
    pub tagged_pdf: bool,
    /// Refresh unlocked fields (table of contents, page references…) before export.
    pub update_fields: bool,
    /// Show tracked changes as markup instead of the final text.
    pub include_markup: bool,
    /// With `include_markup`, show the comments in balloons beside each page.
    pub include_comments: bool,
    /// PDF title (default: the document's title property).
    pub title: Option<String>,
    /// PDF author (default: the document's author property).
    pub author: Option<String>,
}

/// The longest page range text accepted.
const MAX_RANGE_LEN: usize = 4096;

/// Parse a 1-based page range against a document of `total` pages into 0-based page indexes,
/// in the order given.
pub(crate) fn parse_page_range(range: &str, total: usize) -> Result<Vec<usize>, Error> {
    let bad = |why: String| Error::new(ErrorCode::InvalidArgument, format!("invalid page range {range:?}: {why}"));
    if range.len() > MAX_RANGE_LEN {
        return Err(bad("too long".into()));
    }
    let page = |s: &str| -> Result<usize, Error> {
        let n: usize = s.trim().parse().map_err(|_| bad(format!("{:?} is not a page number", s.trim())))?;
        if n == 0 {
            return Err(bad("pages are numbered from 1".into()));
        }
        if n > total {
            return Err(bad(format!("page {n} does not exist (the document has {total})")));
        }
        Ok(n - 1)
    };
    let mut pages = Vec::new();
    for part in range.split(',').map(str::trim) {
        if part.is_empty() {
            return Err(bad("empty item".into()));
        }
        match part.split_once('-') {
            None => pages.push(page(part)?),
            Some((from, to)) => {
                let first = if from.trim().is_empty() { 0 } else { page(from)? };
                let last = if to.trim().is_empty() { total.saturating_sub(1) } else { page(to)? };
                if first > last {
                    return Err(bad(format!("{part:?} runs backwards")));
                }
                pages.extend(first..=last);
            }
        }
    }
    Ok(pages)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ranges_parse_to_zero_based_pages() {
        assert_eq!(parse_page_range("1", 5).ok(), Some(vec![0]));
        assert_eq!(parse_page_range("1-3, 5", 5).ok(), Some(vec![0, 1, 2, 4]));
        assert_eq!(parse_page_range("4-", 5).ok(), Some(vec![3, 4]));
        assert_eq!(parse_page_range("-2", 5).ok(), Some(vec![0, 1]));
        assert_eq!(parse_page_range("3,1", 5).ok(), Some(vec![2, 0]));
    }

    #[test]
    fn bad_ranges_are_invalid_arguments() {
        for r in ["", "0", "6", "3-2", "a", "1,,2", "1-x", "2-9"] {
            let e = parse_page_range(r, 5).err();
            assert_eq!(e.map(|e| e.code), Some(ErrorCode::InvalidArgument), "{r:?}");
        }
    }
}
