"""自绘的版本列表：只绘制可见行，容纳上千条记录也不卡顿。"""

from __future__ import annotations

import tkinter as tk

from . import theme


def round_rect(canvas: tk.Canvas, x1: float, y1: float, x2: float, y2: float, radius: float, fill: str) -> None:
    radius = max(1.0, min(radius, (x2 - x1) / 2, (y2 - y1) / 2))
    points = [
        x1 + radius, y1,
        x2 - radius, y1,
        x2, y1,
        x2, y1 + radius,
        x2, y2 - radius,
        x2, y2,
        x2 - radius, y2,
        x1 + radius, y2,
        x1, y2,
        x1, y2 - radius,
        x1, y1 + radius,
        x1, y1,
    ]
    canvas.create_polygon(points, smooth=True, fill=fill, outline="")


class ThinScrollbar(tk.Canvas):
    """细滚动条，避免高 DPI 下系统滚动条过宽。"""

    def __init__(self, master, scale: float, command):
        super().__init__(
            master,
            width=int(8 * scale),
            bg=theme.CARD,
            highlightthickness=0,
            bd=0,
            cursor="arrow",
        )
        self.scale = scale
        self.command = command
        self.first = 0.0
        self.last = 1.0
        self.hover = False
        self._press: tuple[float, float] | None = None
        self.bind("<Configure>", lambda _event: self._draw())
        self.bind("<Button-1>", self._on_press)
        self.bind("<B1-Motion>", self._on_drag)
        self.bind("<ButtonRelease-1>", lambda _event: setattr(self, "_press", None))
        self.bind("<Enter>", lambda _event: self._set_hover(True))
        self.bind("<Leave>", lambda _event: self._set_hover(False))

    def set(self, first, last) -> None:
        self.first = float(first)
        self.last = float(last)
        self._draw()

    def _set_hover(self, value: bool) -> None:
        self.hover = value
        self._draw()

    def _thumb(self) -> tuple[float, float, float]:
        height = self.winfo_height()
        top = self.first * height
        bottom = self.last * height
        minimum = 28 * self.scale
        if bottom - top < minimum:
            bottom = min(height, top + minimum)
            top = max(0.0, bottom - minimum)
        return top, bottom, height

    def _draw(self) -> None:
        self.delete("all")
        if self.winfo_height() <= 1 or self.last - self.first >= 0.999:
            return
        top, bottom, _height = self._thumb()
        inset = 1.5 * self.scale
        color = "#BFC6CE" if self.hover else "#D6DAE0"
        round_rect(self, inset, top, self.winfo_width() - inset, bottom, 3 * self.scale, color)

    def _on_press(self, event) -> None:
        top, bottom, height = self._thumb()
        if top <= event.y <= bottom:
            self._press = (event.y, self.first)
            return
        span = self.last - self.first
        target = max(0.0, min(1.0 - span, event.y / height - span / 2))
        self.command("moveto", target)

    def _on_drag(self, event) -> None:
        if self._press is None:
            return
        start_y, start_first = self._press
        height = self.winfo_height() or 1
        target = start_first + (event.y - start_y) / height
        target = max(0.0, min(1.0 - (self.last - self.first), target))
        self.command("moveto", target)


