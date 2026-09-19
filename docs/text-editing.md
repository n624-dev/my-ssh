# Built-in text editing

The built-in editor accepts strict UTF-8 (with or without a BOM), UTF-16 LE/BE with a BOM, and UTF-32 LE/BE with a BOM. It preserves the original encoding and preamble when saving. BOM-less files are treated as UTF-8 rather than guessing a legacy encoding; use a configured external editor for other formats.

The editor works with normalized line breaks internally. A save without content changes preserves the original bytes exactly, including mixed line endings and a missing final newline. Line edits retain existing terminators; inserted or replaced regions use the most frequent original terminator, while unchanged prefix/suffix lines retain their own endings. A document without any line breaks uses LF for newly inserted lines.

Invalid Unicode, undecodable source data and binary NUL content are rejected without replacing the previous draft. Source text reads are bounded to 4 MiB. Recovery drafts remain on disk until a successful upload or explicit discard, and the existing conflict and permission protections still apply.

External editors continue to control their own file encoding; their output is uploaded as bytes rather than passing through the built-in codec.
