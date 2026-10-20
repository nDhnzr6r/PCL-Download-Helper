"""网络访问、文件下载与完整性校验。"""

from __future__ import annotations

import hashlib
import json
import os
import threading
import time
from pathlib import Path
from typing import Callable

import requests
from requests.adapters import HTTPAdapter

from .sources import Source

USER_AGENT = "PCLDownloadHelper/1.2 (+https://github.com/)"
CHUNK = 256 * 1024


class Cancelled(Exception):
    """用户取消了操作。"""


class DownloadError(Exception):
    """下载失败。"""


def detect_system_proxy() -> str | None:
    """读取系统代理设置，未启用时返回 None。"""
    if os.name != "nt":
        return None
    try:
        import winreg

        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion\Internet Settings",
        ) as key:
            enabled = winreg.QueryValueEx(key, "ProxyEnable")[0]
            server = winreg.QueryValueEx(key, "ProxyServer")[0]
        if not enabled or not server:
            return None
        if "=" in server:
            parts = dict(item.split("=", 1) for item in server.split(";") if "=" in item)
            server = parts.get("https") or parts.get("http") or ""
        server = server.strip()
        if not server:
            return None
        if "://" not in server:
            server = "http://" + server
        return server
    except Exception:
        return None


def sha1_of(path: Path) -> str:
    digest = hashlib.sha1()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(CHUNK), b""):
            digest.update(block)
    return digest.hexdigest()


def format_size(size: float) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return f"{size:.0f} {unit}" if unit == "B" else f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} GB"


