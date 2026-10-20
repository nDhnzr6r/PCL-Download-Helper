"""Minecraft 版本元数据解析：规则、依赖库、资源索引。"""

from __future__ import annotations

import platform
import re
from dataclasses import dataclass, field

OS_NAME = "windows"


@dataclass
class RemoteFile:
    url: str
    path: str
    sha1: str | None = None
    size: int | None = None


@dataclass
class VersionEntry:
    id: str
    type: str
    url: str
    release_time: str = ""
    sha1: str = ""

    @property
    def is_release(self) -> bool:
        return self.type == "release"

    @property
    def is_snapshot(self) -> bool:
        return self.type == "snapshot"

    @property
    def date_text(self) -> str:
        return self.release_time[:10]


def _os_version() -> str:
    return platform.version()


def rule_matches(rule: dict) -> bool:
    condition = rule.get("os") or {}
    name = condition.get("name")
    if name and name != OS_NAME:
        return False
    arch = condition.get("arch")
    if arch and arch != "x86":  # 规则针对 32 位 Java，按 64 位处理时跳过
        return False
    version_pattern = condition.get("version")
    if version_pattern and not re.search(version_pattern, _os_version()):
        return False
    if rule.get("features"):
        return False
    return True


def is_allowed(rules: list[dict] | None) -> bool:
    if not rules:
        return True
    allowed = False
    for rule in rules:
        if not rule_matches(rule):
            continue
        allowed = rule.get("action") == "allow"
    return allowed


def parse_maven(name: str) -> tuple[str, str, str, str | None]:
    parts = name.split(":")
    if len(parts) < 3:
        raise ValueError(f"无法解析依赖坐标：{name}")
    group, artifact, version = parts[0], parts[1], parts[2]
    classifier = parts[3] if len(parts) > 3 else None
    return group, artifact, version, classifier


def maven_path(name: str) -> str:
    group, artifact, version, classifier = parse_maven(name)
    suffix = f"-{classifier}" if classifier else ""
    return "/".join(
        [
            group.replace(".", "/"),
            artifact,
            version,
            f"{artifact}-{version}{suffix}.jar",
        ]
    )


def library_files(version: dict) -> list[RemoteFile]:
    """解析版本需要的全部依赖库文件（含 natives，按当前系统筛选）。"""
    result: list[RemoteFile] = []
    seen: set[str] = set()

    def add(item: RemoteFile) -> None:
        if item.path in seen:
            return
        seen.add(item.path)
        result.append(item)

    for library in version.get("libraries") or []:
        if not is_allowed(library.get("rules")):
            continue
        downloads = library.get("downloads") or {}
        artifact = downloads.get("artifact")
        if artifact and artifact.get("url"):
            add(
                RemoteFile(
                    url=artifact["url"],
                    path=artifact.get("path") or maven_path(library["name"]),
                    sha1=artifact.get("sha1"),
                    size=artifact.get("size"),
                )
            )
        elif library.get("name") and library.get("url") and not downloads:
            path = maven_path(library["name"])
            base = library["url"]
            if not base.endswith("/"):
                base += "/"
            add(
                RemoteFile(
                    url=base + path,
                    path=path,
                    sha1=library.get("sha1"),
                    size=library.get("size"),
                )
            )
        natives = library.get("natives") or {}
        classifier = natives.get("windows")
        if classifier:
            classifier = classifier.replace("${arch}", "64")
            entry = (downloads.get("classifiers") or {}).get(classifier)
            if entry:
                add(
                    RemoteFile(
                        url=entry["url"],
                        path=entry["path"],
                        sha1=entry.get("sha1"),
                        size=entry.get("size"),
                    )
                )
    return result


def asset_objects(index: dict) -> list[tuple[str, int]]:
    """资源索引中的唯一对象列表（hash, size）。"""
    seen: dict[str, int] = {}
    for value in (index.get("objects") or {}).values():
        digest = value.get("hash", "")
        if digest and digest not in seen:
            seen[digest] = int(value.get("size") or 0)
    return list(seen.items())


def asset_virtual_map(index: dict) -> dict[str, str]:
    """虚拟资源索引：逻辑路径 -> hash。"""
    mapping: dict[str, str] = {}
    if index.get("virtual"):
        for name, value in (index.get("objects") or {}).items():
            digest = value.get("hash")
            if digest:
                mapping[name] = digest
    return mapping


def java_requirement(version: dict) -> str:
    major = (version.get("javaVersion") or {}).get("majorVersion")
    return f"Java {major}" if major else ""
