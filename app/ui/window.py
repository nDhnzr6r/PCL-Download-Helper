"""主窗口。"""

from __future__ import annotations

import os
import queue
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog

import customtkinter as ctk
from PIL import Image

from ..core.config import Config
from ..core.installer import MANIFEST_URL, Installer
from ..core.mcmeta import VersionEntry
from ..core.net import Client, format_size
from ..core.pcl import PclInstall, find_launcher, launcher_running, load_install, scan_launchers
from ..core.sources import Source
from . import theme
from .version_list import VersionList

LOADERS = {"原版": "", "Fabric": "fabric", "Quilt": "quilt"}
TYPE_LABELS = {"release": "正式版", "snapshot": "快照", "old_beta": "远古测试", "old_alpha": "远古版本"}
SEARCH_HINT = "搜索版本号"


def resource_path(relative: str) -> Path:
    import sys

    base = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parents[2]))
    return base / relative


class App(ctk.CTk):
    def __init__(self):
        super().__init__()
        ctk.set_appearance_mode("light")
        self.title("PCL 下载助手")
        self.configure(fg_color=theme.BG)
        self.minsize(940, 600)
        self.geometry("980x650")
        self.scale = float(ctk.ScalingTracker.get_widget_scaling(self)) or 1.0
        self._set_icon()

        self.config_data = Config()
        self.install: PclInstall | None = None
        self.versions: list[VersionEntry] = []
        self.installed: set[str] = set()
        self.downloading = False
        self.cancel_event = threading.Event()
        self.events: queue.Queue = queue.Queue()
        self.selected: VersionEntry | None = None
        self._search_job: str | None = None

        self._manifesting = False

        self._build()
        self.after(60, self._drain_events)
        self.after(120, self._restore_state)

    # ---------- 界面搭建 ----------

    def _set_icon(self) -> None:
        icon = resource_path("assets/icon.ico")
        try:
            if icon.exists():
                self.iconbitmap(str(icon))
        except tk.TclError:
            pass

    def _build(self) -> None:
        self.grid_columnconfigure(0, weight=1)
        self.grid_rowconfigure(1, weight=1)
        self._build_header()
        body = ctk.CTkFrame(self, fg_color="transparent")
        body.grid(row=1, column=0, sticky="nsew", padx=20, pady=(4, 0))
        body.grid_columnconfigure(0, weight=0, minsize=330)
        body.grid_columnconfigure(1, weight=1)
        body.grid_rowconfigure(0, weight=1)
        self._build_launcher_card(body)
        self._build_download_card(body)
        self._build_statusbar()

    def _build_header(self) -> None:
        header = ctk.CTkFrame(self, fg_color="transparent", height=76)
        header.grid(row=0, column=0, sticky="ew", padx=22, pady=(14, 0))
        header.grid_propagate(False)
        header.grid_columnconfigure(1, weight=1)

        logo = resource_path("assets/icon.png")
        if logo.exists():
            self._logo = ctk.CTkImage(light_image=Image.open(logo), size=(40, 40))
            ctk.CTkLabel(header, image=self._logo, text="").grid(row=0, column=0, rowspan=2, sticky="w", padx=(0, 12))
        else:
            ctk.CTkLabel(header, text="◧", font=theme.font(26, "bold"), text_color=theme.ACCENT).grid(
                row=0, column=0, rowspan=2, sticky="w", padx=(0, 12)
            )

        ctk.CTkLabel(header, text="PCL 下载助手", font=theme.font(19, "bold"), text_color=theme.TEXT).grid(
            row=0, column=1, sticky="sw"
        )
        ctk.CTkLabel(
            header,
            text="修复启动器下载入口 · 直接安装 Minecraft",
            font=theme.font(12),
            text_color=theme.MUTED,
        ).grid(row=1, column=1, sticky="nw")

        self.mirror_switch = ctk.CTkSwitch(
            header,
            text="国内镜像",
            font=theme.font(12),
            text_color=theme.TEXT_SOFT,
            progress_color=theme.ACCENT,
            button_color=theme.ACCENT,
            button_hover_color=theme.ACCENT_HOVER,
            command=self._on_mirror_toggle,
        )
        self.mirror_switch.grid(row=0, column=2, rowspan=2, sticky="e")
        if self.config_data.get("mirror", True):
            self.mirror_switch.select()
        else:
            self.mirror_switch.deselect()

        ctk.CTkLabel(header, text="v1.2.0", font=theme.font(11), text_color=theme.MUTED).grid(
            row=0, column=3, rowspan=2, sticky="e", padx=(16, 0)
        )

    def _build_launcher_card(self, master) -> None:
        card = ctk.CTkFrame(
            master,
            fg_color=theme.CARD,
            corner_radius=14,
            border_width=1,
            border_color=theme.BORDER,
        )
        card.grid(row=0, column=0, sticky="nsew", padx=(0, 14), pady=(10, 20))
        card.grid_columnconfigure(0, weight=1)
        card.grid_rowconfigure(6, weight=1)

        head = ctk.CTkFrame(card, fg_color="transparent")
        head.grid(row=0, column=0, sticky="ew", padx=18, pady=(18, 12))
        head.grid_columnconfigure(0, weight=1)
        ctk.CTkLabel(head, text="启动器", font=theme.font(14, "bold"), text_color=theme.TEXT).grid(
            row=0, column=0, sticky="w"
        )
        self.launcher_badge = ctk.CTkLabel(
            head,
            text="检测中",
            font=theme.font(11),
            text_color=theme.MUTED,
            fg_color=theme.CARD_SOFT,
            corner_radius=6,
            height=22,
            width=62,
        )
        self.launcher_badge.grid(row=0, column=1, sticky="e")

        self.launcher_box = ctk.CTkFrame(card, fg_color=theme.CARD_SOFT, corner_radius=10)
        self.launcher_box.grid(row=1, column=0, sticky="ew", padx=18)
        self.launcher_box.grid_columnconfigure(0, weight=1)
        self.launcher_name = ctk.CTkLabel(
            self.launcher_box,
            text="尚未选择启动器",
            font=theme.font(12.5, "bold"),
            text_color=theme.TEXT,
            anchor="w",
        )
        self.launcher_name.grid(row=0, column=0, sticky="ew", padx=14, pady=(11, 0))
        self.launcher_path = ctk.CTkLabel(
            self.launcher_box,
            text="",
            font=theme.font(11),
            text_color=theme.MUTED,
            anchor="w",
            justify="left",
        )
        self.launcher_path.grid(row=1, column=0, sticky="ew", padx=14, pady=(1, 11))

        self.fix_note = ctk.CTkLabel(
            card,
            text="",
            font=theme.font(12),
            text_color=theme.TEXT_SOFT,
            anchor="w",
            justify="left",
            wraplength=280,
        )
        self.fix_note.grid(row=2, column=0, sticky="ew", padx=18, pady=(14, 0))

        self.fix_button = ctk.CTkButton(
            card,
            text="一键修复",
            font=theme.font(13, "bold"),
            height=38,
            corner_radius=9,
            fg_color=theme.ACCENT,
            hover_color=theme.ACCENT_HOVER,
            text_color="#FFFFFF",
            command=self._apply_fix,
        )
        self.fix_button.grid(row=3, column=0, sticky="ew", padx=18, pady=(16, 0))

        actions = ctk.CTkFrame(card, fg_color="transparent")
        actions.grid(row=4, column=0, sticky="ew", padx=18, pady=(8, 0))
        actions.grid_columnconfigure((0, 1), weight=1)
        self.browse_button = self._ghost_button(actions, "选择文件夹", self._choose_folder)
        self.browse_button.grid(row=0, column=0, sticky="ew", padx=(0, 4))
        self.open_button = self._ghost_button(actions, "打开目录", self._open_launcher_dir)
        self.open_button.grid(row=0, column=1, sticky="ew", padx=(4, 0))

        divider = ctk.CTkFrame(card, fg_color=theme.BORDER_SOFT, height=1)
        divider.grid(row=5, column=0, sticky="ew", padx=18, pady=(18, 0))

        info = ctk.CTkFrame(card, fg_color="transparent")
        info.grid(row=6, column=0, sticky="nsew", padx=18, pady=(12, 18))
        info.grid_columnconfigure(0, weight=1)
        info.grid_rowconfigure(1, weight=1)

        self.launcher_hint = ctk.CTkLabel(
            info,
            text="",
            font=theme.font(11),
            text_color=theme.MUTED,
            anchor="nw",
            justify="left",
            wraplength=280,
        )
        self.launcher_hint.grid(row=0, column=0, sticky="new")

        bottom = ctk.CTkFrame(info, fg_color="transparent")
        bottom.grid(row=2, column=0, sticky="sew")
        bottom.grid_columnconfigure(0, weight=1)
        self.installed_count = ctk.CTkLabel(
            bottom,
            text="",
            font=theme.font(11, "bold"),
            text_color=theme.TEXT_SOFT,
            anchor="w",
        )
        self.installed_count.grid(row=0, column=0, sticky="w")
        self.tool_hint = ctk.CTkLabel(
            bottom,
            text="下载的游戏会直接写入启动器目录，重新打开 PCL 即可看到新版本。",
            font=theme.font(11),
            text_color=theme.MUTED,
            anchor="w",
            justify="left",
            wraplength=280,
        )
        self.tool_hint.grid(row=1, column=0, sticky="w", pady=(4, 0))

    def _build_download_card(self, master) -> None:
        card = ctk.CTkFrame(
            master,
            fg_color=theme.CARD,
            corner_radius=14,
            border_width=1,
            border_color=theme.BORDER,
        )
        card.grid(row=0, column=1, sticky="nsew", pady=(10, 20))
        card.grid_columnconfigure(0, weight=1)
        card.grid_rowconfigure(1, weight=1)
        self.download_card = card

        toolbar = ctk.CTkFrame(card, fg_color="transparent")
        toolbar.grid(row=0, column=0, sticky="ew", padx=16, pady=(16, 10))
        toolbar.grid_columnconfigure(0, weight=1)

        self.search_entry = ctk.CTkEntry(
            toolbar,
            placeholder_text=SEARCH_HINT,
            placeholder_text_color="#A6ADB6",
            height=34,
            corner_radius=8,
            border_width=1,
            border_color=theme.BORDER,
            fg_color=theme.CARD,
            font=theme.font(12),
            text_color=theme.TEXT,
        )
        self.search_entry.grid(row=0, column=0, sticky="ew", padx=(0, 10))
        self.search_entry.bind("<KeyRelease>", lambda _event: self._schedule_filter())

        self.type_filter = ctk.CTkSegmentedButton(
            toolbar,
            values=["正式版", "快照", "全部"],
            font=theme.font(12),
            height=34,
            corner_radius=8,
            fg_color=theme.CARD_SOFT,
            selected_color=theme.ACCENT,
            selected_hover_color=theme.ACCENT_HOVER,
            unselected_color=theme.CARD_SOFT,
            unselected_hover_color="#EDF0F3",
            text_color=theme.TEXT_SOFT,
            command=lambda _value: self._refresh_list(),
        )
        self.type_filter.set("正式版")
        self.type_filter.grid(row=0, column=1, padx=(0, 8))

        self.refresh_button = ctk.CTkButton(
            toolbar,
            text="刷新",
            font=theme.font(12),
            width=58,
            height=34,
            corner_radius=8,
            fg_color=theme.CARD,
            hover_color=theme.CARD_SOFT,
            text_color=theme.TEXT_SOFT,
            border_width=1,
            border_color=theme.BORDER,
            command=lambda: self._load_manifest(force=True),
        )
        self.refresh_button.grid(row=0, column=2)

        self.list = VersionList(card, scale=self.scale, on_select=self._on_select)
        self.list.grid(row=1, column=0, sticky="nsew", padx=(10, 10))

        self._build_progress(card)
        self._build_footer(card)

    def _build_progress(self, card) -> None:
        wrapper = ctk.CTkFrame(card, fg_color=theme.CARD_SOFT, corner_radius=10)
        wrapper.grid(row=2, column=0, sticky="ew", padx=16, pady=(10, 0))
        wrapper.grid_columnconfigure(0, weight=1)
        self.progress_panel = wrapper

        line = ctk.CTkFrame(wrapper, fg_color="transparent")
        line.grid(row=0, column=0, sticky="ew", padx=12, pady=(10, 6))
        line.grid_columnconfigure(0, weight=1)
        self.progress_title = ctk.CTkLabel(
            line, text="准备中", font=theme.font(12, "bold"), text_color=theme.TEXT, anchor="w"
        )
        self.progress_title.grid(row=0, column=0, sticky="w")
        self.progress_stats = ctk.CTkLabel(
            line, text="", font=theme.font(11), text_color=theme.MUTED, anchor="e"
        )
        self.progress_stats.grid(row=0, column=1, sticky="e", padx=(8, 10))
        self.cancel_button = ctk.CTkButton(
            line,
            text="取消",
            font=theme.font(11),
            width=48,
            height=24,
            corner_radius=6,
            fg_color=theme.CARD,
            hover_color=theme.DANGER_SOFT,
            text_color=theme.DANGER,
            border_width=1,
            border_color=theme.BORDER,
            command=self._cancel_download,
        )
        self.cancel_button.grid(row=0, column=2, sticky="e")

        self.progress_bar = ctk.CTkProgressBar(
            wrapper,
            height=6,
            corner_radius=3,
            fg_color="#E9ECF0",
            progress_color=theme.ACCENT,
        )
        self.progress_bar.grid(row=1, column=0, sticky="ew", padx=12, pady=(0, 12))
        self.progress_bar.set(0)
        wrapper.grid_remove()

    def _build_footer(self, card) -> None:
        footer = ctk.CTkFrame(card, fg_color="transparent")
        footer.grid(row=3, column=0, sticky="ew", padx=16, pady=(12, 16))
        footer.grid_columnconfigure(0, weight=1)

        target = ctk.CTkFrame(footer, fg_color="transparent")
        target.grid(row=0, column=0, sticky="w")
        ctk.CTkLabel(target, text="游戏目录", font=theme.font(11), text_color=theme.MUTED).pack(anchor="w")
        path_row = ctk.CTkFrame(target, fg_color="transparent")
        path_row.pack(anchor="w")
        self.target_label = ctk.CTkLabel(
            path_row, text="未选择", font=theme.font(12), text_color=theme.TEXT_SOFT, anchor="w"
        )
        self.target_label.pack(side="left")
        self.open_mc_button = ctk.CTkButton(
            path_row,
            text="打开",
            font=theme.font(11),
            width=44,
            height=22,
            corner_radius=6,
            fg_color="transparent",
            hover_color=theme.ACCENT_SOFT,
            text_color=theme.ACCENT,
            command=self._open_mc_dir,
        )
        self.open_mc_button.pack(side="left", padx=(8, 0))

        right = ctk.CTkFrame(footer, fg_color="transparent")
        right.grid(row=0, column=1, sticky="e")
        self.loader_menu = ctk.CTkOptionMenu(
            right,
            values=list(LOADERS.keys()),
            width=104,
            height=36,
            corner_radius=9,
            font=theme.font(12),
            fg_color=theme.CARD_SOFT,
            button_color=theme.CARD_SOFT,
            button_hover_color="#EDF0F3",
            text_color=theme.TEXT_SOFT,
            dropdown_fg_color=theme.CARD,
            dropdown_text_color=theme.TEXT,
            dropdown_font=theme.font(12),
            dropdown_hover_color=theme.ACCENT_SOFT,
        )
        self.loader_menu.set("原版")
        self.loader_menu.pack(side="left", padx=(0, 8))

        self.download_button = ctk.CTkButton(
            right,
            text="下载",
            font=theme.font(13, "bold"),
            width=132,
            height=36,
            corner_radius=9,
            fg_color=theme.ACCENT,
            hover_color=theme.ACCENT_HOVER,
            text_color="#FFFFFF",
            command=self._start_download,
            text_color_disabled="#FFF4EC",
        )
        self.download_button.pack(side="left")
        self.download_button.configure(state="disabled")

    def _build_statusbar(self) -> None:
        bar = ctk.CTkFrame(self, fg_color="transparent")
        bar.grid(row=2, column=0, sticky="ew", padx=22, pady=(0, 10))
        bar.grid_columnconfigure(0, weight=1)
        self.status_label = ctk.CTkLabel(
            bar, text="", font=theme.font(11), text_color=theme.MUTED, anchor="w"
        )
        self.status_label.grid(row=0, column=0, sticky="w")

    def _ghost_button(self, master, text: str, command) -> ctk.CTkButton:
        return ctk.CTkButton(
            master,
            text=text,
            font=theme.font(12),
            height=34,
            corner_radius=9,
            fg_color=theme.CARD,
            hover_color=theme.CARD_SOFT,
            text_color=theme.TEXT_SOFT,
            border_width=1,
            border_color=theme.BORDER,
            command=command,
        )

    # ---------- 启动流程 ----------

    def _restore_state(self) -> None:
        saved = self.config_data.get("pcl_exe")
        if saved and Path(saved).exists():
            self._load_install(Path(saved))
        else:
            self._auto_locate()
        self._load_manifest()

    def _auto_locate(self) -> None:
        self.launcher_badge.configure(text="查找中", text_color=theme.MUTED, fg_color=theme.CARD_SOFT)
        self.launcher_hint.configure(text="正在本机查找 PCL 启动器…")
        threading.Thread(
            target=lambda: self.events.put({"type": "scan", "items": scan_launchers()}),
            daemon=True,
        ).start()

    def _load_install(self, exe: Path) -> None:
        try:
            install = load_install(exe)
        except Exception as exc:  # 读取版本信息失败时仍允许使用
            self._message(f"读取启动器信息失败：{exc}", error=True)
            return
        self.install = install
        self.config_data.set("pcl_exe", str(exe))
        self._refresh_launcher_card()
        self._refresh_installed()
        self.target_label.configure(text=self._short_path(install.mc_dir))
        self._load_manifest()

    # ---------- 左侧卡片 ----------

    def _refresh_launcher_card(self) -> None:
        install = self.install
        if install is None:
            self.launcher_name.configure(text="尚未选择启动器")
            self.launcher_path.configure(text="")
            self.launcher_badge.configure(text="未选择", text_color=theme.MUTED, fg_color=theme.CARD_SOFT)
            self.fix_note.configure(text="选择 PCL 所在的文件夹后即可检查并修复下载功能。")
            self.fix_button.configure(text="一键修复", state="disabled", fg_color=theme.ACCENT)
            self.launcher_hint.configure(text="")
            self.target_label.configure(text="未选择")

            self.installed_count.configure(text="")
            return

        self.launcher_name.configure(text=install.name)
        self.launcher_path.configure(text=f"{install.version_display}   ·   {install.root}")
        self.open_button.configure(state="normal")

        if install.restricted:
            self.launcher_badge.configure(text="下载已关闭", text_color=theme.DANGER, fg_color=theme.DANGER_SOFT)
            self.fix_note.configure(
                text="该版本已移除游戏下载入口，无法通过设置恢复。请使用右侧的下载器把游戏装进启动器目录。"
            )
            self.fix_button.configure(text="该版本无法修复", state="disabled", fg_color="#EDEFF2")
            self.fix_button.configure(text_color=theme.MUTED)
        elif install.needs_fix:
            self.launcher_badge.configure(text="可修复", text_color="#A8621A", fg_color="#FCF1E1")
            self.fix_note.configure(
                text="启动器默认会自动下载新版本，升级到 2.99 后下载入口将消失。修复会关闭更新提示，保留下载功能。"
            )
            self.fix_button.configure(text="一键修复", state="normal", fg_color=theme.ACCENT, text_color="#FFFFFF")
        else:
            self.launcher_badge.configure(text="已修复", text_color=theme.SUCCESS, fg_color=theme.SUCCESS_SOFT)
            self.fix_note.configure(text="更新提示已关闭，启动器不会自动升级，下载功能保持可用。")
            self.fix_button.configure(text="修复已生效", state="disabled", fg_color=theme.SUCCESS_SOFT)
            self.fix_button.configure(text_color=theme.SUCCESS)

        running = launcher_running(install.exe)
        if running:
            self.launcher_hint.configure(text="启动器正在运行，修改设置前请先退出，否则会被覆盖。")
        else:
            self.launcher_hint.configure(text=f"设置文件：{install.setup_path}")
        self.target_label.configure(text=self._short_path(install.mc_dir))

    def _apply_fix(self) -> None:
        install = self.install
        if install is None:
            return
        if launcher_running(install.exe):
            self._message("请先退出 PCL 启动器，再执行修复", error=True)
            return
        try:
            install.apply_fix()
        except Exception as exc:
            self._message(f"修复失败：{exc}", error=True)
            return
        self._refresh_launcher_card()
        self._message("修复完成，重新打开启动器后生效")

    def _choose_folder(self) -> None:
        folder = filedialog.askdirectory(title="选择 PCL 启动器所在的文件夹")
        if not folder:
            return
        exe = find_launcher(Path(folder))
        if exe is None:
            self._message("该文件夹中没有找到 PCL 启动器", error=True)
            return
        self._load_install(exe)

    def _open_launcher_dir(self) -> None:
        if self.install:
            self._open_path(self.install.root)

    def _open_mc_dir(self) -> None:
        if self.install:
            self._open_path(self.install.mc_dir)

    @staticmethod
    def _open_path(path: Path) -> None:
        try:
            os.startfile(str(path))
        except OSError:
            pass

    # ---------- 版本列表 ----------

    def _client(self) -> Client:
        return Client(
            Source(prefer_mirror=bool(self.mirror_switch.get())),
            on_note=lambda text: self.events.put({"type": "note", "message": text}),
        )

    def _on_mirror_toggle(self) -> None:
        self.config_data.set("mirror", bool(self.mirror_switch.get()))
        self._load_manifest(force=True)

    def _load_manifest(self, force: bool = False) -> None:
        if self._manifesting:
            return
        if self.versions and not force:
            return
        self._manifesting = True
        self.list.set_empty("正在获取版本列表…")
        if force:
            self.versions = []
            self.list.set_items([])

        def work() -> None:
            try:
                manifest, _ = self._client().get_json(MANIFEST_URL)
                items = manifest.get("versions") or []
                self.events.put({"type": "manifest", "items": items})
            except Exception as exc:
                self.events.put({"type": "manifest_error", "message": str(exc)})

        threading.Thread(target=work, daemon=True).start()

    def _refresh_list(self) -> None:
        mode = self.type_filter.get()
        keyword = self._query()
        result = []
        for version in self.versions:
            if mode == "正式版" and version.type != "release":
                continue
            if mode == "快照" and version.type != "snapshot":
                continue
            if keyword and keyword not in version.id.lower():
                continue
            result.append(
                {
                    "id": version.id,
                    "type": TYPE_LABELS.get(version.type, version.type),
                    "date": version.date_text,
                    "installed": version.id in self.installed,
                }
            )
        self.list.set_items(result)
        if not self.versions:
            return
        self.list.set_empty("没有找到匹配的版本")
        if self.list.selected_id is None and result:
            self.list.select(result[0]["id"])

    def _query(self) -> str:
        text = self.search_entry.get().strip()
        if text == SEARCH_HINT:
            return ""
        return text.lower()

    def _schedule_filter(self) -> None:
        if self._search_job:
            self.after_cancel(self._search_job)
        self._search_job = self.after(160, self._refresh_list)

    def _on_select(self, item: dict | None) -> None:
        self.selected = None
        if item:
            for version in self.versions:
                if version.id == item["id"]:
                    self.selected = version
                    break
        self._update_download_button()

    def _update_download_button(self) -> None:
        if self.downloading:
            return
        if self.selected is None or self.install is None:
            self.download_button.configure(text="下载", state="disabled")
            return
        installed = self.selected.id in self.installed
        self.download_button.configure(
            text="重新下载" if installed else f"下载 {self.selected.id}",
            state="normal",
        )

    def _refresh_installed(self) -> None:
        self.installed = set()
        if self.install is None:
            self.installed_count.configure(text="")
            return
        folder = self.install.mc_dir / "versions"
        try:
            for entry in os.scandir(folder):
                if entry.is_dir() and (folder / entry.name / f"{entry.name}.json").exists():
                    self.installed.add(entry.name)
        except OSError:
            pass
        self.installed_count.configure(
            text=f"已安装 {len(self.installed)} 个版本" if self.installed else "还没有安装任何版本"
        )

    # ---------- 下载 ----------

    def _start_download(self) -> None:
        if self.downloading or self.selected is None or self.install is None:
            return
        loader = LOADERS.get(self.loader_menu.get(), "")
        entry = self.selected
        self.cancel_event = threading.Event()
        self.downloading = True
        self.download_button.configure(state="disabled", text="下载中…")
        self.loader_menu.configure(state="disabled")
        self.progress_panel.grid()
        self.progress_bar.set(0)
        self.progress_title.configure(text="准备下载")
        self.progress_stats.configure(text="")
        self._message(f"开始下载 {entry.id}")

        def work() -> None:
            try:
                client = self._client()
                installer = Installer(
                    self.install.mc_dir,
                    client,
                    self.events.put,
                    self.cancel_event,
                )
                installer.install(entry, loader)
            except Exception as exc:
                self.events.put({"type": "error", "message": str(exc)})

        threading.Thread(target=work, daemon=True).start()

    def _cancel_download(self) -> None:
        if self.downloading:
            self.cancel_event.set()
            self.progress_title.configure(text="正在取消…")
            self.cancel_button.configure(state="disabled")

    def _finish_download(self) -> None:
        self.downloading = False
        self.progress_panel.grid_remove()
        self.cancel_button.configure(state="normal")
        self.loader_menu.configure(state="normal")
        self._refresh_installed()
        self._refresh_list()
        self._update_download_button()

    # ---------- 事件循环 ----------

    def _drain_events(self) -> None:
        try:
            while True:
                event = self.events.get_nowait()
                self._handle_event(event)
        except queue.Empty:
            pass
        self.after(60, self._drain_events)

    def _handle_event(self, event: dict) -> None:
        kind = event.get("type")
        if kind == "manifest":
            self._manifesting = False
            self.versions = [
                VersionEntry(
                    id=item["id"],
                    type=item.get("type", ""),
                    url=item["url"],
                    release_time=item.get("releaseTime", ""),
                    sha1=item.get("sha1", ""),
                )
                for item in event["items"]
            ]
            self._refresh_list()
            self._message(f"已获取 {len(self.versions)} 个版本")
        elif kind == "manifest_error":
            self._manifesting = False
            self.list.set_empty("获取版本列表失败，请检查网络后重试")
            self._message(f"获取版本列表失败：{event['message']}", error=True)
        elif kind == "scan":
            items = event["items"]
            if self.install is None and items:
                best = max(items, key=lambda path: path.stat().st_mtime if path.exists() else 0)
                self._load_install(best)
            elif self.install is None:
                self.launcher_badge.configure(text="未找到", text_color=theme.MUTED, fg_color=theme.CARD_SOFT)
                self.launcher_hint.configure(text="没有找到 PCL 启动器，请手动选择所在文件夹。")
                self._message("未自动找到启动器，请手动选择文件夹")
        elif kind == "stage":
            self.progress_title.configure(text=event["detail"])
        elif kind == "progress":
            self.progress_bar.set(event.get("overall", 0))
            self.progress_title.configure(text=event.get("detail") or self.progress_title.cget("text"))
            self.progress_stats.configure(text=self._progress_text(event))
        elif kind == "note":
            self._message(event["message"])
        elif kind == "done":
            result = event["result"]
            self._finish_download()
            installed = result["id"]
            if installed in self.installed:
                self.list.select(installed)
            self._message(f"已安装 {installed}，重新打开 PCL 即可在版本列表中看到")
        elif kind == "error":
            self._finish_download()
            self._message(f"下载失败：{event['message']}", error=True)
        elif kind == "cancelled":
            self._finish_download()
            self._message("已取消下载")

    def _progress_text(self, event: dict) -> str:
        parts = []
        files = event.get("files") or (0, 0)
        if files[1]:
            parts.append(f"{files[0]:,} / {files[1]:,} 个文件")
        speed = event.get("speed") or 0
        if speed > 1024:
            parts.append(f"{format_size(speed)}/s")
        overall = event.get("overall") or 0
        parts.append(f"{overall * 100:.0f}%")
        return "   ·   ".join(parts)

    def _message(self, text: str, error: bool = False) -> None:
        self.status_label.configure(text=text, text_color=theme.DANGER if error else theme.MUTED)

    @staticmethod
    def _short_path(path: Path, limit: int = 46) -> str:
        text = str(path)
        if len(text) <= limit:
            return text
        return text[: limit // 2 - 2] + "…" + text[-(limit // 2 - 2):]


def run() -> None:
    app = App()
    app.mainloop()

