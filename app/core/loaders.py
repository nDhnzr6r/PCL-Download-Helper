"""Fabric / Quilt 加载器的版本解析与安装。"""

from __future__ import annotations

import json
from pathlib import Path

from .mcmeta import RemoteFile, maven_path
from .net import Client, DownloadError

FABRIC_API = "https://meta.fabricmc.net/v2/versions/loader/{mc}"
FABRIC_PROFILE = "https://meta.fabricmc.net/v2/versions/loader/{mc}/{loader}/profile/json"
QUILT_API = "https://meta.quiltmc.org/v3/versions/loader/{mc}"
QUILT_PROFILE = "https://meta.quiltmc.org/v3/versions/loader/{mc}/{loader}/profile/json"

DEFAULT_MAVEN = {
    "fabric": "https://maven.fabricmc.net/",
    "quilt": "https://maven.quiltmc.org/repository/release/",
}


def _loader_version(client: Client, kind: str, mc_id: str) -> str:
    api = (FABRIC_API if kind == "fabric" else QUILT_API).format(mc=mc_id)
    display = "Fabric" if kind == "fabric" else "Quilt"
    try:
        data, _ = client.get_json(api)
    except DownloadError as exc:
        raise DownloadError(f"获取 {display} 版本列表失败：{exc}") from exc
    if not isinstance(data, list) or not data:
        raise DownloadError(f"该 Minecraft 版本没有可用的 {display} 加载器")
    for item in data:
        loader = item.get("loader") or {}
        if loader.get("stable") and loader.get("version"):
            return str(loader["version"])
    loader = (data[0].get("loader") or {}).get("version")
    if not loader:
        raise DownloadError(f"该 Minecraft 版本没有可用的 {display} 加载器")
    return str(loader)


def loader_profile(
    mc_id: str,
    kind: str,
    client: Client,
    download_group,
    mc_dir: Path,
) -> str:
    """下载加载器描述文件与依赖，返回新实例所在的文件夹名。"""
    display = "Fabric" if kind == "fabric" else "Quilt"
    loader = _loader_version(client, kind, mc_id)
    profile_url = (FABRIC_PROFILE if kind == "fabric" else QUILT_PROFILE).format(mc=mc_id, loader=loader)
    try:
        profile, _ = client.get_json(profile_url)
    except DownloadError as exc:
        raise DownloadError(f"获取 {display} 安装文件失败：{exc}") from exc

    folder = f"{mc_id}-{display} {loader}"
    profile["id"] = folder
    profile.setdefault("inheritsFrom", mc_id)

    files: list[RemoteFile] = []
    for library in profile.get("libraries") or []:
        name = library.get("name")
        if not name:
            continue
        path = maven_path(name)
        base = library.get("url") or DEFAULT_MAVEN[kind]
        if not base.endswith("/"):
            base += "/"
        files.append(
            RemoteFile(
                url=base + path,
                path=path,
                sha1=library.get("sha1"),
                size=library.get("size"),
            )
        )
    download_group(files, mc_dir / "libraries", 4)

    target_dir = mc_dir / "versions" / folder
    target_dir.mkdir(parents=True, exist_ok=True)
    (target_dir / f"{folder}.json").write_text(
        json.dumps(profile, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    return folder
