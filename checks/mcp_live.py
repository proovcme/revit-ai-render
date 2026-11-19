"""Launch one actual Siliconed test against an explicitly selected Revit test project."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import time
import uuid

from mcp_smoke import Client


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("server", type=Path)
    parser.add_argument("--project", required=True)
    parser.add_argument("--view", required=True)
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--request-id", required=True, type=uuid.UUID)
    parser.add_argument("--prompt", default="Визуализируй фасады из светлого камня и кирпича в мягком вечернем свете. Сохрани ракурс и форму зданий.")
    parser.add_argument("--element-id", action="append", help="ElementId or UniqueId for a masked job")
    args = parser.parse_args()
    request_id = args.request_id.hex
    client = Client([str(args.server.resolve()), "--revit-pid", str(args.pid)])
    try:
        client.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                      "clientInfo": {"name": "air-live-check", "version": "1"}})
        client.send({"method": "notifications/initialized"})
        views = client.call("render_views", {})["structuredContent"]
        if views["project"] != args.project:
            raise RuntimeError("Active project does not match the selected test project.")
        while True:
            view = next((view for view in views["views"] if view["name"] == args.view), None)
            if view:
                break
            if "nextOffset" not in views:
                raise RuntimeError("Test view not found.")
            views = client.call("render_views", {"offset": views["nextOffset"]})["structuredContent"]
            if views["project"] != args.project:
                raise RuntimeError("Active project changed.")
        start = {"requestId": request_id, "project": args.project, "projectToken": views["projectToken"],
                 "viewId": view["id"],
                 "prompt": args.prompt}
        if args.element_id:
            start["elementIds"] = args.element_id
        result = client.call("start_render", start)["structuredContent"]
        backend = "mflux" if args.element_id else "siliconed"
        assert result["backend"] == backend, result
        assert result["id"] == request_id, result
        print(f"ACCEPTED {request_id}", flush=True)
        replay = client.call("start_render", start)["structuredContent"]
        assert replay["id"] == request_id
        previous = None
        deadline = time.monotonic() + 900
        while time.monotonic() < deadline:
            status = client.call("render_status", {"jobId": request_id})["structuredContent"]
            current = (status["state"], status.get("progress"), status.get("message"))
            if current != previous:
                print(json.dumps(current, ensure_ascii=False), flush=True)
                previous = current
            if status["resultReady"]:
                assert status["state"] == "done"
                break
            if status["state"] in {"failed", "interrupted", "cancelled"}:
                raise RuntimeError("Render did not complete.")
            time.sleep(7)
        else:
            raise TimeoutError("Render still running. Use render_status with the same request ID.")
        reply = client.call("get_render", {"jobId": request_id})
        image = next(block for block in reply["content"] if block["type"] == "image")
        assert image["mimeType"] == "image/png"
        received = base64.b64decode(image["data"])
        disk = Path(os.environ["LOCALAPPDATA"]) / "RevitAiRender020" / "jobs" / request_id / "result.png"
        digest = hashlib.sha256(received).hexdigest()
        assert digest == hashlib.sha256(disk.read_bytes()).hexdigest()
        print(f"PASS MCP start → {backend} → PNG: {status['width']}x{status['height']}, SHA-256 {digest}", flush=True)
    finally:
        client.close()


if __name__ == "__main__":
    main()
