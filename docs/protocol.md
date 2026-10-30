# Worker protocol

`wcpdf_worker` is the process the .NET and Java SDKs run conversions in. The SDKs never load
native code into their own process: a crash, abort or hang in the engine kills one worker, never
the host runtime. The SDK reports a failed conversion and starts a fresh worker for the next one.

## Transport

- The SDK starts `wcpdf_worker` with stdin, stdout and stderr redirected (no arguments).
- stdin carries requests, stdout carries responses. stdout carries nothing but frames.
- stderr is free-form text for humans (startup failures); the SDK drains it continuously so the
  worker never blocks on a full pipe, and keeps the tail for error messages.
- Every message is a **frame**: a 4-byte little-endian unsigned length, then that many bytes.
  JSON frames are UTF-8. The largest frame either side accepts is 512 MiB.
- One request is in flight per worker at a time; the worker answers in order.

## Lifecycle

1. `init` → `ready` (or `error`). The worker loads the font folders, scans the system fonts and
   runs one tiny conversion, so the first real conversion runs at full speed.
2. Any number of `convert` / `convert_buffer`.
3. `quit`, or closing stdin: the worker exits with status 0.

A conversion sent before `init` fails with `error_code` 9 (not initialised).

## Requests (SDK → worker)

### `init`

```json
{"type": "init", "font_paths": ["/opt/fonts", "/srv/brand-fonts"]}
```

`font_paths` (optional): folders whose fonts (and their subfolders') are used ahead of the
installed fonts.

### `convert` — file paths, the worker reads and writes the files

```json
{"type": "convert", "id": 1, "input": "/abs/in.docx", "output": "/abs/out.pdf",
 "format": 0, "options": { ... }}
```

The output is written atomically (temporary file, then rename). `format` 0 = from the
extension. Send absolute paths (here and in `font_paths`): the worker's working directory is
unspecified.

### `convert_buffer` — bytes over the pipe, no temporary files

```json
{"type": "convert_buffer", "id": 2, "format": 1, "data_size": 48213, "options": { ... }}
```

followed by **one binary frame** with the document (`data_size` bytes). The frame is always
sent, even if the SDK expects a refusal, so the stream stays in step. `format` 0 sniffs the
content (DOCX, DOC, ODT, RTF); HTML, Markdown and text need an explicit format.

### `quit`

```json
{"type": "quit"}
```

### `options` (all optional)

| Field | Type | Default | Meaning |
|---|---|---|---|
| `page_range` | string | all | 1-based pages, e.g. `"1-3,5,8-"` |
| `password` | string | none | password of an encrypted document |
| `tagged_pdf` | bool | false | write a structure tree (accessibility) |
| `update_fields` | bool | false | refresh TOC, page references… before export |
| `include_markup` | bool | false | show tracked changes as markup |
| `include_comments` | bool | false | with markup, comments in balloons |
| `title` | string | document's | PDF title |
| `author` | string | document's | PDF author |

## Responses (worker → SDK)

### `ready`

```json
{"type": "ready", "version": "WordCraftPdf 0.1.0 (WordCraft v0.12.0)", "diagnostics": [...]}
```

`diagnostics` lists font folders or files that couldn't be used.

### `result` (answers `convert`) and `buffer_result` (answers `convert_buffer`)

Success:

```json
{"type": "result", "id": 1, "success": true, "error_code": null, "error_message": null,
 "page_count": 14, "diagnostics": [...]}
```

`buffer_result` on success also has `"data_size": <bytes>` and is followed by **one binary
frame** with the PDF. A PDF larger than the 512 MiB frame limit is refused with `success: false`
and error code 3 (use file conversion for such documents).

Failure (no PDF frame follows):

```json
{"type": "result", "id": 1, "success": false, "error_code": 2,
 "error_message": "…", "diagnostics": []}
```

### `error`

```json
{"type": "error", "id": 3, "message": "…"}
```

Sent for a malformed frame, an unknown `type`, or a failed `init`. `id` echoes the request's
when it could be read (absent for a frame that isn't valid JSON). If the worker can't stay in
step with the stream (an oversized frame), it sends `error` and exits.

### Diagnostics

```json
{"severity": "warning", "category": "font",
 "message": "font \"Calibri\" is not available; substituted with \"Carlito\"",
 "font": "Calibri", "substituted_with": "Carlito"}
```

`severity`: `info` | `warning`. `category`: `font` | `general`. `font` and `substituted_with`
appear on font substitutions only.

## Codes

`format`: 0 unknown, 1 DOCX, 2 DOC, 3 ODT, 4 RTF, 5 HTML, 6 Markdown, 7 plain text.

`error_code`: 1 init failed, 2 load failed, 3 export failed, 4 invalid format, 5 file not found,
6 out of memory, 7 permission denied, 9 not initialised, 10 invalid argument, 11 password
required, 12 wrong password, 99 unknown (includes engine panics, caught by the worker).

When the worker *process* dies mid-conversion (abort, stack overflow, killed), the SDK sees end of
stream; it reports a failed conversion (SDKs use `WorkerCrashed` = 100, and `Timeout` = 101 when
it killed a worker that exceeded the conversion timeout) and replaces the worker.
