"""把原版 / 加载器版本安装到 PCL 的游戏目录。"""

from __future__ import annotations

import json
import shutil
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Callable

from .loaders import loader_profile
from .mcmeta import RemoteFile, VersionEntry, asset_objects, asset_virtual_map, java_requirement, library_files
from .net import Cancelled, Client, DownloadError

MANIFEST_URL = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"


class Tracker:
    """按步骤权重合成总进度。"""

    def __init__(self, weights: list[float]):
        self.weights = weights
        self.index = 0
        self.files_done = 0
        self.files_total = 0
        self.bytes_done = 0
        self.bytes_total = 0

    def begin(self, index: int, files_total: int = 0, bytes_total: int = 0) -> None:
        self.index = index
        self.files_done = 0
        self.files_total = files_total
        self.bytes_done = 0
        self.bytes_total = bytes_total

    def add_bytes(self, count: int) -> None:
        self.bytes_done += count

    def add_file(self) -> None:
        self.files_done += 1

    def fraction(self) -> float:
        if self.bytes_total:
            return min(1.0, self.bytes_done / self.bytes_total)
        if self.files_total:
            return min(1.0, self.files_done / self.files_total)
        return 0.0

    def overall(self) -> float:
        total = sum(self.weights) or 1.0
        finished = sum(self.weights[: self.index])
        return min(1.0, (finished + self.weights[self.index] * self.fraction()) / total)


