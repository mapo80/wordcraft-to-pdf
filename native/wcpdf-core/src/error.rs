use std::fmt;

/// Error codes shared by the C API, the worker protocol and the SDKs. The values are part of the
/// wire format: never renumber them.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i32)]
pub enum ErrorCode {
    InitFailed = 1,
    LoadFailed = 2,
    ExportFailed = 3,
    InvalidFormat = 4,
    FileNotFound = 5,
    OutOfMemory = 6,
    PermissionDenied = 7,
    NotInitialized = 9,
    InvalidArgument = 10,
    PasswordRequired = 11,
    WrongPassword = 12,
    Unknown = 99,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Error {
    pub code: ErrorCode,
    pub message: String,
}

impl Error {
    pub fn new(code: ErrorCode, message: impl Into<String>) -> Self {
        Error { code, message: message.into() }
    }

    /// An I/O error on `path`, mapped to the closest code.
    pub(crate) fn io(path: &std::path::Path, e: &std::io::Error, fallback: ErrorCode) -> Self {
        let code = match e.kind() {
            std::io::ErrorKind::NotFound => ErrorCode::FileNotFound,
            std::io::ErrorKind::PermissionDenied => ErrorCode::PermissionDenied,
            std::io::ErrorKind::OutOfMemory => ErrorCode::OutOfMemory,
            _ => fallback,
        };
        Error::new(code, format!("{}: {e}", path.display()))
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for Error {}
