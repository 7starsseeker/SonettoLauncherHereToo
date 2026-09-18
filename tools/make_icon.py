"""把上游的 logo.svg 渲染成多尺寸 ICO，供启动器 exe 使用。

产物：
  launcher/assets/app.ico        —— 默认图标：深色圆角底板 + 白描油灯（任务栏深/浅色都清晰）
  launcher/assets/app-glyph.ico  —— 备选：透明背景 + 黑色油灯描边（与网页里的原始观感一致）
  launcher/assets/icon-preview.png —— 预览拼图，方便肉眼检查各尺寸效果

用法（在项目根目录下）：
  .venv\\Scripts\\python.exe launcher\\tools\\make_icon.py
"""

from __future__ import annotations

import io
import os
from pathlib import Path

from PIL import Image, ImageDraw
from playwright.sync_api import sync_playwright

PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
LOGO_SVG = PROJECT_ROOT / "web" / "src" / "assets" / "icons" / "logo.svg"
ASSETS = Path(__file__).resolve().parent.parent / "assets"

CHROME = Path(os.environ["LOCALAPPDATA"]) / "ms-playwright" / "chromium-1234" / "chrome-win64" / "chrome.exe"

SIZES = [16, 24, 32, 48, 64, 128, 256]

# 深色圆角底板 + 白色油灯（启动器 UI 的底色与强调色）
PLATE_HTML = """<!DOCTYPE html>
<html><head><meta charset="utf-8"><style>
  html, body {{ margin: 0; background: transparent; }}
  .plate {{
    width: {size}px; height: {size}px;
    background: linear-gradient(160deg, #262b3d 0%, #14161c 100%);
    border-radius: {radius}px;
    display: flex; align-items: center; justify-content: center;
  }}
  svg {{ width: {glyph}px; height: {glyph}px; fill: #ffffff; }}
</style></head>
<body><div class="plate">{svg}</div></body></html>"""

# 透明背景 + 黑色油灯（原样呈现上游 logo）
GLYPH_HTML = """<!DOCTYPE html>
<html><head><meta charset="utf-8"><style>
  html, body {{ margin: 0; background: transparent; }}
  .box {{ width: {size}px; height: {size}px; display: flex; align-items: center; justify-content: center; }}
  svg {{ width: {size}px; height: {size}px; fill: #000000; }}
</style></head>
<body><div class="box">{svg}</div></body></html>"""


def render(page, html: str, size: int) -> Image.Image:
    page.set_viewport_size({"width": size, "height": size})
    page.set_content(html)
    png = page.screenshot(omit_background=True, animations="disabled")
    return Image.open(io.BytesIO(png)).convert("RGBA")


def build(page, svg: str, template: str, sizes: list[int]) -> list[Image.Image]:
    frames = []
    for size in sizes:
        # 圆角按尺寸等比；小尺寸下圆角略小，避免把画面吃掉
        radius = max(2, round(size * 0.22))
        glyph = round(size * 0.66) if template is PLATE_HTML else size
        frames.append(render(page, template.format(size=size, radius=radius, glyph=glyph, svg=svg), size))
    return frames


def save_ico(frames: list[Image.Image], target: Path) -> None:
    frames[-1].save(target, format="ICO", sizes=[(f.width, f.height) for f in frames])


def preview(frames: list[Image.Image], target: Path) -> None:
    """把各尺寸并排画在棋盘底上，便于肉眼检查。"""
    pad, gap = 8, 10
    width = pad * 2 + sum(f.width for f in frames) + gap * (len(frames) - 1)
    height = pad * 2 + max(f.height for f in frames)
    canvas = Image.new("RGBA", (width, height), (255, 255, 255, 255))
    draw = ImageDraw.Draw(canvas)
    for y in range(0, height, 12):  # 棋盘格底，能看出透明区域
        for x in range(0, width, 12):
            if (x // 12 + y // 12) % 2:
                draw.rectangle([x, y, x + 11, y + 11], fill=(226, 226, 226, 255))
    x = pad
    for frame in frames:
        canvas.alpha_composite(frame, (x, pad + (height - pad * 2 - frame.height) // 2))
        x += frame.width + gap
    canvas.save(target)


def main() -> None:
    if not LOGO_SVG.is_file():
        raise SystemExit(f"找不到 logo：{LOGO_SVG}")
    if not CHROME.is_file():
        raise SystemExit(f"找不到 Chromium：{CHROME}（可先执行 playwright install chromium）")

    svg_markup = LOGO_SVG.read_text(encoding="utf-8")
    ASSETS.mkdir(parents=True, exist_ok=True)

    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(executable_path=str(CHROME))
        page = browser.new_page(device_scale_factor=1)

        plate_frames = build(page, svg_markup, PLATE_HTML, SIZES)
        glyph_frames = build(page, svg_markup, GLYPH_HTML, SIZES)
        browser.close()

    save_ico(plate_frames, ASSETS / "app.ico")
    save_ico(glyph_frames, ASSETS / "app-glyph.ico")
    preview(plate_frames + glyph_frames, ASSETS / "icon-preview.png")

    print("已生成：")
    for name in ("app.ico", "app-glyph.ico", "icon-preview.png"):
        path = ASSETS / name
        print(f"  {path}  ({path.stat().st_size / 1024:.1f} KB)")
    print(f"尺寸：{', '.join(str(s) for s in SIZES)}")


if __name__ == "__main__":
    main()