class Installer:
    def __init__(
        self,
        mc_dir: Path,
        client: Client,
        on_event: Callable[[dict], None],
        cancel: threading.Event | None = None,
    ):
        self.mc_dir = Path(mc_dir)
        self.client = client
        self.on_event = on_event
        self.cancel = cancel or threading.Event()
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._emit_at = 0.0
        self._speed = 0.0
        self._speed_bytes = 0
        self._speed_time = time.monotonic()
        self._detail = ""
        self.tracker = Tracker([1.0])
        self.stage_names: list[str] = []
        self.client.on_note = lambda text: self.on_event({"type": "note", "message": text})

    # ---- 对外入口 ----

    def install(self, entry: VersionEntry, loader: str = "") -> None:
        stages = [("读取版本信息", 1.0), ("下载客户端", 12.0), ("下载依赖库", 14.0), ("下载游戏资源", 28.0)]
        if loader:
            stages.append(("安装加载器", 6.0))
        self.stage_names = [name for name, _ in stages]
        self.tracker = Tracker([weight for _, weight in stages])
        self._speed_time = time.monotonic()
        try:
            result = self._run(entry, loader)
        except Cancelled:
            self.on_event({"type": "cancelled"})
        except Exception as exc:
            self._stop.set()
            self.on_event({"type": "error", "message": str(exc) or exc.__class__.__name__})
        else:
            self.on_event({"type": "done", "result": result})

    # ---- 主流程 ----

    def _run(self, entry: VersionEntry, loader: str) -> dict:
        self._stage(0, "正在获取版本信息")
        version, raw = self.client.get_json(entry.url)
        version_id = version.get("id") or entry.id
        version_dir = self.mc_dir / "versions" / version_id
        version_dir.mkdir(parents=True, exist_ok=True)
        json_path = version_dir / f"{version_id}.json"
        json_path.write_bytes(raw)
        self._emit_detail(f"Minecraft {version_id}")

        self._stage(1, f"正在下载 Minecraft {version_id}")
        client_info = (version.get("downloads") or {}).get("client") or {}
        jar_path = version_dir / f"{version_id}.jar"
        self.tracker.bytes_total = int(client_info.get("size") or 0)
        if client_info.get("url"):
            self.client.fetch(
                client_info["url"],
                jar_path,
                sha1=client_info.get("sha1"),
                size=client_info.get("size"),
                on_bytes=self._on_bytes,
                cancel=self.cancel,
            )
        self._emit_progress(force=True)

        logging_file = ((version.get("logging") or {}).get("client") or {}).get("file") or {}
        if logging_file.get("url"):
            self.client.fetch(
                logging_file["url"],
                self.mc_dir / "assets" / "log_configs" / str(logging_file.get("id") or "client"),
                sha1=logging_file.get("sha1"),
                size=logging_file.get("size"),
                cancel=self.cancel,
            )

        self._stage(2, "正在下载依赖库")
        libraries = library_files(version)
        self._download_group(libraries, self.mc_dir / "libraries", workers=8)

        self._stage(3, "正在下载游戏资源")
        index_info = version.get("assetIndex") or {}
        objects: list[tuple[str, int]] = []
        if index_info.get("url"):
            index_id = str(index_info.get("id") or "legacy")
            index_path = self.mc_dir / "assets" / "indexes" / f"{index_id}.json"
            self.client.fetch(
                index_info["url"],
                index_path,
                sha1=index_info.get("sha1"),
                size=index_info.get("size"),
                cancel=self.cancel,
            )
            index = json.loads(index_path.read_bytes().decode("utf-8-sig"))
            objects = asset_objects(index)
            virtual = asset_virtual_map(index)
            files = [
                RemoteFile(
                    url=f"https://resources.download.minecraft.net/{digest[:2]}/{digest}",
                    path=f"{digest[:2]}/{digest}",
                    sha1=digest,
                    size=size or None,
                )
                for digest, size in objects
            ]
            self._download_group(files, self.mc_dir / "assets" / "objects", workers=24, verify_hash=False)
            if virtual:
                self._build_virtual_assets(virtual)

        loader_folder = ""
        if loader:
            self._stage(4, f"正在安装 {loader}")
            loader_folder = loader_profile(version_id, loader, self.client, self._download_group, self.mc_dir)

        self._emit_progress(force=True, detail="完成")
        installed = loader_folder or version_id
        return {
            "id": installed,
            "vanilla": version_id,
            "dir": self.mc_dir / "versions" / installed,
            "java": java_requirement(version),
        }

    # ---- 资源与文件 ----

    def _download_group(
        self,
        files: list[RemoteFile],
        base: Path,
        workers: int,
        verify_hash: bool = True,
    ) -> None:
        unique: dict[str, RemoteFile] = {}
        for item in files:
            unique.setdefault(item.path, item)
        files = list(unique.values())
        total = len(files)
        if not total:
            return
        self.tracker.begin(self.tracker.index, files_total=total)
        done = 0
        done_lock = threading.Lock()
        first_error: BaseException | None = None

        def worker(item: RemoteFile) -> None:
            nonlocal done
            if self._stop.is_set() or self.cancel.is_set():
                raise Cancelled()
            dest = base / Path(item.path.replace("\\", "/"))
            try:
                self.client.fetch(
                    item.url,
                    dest,
                    sha1=item.sha1,
                    size=item.size,
                    on_bytes=self._on_bytes,
                    cancel=self.cancel,
                    verify_hash=verify_hash,
                )
            except Cancelled:
                raise
            with done_lock:
                done += 1
                self.tracker.add_file()
                self._emit_progress(detail=item.path)

        with ThreadPoolExecutor(max_workers=workers) as pool:
            futures = [pool.submit(worker, item) for item in files]
            for future in as_completed(futures):
                error = future.exception()
                if error is not None and not isinstance(error, Cancelled) and first_error is None:
                    first_error = error
                    self._stop.set()
        if first_error is not None:
            if isinstance(first_error, DownloadError):
                raise first_error
            raise DownloadError(str(first_error))
        self._emit_progress(force=True)

    def _build_virtual_assets(self, mapping: dict[str, str]) -> None:
        target_root = self.mc_dir / "assets" / "virtual" / "legacy"
        objects_root = self.mc_dir / "assets" / "objects"
        for name, digest in mapping.items():
            target = target_root / name.replace("/", "\\")
            if target.exists():
                continue
            source = objects_root / digest[:2] / digest
            if not source.exists():
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            try:
                shutil.copyfile(source, target)
            except OSError:
                continue

    # ---- 进度事件 ----

    def _stage(self, index: int, detail: str) -> None:
        self.tracker.begin(index)
        self._emit_detail(detail)
        self.on_event({"type": "stage", "index": index, "name": self.stage_names[index], "detail": detail})
        self.on_event({"type": "progress", "overall": self.tracker.overall(), "detail": detail, "speed": 0.0})
        self._emit_at = 0.0

    def _emit_detail(self, text: str) -> None:
        self._detail = text

    def _on_bytes(self, count: int) -> None:
        if self.cancel.is_set():
            raise Cancelled()
        self.tracker.add_bytes(count)
        self._speed_bytes += count
        self._emit_progress()

    def _emit_progress(self, force: bool = False, detail: str | None = None) -> None:
        now = time.monotonic()
        with self._lock:
            if not force and now - self._emit_at < 0.1:
                return
            self._emit_at = now
            elapsed = now - self._speed_time
            if elapsed >= 0.5:
                instant = self._speed_bytes / elapsed
                self._speed = instant if self._speed == 0 else self._speed * 0.65 + instant * 0.35
                self._speed_bytes = 0
                self._speed_time = now
            speed = self._speed
        self.on_event(
            {
                "type": "progress",
                "overall": self.tracker.overall(),
                "detail": detail or self._detail,
                "speed": speed,
                "files": (self.tracker.files_done, self.tracker.files_total),
            }
        )