class Client:
    """带镜像回退、重试与哈希校验的下载客户端，可在多线程中使用。"""

    def __init__(
        self,
        source: Source,
        timeout: tuple[float, float] = (8.0, 20.0),
        proxy: str | None = None,
        on_note: Callable[[str], None] | None = None,
    ):
        self.source = source
        self.timeout = timeout
        self.on_note = on_note
        self._local = threading.local()
        self._proxy = proxy if proxy is not None else detect_system_proxy()
        self._mirror_failures = 0
        self._state_lock = threading.Lock()
        self._active: dict[int, list] = {}
        self._watchdog: threading.Thread | None = None

    def _session(self) -> requests.Session:
        session = getattr(self._local, "session", None)
        if session is None:
            session = requests.Session()
            adapter = HTTPAdapter(pool_connections=4, pool_maxsize=4, max_retries=0)
            session.mount("https://", adapter)
            session.mount("http://", adapter)
            session.headers["User-Agent"] = USER_AGENT
            if self._proxy:
                session.proxies.update({"http": self._proxy, "https": self._proxy})
            self._local.session = session
        return session

    def set_proxy(self, proxy: str | None) -> None:
        self._proxy = proxy
        self._local = threading.local()

    def get_json(self, url: str, retries: int = 3) -> tuple[dict, bytes]:
        raw = self.get_bytes(url, retries=retries)
        try:
            return json.loads(raw.decode("utf-8-sig")), raw
        except json.JSONDecodeError as exc:
            raise DownloadError(f"服务器返回的数据无法解析：{url}") from exc

    def get_bytes(self, url: str, retries: int = 3, official_only: bool = False) -> bytes:
        candidates = [url] if official_only else self.source.candidates(url)
        last: Exception | None = None
        for attempt in range(retries):
            for candidate in candidates:
                try:
                    response = self._session().get(candidate, timeout=self.timeout)
                    if response.status_code == 200:
                        return response.content
                    last = DownloadError(f"HTTP {response.status_code}：{candidate}")
                except requests.RequestException as exc:
                    last = DownloadError(f"网络错误：{exc}")
                    self._maybe_drop_proxy(exc)
                if candidate != url:
                    self._mirror_failed()
            if attempt < retries - 1:
                time.sleep(0.4 * (attempt + 1))
        raise last or DownloadError(f"无法访问：{url}")

    def _maybe_drop_proxy(self, exc: Exception) -> None:
        if self._proxy and isinstance(exc, (requests.exceptions.ProxyError, requests.exceptions.SSLError)):
            self.set_proxy(None)

    def fetch(
        self,
        url: str,
        dest: Path,
        sha1: str | None = None,
        size: int | None = None,
        on_bytes: Callable[[int], None] | None = None,
        cancel: threading.Event | None = None,
        verify_hash: bool = True,
    ) -> int:
        """下载到 dest，返回实际写入的字节数；已存在且校验通过时返回 0。"""
        dest = Path(dest)
        if self._is_complete(dest, sha1, size, verify_hash):
            return 0
        dest.parent.mkdir(parents=True, exist_ok=True)
        part = dest.with_name(dest.name + ".part")
        last: Exception | None = None
        for _round in range(2):
            for candidate in self.source.candidates(url):
                if cancel is not None and cancel.is_set():
                    raise Cancelled()
                try:
                    return self._fetch_one(candidate, part, dest, sha1, size, on_bytes, cancel)
                except Cancelled:
                    raise
                except Exception as exc:  # 单个地址失败后换下一个源
                    last = exc
                    part.unlink(missing_ok=True)
                    if candidate != url:
                        self._mirror_failed()
                    time.sleep(0.2)
        part.unlink(missing_ok=True)
        raise DownloadError(f"下载失败：{Path(url).name}（{last}）")

    def _mirror_failed(self) -> None:
        """镜像连续失败时改用官方源，避免卡在跳转后的慢节点上。"""
        with self._state_lock:
            self._mirror_failures += 1
            if self._mirror_failures < 6 or not self.source.prefer_mirror:
                return
            self.source.prefer_mirror = False
        if self.on_note:
            self.on_note("镜像节点响应异常，已改用官方源继续下载")

    def _fetch_one(
        self,
        url: str,
        part: Path,
        dest: Path,
        sha1: str | None,
        size: int | None,
        on_bytes: Callable[[int], None] | None,
        cancel: threading.Event | None,
    ) -> int:
        digest = hashlib.sha1()
        written = 0
        self._start_watchdog()
        response = self._session().get(url, stream=True, timeout=self.timeout)
        state = [response, time.monotonic()]
        key = id(response)
        with self._state_lock:
            self._active[key] = state
        try:
            if response.status_code != 200:
                raise DownloadError(f"HTTP {response.status_code}")
            total = int(response.headers.get("Content-Length") or 0)
            if size and total and total != size:
                raise DownloadError("文件大小与记录不符")
            with open(part, "wb") as handle:
                for block in response.iter_content(CHUNK):
                    if cancel is not None and cancel.is_set():
                        raise Cancelled()
                    if not block:
                        continue
                    handle.write(block)
                    digest.update(block)
                    written += len(block)
                    state[1] = time.monotonic()
                    if on_bytes:
                        on_bytes(len(block))
        finally:
            with self._state_lock:
                self._active.pop(key, None)
            response.close()
        if size and written != size:
            raise DownloadError("文件大小与记录不符")
        if sha1 and digest.hexdigest().lower() != sha1.lower():
            raise DownloadError("文件校验失败")
        os.replace(part, dest)
        return written

    def _start_watchdog(self) -> None:
        if self._watchdog is not None:
            return
        with self._state_lock:
            if self._watchdog is not None:
                return
            self._watchdog = threading.Thread(target=self._watch, name="net-watchdog", daemon=True)
            self._watchdog.start()

    def _watch(self) -> None:
        """回收长时间没有数据的连接，避免卡在假死的节点上。"""
        while True:
            time.sleep(2.0)
            now = time.monotonic()
            with self._state_lock:
                stalled = [entry for entry in self._active.values() if now - entry[1] > 30]
                for entry in stalled:
                    self._active.pop(id(entry[0]), None)
            for entry in stalled:
                try:
                    entry[0].close()
                except Exception:
                    pass

    @staticmethod
    def _is_complete(dest: Path, sha1: str | None, size: int | None, verify_hash: bool = True) -> bool:
        try:
            stat = dest.stat()
        except OSError:
            return False
        if size and stat.st_size != size:
            return False
        if sha1 and verify_hash and not size:
            return sha1_of(dest).lower() == sha1.lower()
        return bool(size or sha1)
