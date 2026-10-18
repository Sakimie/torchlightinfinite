"""Explicit-action update UI. Starting the launcher or game never checks online."""
from __future__ import annotations

import importlib.util
from pathlib import Path
import queue
import threading
import tkinter as tk
from tkinter import filedialog, ttk


def _updater_type():
    try:
        from talos_update_core import Updater
        return Updater
    except ModuleNotFoundError as error:
        if error.name != "talos_update_core":
            raise
        spec = importlib.util.spec_from_file_location("talos_update_core", Path(__file__).with_name("talos_update_core.py"))
        if spec is None or spec.loader is None:
            raise RuntimeError("未找到更新组件，请重新解压首次更新器包。")
        module = importlib.util.module_from_spec(spec)
        import sys
        sys.modules[spec.name] = module
        spec.loader.exec_module(module)
        return module.Updater


def version_is_newer(candidate, current):
    if not current:
        return True
    try:
        left, right = tuple(int(n) for n in candidate.split(".")), tuple(int(n) for n in current.split("."))
        return left > right
    except (ValueError, AttributeError):
        return False


def package_size(size):
    size = max(0, int(size or 0))
    return f"{size / (1024 * 1024):.2f} MB" if size >= 1024 * 1024 else f"{size / 1024:.1f} KB"


class LauncherController:
    """Worker-only core calls; clients drain events on their own UI thread."""
    def __init__(self, install_root, public_key_pem, feed_url="", updater_factory=None):
        self.install_root = Path(install_root)
        self.public_key_pem = public_key_pem
        self.feed_url = feed_url
        self.factory = updater_factory
        self.updater = None
        self.events = queue.Queue()
        self.busy = False
        self.token = 0
        self.operation = None
        self.cancel_event = threading.Event()

    def start(self, operation, payload=None):
        if self.busy:
            return False
        self.busy = True
        self.token += 1
        token = self.token
        self.operation = operation
        self.cancel_event = threading.Event()
        cancel_event = self.cancel_event

        def progress(data):
            self.events.put({"type": "progress", "token": token, "operation": operation, "data": data})
            return not cancel_event.is_set()

        def work():
            try:
                if self.updater is None:
                    factory = self.factory or _updater_type()
                    self.updater = factory(self.install_root, self.public_key_pem, feed_url=self.feed_url)
                if operation == "status":
                    result = {"status": self.updater.status()}
                elif operation == "check":
                    if not self.feed_url:
                        raise RuntimeError("尚未配置更新地址。当前助手仍可离线使用，也可以导入已签名的离线更新文件。")
                    manifest = self.updater.check()
                    result = {"manifest": manifest}
                elif operation == "install":
                    installed = self.updater.install(payload, progress=progress)
                    result = {"installed": installed, "status": self.updater.status()}
                elif operation == "install_local":
                    installed = self.updater.install_local(payload[0], payload[1])
                    result = {"installed": installed, "status": self.updater.status()}
                elif operation == "rollback":
                    rolled_back = self.updater.rollback()
                    result = {"installed": rolled_back, "status": self.updater.status()}
                elif operation == "launch":
                    result = {"launched": self.updater.launch_active()}
                else:
                    raise RuntimeError("未知更新操作。")
                if operation == "check" and cancel_event.is_set():
                    self.events.put({"type": "cancelled", "token": token, "operation": operation})
                else:
                    self.events.put({"type": "done", "token": token, "operation": operation, **result})
            except Exception as error:
                cancelled = getattr(error, "code", "") == "cancelled" or (operation == "check" and cancel_event.is_set())
                self.events.put({"type": "cancelled" if cancelled else "error", "token": token, "operation": operation, "message": str(error)})
        self.worker = threading.Thread(target=work, daemon=False)
        self.worker.start()
        return True

    def finish(self, token):
        if token == self.token:
            self.busy = False

    def cancel(self):
        if self.busy and self.operation in ("check", "install"):
            self.cancel_event.set()
            return True
        return False


