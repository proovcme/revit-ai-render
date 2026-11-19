"""Check MFLUX crop and composition without a model or Mac."""
import os
from pathlib import Path
import runpy
import sys
import tempfile
import types

from PIL import Image, ImageDraw

worker_path = Path(__file__).resolve().parents[1] / "src" / "mflux-worker.py"
if os.name == "nt":
    # The test uses only the image functions. The Mac worker owns the file lock.
    sys.modules["fcntl"] = types.ModuleType("fcntl")
worker = runpy.run_path(str(worker_path))


def check_crop():
    source = Image.new("RGBA", (1920, 1080), (8, 31, 66, 197))
    mask = Image.new("L", source.size)
    ImageDraw.Draw(mask).rectangle((1830, 1000, 1870, 1040), fill=255)
    source.save("source.png")
    mask.save("mask.png")
    original, selected, box, content, canvas = worker["prepare"]()
    assert box == (1280, 440, 1920, 1080) and content == canvas == (640, 640)
    with Image.open("working-mask.png") as working_mask:
        assert working_mask.getbbox() == (550, 560, 591, 601)
    raw = Image.new("RGB", canvas, (120, 90, 240))
    raw.putpixel((550, 560), (88, 190, 9))
    raw.save("raw.png")
    assert worker["compose"](original, selected, box, content, canvas) == 41 * 41
    with Image.open("result.png") as result:
        assert result.size == source.size
        assert result.getpixel((1830, 1000)) == (88, 190, 9, 255)
        assert result.getpixel((1829, 1000)) == source.getpixel((1829, 1000))
        assert result.getpixel((0, 0)) == source.getpixel((0, 0))


def check_wide_mask():
    size = (1920, 1080)
    bounds = (40, 100, 1830, 1010)
    assert worker["working_region"](size, bounds) == (0, 0, *size)
    base = Image.new("RGB", size, (10, 20, 30))
    base.save("source.png")
    mask = Image.new("L", size)
    mask.paste(255, bounds)
    mask.save("mask.png")
    _, _, _, content, canvas = worker["prepare"]()
    assert content[0] < size[0] and canvas[0] * canvas[1] <= 1024 ** 2
    assert all(side % 32 == 0 for side in canvas)


def main():
    cwd = Path.cwd()
    with tempfile.TemporaryDirectory() as temp:
        try:
            os.chdir(temp)
            check_crop()
            check_wide_mask()
        finally:
            os.chdir(cwd)
    print("PASS native detail crop, edge alignment, exact outside pixels and wide-mask area budget.")


if __name__ == "__main__":
    main()
