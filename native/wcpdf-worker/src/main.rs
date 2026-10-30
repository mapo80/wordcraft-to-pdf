//! `wcpdf_worker`: out-of-process WordCraftPdf conversions for the .NET and Java SDKs.
//!
//! Reads length-prefixed JSON commands on stdin and writes length-prefixed JSON responses on
//! stdout (see docs/protocol.md). Each frame is a 4-byte little-endian length, then the payload.
//! Buffer conversions follow their JSON frame with a binary frame (document in, PDF out).
//!
//! Lifecycle: `init` (fonts) → any number of `convert` / `convert_buffer` → `quit` or EOF.
#![forbid(unsafe_code)]

use std::io::{self, BufReader, BufWriter, Read, Write};

use serde::Deserialize;
use serde_json::{Value, json};
use wcpdf_core::{ConvertOptions, Diagnostic, Error, ErrorCode, Format};

/// Largest frame accepted, in bytes (documents travel in frames in buffer mode).
const MAX_FRAME: u32 = 512 * 1024 * 1024;

enum Frame {
    Data(Vec<u8>),
    Eof,
    TooLarge(u32),
}

fn read_frame(r: &mut impl Read) -> io::Result<Frame> {
    let mut len = [0u8; 4];
    match r.read_exact(&mut len) {
        Ok(()) => {}
        Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => return Ok(Frame::Eof),
        Err(e) => return Err(e),
    }
    let len = u32::from_le_bytes(len);
    if len > MAX_FRAME {
        return Ok(Frame::TooLarge(len));
    }
    let mut buf = vec![0u8; len as usize];
    r.read_exact(&mut buf)?;
    Ok(Frame::Data(buf))
}

fn write_frame(w: &mut impl Write, payload: &[u8]) -> io::Result<()> {
    let len = u32::try_from(payload.len()).map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "frame larger than 4 GiB"))?;
    w.write_all(&len.to_le_bytes())?;
    w.write_all(payload)?;
    w.flush()
}

fn send(w: &mut impl Write, v: &Value) -> io::Result<()> {
    write_frame(w, v.to_string().as_bytes())
}

#[derive(Deserialize)]
struct Request {
    #[serde(rename = "type")]
    kind: String,
    #[serde(default)]
    id: i64,
    #[serde(default)]
    font_paths: Vec<String>,
    input: Option<String>,
    output: Option<String>,
    #[serde(default)]
    format: i32,
    #[serde(default)]
    options: Option<ConvertOptions>,
    data_size: Option<u64>,
}

fn result_json(kind: &str, id: i64, r: &Result<wcpdf_core::Conversion, Error>, extra: Option<(&str, Value)>) -> Value {
    let mut v = match r {
        Ok(c) => json!({
            "type": kind, "id": id, "success": true,
            "error_code": null, "error_message": null,
            "page_count": c.page_count, "diagnostics": c.diagnostics,
        }),
        Err(e) => json!({
            "type": kind, "id": id, "success": false,
            "error_code": e.code as i32, "error_message": e.message,
            "diagnostics": Vec::<Diagnostic>::new(),
        }),
    };
    if let (Some((k, x)), Some(o)) = (extra, v.as_object_mut()) {
        o.insert(k.into(), x);
    }
    v
}

fn format_of(v: i32) -> Result<Format, Error> {
    Format::from_i32(v).ok_or_else(|| Error::new(ErrorCode::InvalidFormat, format!("unknown format {v}")))
}

#[cfg(feature = "mimalloc")]
#[global_allocator]
static GLOBAL: mimalloc::MiMalloc = mimalloc::MiMalloc;

/// Stack for the conversion thread: hostile documents nest deeply, and a stack overflow aborts
/// the process instead of unwinding. It is address space reserved, not memory used.
const STACK_BYTES: usize = 256 * 1024 * 1024;

/// Pipe buffers sized for whole documents in buffer mode.
const IO_BUFFER: usize = 1024 * 1024;

fn main() -> std::process::ExitCode {
    let worker = std::thread::Builder::new().name("wcpdf-worker".into()).stack_size(STACK_BYTES).spawn(|| {
        let mut input = BufReader::with_capacity(IO_BUFFER, io::stdin().lock());
        let mut output = BufWriter::with_capacity(IO_BUFFER, io::stdout().lock());
        run(&mut input, &mut output)
    });
    let outcome = match worker {
        Ok(handle) => handle.join().unwrap_or_else(|_| Err(io::Error::other("worker thread panicked"))),
        Err(e) => Err(e),
    };
    match outcome {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("wcpdf_worker: {e}");
            std::process::ExitCode::FAILURE
        }
    }
}

