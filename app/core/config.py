"""应用配置，保存在当前用户的本地应用数据目录。"""

from __future__ import annotations

import json
import os
from pathlib import Path

APP_DIR_NAME = "PCLDownloadHelper"


def app_dir() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    path = Path(base) / APP_DIR_NAME
    path.mkdir(parents=True, exist_ok=True)
    return path


class Config:
    def __init__(self, name: str = "config.json"):
        self.path = app_dir() / name
        self.data: dict = {}
        try:
            loaded = json.loads(self.path.read_text("utf-8"))
            if isinstance(loaded, dict):
                self.data = loaded
        except (OSError, ValueError):
            self.data = {}

    def get(self, key: str, default=None):
        return self.data.get(key, default)

    def set(self, key: str, value) -> None:
        if self.data.get(key) == value:
            return
        self.data[key] = value
        self.save()

    def save(self) -> None:
        try:
            temp = self.path.with_name(self.path.name + ".tmp")
            temp.write_text(json.dumps(self.data, ensure_ascii=False, indent=2), "utf-8")
            os.replace(temp, self.path)
        except OSError:
            pass
