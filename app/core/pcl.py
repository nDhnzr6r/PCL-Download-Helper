"""PCL 启动器定位、版本识别与设置修复。"""

from __future__ import annotations

import ctypes
import os
import string
import time
from ctypes import wintypes
from dataclasses import dataclass
from pathlib import Path

from .setupini import SetupIni

# 关闭游戏下载功能的起始版本
RESTRICTED_FROM = (2, 99, 0, 0)
UPDATE_SETTING_KEY = "SystemSystemUpdate"
UPDATE_DISABLED = "3"
LAUNCHER_NAMES = ("plain craft launcher 2.exe", "plain craft launcher.exe", "pcl.exe", "pcl2.exe")
SKIP_DIRS = {
    "windows",
    "windows.old",
    "program files",
    "program files (x86)",
    "programdata",
    "$recycle.bin",
    "system volume information",
    "recovery",
    "perflogs",
    "msocache",
    "appdata",
    "node_modules",
}

_ver = ctypes.windll.version if os.name == "nt" else None
if _ver is not None:
    _ver.GetFileVersionInfoSizeW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD)]
    _ver.GetFileVersionInfoSizeW.restype = wintypes.DWORD
    _ver.GetFileVersionInfoW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
    _ver.GetFileVersionInfoW.restype = wintypes.BOOL
    _ver.VerQueryValueW.argtypes = [
        ctypes.c_void_p,
        wintypes.LPCWSTR,
        ctypes.POINTER(ctypes.c_void_p),
        ctypes.POINTER(wintypes.UINT),
    ]
    _ver.VerQueryValueW.restype = wintypes.BOOL


def read_version(path: Path) -> tuple[int, int, int, int] | None:
    """读取可执行文件的版本号。"""
    if _ver is None:
        return None
    try:
        path_str = str(path)
        size = _ver.GetFileVersionInfoSizeW(path_str, None)
        if not size:
            return None
        buffer = ctypes.create_string_buffer(size)
        if not _ver.GetFileVersionInfoW(path_str, 0, size, buffer):
            return None
        pointer = ctypes.c_void_p()
        length = wintypes.UINT()
        if not _ver.VerQueryValueW(buffer, "\\", ctypes.byref(pointer), ctypes.byref(length)):
            return None
        info = ctypes.cast(pointer, ctypes.POINTER(ctypes.c_uint32))
        version_ms, version_ls = info[2], info[3]
        return (version_ms >> 16, version_ms & 0xFFFF, version_ls >> 16, version_ls & 0xFFFF)
    except (OSError, ValueError):
        return None


def version_text(version: tuple[int, int, int, int] | None) -> str:
    if not version:
        return "未知"
    return ".".join(str(part) for part in version)


def logical_drives() -> list[str]:
    if os.name != "nt":
        return []
    mask = ctypes.windll.kernel32.GetLogicalDrives()
    return [f"{letter}:" for index, letter in enumerate(string.ascii_uppercase) if mask & (1 << index)]


def scan_launchers(max_depth: int = 2, budget: float = 4.0) -> list[Path]:
    """在本地磁盘浅层目录中查找启动器可执行文件。"""
    deadline = time.monotonic() + budget
    found: list[Path] = []
    for drive in logical_drives():
        if time.monotonic() > deadline:
            break
        root = Path(drive + "\\")
        queue: list[tuple[Path, int]] = [(root, 0)]
        while queue and time.monotonic() < deadline:
            folder, depth = queue.pop(0)
            try:
                entries = list(os.scandir(folder))
            except OSError:
                continue
            for entry in entries:
                try:
                    if entry.is_file():
                        name = entry.name.lower()
                        if name in LAUNCHER_NAMES and entry.name.lower().endswith(".exe"):
                            found.append(Path(entry.path))
                    elif entry.is_dir() and depth < max_depth:
                        name = entry.name.lower()
                        if name in SKIP_DIRS or name.startswith((".", "$")):
                            continue
                        queue.append((Path(entry.path), depth + 1))
                except OSError:
                    continue
    return found


