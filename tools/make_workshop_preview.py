#!/usr/bin/env python3
"""生成创意工坊预览图（512x512）——纯拼接/抠图，不用任何 AI 生图。

素材：本仓库自带的鲸鱼娘贴纸 `src/bridge/assets/whale.png`（透明底）。
做法：深色渐变底 + 径向光晕 + 抠像 + 投影 + 中文字排版 + 依赖小标。
重新生成：`python3 tools/make_workshop_preview.py`
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
SRC = ROOT / "src/bridge/assets/whale.png"
OUT = ROOT / "workshop-preview.png"

SIZE = 512
BG_TOP = (13, 20, 36)
BG_BOTTOM = (27, 39, 64)
GLOW = (64, 132, 214)
ACCENT = (124, 196, 255)
WHITE = (244, 248, 255)

FONT_BOLD = "/System/Library/Fonts/STHeiti Medium.ttc"
FONT_REG = "/System/Library/Fonts/Hiragino Sans GB.ttc"


def font(path: str, size: int):
    try:
        return ImageFont.truetype(path, size)
    except Exception:
        return ImageFont.load_default()


def vertical_gradient(w: int, h: int, top, bottom) -> Image.Image:
    img = Image.new("RGB", (1, h))
    for y in range(h):
        t = y / max(1, h - 1)
        img.putpixel((0, y), tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    return img.resize((w, h)).convert("RGBA")


def radial_glow(w: int, h: int, cx: int, cy: int, radius: int, color, strength: int) -> Image.Image:
    mask = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(mask)
    steps = 42
    for i in range(steps, 0, -1):
        r = int(radius * i / steps)
        a = int(strength * (1 - i / steps) ** 1.6)
        d.ellipse((cx - r, cy - r * 0.9, cx + r, cy + r * 0.9), fill=a)
    mask = mask.filter(ImageFilter.GaussianBlur(radius // 8))
    layer = Image.new("RGBA", (w, h), color + (255,))
    layer.putalpha(mask)
    return layer


def tinted_shadow(alpha: Image.Image, blur: int = 18, opacity: int = 150, offset=(0, 14)) -> Image.Image:
    pad = blur * 3
    canvas = Image.new("L", (alpha.width + pad * 2, alpha.height + pad * 2), 0)
    canvas.paste(alpha, (pad + offset[0], pad + offset[1]))
    canvas = canvas.filter(ImageFilter.GaussianBlur(blur)).point(lambda v: int(v * opacity / 255))
    out = Image.new("RGBA", canvas.size, (0, 0, 0, 255))
    out.putalpha(canvas)
    return out


def pill(draw: ImageDraw.ImageDraw, xy, text: str, fg, bg) -> None:
    x, y = xy
    bbox = draw.textbbox((0, 0), text, font=FONT_REG_18)
    w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
    pad_x, pad_y = 14, 8
    draw.rounded_rectangle((x, y, x + w + pad_x * 2, y + h + pad_y * 2), radius=14, fill=bg)
    draw.text((x + pad_x - bbox[0], y + pad_y - bbox[1]), text, font=FONT_REG_18, fill=fg)


def version() -> str:
    """从 manifest 读版本号，别在图片里写死。"""
    try:
        import json
        d = json.loads((ROOT / "Sts2Whale.json").read_text(encoding="utf-8"))
        return str(d.get("version", "")) or "dev"
    except Exception:
        return "dev"


def main() -> None:
    global FONT_REG_18
    FONT_REG_18 = font(FONT_REG, 17)

    card = vertical_gradient(SIZE, SIZE, BG_TOP, BG_BOTTOM)

    # 右后方一团冷光，把人物从背景里托出来
    card.alpha_composite(radial_glow(SIZE, SIZE, 350, 300, 300, GLOW, 120))

    # 左上角一道斜向高光，避免整块死黑
    wave = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(wave).polygon([(0, 0), (SIZE, 0), (0, SIZE)], fill=26)
    card.alpha_composite(Image.merge("RGBA", (*Image.new("RGB", (SIZE, SIZE), (80, 120, 190)).split(), wave)))

    # 抠像：裁掉透明边，缩放到右下（左边 40% 留给文字）
    whale = Image.open(SRC).convert("RGBA")
    whale = whale.crop(whale.getbbox())
    target_h = 285
    target_w = int(whale.width * target_h / whale.height)
    whale = whale.resize((target_w, target_h), Image.LANCZOS)

    sx = SIZE - target_w - 4
    sy = SIZE - target_h - 6
    pad = 16 * 3
    shadow = tinted_shadow(whale.getchannel("A"), blur=16, opacity=150, offset=(6, 14))
    card.alpha_composite(shadow, (sx - pad + 6, sy - pad + 14))
    card.alpha_composite(whale, (sx, sy))

    d = ImageDraw.Draw(card)

    # 标题 + 版本号（跟在标题右边，不额外占一行）
    title = font(FONT_BOLD, 62)
    d.text((34, 46), "鲸鱼爬塔", font=title, fill=WHITE,
           stroke_width=3, stroke_fill=(10, 16, 28, 200))
    tw = d.textlength("鲸鱼爬塔", font=title)
    d.text((36 + tw + 10, 84), f"v{version()}", font=font(FONT_REG, 20), fill=(126, 156, 196))

    # 副标题两行
    sub1 = font(FONT_BOLD, 24)
    sub2 = font(FONT_REG, 21)
    d.text((37, 132), "战斗求解器 + 战略助手", font=sub1, fill=ACCENT)
    d.text((37, 168), "鲸鱼娘解说 · 自动爬塔", font=sub2, fill=(206, 220, 240))

    # 分隔线
    d.line([(37, 212), (37 + 150, 212)], fill=(70, 104, 150), width=2)

    # 左下角只留一个「必须先装依赖」的提示，别堆徽章
    pill(d, (30, 424), "需要 STS2-RitsuLib", WHITE, (32, 74, 128))

    card.convert("RGB").save(OUT, "PNG", optimize=True)
    print(f"已生成 {OUT}  ({OUT.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
