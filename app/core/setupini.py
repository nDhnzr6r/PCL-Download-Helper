"""Setup.ini 的读取与写回，保留原有键顺序与换行风格。"""

from __future__ import annotations

import os
from pathlib import Path


class SetupIni:
    def __init__(self, path: Path):
        self.path = Path(path)
        self.bom = False
        self.lines: list[str] = []
        self.values: dict[str, str] = {}
        if self.path.exists():
            raw = self.path.read_bytes()
            self.bom = raw.startswith(b"\xef\xbb\xbf")
            text = raw.decode("utf-8-sig", errors="replace")
            self.newline = "\r\n" if "\r\n" in text else "\n"
            self.lines = text.split(self.newline) if text else []
        else:
            self.newline = "\r\n"
        for line in self.lines:
            key, sep, value = line.partition(":")
            if sep and key.strip():
                self.values[key.strip()] = value

    def get(self, key: str, default: str | None = None) -> str | None:
        return self.values.get(key, default)

    def set(self, key: str, value: str) -> None:
        value = str(value)
        self.values[key] = value
        for index, line in enumerate(self.lines):
            name, sep, _ = line.partition(":")
            if sep and name.strip() == key:
                self.lines[index] = f"{name}:{value}"
                return
        if self.lines and self.lines[-1] == "":
            self.lines.insert(len(self.lines) - 1, f"{key}:{value}")
        else:
            self.lines.append(f"{key}:{value}")

    def save(self) -> None:
        text = self.newline.join(self.lines)
        data = text.encode("utf-8")
        if self.bom:
            data = b"\xef\xbb\xbf" + data
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_name(self.path.name + ".tmp")
        temp.write_bytes(data)
        os.replace(temp, self.path)