class Launcher:
    def __init__(self, root, install_root, public_key_pem, config=None, controller=None):
        self.root = root
        self.config = config or {}
        self.controller = controller or LauncherController(install_root, public_key_pem, self.config.get("feed_url", ""))
        self.local_status = {"active": None, "history": [], "installed": {}}
        self.status_loaded = False
        self.manifest = None
        self.closed = False
        self.display_token = 0
        self.version_text = tk.StringVar(root, "正在读取本地版本…")
        self.status_text = tk.StringVar(root, "正在检查本地文件，不联网。")
        self.release_text = tk.StringVar(root, "尚未检查更新")
        self.size_text = tk.StringVar(root, "")
        self.history_text = tk.StringVar(root, "")
        self._build()
        root.protocol("WM_DELETE_WINDOW", self.close)
        self.start("status")
        root.after(90, self.pump)

    def _build(self):
        root = self.root
        root.title("典狱助手 · 启动与更新")
        root.geometry("740x600")
        root.minsize(620, 540)
        root.configure(background="#101720")
        style = ttk.Style(root)
        style.theme_use("clam")
        style.configure("Update.TFrame", background="#101720")
        style.configure("Update.TLabel", background="#101720", foreground="#e5ecf5", font=("Microsoft YaHei UI", 10))
        style.configure("Update.Muted.TLabel", background="#101720", foreground="#a1afbd", font=("Microsoft YaHei UI", 9))
        style.configure("Update.Title.TLabel", background="#101720", foreground="#e5ecf5", font=("Microsoft YaHei UI", 19, "bold"))
        style.configure("Update.TButton", padding=(12, 8), font=("Microsoft YaHei UI", 10))
        frame = ttk.Frame(root, style="Update.TFrame", padding=24)
        frame.pack(fill="both", expand=True)
        ttk.Label(frame, text="典狱助手", style="Update.Title.TLabel").pack(anchor="w")
        ttk.Label(frame, text="截图和建议保持离线；仅在点击检查或下载更新时联网。", style="Update.Muted.TLabel", wraplength=660).pack(anchor="w", pady=(7, 18))
        ttk.Label(frame, textvariable=self.version_text, style="Update.TLabel").pack(anchor="w")
        actions = ttk.Frame(frame, style="Update.TFrame")
        actions.pack(fill="x", pady=(13, 18))
        self.launch_button = ttk.Button(actions, text="启动助手", style="Update.TButton", command=lambda: self.start("launch"))
        self.launch_button.pack(side="left")
        self.check_button = ttk.Button(actions, text="检查更新", style="Update.TButton", command=lambda: self.start("check"))
        self.check_button.pack(side="left", padx=10)
        self.refresh_button = ttk.Button(actions, text="刷新本地状态", command=lambda: self.start("status"))
        self.refresh_button.pack(side="right")
        ttk.Separator(frame).pack(fill="x")
        details = ttk.Frame(frame, style="Update.TFrame")
        details.pack(fill="x", pady=(18, 8))
        ttk.Label(details, textvariable=self.release_text, style="Update.TLabel").pack(side="left")
        ttk.Label(details, textvariable=self.size_text, style="Update.Muted.TLabel").pack(side="right")
        self.notes = tk.Text(frame, height=8, wrap="word", background="#18222e", foreground="#cad5df", insertbackground="#cad5df", relief="flat", padx=13, pady=12, font=("Microsoft YaHei UI", 10))
        self.notes.pack(fill="both", expand=True)
        self._notes("更新由你手动检查和安装。检查失败或没有网络时，仍可启动已经安装的助手。\n\n首次配置可在本机离线完成；尚未配置公开更新渠道时，可导入发布者提供的签名文件与 ZIP。")
        install_actions = ttk.Frame(frame, style="Update.TFrame")
        install_actions.pack(fill="x", pady=(13, 8))
        self.install_button = ttk.Button(install_actions, text="下载并安装此更新", style="Update.TButton", command=lambda: self.start("install", self.manifest))
        self.install_button.pack(side="left")
        self.cancel_button = ttk.Button(install_actions, text="取消", command=self.cancel)
        self.cancel_button.pack(side="left", padx=10)
        self.progress = ttk.Progressbar(frame, mode="determinate")
        self.progress.pack(fill="x", pady=(5, 9))
        ttk.Label(frame, textvariable=self.status_text, style="Update.Muted.TLabel", wraplength=670).pack(anchor="w")
        bottom = ttk.Frame(frame, style="Update.TFrame")
        bottom.pack(fill="x", pady=(16, 4))
        self.local_button = ttk.Button(bottom, text="离线导入更新…", command=self.import_local)
        self.local_button.pack(side="left")
        self.rollback_button = ttk.Button(bottom, text="回退上一版", command=lambda: self.start("rollback"))
        self.rollback_button.pack(side="left", padx=10)
        ttk.Label(frame, textvariable=self.history_text, style="Update.Muted.TLabel", wraplength=670).pack(anchor="w", pady=(7, 0))
        self.buttons = [self.launch_button, self.check_button, self.refresh_button, self.install_button, self.local_button, self.rollback_button]

    def _notes(self, text):
        self.notes.configure(state="normal")
        self.notes.delete("1.0", "end")
        self.notes.insert("end", text)
        self.notes.configure(state="disabled")

    def _controls(self):
        busy = self.controller.busy
        for button in self.buttons:
            button.configure(state="disabled" if busy else "normal")
        self.launch_button.configure(state="normal" if not busy and self.local_status.get("active") and self.local_status.get("active_valid") is not False else "disabled")
        active = self.local_status.get("active")
        repair = self.manifest is not None and self.local_status.get("active_valid") is False and self.manifest.get("version") == active
        available = self.manifest is not None and (version_is_newer(self.manifest.get("version", ""), active) or repair)
        self.install_button.configure(state="normal" if not busy and available else "disabled")
        older = [version for version in self.local_status.get("history", []) if version != active and ("active_valid" not in self.local_status or version in self.local_status.get("installed", {}))]
        can_recover = not getattr(self, "status_loaded", False) or bool(older)
        self.rollback_button.configure(state="normal" if not busy and can_recover else "disabled")
        self.cancel_button.configure(state="normal" if busy and self.controller.operation in ("check", "install") else "disabled")

    def start(self, operation, payload=None):
        if self.closed:
            return
        if operation == "install" and payload is None:
            return
        if not self.controller.start(operation, payload):
            return
        self.display_token = self.controller.token
        labels = {"status": "正在验证本地文件，不联网…", "check": "正在联网检查更新…", "install": "正在下载和验证更新；完成前保留当前版本…", "install_local": "正在离线验证并安装更新…", "rollback": "正在验证并回退本地版本，不联网…", "launch": "正在验证并启动当前助手，不联网…"}
        self.status_text.set(labels[operation])
        self.progress.configure(mode="indeterminate")
        self.progress.start(12)
        self._controls()

    def cancel(self):
        if self.controller.cancel():
            text = "已请求取消；等待当前网络请求结束，不会应用检查结果。" if self.controller.operation == "check" else "已请求取消下载，正在保留原版本并清理临时文件。"
            self.status_text.set(text)
            self.cancel_button.configure(state="disabled")

    def import_local(self):
        if self.controller.busy:
            return
        feed = filedialog.askopenfilename(parent=self.root, title="选择发布者提供的签名更新文件", filetypes=[("签名更新文件", "*.json")])
        if not feed:
            return
        archive = filedialog.askopenfilename(parent=self.root, title="选择与签名文件配套的更新 ZIP", filetypes=[("更新包", "*.zip")])
        if archive:
            self.start("install_local", (feed, archive))

    def _status(self, status):
        self.status_loaded = True
        self.local_status = status
        active = status.get("active")
        damaged = status.get("active_valid") is False
        self.version_text.set("当前版本：" + (active or "尚未安装") + "（文件异常，暂不可启动）" if damaged else "当前已启用版本：" + (active or "尚未安装"))
        previous = [version for version in status.get("history", []) if version != active and ("active_valid" not in status or version in status.get("installed", {}))]
        self.history_text.set("保留的本地版本：" + ("、".join(previous) if previous else "暂无；更新成功后会保留前版供回退。"))

    def _event(self, event):
        if event.get("token") != self.display_token:
            return
        kind, operation = event["type"], event["operation"]
        if kind == "progress":
            data = event.get("data") or {}
            if data.get("total", 0) > 0:
                self.progress.stop()
                self.progress.configure(mode="determinate", maximum=data["total"], value=data.get("done", 0))
            if not self.controller.cancel_event.is_set():
                self.status_text.set(data.get("message") or {"download": "正在下载更新…", "verify": "正在验证更新文件…", "extract": "正在准备新版本…", "dependencies": "正在准备本机运行文件…", "activate": "正在切换新版本…"}.get(data.get("stage"), "正在更新…"))
            return
        self.progress.stop()
        self.progress.configure(mode="determinate", value=0)
        self.controller.finish(event["token"])
        if kind == "error":
            if operation == "status":
                self.status_loaded = False
            suffix = " 已安装版本仍保留，可以继续启动。" if self.local_status.get("active") else " 可以重试或导入离线更新包。"
            if operation in ("status", "launch"):
                suffix = " 可尝试回退健康旧版或离线导入更新恢复；不会启动未经验证的文件。"
            self.status_text.set("操作未完成：" + event.get("message", "未知错误") + suffix)
        elif kind == "cancelled":
            self.status_text.set("本次操作已取消，当前启用版本保持不变。")
        else:
            if "status" in event:
                self._status(event["status"])
            if operation == "check":
                self.manifest = event["manifest"]
                version = self.manifest["version"]
                newer = version_is_newer(version, self.local_status.get("active"))
                repair = self.local_status.get("active_valid") is False and version == self.local_status.get("active")
                self.release_text.set(("可修复当前版本：" if repair else "可用新版本：" if newer else "没有更高版本：") + version)
                self.size_text.set("下载大小：" + package_size(self.manifest.get("package", {}).get("bytes", 0)))
                notes = self.manifest.get("notes", [])
                self._notes("\n\n".join("• " + str(note) for note in notes) or "此版本没有附加更新说明。")
                self.status_text.set("当前版本文件异常。更新说明已验证，点击下载并安装可重新恢复此版本。" if repair else "更新说明已验证。点击“下载并安装此更新”后才会下载。" if newer else "当前已经是此渠道提供的最新版本，无需下载。")
            elif operation in ("install", "install_local"):
                self.status_text.set("更新已安装并启用。正在运行的旧窗口不受影响；下次启动助手将使用新版本。")
                self.release_text.set("已安装版本：" + event["installed"]["version"])
            elif operation == "rollback":
                self.status_text.set("已回退并启用 " + event["installed"]["version"] + "，点击“启动助手”打开此版本。")
            elif operation == "launch":
                self.status_text.set("助手 " + event["launched"]["version"] + " 已启动。游戏截图与建议保持离线。")
            else:
                self.status_text.set("当前版本文件损坏，请回退健康前版或安装更新；未连接更新网站。" if self.local_status.get("active_valid") is False else "本地版本已就绪；未连接更新网站。" if self.local_status.get("active") else "没有已启用版本，请导入离线更新包或手动检查更新。")
        self._controls()

    def pump(self):
        if self.closed:
            return
        try:
            while True:
                try:
                    event = self.controller.events.get_nowait()
                except queue.Empty:
                    break
                self._event(event)
            self.root.after(90, self.pump)
        except tk.TclError:
            self.closed = True

    def close(self):
        self.closed = True
        self.controller.cancel()
        self.root.destroy()


def run(install_root, public_key_pem, config=None):
    root = tk.Tk()
    Launcher(root, install_root, public_key_pem, config=config)
    root.mainloop()
