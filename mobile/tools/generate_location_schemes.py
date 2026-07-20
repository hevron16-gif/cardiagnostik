# -*- coding: utf-8 -*-
"""
Location diagrams in Walker-style (approved etalon):
gray technical sketch, red arrows on problem sensors, no article text.
Covers popular OBD2 codes for Russian market (LADA/VAZ, GAZ, UAZ, KAMAZ).
"""
from __future__ import annotations

import json
import math
import os
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
OUT_DIRS = [
    ROOT / "Data" / "schemes",
    ROOT / "Resources" / "Raw" / "schemes",
    Path(r"C:\Users\User\Desktop\CarDiagnosticApp\Data\schemes"),
    Path(r"C:\Users\User\Desktop\CarDiagnosticApp\schemes"),
]


def font(sz: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    cands = [
        r"C:\Windows\Fonts\arialbd.ttf" if bold else r"C:\Windows\Fonts\arial.ttf",
        r"C:\Windows\Fonts\calibri.ttf",
        r"C:\Windows\Fonts\segoeui.ttf",
    ]
    for p in cands:
        if os.path.exists(p):
            return ImageFont.truetype(p, sz)
    return ImageFont.load_default()


F18b, F16b, F14, F12, F11 = font(18, True), font(16, True), font(14), font(12), font(11)

# Более контрастные тона — на экране MAUI/WinUI светло-серый на белом «пропадал»
GRAY = (140, 140, 140)
GRAY_D = (110, 110, 110)
GRAY_L = (175, 175, 175)
LINE = (45, 45, 45)
BOX = (248, 248, 248)
BOX_OUT = (30, 30, 30)
RED = (200, 30, 30)
BG = (245, 247, 250)  # лёгкий холодный фон, не чистый белый
W, H = 1000, 620

# code -> (highlight_keys, layout, title_ru)
# layouts: exhaust (Walker O2/cat), engine (misfire/ign/fuel), electrical, idle, diesel
CODES: dict[str, tuple[list[str], str, str]] = {
    # O2 / cat — Walker exhaust style
    "P0134": (["o2u"], "exhaust", "ДК1 — нет активности (B1S1)"),
    "P0130": (["o2u"], "exhaust", "ДК1 — неисправность цепи"),
    "P0131": (["o2u"], "exhaust", "ДК1 — низкий сигнал"),
    "P0132": (["o2u"], "exhaust", "ДК1 — высокий сигнал"),
    "P0133": (["o2u"], "exhaust", "ДК1 — медленный отклик"),
    "P0135": (["o2u"], "exhaust", "ДК1 — нагреватель"),
    "P0136": (["o2d"], "exhaust", "ДК2 — неисправность цепи"),
    "P0137": (["o2d"], "exhaust", "ДК2 — низкий сигнал"),
    "P0138": (["o2d"], "exhaust", "ДК2 — высокий сигнал"),
    "P0140": (["o2d"], "exhaust", "ДК2 — нет активности"),
    "P0141": (["o2d"], "exhaust", "ДК2 — нагреватель"),
    "P0154": (["o2u"], "exhaust", "ДК Bank2 — нет активности"),
    "P0420": (["cat", "o2u", "o2d"], "exhaust", "Катализатор — низкая эффективность"),
    "P0430": (["cat"], "exhaust", "Катализатор Bank 2"),
    "P0400": (["egr"], "exhaust", "EGR — расход"),
    "P0401": (["egr"], "exhaust", "EGR — недостаточный расход"),
    "P0402": (["egr"], "exhaust", "EGR — избыточный расход"),
    # misfire / ignition
    "P0300": (["coil", "cyl"], "engine", "Случайные пропуски воспламенения"),
    "P0301": (["coil", "cyl1"], "engine", "Пропуски — цилиндр 1"),
    "P0302": (["coil", "cyl2"], "engine", "Пропуски — цилиндр 2"),
    "P0303": (["coil", "cyl3"], "engine", "Пропуски — цилиндр 3"),
    "P0304": (["coil", "cyl4"], "engine", "Пропуски — цилиндр 4"),
    "P0351": (["coil", "cyl1"], "engine", "Катушка A / цилиндр 1"),
    "P0352": (["coil", "cyl2"], "engine", "Катушка B / цилиндр 2"),
    "P0353": (["coil", "cyl3"], "engine", "Катушка C / цилиндр 3"),
    "P0354": (["coil", "cyl4"], "engine", "Катушка D / цилиндр 4"),
    "P0363": (["coil", "inj"], "engine", "Пропуски — отключение топлива"),
    "P0325": (["knock"], "engine", "Датчик детонации 1"),
    "P0335": (["ckp"], "engine", "ДПКВ — нет сигнала"),
    "P0340": (["cmp"], "engine", "ДПРВ — нет сигнала"),
    # fuel / mixture
    "P0171": (["maf", "inj", "o2u"], "engine", "Бедная смесь Bank 1"),
    "P0172": (["maf", "inj", "o2u"], "engine", "Богатая смесь Bank 1"),
    "P0174": (["maf", "inj"], "engine", "Бедная смесь Bank 2"),
    "P0175": (["maf", "inj"], "engine", "Богатая смесь Bank 2"),
    "P0100": (["maf"], "engine", "ДМРВ — неисправность"),
    "P0101": (["maf"], "engine", "ДМРВ — диапазон"),
    "P0102": (["maf"], "engine", "ДМРВ — низкий сигнал"),
    "P0103": (["maf"], "engine", "ДМРВ — высокий сигнал"),
    "P0115": (["ect"], "engine", "ДТОЖ — неисправность"),
    "P0117": (["ect"], "engine", "ДТОЖ — низкий сигнал"),
    "P0118": (["ect"], "engine", "ДТОЖ — высокий сигнал"),
    "P0120": (["thr"], "engine", "ДПДЗ — неисправность"),
    "P0121": (["thr"], "engine", "ДПДЗ — диапазон"),
    "P0122": (["thr"], "engine", "ДПДЗ — низкий сигнал"),
    "P0200": (["inj"], "engine", "Форсунки — цепь"),
    "P0201": (["inj", "cyl1"], "engine", "Форсунка 1"),
    "P0202": (["inj", "cyl2"], "engine", "Форсунка 2"),
    "P0203": (["inj", "cyl3"], "engine", "Форсунка 3"),
    "P0204": (["inj", "cyl4"], "engine", "Форсунка 4"),
    "P0230": (["pump"], "engine", "Топливный насос — цепь"),
    "P0087": (["pump"], "engine", "Давление топлива низкое"),
    # EVAP
    "P0440": (["evap"], "engine", "EVAP — общая неисправность"),
    "P0442": (["evap"], "engine", "EVAP — малая утечка"),
    "P0443": (["evap"], "engine", "EVAP — клапан продувки"),
    "P0455": (["evap"], "engine", "EVAP — большая утечка"),
    "P0456": (["evap"], "engine", "EVAP — очень малая утечка"),
    # idle / electrical
    "P0505": (["thr", "iac"], "engine", "РХХ — неисправность"),
    "P0506": (["thr", "iac"], "engine", "ХХ — обороты ниже нормы"),
    "P0507": (["thr", "iac"], "engine", "ХХ — обороты выше нормы"),
    "P0560": (["batt", "alt"], "electrical", "Бортовая сеть — неисправность"),
    "P0562": (["batt", "alt"], "electrical", "Бортовая сеть — низкое напряжение"),
    "P0563": (["batt", "alt"], "electrical", "Бортовая сеть — высокое напряжение"),
    "P0620": (["alt"], "electrical", "Генератор — цепь управления"),
    "P0622": (["alt"], "electrical", "Генератор — поле"),
    # diesel / truck (KAMAZ / GAZ diesel common)
    "P0088": (["pump"], "diesel", "Давление топлива высокое"),
    "P0093": (["pump"], "diesel", "Утечка топлива — большая"),
    "P0190": (["rail"], "diesel", "Датчик давления рампы"),
    "P0191": (["rail"], "diesel", "Давление рампы — диапазон"),
    "P0192": (["rail"], "diesel", "Давление рампы — низкий"),
    "P0193": (["rail"], "diesel", "Давление рампы — высокий"),
    "P0216": (["inj"], "diesel", "Впрыск — тайминг"),
    "P0251": (["pump"], "diesel", "ТНВД A — диапазон"),
    "P0263": (["inj", "cyl1"], "diesel", "Цил.1 вклад"),
    "P0299": (["turbo"], "diesel", "Турбина — недостаточный наддув"),
    "P0403": (["egr"], "diesel", "EGR — цепь"),
    "P0404": (["egr"], "diesel", "EGR — диапазон"),
    "P0470": (["exhp"], "diesel", "Датчик давления выхлопа"),
    "P0480": (["fan"], "diesel", "Вентилятор 1 — цепь"),
    "P0544": (["egt"], "diesel", "Датчик EGT"),
    "P2002": (["dpf"], "diesel", "Сажевый фильтр — эффективность"),
    "P2031": (["egt"], "diesel", "EGT Bank1 Sensor2"),
    "P242F": (["dpf"], "diesel", "DPF — ограничение / зола"),
}


def draw_exhaust(d: ImageDraw.ImageDraw, hot: set[str]) -> dict[str, tuple[float, float]]:
    """Walker-style exhaust / O2 / cat layout. Returns sensor centers."""
    # Exhaust Manifold label
    d.rounded_rectangle([200, 55, 470, 100], radius=10, fill=GRAY, outline=LINE, width=1)
    d.text((230, 65), "Exhaust Manifold", fill=(45, 45, 45), font=F16b)

    # Y runners
    d.polygon(
        [(140, 115), (255, 115), (305, 235), (265, 250), (185, 155), (140, 155)],
        fill=GRAY,
        outline=LINE,
    )
    d.polygon([(265, 115), (365, 115), (345, 235), (295, 235)], fill=GRAY, outline=LINE)
    d.polygon(
        [(385, 115), (520, 115), (520, 155), (445, 155), (365, 250), (325, 235)],
        fill=GRAY,
        outline=LINE,
    )
    d.polygon([(265, 235), (365, 235), (380, 310), (250, 310)], fill=GRAY_D, outline=LINE)
    d.rounded_rectangle([250, 305, 380, 415], radius=14, fill=GRAY, outline=LINE, width=1)
    d.line([(140, 115), (255, 115), (305, 235)], fill=LINE, width=2)
    d.line([(265, 115), (365, 115), (345, 235)], fill=LINE, width=2)
    d.line([(385, 115), (520, 115), (520, 155), (445, 155), (365, 250)], fill=LINE, width=2)

    # Cat
    d.rounded_rectangle([215, 415, 420, 530], radius=4, fill=BOX, outline=BOX_OUT, width=2)
    d.text((255, 448), "Catalytic", fill=(30, 30, 30), font=F18b)
    d.text((255, 480), "Converter", fill=(30, 30, 30), font=F18b)
    d.rounded_rectangle([420, 450, 640, 500], radius=12, fill=GRAY, outline=LINE, width=1)

    # ECU
    d.rounded_rectangle([690, 130, 950, 260], radius=4, fill=BOX, outline=BOX_OUT, width=2)
    d.text((735, 165), "Engine", fill=(25, 25, 25), font=F18b)
    d.text((735, 200), "Control Unit", fill=(25, 25, 25), font=F18b)

    # EGR optional block on manifold
    d.rounded_rectangle([400, 200, 470, 250], radius=4, fill=GRAY_L, outline=LINE, width=1)
    d.text((412, 215), "EGR", fill=(40, 40, 40), font=F12)

    pos = {
        "o2u": (315.0, 365.0),
        "o2d": (535.0, 475.0),
        "cat": (318.0, 472.0),
        "egr": (435.0, 225.0),
        "ecu": (820.0, 195.0),
    }

    # sensors hardware
    draw_o2_sensor(d, pos["o2u"][0], pos["o2u"][1], hot="o2u" in hot)
    draw_o2_sensor(d, pos["o2d"][0], pos["o2d"][1], hot="o2d" in hot)

    # wires
    d.line([(pos["o2u"][0] + 20, pos["o2u"][1] - 8), (480, 290), (620, 220), (690, 200)], fill=(45, 45, 45), width=3)
    d.line([(pos["o2d"][0] + 18, pos["o2d"][1] - 10), (620, 380), (720, 300), (690, 230)], fill=(45, 45, 45), width=3)

    # red labels if O2 related
    if hot & {"o2u", "o2d", "cat"}:
        d.text((48, 310), "Oxygen", fill=RED, font=F18b)
        d.text((48, 338), "Sensors", fill=RED, font=F18b)
        if "o2u" in hot or "cat" in hot:
            d.line([(145, 348), (pos["o2u"][0] - 30, pos["o2u"][1] - 4)], fill=RED, width=2)
            arrow(d, pos["o2u"][0] - 28, pos["o2u"][1] - 4, RED)
        if "o2d" in hot or "cat" in hot:
            d.line([(145, 370), (280, 430), (pos["o2d"][0] - 30, pos["o2d"][1])], fill=RED, width=2)
            arrow(d, pos["o2d"][0] - 28, pos["o2d"][1], RED)
        if "cat" in hot:
            # ring around cat box
            d.rectangle([212, 412, 423, 533], outline=RED, width=3)

    if "egr" in hot:
        red_callout(d, 48, 200, "EGR", pos["egr"][0], pos["egr"][1])

    return pos


def draw_o2_sensor(d: ImageDraw.ImageDraw, x: float, y: float, hot: bool = False) -> None:
    r_out = 28 if not hot else 30
    d.ellipse([x - r_out, y - r_out, x + r_out, y + r_out], fill=GRAY_L, outline=RED if hot else LINE, width=3 if hot else 2)
    hex_r = 17
    hex_pts = []
    for i in range(6):
        a = math.radians(30 + i * 60)
        hex_pts.append((x + hex_r * math.cos(a), y - 6 + hex_r * math.sin(a)))
    d.polygon(hex_pts, fill=(198, 198, 198), outline=LINE)
    d.rounded_rectangle([x - 9, y - 52, x + 9, y - 12], radius=3, fill=(175, 175, 175), outline=LINE)
    d.ellipse([x - 11, y - 58, x + 11, y - 42], fill=(165, 165, 165), outline=LINE)
    if hot:
        d.ellipse([x - r_out - 6, y - r_out - 6, x + r_out + 6, y + r_out + 6], outline=RED, width=2)


def arrow(d: ImageDraw.ImageDraw, x: float, y: float, color: tuple[int, int, int]) -> None:
    d.polygon([(x, y), (x - 15, y - 8), (x - 15, y + 8)], fill=color)


def red_callout(d: ImageDraw.ImageDraw, tx: float, ty: float, text: str, sx: float, sy: float) -> None:
    d.text((tx, ty), text, fill=RED, font=F18b)
    d.line([(tx + 50, ty + 14), (sx - 20, sy)], fill=RED, width=2)
    arrow(d, sx - 18, sy, RED)


def draw_engine(d: ImageDraw.ImageDraw, hot: set[str], diesel: bool = False) -> dict[str, tuple[float, float]]:
    """Top engine bay sketch for Russian inline-4 / similar (VAZ, GAZ, UAZ)."""
    d.rectangle([50, 90, 950, 540], outline=(180, 180, 180), width=1)
    label = "Моторный отсек · рядный 4 (ВАЗ/LADA, ГАЗ, УАЗ)" if not diesel else "Дизель · ТНВД / рампа (КАМАЗ, ГАЗ diesel)"
    d.text((58, 96), label, fill=(100, 100, 100), font=F11)

    # block
    d.rounded_rectangle([320, 220, 640, 420], radius=20, fill=(240, 240, 240), outline=LINE, width=3)
    d.rounded_rectangle([320, 175, 640, 225], radius=10, fill=(230, 230, 230), outline=LINE, width=2)
    d.text((450, 190), "ГБЦ", fill=(40, 40, 40), font=F16b)
    d.text((430, 300), "Блок двигателя", fill=(50, 50, 50), font=F14)

    cyl_x = [380, 450, 520, 590]
    for i, x in enumerate(cyl_x):
        d.ellipse([x - 22, 250, x + 22, 294], outline=LINE, width=2, fill=(250, 250, 250))
        d.text((x - 6, 260), str(i + 1), fill=(0, 0, 0), font=F14)

    # intake left
    d.polygon([(200, 160), (320, 180), (320, 230), (200, 250), (155, 205)], fill=(248, 248, 248), outline=LINE)
    d.ellipse([175, 185, 225, 235], outline=LINE, width=2, fill=(250, 250, 250))
    d.rounded_rectangle([55, 150, 145, 230], radius=14, fill=(250, 250, 250), outline=LINE, width=2)
    d.text((70, 178), "Возд.\nфильтр", fill=(40, 40, 40), font=F12)
    d.line([(145, 190), (175, 210)], fill=LINE, width=2)

    # exhaust right
    d.polygon([(640, 200), (720, 230), (720, 340), (640, 370)], fill=(238, 238, 238), outline=LINE)
    d.rounded_rectangle([720, 255, 880, 350], radius=26, fill=(235, 235, 235), outline=LINE, width=2)
    d.text((750, 290), "катализатор", fill=(40, 40, 40), font=F12)
    d.line([(880, 300), (950, 300)], fill=LINE, width=4)

    # KPP
    d.rounded_rectangle([410, 420, 550, 480], radius=8, fill=(248, 248, 248), outline=(100, 100, 100), width=1)
    d.text((460, 440), "КПП", fill=(100, 100, 100), font=F14)

    if diesel:
        # common rail / HPFP
        d.rounded_rectangle([340, 200, 620, 230], radius=4, fill=GRAY_L, outline=LINE, width=2)
        d.text((430, 205), "Топливная рампа", fill=(30, 30, 30), font=F12)
        d.rounded_rectangle([80, 300, 180, 380], radius=8, fill=GRAY, outline=LINE, width=2)
        d.text((95, 330), "ТНВД", fill=(30, 30, 30), font=F14)
        d.rounded_rectangle([720, 120, 880, 200], radius=10, fill=GRAY_L, outline=LINE, width=2)
        d.text((755, 145), "Турбина", fill=(30, 30, 30), font=F14)
        d.rounded_rectangle([720, 380, 900, 470], radius=10, fill=GRAY, outline=LINE, width=2)
        d.text((760, 410), "DPF / сажевый", fill=(30, 30, 30), font=F12)

    pos = {
        "air": (100.0, 190.0),
        "maf": (160.0, 195.0),
        "thr": (200.0, 210.0),
        "inj": (400.0, 215.0),
        "coil": (420.0, 195.0),
        "ckp": (370.0, 360.0),
        "cmp": (560.0, 195.0),
        "knock": (480.0, 300.0),
        "ect": (340.0, 210.0),
        "o2u": (700.0, 250.0),
        "o2d": (800.0, 320.0),
        "cat": (800.0, 300.0),
        "evap": (900.0, 420.0),
        "pump": (100.0, 340.0),
        "ecu": (100.0, 450.0),
        "batt": (900.0, 120.0),
        "alt": (850.0, 180.0),
        "iac": (210.0, 230.0),
        "cyl": (480.0, 270.0),
        "cyl1": (380.0, 270.0),
        "cyl2": (450.0, 270.0),
        "cyl3": (520.0, 270.0),
        "cyl4": (590.0, 270.0),
        "rail": (480.0, 215.0),
        "turbo": (800.0, 160.0),
        "dpf": (810.0, 425.0),
        "egt": (760.0, 280.0),
        "exhp": (780.0, 360.0),
        "fan": (120.0, 280.0),
        "egr": (680.0, 220.0),
    }

    # draw landmark dots
    landmarks = ["maf", "thr", "coil", "ckp", "cmp", "o2u", "o2d", "ecu", "batt"]
    if diesel:
        landmarks += ["rail", "turbo", "dpf", "pump"]
    labels = {
        "maf": "ДМРВ",
        "thr": "Дроссель",
        "coil": "Катушки",
        "ckp": "ДПКВ",
        "cmp": "ДПРВ",
        "o2u": "ДК1",
        "o2d": "ДК2",
        "ecu": "ЭБУ",
        "batt": "АКБ",
        "inj": "Форсунки",
        "knock": "ДД",
        "ect": "ДТОЖ",
        "evap": "EVAP",
        "pump": "Насос",
        "alt": "Генератор",
        "iac": "РХХ",
        "cyl1": "Цил.1",
        "cyl2": "Цил.2",
        "cyl3": "Цил.3",
        "cyl4": "Цил.4",
        "cyl": "Цилиндры",
        "rail": "Рампа",
        "turbo": "Турбина",
        "dpf": "DPF",
        "egt": "EGT",
        "exhp": "Pвыхл.",
        "fan": "Вент.",
        "egr": "EGR",
        "cat": "Кат.",
        "air": "Фильтр",
    }

    drawn = set()
    for k in landmarks:
        if k in pos:
            mark_sensor(d, pos[k][0], pos[k][1], labels.get(k, k), k in hot)
            drawn.add(k)
    for k in hot:
        if k not in drawn and k in pos:
            mark_sensor(d, pos[k][0], pos[k][1], labels.get(k, k), True)

    return pos


def mark_sensor(d: ImageDraw.ImageDraw, x: float, y: float, label: str, hot: bool) -> None:
    r = 11 if hot else 8
    d.ellipse(
        [x - r, y - r, x + r, y + r],
        fill=(255, 220, 220) if hot else (255, 255, 255),
        outline=RED if hot else LINE,
        width=3 if hot else 2,
    )
    if hot:
        d.ellipse([x - r - 5, y - r - 5, x + r + 5, y + r + 5], outline=RED, width=2)
        d.polygon([(x + r + 3, y), (x + r + 22, y - 10), (x + r + 22, y + 10)], fill=RED)

    lx, ly = x + 28, y - 32
    if lx > 880:
        lx = x - 100
    if ly < 100:
        ly = y + 18
    bw = max(70, len(label) * 9 + 16)
    bh = 26
    d.line([(x + r, y), (lx, ly + 12)], fill=RED if hot else (60, 60, 60), width=2 if hot else 1)
    d.rectangle(
        [lx, ly, lx + bw, ly + bh],
        fill=(255, 240, 240) if hot else (255, 255, 255),
        outline=RED if hot else LINE,
        width=2 if hot else 1,
    )
    d.text((lx + 6, ly + 5), label, fill=RED if hot else (20, 20, 20), font=F12)


def draw_electrical(d: ImageDraw.ImageDraw, hot: set[str]) -> dict[str, tuple[float, float]]:
    d.text((58, 96), "Бортовая сеть · АКБ / генератор (все РФ марки)", fill=(100, 100, 100), font=F11)
    # battery
    d.rounded_rectangle([120, 180, 320, 320], radius=8, fill=(245, 245, 245), outline=LINE, width=3)
    d.text((180, 230), "АКБ", fill=(30, 30, 30), font=F18b)
    d.text((155, 265), "12V", fill=(80, 80, 80), font=F14)
    # alternator
    d.ellipse([450, 180, 650, 380], fill=(235, 235, 235), outline=LINE, width=3)
    d.text((500, 260), "Генератор", fill=(30, 30, 30), font=F16b)
    # ECU
    d.rounded_rectangle([720, 200, 920, 320], radius=6, fill=BOX, outline=BOX_OUT, width=2)
    d.text((760, 240), "ЭБУ", fill=(30, 30, 30), font=F18b)
    # cables
    d.line([(320, 250), (450, 280)], fill=(40, 40, 40), width=4)
    d.line([(650, 280), (720, 260)], fill=(40, 40, 40), width=4)

    pos = {"batt": (220.0, 250.0), "alt": (550.0, 280.0), "ecu": (820.0, 260.0)}
    for k, lab in [("batt", "АКБ"), ("alt", "Генератор"), ("ecu", "ЭБУ")]:
        mark_sensor(d, pos[k][0], pos[k][1], lab, k in hot)
    return pos


def make(code: str, keys: list[str], layout: str, title: str) -> Image.Image:
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, W - 1, H - 1], outline=(160, 160, 170), width=2)

    hot = set(keys)
    if layout == "exhaust":
        draw_exhaust(d, hot)
    elif layout == "electrical":
        draw_electrical(d, hot)
    elif layout == "diesel":
        draw_engine(d, hot, diesel=True)
    else:
        draw_engine(d, hot, diesel=False)

    # minimal footer only
    d.text((24, H - 28), f"{code}  ·  {title}", fill=(120, 120, 120), font=F12)
    d.text((700, H - 28), "РФ: ВАЗ·ГАЗ·УАЗ·КАМАЗ", fill=(150, 150, 150), font=F11)
    return img


def main() -> None:
    for d in OUT_DIRS:
        d.mkdir(parents=True, exist_ok=True)

    index: dict[str, str] = {}
    for code, (keys, layout, title) in sorted(CODES.items()):
        img = make(code, keys, layout, title)
        for out in OUT_DIRS:
            path = out / f"{code}.png"
            img.save(path, "PNG", optimize=True)
        index[code] = f"{code}.png"
        # location aliases for priority
        if code in ("P0134", "P0420", "P0301", "P0171", "P0442", "P0562", "P0299"):
            for out in OUT_DIRS:
                img.save(out / f"{code}_location.png", "PNG", optimize=True)
        print(f"OK {code} [{layout}] {title}")

    meta = {
        "version": 4,
        "style": "walker_location_bw_red",
        "market": "RU (LADA/VAZ, GAZ, UAZ, KAMAZ)",
        "schemes": index,
        "count": len(index),
    }
    for out in OUT_DIRS:
        (out / "index.json").write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"\nDone: {len(index)} schemes → {OUT_DIRS[0]}")


if __name__ == "__main__":
    main()
