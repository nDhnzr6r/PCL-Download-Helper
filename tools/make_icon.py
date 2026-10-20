"""生成应用图标。"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 512
TOP = (240, 138, 76)
BOTTOM = (222, 92, 26)


def rounded_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return mask


def build() -> Image.Image:
    gradient = Image.new("RGB", (SIZE, SIZE))
    draw = ImageDraw.Draw(gradient)
    for y in range(SIZE):
        ratio = y / (SIZE - 1)
        color = tuple(round(TOP[i] + (BOTTOM[i] - TOP[i]) * ratio) for i in range(3))
        draw.line((0, y, SIZE, y), fill=color)
    icon = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    icon.paste(gradient, (0, 0), rounded_mask(SIZE, 116))

    layer = ImageDraw.Draw(icon)
    white = (255, 255, 255, 255)
    # 下载箭头
    layer.rounded_rectangle((225, 118, 287, 250), radius=20, fill=white)
    layer.polygon([(170, 246), (342, 246), (256, 356)], fill=white)
    # 底部托盘
    layer.rounded_rectangle((150, 386, 362, 414), radius=14, fill=white)
    layer.rounded_rectangle((150, 340, 180, 414), radius=14, fill=white)
    layer.rounded_rectangle((332, 340, 362, 414), radius=14, fill=white)
    return icon


def main() -> None:
    target = Path(__file__).resolve().parents[1] / "assets"
    target.mkdir(exist_ok=True)
    icon = build()
    icon.save(target / "icon.png")
    icon.save(target / "icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print("已生成图标", target)


if __name__ == "__main__":
    main()