fn run(input: &mut impl Read, out: &mut impl Write) -> io::Result<()> {
    let mut initialized = false;
    loop {
        let raw = match read_frame(input)? {
            Frame::Data(d) => d,
            Frame::Eof => return Ok(()),
            Frame::TooLarge(n) => {
                // The stream can't be resynchronised past a frame we won't read: report and stop.
                send(out, &json!({"type": "error", "message": format!("frame of {n} bytes exceeds the {MAX_FRAME}-byte limit")}))?;
                return Ok(());
            }
        };
        let req: Request = match serde_json::from_slice(&raw) {
            Ok(r) => r,
            Err(e) => {
                send(out, &json!({"type": "error", "message": format!("invalid message: {e}")}))?;
                continue;
            }
        };
        match req.kind.as_str() {
            "init" => {
                let diagnostics = wcpdf_core::add_font_dirs(&req.font_paths);
                wcpdf_core::warm_up();
                initialized = true;
                send(out, &json!({"type": "ready", "version": wcpdf_core::version(), "diagnostics": diagnostics}))?;
            }
            "convert" => {
                let r = if !initialized {
                    Err(Error::new(ErrorCode::NotInitialized, "worker not initialised: send init first"))
                } else {
                    match (&req.input, &req.output) {
                        (Some(i), Some(o)) => format_of(req.format).and_then(|f| {
                            wcpdf_core::convert_file(i.as_ref(), o.as_ref(), f, &req.options.clone().unwrap_or_default())
                        }),
                        _ => Err(Error::new(ErrorCode::InvalidArgument, "missing input or output path")),
                    }
                };
                send(out, &result_json("result", req.id, &r, None))?;
            }
            "convert_buffer" => {
                // The document frame always follows, even when the request is refused, so the
                // stream stays in step: read it first.
                let doc = match read_frame(input)? {
                    Frame::Data(d) => d,
                    Frame::Eof => return Ok(()),
                    Frame::TooLarge(n) => {
                        // Unread payload: the stream is out of step, so answer and stop.
                        let e = Error::new(ErrorCode::InvalidArgument, format!("document of {n} bytes exceeds the {MAX_FRAME}-byte limit"));
                        send(out, &result_json("buffer_result", req.id, &Err(e), None))?;
                        return Ok(());
                    }
                };
                let r = if !initialized {
                    Err(Error::new(ErrorCode::NotInitialized, "worker not initialised: send init first"))
                } else if req.data_size.is_some_and(|n| n != doc.len() as u64) {
                    Err(Error::new(ErrorCode::InvalidArgument, "data frame size mismatch"))
                } else {
                    format_of(req.format)
                        .and_then(|f| wcpdf_core::convert_bytes(&doc, f, &req.options.clone().unwrap_or_default()))
                        .and_then(|c| {
                            // Larger than the SDKs accept as a frame: refuse cleanly instead of
                            // sending a frame they'd treat as a broken stream.
                            if c.pdf.len() as u64 > u64::from(MAX_FRAME) {
                                Err(Error::new(ErrorCode::ExportFailed, format!("the PDF ({} bytes) exceeds the {MAX_FRAME}-byte frame limit; use file conversion", c.pdf.len())))
                            } else {
                                Ok(c)
                            }
                        })
                };
                let size = r.as_ref().ok().map(|c| json!(c.pdf.len()));
                send(out, &result_json("buffer_result", req.id, &r, size.map(|s| ("data_size", s))))?;
                if let Ok(c) = &r {
                    write_frame(out, &c.pdf)?;
                }
            }
            "quit" => return Ok(()),
            other => send(out, &json!({"type": "error", "id": req.id, "message": format!("unknown message type {other:?}")}))?,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame(payload: &[u8]) -> Vec<u8> {
        let mut v = (payload.len() as u32).to_le_bytes().to_vec();
        v.extend_from_slice(payload);
        v
    }

    fn responses(mut bytes: &[u8]) -> Vec<Vec<u8>> {
        let mut out = Vec::new();
        while let Ok(Frame::Data(d)) = read_frame(&mut bytes) {
            out.push(d);
        }
        out
    }

    fn json_of(b: &[u8]) -> Value {
        serde_json::from_slice(b).unwrap_or(Value::Null)
    }

    #[test]
    fn convert_before_init_is_refused_and_unknown_types_answered() {
        let mut input = frame(br#"{"type":"convert","id":7,"input":"a.docx","output":"a.pdf"}"#);
        input.extend(frame(br#"{"type":"bogus"}"#));
        input.extend(frame(br#"not json"#));
        input.extend(frame(br#"{"type":"quit"}"#));
        let mut out = Vec::new();
        assert!(run(&mut input.as_slice(), &mut out).is_ok());
        let r = responses(&out);
        assert_eq!(r.len(), 3);
        let first = json_of(&r[0]);
        assert_eq!(first["id"], 7);
        assert_eq!(first["success"], false);
        assert_eq!(first["error_code"], ErrorCode::NotInitialized as i32);
        assert_eq!(json_of(&r[1])["type"], "error");
        assert_eq!(json_of(&r[2])["type"], "error");
    }

    #[test]
    fn a_refused_buffer_request_still_consumes_its_data_frame() {
        let mut input = frame(br#"{"type":"init"}"#);
        input.extend(frame(br#"{"type":"convert_buffer","id":1,"format":1,"data_size":99}"#));
        input.extend(frame(b"not 99 bytes"));
        input.extend(frame(br#"{"type":"bogus"}"#));
        let mut out = Vec::new();
        assert!(run(&mut input.as_slice(), &mut out).is_ok());
        let r = responses(&out);
        assert_eq!(json_of(&r[0])["type"], "ready");
        assert_eq!(json_of(&r[1])["error_code"], ErrorCode::InvalidArgument as i32);
        // The next frame was read as a message, not as leftover document bytes.
        assert_eq!(json_of(&r[2])["type"], "error");
    }
}
