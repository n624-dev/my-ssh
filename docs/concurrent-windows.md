# Multiple windows

File-manager windows may use the same connection simultaneously. Their last changed and saved browser view becomes the view restored next time. Paths, selections, marked files, filters, sorting, hidden-file visibility and the active pane are saved together. An unchanged window saving again or closing does not replace another window's saved view, and other connections retain their own state.

Server configuration and bookmarks retain conflict detection: independently changed fields are merged, while incompatible changes to the same setting are rejected without overwriting the file. Transfer checkpoints and their ownership locks are separate from browser view state.
