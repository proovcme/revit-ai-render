import errno
import fcntl
import json
import os
import re
import signal
import subprocess
import sys
import time
import traceback
from pathlib import Path

import numpy as np
from PIL import Image


def publish(state, **values):
    new_state = Path("state.next")
    with new_state.open("w", encoding="utf-8") as file:
        json.dump({"state": state, **values}, file, allow_nan=False)
    new_state.replace("state.json")


class JobFailure(Exception):
    def __init__(self, code):
        self.code = code


def siliconed_idle(cli):
    try:
        state = json.loads(subprocess.check_output([cli, "status"], timeout=8, text=True, stderr=subprocess.DEVNULL))
    except (OSError, subprocess.SubprocessError, ValueError) as error:
        raise JobFailure("siliconed-status") from error
    return state.get("ok") and not any(state.get(k) for k in ("running", "queue", "installing", "waiting_for_license"))


def prepare():
    with Image.open("source.png") as image, Image.open("mask.png") as mask_file:
        source = image.convert("RGBA")
        mask = mask_file.convert("L")
    if source.size != mask.size or set(np.unique(np.asarray(mask))) - {0, 255} or mask.getbbox() is None:
        raise JobFailure("invalid-mask")

    headroom = 32
    width, height = source.size
    scale = min(1.0, (1048576 / (width * height)) ** 0.5, 1536 / max(width, height))
    while True:
        content = (max(1, round(width * scale)), max(1, round(height * scale)))
        canvas = tuple((dim + headroom - 1) // headroom * headroom for dim in content)
        if canvas[0] * canvas[1] <= 1048576:
            break
        scale *= 0.98

    if min(content) < 64:
        raise JobFailure("frame-small")
    reference = Image.new("RGB", canvas, (255, 255, 255))
    resized = source if source.size == content else source.resize(content, Image.Resampling.LANCZOS)
    reference.paste(resized, (0, 0), resized.getchannel("A"))
    selected = Image.new("L", canvas)
    selected.paste(mask.resize(content, Image.Resampling.NEAREST), (0, 0))
    if selected.getbbox() is None:
        raise JobFailure("mask-small")
    reference.save("working-source.png")
    selected.save("working-mask.png")
    return source, mask, content, canvas


def compose(source, mask, content, canvas):
    with Image.open("raw.png") as raw_file:
        if raw_file.size != canvas:
            raise JobFailure("wrong-result-size")
        region = raw_file.convert("RGBA").crop((0, 0, *content))
    region = region.resize(source.size, Image.Resampling.LANCZOS)
    result = Image.composite(region, source, mask)
    before = np.asarray(source)
    after = np.asarray(result)
    area = np.asarray(mask) == 255
    changed = np.any(after != before, axis=2)
    outside = int(np.count_nonzero(changed & ~area))
    inside = int(np.count_nonzero(changed & area))
    if outside:
        raise JobFailure("outside-changed")
    if not inside:
        raise JobFailure("inside-unchanged")
    result.save("result.png")
    with Path("pixels.json").open("x", encoding="utf-8") as file:
        json.dump({"outsideChanged": outside, "insideChanged": inside, "selected": int(area.sum())}, file)
    return inside


def stop_child(child):
    if child.poll() is not None:
        return
    try:
        os.killpg(child.pid, signal.SIGTERM)
        child.wait(timeout=5)
    except ProcessLookupError:
        child.wait()
    except subprocess.TimeoutExpired:
        try:
            os.killpg(child.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        child.wait()


def progress():
    with Path("generation.log").open("rb") as file:
        file.seek(max(0, os.fstat(file.fileno()).st_size - 16384))
        log = file.read().decode("utf-8", "replace")
    steps = re.findall(r"(\d+)/8\s+\[", log)
    return min(8, int(steps[-1])) if steps else 0


def run():
    if Path("cancel-requested").is_dir():
        publish("cancelled")
        return
    config = json.loads(Path("mflux.json").read_text(encoding="utf-8"))
    paths = [Path(config[name]) for name in ("cli", "model", "silicontrol")]
    if not all(path.is_absolute() for path in paths):
        raise JobFailure("mflux-settings")
    cli, model, silicontrol = paths
    if not cli.is_file() or not model.is_dir():
        raise JobFailure("mflux-settings")
    if not siliconed_idle(str(silicontrol)):
        raise JobFailure("siliconed-busy")
    # Also cover a manually launched MFLUX tool that does not use the add-in's lock.
    external = subprocess.run(["/usr/bin/pgrep", "-f", "[m]flux-generate-"], stdout=subprocess.DEVNULL)
    if external.returncode == 0:
        raise JobFailure("mflux-busy")

    publish("preparing", step=0)
    source, mask, content, canvas = prepare()
    if Path("cancel-requested").is_dir():
        publish("cancelled")
        return
    if not siliconed_idle(str(silicontrol)):
        raise JobFailure("siliconed-busy")
    seed = config["seed"]
    if type(seed) is not int or not 0 <= seed <= 2**31 - 1:
        raise JobFailure("mflux-settings")
    args = [
        str(cli), "--model", str(model), "--base-model", "qwen-image-2.1-turbo",
        "--image-paths", "working-source.png", "--mask-image", "working-mask.png",
        "--prompt-file", "prompt.txt", "--seed", str(seed), "--steps", "8",
        "--width", str(canvas[0]), "--height", str(canvas[1]), "--low-ram",
        "--make-conf", "--output", "raw.png",
    ]
    env = os.environ | {"HF_HUB_OFFLINE": "1", "PYTHONUNBUFFERED": "1", "HF_HOME": str(Path.cwd() / "hf-cache")}
    begin = time.monotonic()
    with Path("generation.log").open("xb", buffering=0) as log:
        child = subprocess.Popen(args, env=env, start_new_session=True, stdout=log, stderr=subprocess.STDOUT)
        try:
            while child.poll() is None:
                if Path("cancel-requested").is_dir():
                    stop_child(child)
                    publish("cancelled")
                    return
                if not siliconed_idle(str(silicontrol)):
                    raise JobFailure("siliconed-conflict")
                publish("running", step=progress())
                time.sleep(2)
        finally:
            stop_child(child)
    if Path("cancel-requested").is_dir():
        publish("cancelled")
        return
    if child.returncode != 0:
        raise JobFailure("mflux-exit")
    publish("composing", step=8)
    pixels = compose(source, mask, content, canvas)
    log = Path("generation.log").read_text(encoding="utf-8", errors="replace")
    gpu = re.search(r"Peak MLX memory:\s*([\d.]+)\s*GB", log)
    publish("done", step=8, width=source.width, height=source.height, rawWidth=canvas[0],
            rawHeight=canvas[1], seconds=time.monotonic() - begin,
            peakMlxGb=float(gpu.group(1)) if gpu else None, insideChanged=pixels, outsideChanged=0)


def main():
    try:
        lock_path = Path.home() / ".revit-ai-render-v020" / "inference.lock"
        with lock_path.open("a+") as lease:
            try:
                fcntl.flock(lease.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError as error:
                if error.errno in (errno.EACCES, errno.EAGAIN):
                    raise JobFailure("mflux-busy") from error
                raise
            run()
    except JobFailure as error:
        publish("failed", code=error.code)
        print(error.code, flush=True)
        return 1
    except Exception:
        traceback.print_exc()
        publish("failed", code="mflux-worker")
        return 1
    return 0


if __name__ == "__main__":
    os.chdir(sys.argv[1])
    raise SystemExit(main())
