"""界面配色与字体。"""

from __future__ import annotations

import customtkinter as ctk

BG = "#F4F5F7"
CARD = "#FFFFFF"
CARD_SOFT = "#FAFBFC"
BORDER = "#E5E8EC"
BORDER_SOFT = "#EEF1F4"
TEXT = "#1B1F24"
TEXT_SOFT = "#5B6470"
MUTED = "#8A929C"
ACCENT = "#E96F2C"
ACCENT_HOVER = "#F0854A"
ACCENT_SOFT = "#FDF1E8"
SUCCESS = "#1E9E5A"
SUCCESS_SOFT = "#E8F6EE"
DANGER = "#D64545"
DANGER_SOFT = "#FBEAEA"

FAMILY = "Microsoft YaHei UI"


def font(size: int = 13, weight: str = "normal") -> ctk.CTkFont:
    return ctk.CTkFont(family=FAMILY, size=int(round(size)), weight=weight)


def px_font(size: float, weight: str = "normal"):
    """像素字号，用于自绘控件。"""
    import tkinter.font as tkfont

    return tkfont.Font(family=FAMILY, size=-max(8, int(round(size))), weight=weight)
