# File-manager key bindings

The `keys` object in `config.json` controls `help`, `rename`, `copy`, `move`, `mkdir`, `actions`, and `delete`. Omitted actions use their defaults. The active key map generates both the status bar and Help; changes take effect when a file-manager window is created.

Defaults: Help F1, Rename F2, Copy F5, Move F6, New directory F7, Actions F9, Delete DeleteChar. Alt+A always opens Actions without function keys. In a path prompt, Tab completes the path and Shift+Tab changes focus.

For example, `"copy": "F8"` or `"copy": "Ctrl+Shift+X"`. Existing enum-style `"CtrlMask, ShiftMask, X"` is also accepted. Function keys F1-F12, DeleteChar, InsertChar, and modified named letters/digits are supported. Unmodified typing/navigation keys, Alt+A, and Ctrl+Q cannot be reassigned here.

Unknown actions, invalid key names and duplicate bindings are reported as configuration errors rather than silently falling back to another command. Fix the `keys` object to enter the file manager; validation does not rewrite the file. A custom binding must not conflict with another action's configured or default key.
