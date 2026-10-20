"""校验已安装版本的完整性。"""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app.core.mcmeta import asset_objects, library_files


def main() -> int:
    mc_dir = Path(sys.argv[1])
    version_id = sys.argv[2]
    version_dir = mc_dir / "versions" / version_id
    data = json.loads((version_dir / f"{version_id}.json").read_text("utf-8"))

    missing: list[str] = []
    client = (data.get("downloads") or {}).get("client") or {}
    jar = version_dir / f"{version_id}.jar"
    if not jar.exists() or jar.stat().st_size != client.get("size"):
        missing.append(f"客户端 jar：{jar}")

    libraries = library_files(data)
    for item in libraries:
        target = mc_dir / "libraries" / item.path
        if not target.exists():
            missing.append(f"依赖库：{item.path}")

    index_info = data.get("assetIndex") or {}
    index_path = mc_dir / "assets" / "indexes" / f"{index_info.get('id')}.json"
    objects = 0
    if not index_path.exists():
        missing.append(f"资源索引：{index_path.name}")
    else:
        index = json.loads(index_path.read_text("utf-8"))
        items = asset_objects(index)
        objects = len(items)
        for digest, _size in items:
            target = mc_dir / "assets" / "objects" / digest[:2] / digest
            if not target.exists():
                missing.append(f"资源：{digest}")

    print(f"版本 {version_id}")
    print(f"  依赖库 {len(libraries)} 个")
    print(f"  资源对象 {objects} 个")
    print(f"  缺失 {len(missing)} 项")
    for entry in missing[:10]:
        print("   -", entry)
    return 0 if not missing else 1


if __name__ == "__main__":
    raise SystemExit(main())
