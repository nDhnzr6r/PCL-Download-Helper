"""程序入口。"""

from __future__ import annotations

import ctypes
import os
import sys


def main() -> None:
    _enable_dpi_awareness()
    from .ui.window import run

    sys.excepthook = _report
    run()


def _enable_dpi_awareness() -> None:
    if os.name != "nt":
        return
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


def _report(exc_type, exc_value, exc_tb) -> None:
    import traceback

    text = "".join(traceback.format_exception(exc_type, exc_value, exc_tb))
    sys.stderr.write(text)
    try:
        from .core.config import app_dir

        (app_dir() / "error.log").write_text(text, encoding="utf-8")
    except Exception:
        pass


if __name__ == "__main__":
    main()
