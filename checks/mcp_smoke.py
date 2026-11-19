"""Exercise the published MCP executable over stdio against a test or live Revit session."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import threading


class Client:
    def __init__(self, command):
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        encoding="utf-8", text=True)
        self.messages = queue.Queue()
        self.request_id = 0
        threading.Thread(target=self.read, daemon=True).start()

    def read(self):
        for line in self.process.stdout:
            self.messages.put(json.loads(line))

    def send(self, value):
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", **value}) + "\n")
        self.process.stdin.flush()

    def request(self, method, params):
        self.request_id += 1
        self.send({"id": self.request_id, "method": method, "params": params})
        while True:
            reply = self.messages.get(timeout=30)
            if reply.get("id") == self.request_id:
                if "error" in reply:
                    raise RuntimeError(reply["error"])
                return reply["result"]

    def call(self, name, args, expect_error=False):
        reply = self.request("tools/call", {"name": name, "arguments": args})
        if bool(reply.get("isError")) != expect_error:
            raise RuntimeError(reply.get("content", [{}])[0].get("text", f"Unexpected response from {name}"))
        return reply

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=6)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=6)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("server", type=Path)
    parser.add_argument("--pid", type=int)
    parser.add_argument("--fixture", type=Path)
    parser.add_argument("--job-id", required=True)
    args = parser.parse_args()
    fixture = None
    if args.fixture:
        fixture = subprocess.Popen([str(args.fixture.resolve()), "--mcp-fixture"], stdout=subprocess.PIPE,
                                   text=True)
        while True:
            line = fixture.stdout.readline()
            if line.startswith("READY "):
                args.pid = int(line.split()[1])
                break
            if not line:
                raise RuntimeError("Test fixture stopped.")
    command = [str(args.server.resolve())]
    if args.pid:
        command += ["--revit-pid", str(args.pid)]

    client = Client(command)
    try:
        client.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                      "clientInfo": {"name": "air-smoke", "version": "1"}})
        client.send({"method": "notifications/initialized"})
        names = {tool["name"] for tool in client.request("tools/list", {})["tools"]}
        assert names == {"render_views", "start_render", "render_status", "get_render", "list_renders"}, names
        views = client.call("render_views", {})["structuredContent"]
        assert views["project"] and views["projectToken"] and views["views"]
        root = Path(os.environ["LOCALAPPDATA"]) / "RevitAiRender020" / "jobs" / args.job_id
        stored = json.loads((root / "job.json").read_text(encoding="utf-8"))
        status = client.call("render_status", {"jobId": args.job_id})["structuredContent"]
        assert status["resultReady"] and status["state"] == "done"
        listing = client.call("list_renders", {"project": status["project"], "limit": 25})["structuredContent"]
        assert any(job["id"] == args.job_id for job in listing["jobs"])
        assert all(job["project"] == status["project"] for job in listing["jobs"])
        for original, name in [(False, "result.png"), (True, "source.png")]:
            reply = client.call("get_render", {"jobId": args.job_id, "original": original})
            images = [block for block in reply["content"] if block["type"] == "image"]
            assert len(images) == 1 and images[0]["mimeType"] == "image/png"
            assert hashlib.sha256(base64.b64decode(images[0]["data"])).digest() == hashlib.sha256((root / name).read_bytes()).digest()
        client.call("render_status", {"jobId": "../settings.json"}, expect_error=True)
        start = {"requestId": args.job_id, "project": stored["Project"], "projectToken": views["projectToken"],
                 "viewId": stored["ViewUniqueId"], "preserveGeometry": stored["PreserveGeometry"], "prompt": stored["Prompt"]}
        if not status["backend"] == "mflux":
            again = client.call("start_render", start)["structuredContent"]
            assert again["id"] == args.job_id and again["state"] == "done"
            start["prompt"] += " conflicting input"
            client.call("start_render", start, expect_error=True)
        print(f"PASS MCP: five tools, project views, history, status, source/render bytes, invalid ID and request replay. Pipe PID={args.pid}")
    finally:
        client.close()
        if fixture:
            fixture.terminate()
            fixture.wait(timeout=5)


if __name__ == "__main__":
    main()
