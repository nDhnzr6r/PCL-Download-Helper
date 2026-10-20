"""命令行安装测试脚本。"""

from __future__ import annotations

import argparse
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app.core.installer import MANIFEST_URL, Installer
from app.core.mcmeta import VersionEntry
from app.core.net import Client
from app.core.sources import Source


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("version")
    parser.add_argument("--dir", default=".testmc")
    parser.add_argument("--loader", default="")
    parser.add_argument("--official", action="store_true")
    parser.add_argument("--timeout", type=float, default=600)
    args = parser.parse_args()

    client = Client(Source(prefer_mirror=not args.official))
    manifest, _ = client.get_json(MANIFEST_URL)
    entry = None
    for item in manifest["versions"]:
        if item["id"] == args.version:
            entry = VersionEntry(
                id=item["id"],
                type=item.get("type", ""),
                url=item["url"],
                release_time=item.get("releaseTime", ""),
                sha1=item.get("sha1", ""),
            )
            break
    if entry is None:
        print("未找到版本", args.version)
        return 1

    cancel = threading.Event()
    state = {"pct": -1, "error": None, "done": False}

    def on_event(event: dict) -> None:
        if event["type"] == "progress":
            pct = int(event["overall"] * 100)
            if pct != state["pct"]:
                state["pct"] = pct
                speed = event.get("speed") or 0
                detail = (event.get("detail") or "")[:64]
                print(f"\r{pct:3d}%  {detail:<64}  {speed / 1048576:6.2f} MB/s", end="", flush=True)
        elif event["type"] == "stage":
            print(f"\n[{event['name']}]")
        elif event["type"] == "done":
            state["done"] = True
            print("\n完成：", event["result"])
        elif event["type"] == "error":
            state["error"] = event["message"]
            print("\n错误：", event["message"])
        elif event["type"] == "cancelled":
            state["error"] = "已取消"
            print("\n已取消")

    started = time.monotonic()
    Installer(Path(args.dir), client, on_event, cancel).install(entry, args.loader)
    print(f"耗时 {time.monotonic() - started:.1f}s")
    return 0 if state["done"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