def find_launcher(folder: Path) -> Path | None:
    """在指定目录中查找启动器可执行文件。"""
    if folder.is_file() and folder.suffix.lower() == ".exe":
        return folder
    for name in LAUNCHER_NAMES:
        candidate = folder / name
        if candidate.exists():
            return candidate
    try:
        for entry in os.scandir(folder):
            if entry.is_file() and entry.name.lower().endswith(".exe"):
                if "pcl" in entry.name.lower() or "plain craft launcher" in entry.name.lower():
                    return Path(entry.path)
    except OSError:
        pass
    return None


def launcher_running(exe: Path) -> bool:
    """检查启动器是否正在运行。"""
    if os.name != "nt":
        return False
    class PROCESSENTRY32W(ctypes.Structure):
        _fields_ = [
            ("dwSize", wintypes.DWORD),
            ("cntUsage", wintypes.DWORD),
            ("th32ProcessID", wintypes.DWORD),
            ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
            ("th32ModuleID", wintypes.DWORD),
            ("cntThreads", wintypes.DWORD),
            ("th32ParentProcessID", wintypes.DWORD),
            ("pcPriClassBase", ctypes.c_long),
            ("dwFlags", wintypes.DWORD),
            ("szExeFile", wintypes.WCHAR * 260),
        ]

    kernel32 = ctypes.windll.kernel32
    snapshot = kernel32.CreateToolhelp32Snapshot(0x00000002, 0)
    if snapshot == -1:
        return False
    entry = PROCESSENTRY32W()
    entry.dwSize = ctypes.sizeof(PROCESSENTRY32W)
    target = exe.name.lower()
    running = False
    try:
        if kernel32.Process32FirstW(snapshot, ctypes.byref(entry)):
            while True:
                if entry.szExeFile.lower() == target:
                    running = True
                    break
                if not kernel32.Process32NextW(snapshot, ctypes.byref(entry)):
                    break
    finally:
        kernel32.CloseHandle(snapshot)
    return running


@dataclass
class PclInstall:
    exe: Path
    version: tuple[int, int, int, int] | None
    data_dir: Path
    setup_path: Path
    mc_dir: Path
    setup: SetupIni | None

    @property
    def root(self) -> Path:
        return self.exe.parent

    @property
    def name(self) -> str:
        return self.exe.stem

    @property
    def version_display(self) -> str:
        return version_text(self.version)

    @property
    def restricted(self) -> bool:
        """该版本是否已经移除了游戏下载功能。"""
        return bool(self.version) and self.version >= RESTRICTED_FROM

    @property
    def update_value(self) -> str | None:
        return self.setup.get(UPDATE_SETTING_KEY, "0") if self.setup else None

    @property
    def fixed(self) -> bool:
        return self.update_value == UPDATE_DISABLED

    @property
    def needs_fix(self) -> bool:
        return not self.restricted and not self.fixed

    def apply_fix(self) -> None:
        if self.setup is None:
            raise FileNotFoundError(f"未找到设置文件：{self.setup_path}")
        self.setup.set(UPDATE_SETTING_KEY, UPDATE_DISABLED)
        self.setup.save()
        self.setup = SetupIni(self.setup_path)
        if not self.fixed:
            raise OSError("设置写入后校验失败")


def _resolve_mc_dir(root: Path, raw: str | None) -> Path:
    if not raw or not raw.strip():
        return root / ".minecraft"
    raw = raw.strip()
    if raw.startswith("$"):
        relative = raw[1:].lstrip("\\/")
        return root / relative if relative else root / ".minecraft"
    path = Path(raw.rstrip("\\/"))
    return path if path.is_absolute() else root / path


def load_install(exe: Path) -> PclInstall:
    exe = Path(exe)
    root = exe.parent
    data_dir = root / "PCL"
    setup_path = data_dir / "Setup.ini"
    setup = SetupIni(setup_path)
    mc_dir = _resolve_mc_dir(root, setup.get("LaunchFolderSelect") if setup else None)
    return PclInstall(
        exe=exe,
        version=read_version(exe),
        data_dir=data_dir,
        setup_path=setup_path,
        mc_dir=mc_dir,
        setup=setup,
    )