class VersionList(tk.Frame):
    def __init__(self, master, scale: float = 1.0, on_select=None):
        super().__init__(master, bg=theme.CARD, bd=0, highlightthickness=0)
        self.scale = scale
        self.on_select = on_select
        self.items: list[dict] = []
        self.selected_id: str | None = None
        self.hover_index = -1
        self.row_h = int(48 * scale)
        self.pad = int(16 * scale)
        self.empty_text = "正在获取版本列表…"
        self.font_title = theme.px_font(13 * scale, "bold")
        self.font_meta = theme.px_font(11.5 * scale)
        self.font_tag = theme.px_font(11 * scale)

        self.canvas = tk.Canvas(self, bg=theme.CARD, highlightthickness=0, bd=0)
        self.canvas.configure(yscrollincrement=int(38 * scale))
        self.scroll = ThinScrollbar(self, scale, self.canvas.yview)
        self.canvas.configure(yscrollcommand=self.scroll.set)
        self.canvas.pack(side="left", fill="both", expand=True)
        self.scroll.pack(side="right", fill="y", padx=(6, 0))

        self.canvas.bind("<Configure>", lambda _event: self._redraw())
        self.canvas.bind("<Motion>", self._on_motion)
        self.canvas.bind("<Leave>", self._on_leave)
        self.canvas.bind("<Button-1>", self._on_click)
        self.canvas.bind("<MouseWheel>", self._on_wheel)

    # ---- 数据 ----

    def set_items(self, items: list[dict], keep_selection: bool = True) -> None:
        previous = self.selected_id if keep_selection else None
        self.items = items
        if previous and not any(item["id"] == previous for item in items):
            self.selected_id = None
        self.canvas.yview_moveto(0)
        self._redraw()

    def set_empty(self, text: str) -> None:
        self.empty_text = text
        self._redraw()

    def select(self, version_id: str | None, notify: bool = True) -> None:
        self.selected_id = version_id
        self._redraw()
        if notify and self.on_select:
            self.on_select(self.current())

    def current(self) -> dict | None:
        for item in self.items:
            if item["id"] == self.selected_id:
                return item
        return None

    # ---- 交互 ----

    def _index_at(self, y: int) -> int:
        return int((y + self.canvas.canvasy(0)) // self.row_h)

    def _on_click(self, event) -> None:
        index = self._index_at(event.y)
        if 0 <= index < len(self.items):
            self.select(self.items[index]["id"])

    def _on_motion(self, event) -> None:
        index = self._index_at(event.y)
        if index != self.hover_index:
            self.hover_index = index
            self.canvas.configure(cursor="hand2" if 0 <= index < len(self.items) else "")
            self._redraw()

    def _on_leave(self, _event) -> None:
        if self.hover_index != -1:
            self.hover_index = -1
            self._redraw()

    def _on_wheel(self, event) -> None:
        step = -1 if event.delta > 0 else 1
        self.canvas.yview_scroll(step, "units")

    # ---- 绘制 ----

    def _redraw(self) -> None:
        canvas = self.canvas
        canvas.delete("all")
        width = canvas.winfo_width()
        height = canvas.winfo_height()
        if width <= 1 or height <= 1:
            return
        total_height = max(len(self.items) * self.row_h, height)
        canvas.configure(scrollregion=(0, 0, width, total_height))
        if not self.items:
            canvas.create_text(
                width / 2,
                height / 2,
                text=self.empty_text,
                fill=theme.MUTED,
                font=self.font_meta,
            )
            return
        top = canvas.canvasy(0)
        first = max(0, int(top // self.row_h))
        last = min(len(self.items), int((top + height) // self.row_h) + 1)
        for index in range(first, last):
            self._draw_row(index, width)

    def _draw_row(self, index: int, width: int) -> None:
        canvas = self.canvas
        item = self.items[index]
        y = index * self.row_h
        bottom = y + self.row_h
        if item["id"] == self.selected_id:
            canvas.create_rectangle(0, y, width, bottom, fill=theme.ACCENT_SOFT, width=0)
        elif index == self.hover_index:
            canvas.create_rectangle(0, y, width, bottom, fill="#F7F8FA", width=0)
        center = y + self.row_h / 2
        color = theme.ACCENT if item["id"] == self.selected_id else theme.TEXT
        canvas.create_text(
            self.pad,
            center - 9 * self.scale,
            text=item["id"],
            anchor="w",
            fill=color,
            font=self.font_title,
        )
        meta = f'{item["type"]}  ·  {item["date"]}'
        canvas.create_text(
            self.pad,
            center + 10 * self.scale,
            text=meta,
            anchor="w",
            fill=theme.MUTED,
            font=self.font_meta,
        )
        if item.get("installed"):
            canvas.create_text(
                width - self.pad,
                center,
                text="已安装",
                anchor="e",
                fill=theme.SUCCESS,
                font=self.font_tag,
            )
        if index < len(self.items) - 1:
            canvas.create_line(
                self.pad,
                bottom,
                width - self.pad,
                bottom,
                fill=theme.BORDER_SOFT,
            )
