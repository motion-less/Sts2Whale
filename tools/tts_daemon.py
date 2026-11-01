#!/usr/bin/env python3
"""常驻 TTS 服务（edge-tts 后端）。

游戏启动时由 mod 拉起，游戏退出时收掉，外加三道保险：
  1. --parent <pid>：父进程没了就自杀
  2. --idle <分钟>：长时间没请求就自退
  3. 端口/健康检查：下次启动先探活，死了就重拉

接口：
  GET  /health          → {"ok": true, "voice": ...}
  POST /say             → {"text","voice","rate","pitch"}，返回 24k 单声道 wav 字节
  POST /shutdown        → 关闭
"""

import argparse
import asyncio
import json
import os
import sys
import shutil
import subprocess
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import edge_tts

STATE = {
    "voice": "zh-CN-XiaoyiNeural",
    "last_request": time.time(),
    "synth_ms": 0,
    "requests": 0,
}
LOCK = threading.Lock()


def synth_audio(text: str, voice: str, rate: str, pitch: str) -> tuple[bytes, str]:
    """合成一句话，返回 (音频字节, MIME)。

    macOS 上用 afconvert 转成 24k 单声道 wav（mod 用它算精确口型）；
    其他平台直接返回 mp3（mod 用 Godot 内置解码播放，口型按字数估）。
    """
    tmpdir = tempfile.mkdtemp(prefix="ttsd-")
    mp3 = os.path.join(tmpdir, "a.mp3")
    wav = os.path.join(tmpdir, "a.wav")

    async def run():
        await edge_tts.Communicate(text, voice, rate=rate, pitch=pitch).save(mp3)

    asyncio.run(run())

    with open(mp3, "rb") as f:
        raw = f.read()

    data, mime = raw, "audio/mpeg"
    if sys.platform == "darwin" and shutil.which("afconvert"):
        try:
            subprocess.run(
                ["afconvert", "-f", "WAVE", "-d", "LEI16@24000", "-c", "1", mp3, wav],
                check=True,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            with open(wav, "rb") as f:
                data, mime = f.read(), "audio/wav"
        except Exception as e:
            log(f"afconvert 失败，回退 mp3: {e}")

    for p in (mp3, wav):
        try:
            os.remove(p)
        except OSError:
            pass
    try:
        os.rmdir(tmpdir)
    except OSError:
        pass
    return data, mime


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def _json(self, obj, code=200):
        body = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.startswith("/health"):
            self._json({"ok": True, "voice": STATE["voice"], "requests": STATE["requests"]})
        else:
            self._json({"ok": False, "error": "not found"}, 404)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length) if length else b"{}"

        if self.path.startswith("/shutdown"):
            self._json({"ok": True})
            threading.Timer(0.2, lambda: os._exit(0)).start()
            return

        if not self.path.startswith("/say"):
            self._json({"ok": False, "error": "not found"}, 404)
            return

        try:
            req = json.loads(raw or b"{}")
            text = (req.get("text") or "").strip()
            if not text:
                self._json({"ok": False, "error": "empty text"}, 400)
                return

            voice = req.get("voice") or STATE["voice"]
            rate = req.get("rate") or "+0%"
            pitch = req.get("pitch") or "+0Hz"

            started = time.time()
            wav, mime = synth_audio(text, voice, rate, pitch)
            elapsed = int((time.time() - started) * 1000)

            with LOCK:
                STATE["last_request"] = time.time()
                STATE["synth_ms"] = elapsed
                STATE["requests"] += 1

            self.send_response(200)
            self.send_header("Content-Type", mime)
            self.send_header("Content-Length", str(len(wav)))
            self.send_header("X-Synth-Ms", str(elapsed))
            self.end_headers()
            self.wfile.write(wav)
        except Exception as e:
            self._json({"ok": False, "error": f"{type(e).__name__}: {e}"}, 500)


def watch_parent(pid: int, interval: float = 1.5):
    """父进程（游戏）没了就跟着退出。

    Windows 上 os.kill(pid, 0) 不是「探活」而是 TerminateProcess(pid, 0)：
    会把游戏直接杀掉；只有父进程已经退出时才会抛 OSError。所以 Windows
    必须用 OpenProcess + WaitForSingleObject 等句柄，绝不能调 os.kill。
    """
    if sys.platform == "win32":
        import ctypes

        SYNCHRONIZE = 0x00100000
        INFINITE = 0xFFFFFFFF
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(SYNCHRONIZE, False, pid)
        if not handle:
            print(f"watch_parent: OpenProcess({pid}) failed, watchdog disabled", flush=True)
            return
        try:
            kernel32.WaitForSingleObject(handle, INFINITE)
        finally:
            kernel32.CloseHandle(handle)
        os._exit(0)
        return

    while True:
        time.sleep(interval)
        try:
            os.kill(pid, 0)
        except OSError:
            os._exit(0)


def watch_idle(minutes: int):
    if minutes <= 0:
        return
    while True:
        time.sleep(30)
        with LOCK:
            idle = time.time() - STATE["last_request"]
        if idle > minutes * 60:
            os._exit(0)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=48765)
    ap.add_argument("--parent", type=int, default=0)
    ap.add_argument("--idle", type=int, default=30)
    ap.add_argument("--voice", default="zh-CN-XiaoyiNeural")
    args = ap.parse_args()

    STATE["voice"] = args.voice

    if args.parent:
        threading.Thread(target=watch_parent, args=(args.parent,), daemon=True).start()
    threading.Thread(target=watch_idle, args=(args.idle,), daemon=True).start()

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"tts-daemon listening 127.0.0.1:{args.port} parent={args.parent} idle={args.idle}m", flush=True)

    # 预热：把 TLS/edge 连接先热起来，第一句话不再等 5 秒
    def warm():
        try:
            synth_audio("在。", args.voice, "+0%", "+0Hz")
            print("warmup done", flush=True)
        except Exception as e:
            print(f"warmup failed: {e}", flush=True)

    threading.Thread(target=warm, daemon=True).start()

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    sys.exit(main())
