"""下载源的地址改写。"""

from __future__ import annotations

OFFICIAL_PREFIXES = (
    "https://piston-meta.mojang.com/",
    "https://piston-data.mojang.com/",
    "https://launchermeta.mojang.com/",
    "https://launcher.mojang.com/",
    "https://libraries.minecraft.net/",
    "https://resources.download.minecraft.net/",
)

MIRROR = "https://bmclapi2.bangbang93.com/"

_REWRITES = (
    ("https://resources.download.minecraft.net/", MIRROR + "assets/"),
    ("https://libraries.minecraft.net/", MIRROR + "maven/"),
    ("https://maven.fabricmc.net/", MIRROR + "maven/"),
    ("https://maven.minecraftforge.net/", MIRROR + "maven/"),
    ("https://maven.neoforged.net/releases/", MIRROR + "maven/"),
    ("https://meta.fabricmc.net/", MIRROR + "fabric-meta/"),
    ("https://launchermeta.mojang.com/", MIRROR),
    ("https://launcher.mojang.com/", MIRROR),
    ("https://piston-meta.mojang.com/", MIRROR),
    ("https://piston-data.mojang.com/", MIRROR),
)


def mirror_url(url: str) -> str | None:
    """返回镜像地址；没有对应镜像时返回 None。"""
    for prefix, replacement in _REWRITES:
        if url.startswith(prefix):
            return replacement + url[len(prefix):]
    return None


class Source:
    """按偏好给出同一资源的候选地址，镜像在前时官方作为兜底。"""

    def __init__(self, prefer_mirror: bool = True):
        self.prefer_mirror = prefer_mirror

    def candidates(self, url: str) -> list[str]:
        if not url:
            return []
        mirrored = mirror_url(url)
        if not mirrored or mirrored == url:
            return [url]
        return [mirrored, url] if self.prefer_mirror else [url, mirrored]
