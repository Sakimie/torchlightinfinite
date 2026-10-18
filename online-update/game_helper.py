"""Small offline advice bridge for the existing TalosHelper v0.3 Tk application.

The original screenshot, hotkey and image import code remains in the host app.
Recognized fields are suggestions for a mandatory manual review, never ground truth.
"""
from __future__ import annotations

import copy
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import tkinter as tk
from tkinter import ttk


PHASES = {
    "patrol": "巡逻 / 选择目的地", "manage": "管理牢房", "office": "审讯室",
    "event": "事件选择", "tool": "选择刑具", "gift": "选择珍藏 / 恩赐",
    "chain": "连吃 / 选择食物", "ratway": "鼠道 / 选择牢房",
}
KINDS = {"unknown": "未核对", "locked": "未开启", "empty": "空牢房", "nest": "鼠巢", "inmate": "囚犯", "office": "审讯室"}
BEASTS = {"mammoth": "巨象", "lion": "狂狮", "wolf": "头狼", "rat": "硕鼠"}
TIERS = ["普通", "魔法", "稀有", "传奇", "至臻", "绮彩（请确认入库）"]
OBJECTIVES = {"king": "至少一名至臻", "walk": "移动任务", "incident": "事件任务", "consume": "吞噬任务", "other": "其他任务"}
OLD_PHASES = {"move": "patrol", "decision": "manage", "eat": "chain", "interrogation": "office", "treasure": "gift"}
SOURCE_NOTE = "本地近似规则建议；未知房间和随机事件会影响结果，不保证实机最优。"
UNKNOWN_EVENT = "类型尚未揭示 / 未知"


def _integer(value, low, high, label):
    if isinstance(value, bool):
        raise ValueError(f"{label}必须为整数。")
    try:
        number = int(value)
    except (TypeError, ValueError):
        raise ValueError(f"请手动填写{label}（{low}–{high}）。") from None
    if str(value).strip() not in (str(number), f"{number}.0") or not low <= number <= high:
        raise ValueError(f"{label}须为 {low}–{high} 的整数。")
    return number


def prefill_state(old, previous=None):
    """Create visibly unconfirmed fields; old remaining-days never becomes day."""
    old = old if isinstance(old, dict) else {}
    prior = copy.deepcopy(previous) if isinstance(previous, dict) else {}
    cells = []
    observed = old.get("board", {})
    for n in range(16):
        prisoner = observed.get(str(n + 1), observed.get(n + 1))
        cell = {"n": n, "kind": "office" if n == 0 else "unknown", "event": False, "ivory": False, "scratch": False}
        if n and isinstance(prisoner, dict):
            beast = {"elephant": "mammoth"}.get(prisoner.get("species"), prisoner.get("species"))
            cell.update(kind="inmate", beast=beast, tier=prisoner.get("tier"))
        cells.append(cell)
    choices = []
    for choice in old.get("choices", []):
        if isinstance(choice, dict) and isinstance(choice.get("room"), int) and 1 <= choice["room"] <= 16:
            n = choice["room"] - 1
            choices.append(n)
            if choice.get("event"):
                cells[n]["event"] = True
                cells[n]["eventKind"] = "unknown"
    tools = [key for flag, key in (("box", "revenge"), ("saw", "saw"), ("collar", "collar")) if old.get(flag)]
    current = old.get("current")
    position = current - 1 if isinstance(current, int) and not isinstance(current, bool) and 1 <= current <= 16 else ""
    result = {
        "day": prior.get("day", ""), "totalDays": 20,
        "position": position, "phase": OLD_PHASES.get(old.get("phase"), "patrol"),
        "destinations": choices, "cells": cells, "tools": tools,
        "vault": old.get("rainbows", 0), "objective": prior.get("objective", "other"),
        "objectiveProgress": old.get("progress", prior.get("objectiveProgress", 0)),
        "milestone": prior.get("milestone", False), "usedAction": prior.get("usedAction", False),
        "chainArmed": prior.get("chainArmed", False), "chainConsumed": old.get("today_eats", ""),
        "usurpStage": prior.get("usurpStage", "unknown"), "eventKind": prior.get("eventKind"),
        "offerIds": [], "resumePhase": None, "startEvent": None,
        "source": "manual", "confirmed": False, "needsReview": ["全部识图预填和上次人工值均待核对。"],
        "remainingDaysHint": old.get("days"),
    }
    if prior:
        for key in ("day", "position", "phase", "destinations", "cells", "tools", "vault", "objective", "objectiveProgress", "milestone", "usedAction", "chainArmed", "chainConsumed", "usurpStage", "eventKind", "offerIds", "resumePhase"):
            if key in prior:
                result[key] = copy.deepcopy(prior[key])
        result["confirmed"] = False
    return result


def validate_board(form, metadata):
    """Validate explicit user entries without inventing unobserved room states."""
    if form.get("confirmed") is not True:
        raise ValueError("请先勾选“已逐格核对当前画面和控制信息”。")
    if form.get("preyPresent"):
        raise ValueError("当前存在猎物标记，本补丁尚未录入其对象与剩余天数，不能忽略后推荐。")
    if form.get("usedExtra") or form.get("devourAfterSwap"):
        raise ValueError("当前存在额外管理操作或替换后继续吞噬，本补丁暂不支持，不能按普通操作推荐。")
    if form.get("hiddenKnownPresent"):
        raise ValueError("当前已探知隐藏房间的种族或稀有度，本补丁尚无对应录入，不能当作未知房间推荐。")
    if form.get("refreshPresent"):
        raise ValueError("当前存在刑具或珍藏刷新机会，本补丁尚不支持，请先处理后重新核对。")
    if form.get("unsupportedToolPresent"):
        raise ValueError("持有未列出的刑具（例如捕兽夹），本补丁尚未支持其效果，不能遗漏后推荐。")
    if form.get("unsupportedEventPresent"):
        raise ValueError("当前为未列出的事件（例如有福同享、宿敌、同罪移押），本补丁尚不支持，不能忽略后推荐。")
    if form.get("startEvent") not in (None, "", "ordinary"):
        raise ValueError("当前特殊开局尚未支持，请仅在普通20天开局使用此建议。")
    board = copy.deepcopy(form)
    board["day"] = _integer(form.get("day"), 1, 20, "当前第几天")
    board["totalDays"] = 20
    board["position"] = _integer(form.get("position"), 0, 15, "当前位置索引")
    if form.get("phase") not in PHASES:
        raise ValueError("请确认当前操作阶段。")
    cells = form.get("cells")
    if not isinstance(cells, list) or len(cells) != 16:
        raise ValueError("请核对完整16格，包括审讯室。")
    for n, cell in enumerate(cells):
        if not isinstance(cell, dict) or cell.get("n") != n:
            raise ValueError("房号必须从01审讯室连续至16号。")
        if cell.get("scratch"):
            raise ValueError(f"{n + 1}号存在抓痕，尚未录入隐藏信息，本补丁不能忽略标记推荐。")
        kind = cell.get("kind")
        if (n == 0 and kind != "office") or (n > 0 and kind not in ("locked", "empty", "nest", "inmate")):
            raise ValueError(f"请逐格确认{n + 1}号类型；旧版空白不能区分未开启和空房。")
        if kind == "inmate":
            beast = {"elephant": "mammoth"}.get(cell.get("beast"), cell.get("beast"))
            if beast not in BEASTS:
                raise ValueError(f"请确认{n + 1}号囚犯种族。")
            tier = _integer(cell.get("tier"), 0, 5, f"{n + 1}号稀有度")
            if tier == 5:
                raise ValueError(f"{n + 1}号绮彩应先确认是否已入库；入库后改为未开启，并更新已捕获绮彩数。")
            board["cells"][n]["beast"] = beast
            board["cells"][n]["tier"] = tier
        if kind == "nest":
            board["cells"][n]["due"] = _integer(cell.get("due"), board["day"] + 1, 30, f"{n + 1}号巢穴孵化日")
        if cell.get("event"):
            event_kind = cell.get("eventKind")
            if event_kind in (None, "", "unknown"):
                board["cells"][n].pop("eventKind", None)
            elif event_kind not in {x["id"] for x in metadata.get("events", [])}:
                raise ValueError(f"{n + 1}号事件类型尚未支持，不能忽略后推荐。")
    board["destinations"] = list(dict.fromkeys(_integer(n, 0, 15, "目的地索引") for n in form.get("destinations", [])))
    if form["phase"] == "patrol" and not board["destinations"]:
        raise ValueError("巡逻阶段请逐格勾选游戏当前提供的目的地。")
    tool_ids = {x["id"] for x in metadata.get("tools", [])}
    gift_ids = {x["id"] for x in metadata.get("gifts", [])}
    event_ids = {x["id"] for x in metadata.get("events", [])}
    board["tools"] = list(dict.fromkeys(form.get("tools", [])))
    if any(tool not in tool_ids for tool in board["tools"]):
        raise ValueError("存在未录入的刑具，不能忽略后推荐。")
    board["vault"] = _integer(form.get("vault", 0), 0, 200, "已捕获绮彩数")
    if form.get("objective") not in OBJECTIVES:
        raise ValueError("请确认左上角任务。")
    board["objectiveProgress"] = _integer(form.get("objectiveProgress", 0), 0, 99, "任务进度")
    stage = form.get("usurpStage")
    if stage == "done":
        board["usurpStage"] = "done"
    elif stage in (1, 2, 3, 4, "1", "2", "3", "4") and not isinstance(stage, bool):
        board["usurpStage"] = int(stage)
    else:
        raise ValueError("请确认僭越进度：尚未触发其一 / 下一次其二、其三、其四 / 其四已完成。")
    if form["phase"] in ("tool", "gift"):
        if form.get("resumePhase") not in ("patrol", "manage", "office", "chain", "event"):
            raise ValueError("请选择刑具或珍藏弹窗关闭后实际返回的阶段。")
        allowed = tool_ids if form["phase"] == "tool" else gift_ids
        if not form.get("offerIds") or any(item not in allowed for item in form["offerIds"]):
            raise ValueError("请只勾选本次弹窗实际提供的刑具 / 珍藏。")
        if form["resumePhase"] == "patrol" and not board["destinations"]:
            raise ValueError("返回巡逻阶段时，必须同时确认可去目的地。")
    else:
        board["resumePhase"] = None
        board["offerIds"] = []
    if form["phase"] == "event" or board.get("resumePhase") == "event":
        if form.get("eventKind") not in event_ids:
            raise ValueError("请确认当前事件的具体类型。")
    if form["phase"] == "chain" or board.get("resumePhase") == "chain":
        board["chainConsumed"] = _integer(form.get("chainConsumed"), 1, 99, "本次连吃已吞噬口数")
    else:
        board["chainConsumed"] = 0
    board.update(source="manual", confirmed=True, needsReview=[], startEvent=None)
    for key in ("preyPresent", "remainingDaysHint", "hiddenKnownPresent", "refreshPresent", "unsupportedToolPresent", "unsupportedEventPresent"):
        board.pop(key, None)
    return board


def _root_dir(app):
    explicit = getattr(app, "_talos_install_root", None)
    if explicit:
        return Path(explicit)
    for name in ("node", "get_state"):
        method = getattr(app, name, None)
        globals_ = getattr(getattr(method, "__func__", method), "__globals__", {})
        if globals_.get("ROOT"):
            return Path(globals_["ROOT"])
    return Path(__file__).resolve().parent


def _base_dir(app):
    explicit = getattr(app, "_talos_base_root", None)
    if explicit:
        return Path(explicit)
    method = getattr(app, "node", None)
    globals_ = getattr(getattr(method, "__func__", method), "__globals__", {})
    return Path(globals_.get("ROOT") or _root_dir(app))


def _open_update_launcher(app):
    base = _base_dir(app)
    launcher = base / "TalosHelper-启动器.exe"
    if not launcher.is_file():
        _set_var(app, "status", "未找到更新启动器。请重新解压首次更新器包到原程序目录；当前游戏助手仍可离线使用。")
        return
    try:
        subprocess.Popen([str(launcher), "--updates"], cwd=str(base), shell=False, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        _set_var(app, "status", "已打开更新启动器；仅点击其中“检查更新 / 下载更新”时联网，截图和建议仍离线。")
    except OSError as error:
        _set_var(app, "status", "更新启动器未能打开：" + str(error) + "。当前助手仍可离线使用。")


def _add_update_button(app):
    state = app._talos_game_update
    pending = [app.root]
    parent = None
    while pending:
        widget = pending.pop()
        try:
            pending.extend(widget.winfo_children())
            text = str(widget.cget("text"))
            if "读取游戏" in text and "F8" in text:
                parent = widget.master
                break
        except tk.TclError:
            continue
    if parent is not None:
        button = ttk.Button(parent, text="检查更新", command=lambda: _open_update_launcher(app))
        if any(child.winfo_manager() == "grid" for child in parent.winfo_children() if child is not button):
            button.grid(row=0, column=parent.grid_size()[0], padx=6)
        else:
            button.pack(side="left", padx=6)
    else:
        toolbar = ttk.Frame(app.root)
        toolbar.pack(side="bottom", fill="x")
        button = ttk.Button(toolbar, text="检查更新", command=lambda: _open_update_launcher(app))
        button.pack(side="right", padx=10, pady=5)
    state["update_button"] = button


def _metadata(app):
    roots = [_root_dir(app) / "training" / "game-metadata.json", Path(__file__).with_name("game-metadata.json")]
    for path in roots:
        if path.is_file():
            data = json.loads(path.read_text(encoding="utf-8"))
            if not all(isinstance(data.get(group), list) for group in ("tools", "events", "gifts")) or len(data["tools"]) < 28:
                raise ValueError("刑具 / 事件清单不完整，请重新解压完整更新包。")
            return data
    raise FileNotFoundError("缺少 training/game-metadata.json，请把完整更新包解压到原程序目录。")


def _signature(app):
    try:
        state = app.get_state()
    except Exception:
        state = {"invalid": True}
    text = json.dumps(state, sort_keys=True, ensure_ascii=False, default=str)
    return (id(getattr(app, "image", None)), bool(getattr(app, "busy", False)), hashlib.sha256(text.encode("utf-8")).hexdigest())


def _set_var(app, name, text):
    variable = getattr(app, name, None)
    if variable is not None:
        variable.set(text)


def _write(app, text):
    widget = getattr(app, "output", None)
    if widget is not None:
        widget.configure(state="normal")
        widget.delete("1.0", "end")
        widget.insert("end", text)
        widget.configure(state="disabled")


def _invalidate(app, reason, keep_status=False, close_dialog=True):
    state = app._talos_game_update
    state["generation"] += 1
    _write(app, f"{reason}\n\n点击“计算下一步”，完整核对当前画面和控制信息后再获取建议。\n{SOURCE_NOTE}")
    _set_var(app, "review", reason)
    if not keep_status:
        _set_var(app, "status", reason)
    dialog = state.get("dialog")
    if dialog is not None and close_dialog:
        try:
            dialog.destroy()
        except tk.TclError:
            pass
        state["dialog"] = None
    overlay = state.get("overlay_text")
    if overlay is not None:
        try:
            overlay.set(reason)
        except tk.TclError:
            pass


def _install_monitor(app):
    if hasattr(app, "_talos_game_update"):
        return
    state = {"generation": 0, "busy": False, "dialog": None, "queue": queue.Queue(), "watched": _signature(app), "last_meta": None}
    app._talos_game_update = state
    _add_update_button(app)
    _wrap_analysis(app)
    _wrap_capture(app)

    def tick():
        try:
            if not app.root.winfo_exists():
                return
            signature = _signature(app)
            if signature != state["watched"]:
                state["watched"] = signature
                _invalidate(app, "画面或原面板局面已变化，旧建议已清除，请重新核对。", keep_status=True)
            while True:
                try:
                    message = state["queue"].get_nowait()
                except queue.Empty:
                    break
                if message.get("kind") == "recognition":
                    _deliver_recognition(app, message)
                elif message.get("kind") == "advice":
                    _deliver(app, message)
            app.root.after(150, tick)
        except tk.TclError:
            return

    app.root.after(150, tick)


def _wrap_analysis(app):
    """Keep recognition workers away from Tk, including root.after calls."""
    original = getattr(app, "analyze", None)
    if not callable(original):
        return
    state = app._talos_game_update
    state["recognition_generation"] = 0
    state["recognition_active"] = None

    def analyze(image):
        if getattr(app, "busy", False):
            return
        state["recognition_generation"] += 1
        token = state["recognition_generation"]
        state["recognition_active"] = token
        app.busy = True
        app.image = image
        _invalidate(app, "新画面正在识别，旧建议已清除；识别结果仍需人工核对。", keep_status=True)
        state["watched"] = _signature(app)
        try:
            app.draw_preview()
            _set_var(app, "status", "正在本地识别沙盘…")
            globals_ = getattr(getattr(original, "__func__", original), "__globals__", {})
            recognize = globals_.get("recognize")
            if not callable(recognize):
                raise RuntimeError("未找到原版离线识图函数，请重新解压完整更新包。")
        except Exception as error:
            app.busy = False
            state["recognition_active"] = None
            state["watched"] = _signature(app)
            _set_var(app, "status", "识别失败：" + str(error))
            return

        def work():
            started = time.perf_counter()
            result, error = None, None
            try:
                result = recognize(image)
            except Exception as exception:
                error = str(exception)
            state["queue"].put({"kind": "recognition", "recognitionGeneration": token, "image": image, "result": result, "error": error, "seconds": time.perf_counter() - started})

        threading.Thread(target=work, daemon=True).start()

    app.analyze = analyze


def _deliver_recognition(app, message):
    state = app._talos_game_update
    token = message.get("recognitionGeneration")
    if token != state.get("recognition_active"):
        return
    state["recognition_active"] = None
    app.busy = False
    if token != state.get("recognition_generation") or message.get("image") is not getattr(app, "image", None):
        state["watched"] = _signature(app)
        return
    try:
        app.finish_analysis(message.get("result"), message.get("error"), message.get("seconds", 0.0))
    except Exception as error:
        app.busy = False
        _invalidate(app, "识图结果未能应用，旧建议已清除，请重新读取或手动核对。", keep_status=True)
        _set_var(app, "status", "识别结果无法应用：" + str(error))
    finally:
        state["watched"] = _signature(app)


def _wrap_capture(app):
    """Hide this helper before the host's synchronous ImageGrab capture call."""
    original = getattr(app, "capture", None)
    if not callable(original):
        return
    state = app._talos_game_update

    def capture():
        if state.get("capture_pending") or getattr(app, "busy", False):
            return
        state["capture_pending"] = True
        _invalidate(app, "正在重新读取游戏，旧建议已失效。", keep_status=True, close_dialog=False)
        dialog = state.get("dialog")
        if dialog is not None:
            dialog.confirmed.set(False)
        hidden = []
        for window in (app.root, dialog, state.get("overlay")):
            if window is None:
                continue
            try:
                mode = window.state()
                if mode not in ("withdrawn", "iconic"):
                    hidden.append((window, mode))
                    window.withdraw()
            except tk.TclError:
                continue

        def take():
            try:
                original()
            except Exception as error:
                _set_var(app, "status", "游戏读取失败：" + str(error))
            finally:
                state["capture_pending"] = False
                for window, mode in hidden:
                    try:
                        if window.winfo_exists():
                            window.state(mode)
                    except tk.TclError:
                        pass

        app.root.after(160, take)

    app.capture = capture
    pending = [app.root]
    while pending:
        widget = pending.pop()
        try:
            pending.extend(widget.winfo_children())
            text = str(widget.cget("text"))
            if "F8" in text and "读取游戏" in text:
                widget.configure(command=capture)
        except tk.TclError:
            continue


def _invoke_node(node, root, board):
    script = root / "training" / "game-advice.cjs"
    if not script.is_file():
        raise FileNotFoundError("缺少 training/game-advice.cjs，请完整解压更新包到原目录。")
    completed = subprocess.run(
        [str(node), str(script)], cwd=str(root), input=json.dumps({"board": board, "options": {"budgetMs": 1800, "rollouts": 64}}, ensure_ascii=False),
        text=True, encoding="utf-8", capture_output=True, timeout=20, shell=False,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    if completed.returncode:
        raise RuntimeError((completed.stderr.strip() or completed.stdout.strip() or "离线建议服务异常退出。")[:8000])
    try:
        result = json.loads(completed.stdout)
    except (TypeError, json.JSONDecodeError):
        raise RuntimeError("离线建议服务没有返回有效JSON，请重新解压完整更新包。") from None
    if not isinstance(result, dict) or result.get("ok") is not True:
        raise RuntimeError(result.get("error", "当前信息未通过离线规则校验。") if isinstance(result, dict) else "离线返回格式错误。")
    return result


def _deliver(app, message):
    state = app._talos_game_update
    state["busy"] = False
    if message["generation"] != state["generation"] or message["signature"] != _signature(app):
        return
    if message.get("error"):
        _write(app, "无法给出当前建议：\n" + message["error"] + "\n\n没有回退到旧Python评分。请核对信息后重试。")
        _set_var(app, "status", "离线计算未完成：" + message["error"][:240])
        return
    result = message["result"]
    primary = result.get("primary") or {}
    label = primary.get("label", "当前没有可执行建议")
    source_label = message.get("sourceLabel", "人工核对局面")
    validation = result.get("validation") or {}
    if validation.get("ready") and validation.get("selected") == "baseline":
        strategy_label = "当前策略：修正规则基线（历史参数复验未胜出）"
    elif validation.get("ready") and validation.get("selected") == "historical":
        strategy_label = "当前策略：历史参数（经本轮复验）"
    else:
        strategy_label = "当前策略：历史参数试用，尚未复验"
    lines = ["已核对局面 · 普通20天 / 绮彩优先", "来源：" + source_label, strategy_label, "", "建议：" + label]
    if primary.get("reason"):
        lines.extend(["", str(primary["reason"])])
    for note in result.get("notes", []):
        lines.append("• " + str(note))
    lines.extend(["", SOURCE_NOTE, "仅提供建议，不会点击或操作游戏。"])
    _write(app, "\n".join(lines))
    _set_var(app, "status", "离线规则建议已生成；局面变化后将清除，需重新核对。")
    overlay = state.get("overlay_text")
    if overlay is not None:
        overlay.set(label + "\n" + source_label + "\n" + strategy_label + "\n" + SOURCE_NOTE)


def calculate(app, keep_status=False):
    """Replacement App.calculate entry point; screenshot callbacks never infer."""
    _install_monitor(app)
    state = app._talos_game_update
    if keep_status:
        state["watched"] = _signature(app)
        _invalidate(app, "识图结果仅为预填，尚未核对；旧推荐已清除。", keep_status=True)
        return
    frame = sys._getframe(1)
    for _ in range(6):
        if frame is None:
            break
        if frame.f_code.co_name == "load_example":
            state["watched"] = _signature(app)
            _invalidate(app, "当前为示例研究，未自动生成建议。点击“计算下一步”可手动核对和研究。", keep_status=True)
            state["last_meta"] = None
            return
        frame = frame.f_back
    if getattr(app, "busy", False) or state["busy"]:
        _set_var(app, "status", "正在读取画面或计算，请等待完成。")
        return
    try:
        old = app.get_state()
        metadata = _metadata(app)
        _invalidate(app, "请完整核对当前局面；以下预填均未确认。", keep_status=True)
        state["watched"] = _signature(app)
        previous = state.get("last_meta") if state.get("last_meta_signature") == _signature(app) else None
        dialog = ReviewDialog(app, prefill_state(old, previous), metadata)
        state["dialog"] = dialog
    except Exception as error:
        _write(app, "无法打开人工核对：" + str(error))
        _set_var(app, "status", "人工核对未启动：" + str(error))


class ReviewDialog(tk.Toplevel):
    def __init__(self, app, initial, metadata):
        super().__init__(app.root)
        self.app, self.initial, self.metadata = app, initial, metadata
        self.signature = _signature(app)
        self.title("完整局面核对 · 旧识图预填尚未确认")
        self.geometry("1120x800")
        self.minsize(900, 640)
        self.transient(app.root)
        self.vars, self.cell_vars = {}, []
        self.error = tk.StringVar(self, "")
        self.confirmed = tk.BooleanVar(self, False)
        self.pin = tk.BooleanVar(self, False)
        self.overlay_enabled = tk.BooleanVar(self, True)
        self.event_names = {x["id"]: x["name"] for x in metadata["events"]}
        self.tool_names = {x["id"]: x["name"] for x in metadata["tools"]}
        self.gift_names = {x["id"]: x["name"] for x in metadata["gifts"]}
        source = "截图预填（未经实机准确率验证）" if getattr(app, "image", None) is not None else "无当前截图：手工 / 示例研究；不会作为实机识图结果"
        ttk.Label(self, text=source + "。所有预填和上次人工值均需重新核对。", wraplength=1050).pack(anchor="w", padx=14, pady=(12, 3))
        ttk.Label(self, text=f"旧面板剩余天数：{initial.get('remainingDaysHint', '未读到')}（仅提示，不换算当前天数）。刑具旧开关也仅为预填。", wraplength=1050).pack(anchor="w", padx=14, pady=(0, 8))
        tabs = ttk.Notebook(self)
        tabs.pack(fill="both", expand=True, padx=12, pady=4)
        room_tab, control_tab, tool_tab = (ttk.Frame(tabs) for _ in range(3))
        tabs.add(room_tab, text="① 逐格房间 / 目的地")
        tabs.add(control_tab, text="② 天数 / 控制 / 事件")
        tabs.add(tool_tab, text="③ 全部刑具 / 当前奖励")
        self._rooms(self._scroll_frame(room_tab))
        self._controls(self._scroll_frame(control_tab))
        self._tools(tool_tab)
        ttk.Label(self, textvariable=self.error, foreground="#ba493b", wraplength=1050).pack(anchor="w", padx=14, pady=5)
        ttk.Checkbutton(self, text="已逐格核对当前画面和控制信息（包含全部刑具、天数、事件及标记）", variable=self.confirmed).pack(anchor="w", padx=14, pady=5)
        footer = ttk.Frame(self)
        footer.pack(fill="x", padx=14, pady=(4, 12))
        ttk.Checkbutton(footer, text="核对窗口置顶", variable=self.pin, command=lambda: self.attributes("-topmost", self.pin.get())).pack(side="left")
        ttk.Checkbutton(footer, text="显示游戏建议浮窗", variable=self.overlay_enabled).pack(side="left", padx=12)
        ttk.Button(footer, text="取消", command=self.destroy).pack(side="right")
        self.submit_button = ttk.Button(footer, text="核对完成，离线计算", command=self.submit)
        self.submit_button.pack(side="right", padx=8)
        self.bind("<Escape>", lambda event: self.destroy())

    @staticmethod
    def _scroll_frame(parent):
        canvas = tk.Canvas(parent, highlightthickness=0)
        scrollbar = ttk.Scrollbar(parent, orient="vertical", command=canvas.yview)
        canvas.configure(yscrollcommand=scrollbar.set)
        scrollbar.pack(side="right", fill="y")
        canvas.pack(side="left", fill="both", expand=True)
        content = ttk.Frame(canvas)
        item = canvas.create_window((0, 0), window=content, anchor="nw")
        content.bind("<Configure>", lambda event: canvas.configure(scrollregion=canvas.bbox("all")))
        canvas.bind("<Configure>", lambda event: canvas.itemconfigure(item, width=event.width))
        return content

    def _combo(self, frame, row, col, name, mapping, value, width=22):
        variable = tk.StringVar(self, mapping.get(value, "未确认"))
        self.vars[name] = variable
        combo = ttk.Combobox(frame, textvariable=variable, values=["未确认", *mapping.values()], state="readonly", width=width)
        combo.grid(row=row, column=col, sticky="ew", padx=6, pady=6)
        return combo

    def _entry(self, frame, row, col, name, value, width=12):
        variable = tk.StringVar(self, str(value if value is not None else ""))
        self.vars[name] = variable
        ttk.Entry(frame, textvariable=variable, width=width).grid(row=row, column=col, sticky="ew", padx=6, pady=6)

    def _check(self, frame, row, col, name, text, value=False):
        variable = tk.BooleanVar(self, bool(value))
        self.vars[name] = variable
        ttk.Checkbutton(frame, text=text, variable=variable, command=lambda: self.confirmed.set(False)).grid(row=row, column=col, columnspan=2, sticky="w", padx=6, pady=6)

    def _rooms(self, frame):
        self.position = tk.IntVar(self, self.initial.get("position") if isinstance(self.initial.get("position"), int) else -1)
        titles = ["房号", "当前", "可去", "房间类型", "种族", "阶级", "可见事件", "孵化日", "象牙", "抓痕"]
        for col, title in enumerate(titles):
            ttk.Label(frame, text=title).grid(row=0, column=col, padx=4, pady=6)
        for n, cell in enumerate(self.initial["cells"]):
            row = n + 1
            ttk.Label(frame, text=f"{n + 1:02d}" + (" 审讯室" if n == 0 else "")).grid(row=row, column=0, padx=4)
            ttk.Radiobutton(frame, variable=self.position, value=n, command=lambda: self.confirmed.set(False)).grid(row=row, column=1)
            values = {"kind": tk.StringVar(self, KINDS[cell["kind"]]), "beast": tk.StringVar(self, BEASTS.get(cell.get("beast"), "未确认")), "tier": tk.StringVar(self, TIERS[cell["tier"]] if isinstance(cell.get("tier"), int) and 0 <= cell["tier"] <= 5 else "未确认"), "destination": tk.BooleanVar(self, n in self.initial["destinations"]), "event": tk.StringVar(self, UNKNOWN_EVENT if cell.get("event") else "无"), "due": tk.StringVar(self, ""), "ivory": tk.BooleanVar(self, False), "scratch": tk.BooleanVar(self, False)}
            ttk.Checkbutton(frame, variable=values["destination"]).grid(row=row, column=2)
            ttk.Combobox(frame, textvariable=values["kind"], values=[KINDS[k] for k in ("unknown", "locked", "empty", "nest", "inmate")] if n else [KINDS["office"]], state="readonly", width=10).grid(row=row, column=3, padx=3, pady=3)
            ttk.Combobox(frame, textvariable=values["beast"], values=["未确认", *BEASTS.values()], state="readonly", width=7).grid(row=row, column=4, padx=3)
            ttk.Combobox(frame, textvariable=values["tier"], values=["未确认", *TIERS], state="readonly", width=17).grid(row=row, column=5, padx=3)
            ttk.Combobox(frame, textvariable=values["event"], values=["无", UNKNOWN_EVENT, *self.event_names.values()], state="readonly", width=20).grid(row=row, column=6, padx=3)
            ttk.Entry(frame, textvariable=values["due"], width=5).grid(row=row, column=7, padx=3)
            ttk.Checkbutton(frame, variable=values["ivory"]).grid(row=row, column=8)
            ttk.Checkbutton(frame, variable=values["scratch"]).grid(row=row, column=9)
            for variable in values.values():
                variable.trace_add("write", lambda *_: self.confirmed.set(False))
            self.cell_vars.append(values)
        ttk.Label(frame, text="缺失囚犯的房间默认“未核对”。请区分未开启、空牢房和鼠巢；1号固定为审讯室。", wraplength=1000).grid(row=17, column=0, columnspan=10, sticky="w", padx=8, pady=8)
        ttk.Button(frame, text="我已逐格确认：其余未知格均为未开启", command=self.mark_unknown_locked).grid(row=18, column=0, columnspan=10, sticky="w", padx=8)

    def mark_unknown_locked(self):
        for row in self.cell_vars[1:]:
            if row["kind"].get() == KINDS["unknown"]:
                row["kind"].set(KINDS["locked"])
        self.confirmed.set(False)

    def _controls(self, frame):
        labels = [("当前第几天（1–20，手动填写）", "当前位置在第一页选择"), ("操作阶段", "左上角任务"), ("任务进度", "已捕获绮彩数"), ("僭越进度", "当前事件"), ("本次连吃已吃口数（>=1）", "奖励结束后实际返回阶段"), ("当前开局", "")]
        for group, (left, right) in enumerate(labels):
            ttk.Label(frame, text=left).grid(row=group * 2, column=0, sticky="w", padx=8, pady=(10, 0))
            ttk.Label(frame, text=right).grid(row=group * 2, column=1, sticky="w", padx=8, pady=(10, 0))
        self._entry(frame, 1, 0, "day", self.initial["day"])
        self._combo(frame, 3, 0, "phase", PHASES, self.initial["phase"])
        self._combo(frame, 3, 1, "objective", OBJECTIVES, self.initial["objective"])
        self._entry(frame, 5, 0, "objectiveProgress", self.initial["objectiveProgress"])
        self._entry(frame, 5, 1, "vault", self.initial["vault"])
        self.usurp_names = {"unknown": "未确认", "1": "尚未触发其一", "2": "下一次其二", "3": "下一次其三", "4": "下一次其四", "done": "其四已完成"}
        self._combo(frame, 7, 0, "usurpStage", self.usurp_names, str(self.initial["usurpStage"]))
        self._combo(frame, 7, 1, "eventKind", self.event_names, self.initial.get("eventKind"))
        self._entry(frame, 9, 0, "chainConsumed", self.initial["chainConsumed"])
        self._combo(frame, 9, 1, "resumePhase", {key: PHASES[key] for key in ("patrol", "manage", "office", "chain", "event")}, None)
        self._combo(frame, 11, 0, "startEvent", {"ordinary": "普通20天开局", "special": "存在特殊开局（暂不支持）"}, "ordinary")
        self._check(frame, 12, 0, "milestone", "任务里程碑已经完成", self.initial["milestone"])
        self._check(frame, 13, 0, "usedAction", "本日管理操作已经使用（替换 / 吞噬共享）", self.initial["usedAction"])
        self._check(frame, 14, 0, "chainArmed", "连锁反应效果当前已激活", self.initial["chainArmed"])
        self._check(frame, 15, 0, "preyPresent", "当前存在狂狮猎物标记（暂不支持，勾选将拒绝计算）")
        self._check(frame, 16, 0, "usedExtra", "当前存在额外管理操作 / 额外次数（暂不支持）")
        self._check(frame, 17, 0, "devourAfterSwap", "当前可在替换后继续吞噬（暂不支持）")
        self._check(frame, 18, 0, "hiddenKnownPresent", "当前已探知隐藏房间种族 / 阶级（暂不支持）")
        self._check(frame, 19, 0, "refreshPresent", "当前仍有刑具 / 珍藏刷新机会（暂不支持）")
        self._check(frame, 20, 0, "unsupportedToolPresent", "持有未列出的刑具，例如捕兽夹（暂不支持，不能漏填）")
        self._check(frame, 21, 0, "unsupportedEventPresent", "当前为未列出的事件，例如有福同享 / 宿敌 / 同罪移押（暂不支持）")
        frame.columnconfigure(0, weight=1)
        frame.columnconfigure(1, weight=1)
        for variable in self.vars.values():
            variable.trace_add("write", lambda *_: self.confirmed.set(False))

    def _tools(self, frame):
        ttk.Label(frame, text="完整刑具列表：仅勾选实际持有的刑具。旧“之匣 / 锯 / 项圈”开关仅预填，必须核对。", wraplength=1000).grid(row=0, column=0, columnspan=2, sticky="w", padx=8, pady=10)
        ttk.Label(frame, text="已经持有的刑具（可多选）").grid(row=1, column=0, sticky="w", padx=8)
        ttk.Label(frame, text="当前弹窗提供的选项（可多选，仅tool/gift阶段使用）").grid(row=1, column=1, sticky="w", padx=8)
        self.tools = tk.Listbox(frame, selectmode="multiple", exportselection=False, height=22, width=43)
        self.offers = tk.Listbox(frame, selectmode="multiple", exportselection=False, height=22, width=46)
        self.tool_ids = [item["id"] for item in self.metadata["tools"]]
        self.offer_ids = self.tool_ids + [item["id"] for item in self.metadata["gifts"]]
        for index, item in enumerate(self.metadata["tools"]):
            self.tools.insert("end", item["name"])
            if item["id"] in self.initial["tools"]:
                self.tools.selection_set(index)
            self.offers.insert("end", "刑具 · " + item["name"])
        for item in self.metadata["gifts"]:
            self.offers.insert("end", "珍藏 · " + item["name"])
        self.tools.grid(row=2, column=0, sticky="nsew", padx=8, pady=8)
        self.offers.grid(row=2, column=1, sticky="nsew", padx=8, pady=8)
        self.tools.bind("<<ListboxSelect>>", lambda event: self.confirmed.set(False))
        self.offers.bind("<<ListboxSelect>>", lambda event: self.confirmed.set(False))
        frame.columnconfigure(0, weight=1)
        frame.columnconfigure(1, weight=1)
        frame.rowconfigure(2, weight=1)

    @staticmethod
    def _key(mapping, label):
        return next((key for key, name in mapping.items() if name == label), None)

    def collect(self):
        cells, destinations = [], []
        for n, values in enumerate(self.cell_vars):
            event = values["event"].get()
            cell = {"n": n, "kind": self._key(KINDS, values["kind"].get()), "beast": self._key(BEASTS, values["beast"].get()), "tier": TIERS.index(values["tier"].get()) if values["tier"].get() in TIERS else "", "event": event != "无", "ivory": values["ivory"].get(), "scratch": values["scratch"].get()}
            if cell["kind"] == "nest":
                cell["due"] = values["due"].get()
            if cell["event"]:
                cell["eventKind"] = self._key(self.event_names, event)
            if values["destination"].get():
                destinations.append(n)
            cells.append(cell)
        form = {name: variable.get() for name, variable in self.vars.items()}
        for name, mapping in (("phase", PHASES), ("objective", OBJECTIVES), ("usurpStage", self.usurp_names), ("eventKind", self.event_names), ("resumePhase", PHASES), ("startEvent", {"ordinary": "普通20天开局", "special": "存在特殊开局（暂不支持）"})):
            form[name] = self._key(mapping, self.vars[name].get())
        form.update(cells=cells, position=self.position.get(), destinations=destinations, tools=[self.tool_ids[n] for n in self.tools.curselection()], offerIds=[self.offer_ids[n] for n in self.offers.curselection()], confirmed=self.confirmed.get())
        return validate_board(form, self.metadata)

    def submit(self):
        state = self.app._talos_game_update
        if state["busy"]:
            self.error.set("已有计算进行中，请稍候。")
            return
        if self.signature != _signature(self.app):
            self.error.set("画面或原面板已变化，请关闭并重新打开核对。")
            self.confirmed.set(False)
            return
        try:
            board = self.collect()
            node, root = self.app.node(), _root_dir(self.app)
        except Exception as error:
            self.error.set(str(error))
            return
        state["last_meta"] = copy.deepcopy(board)
        state["last_meta"]["confirmed"] = False
        state["last_meta_signature"] = self.signature
        state["busy"] = True
        token, signature = state["generation"], self.signature
        source_label = "当前截图人工核对" if getattr(self.app, "image", None) is not None else "无图手工 / 示例研究（非游戏实况）"
        if self.overlay_enabled.get():
            _show_overlay(self.app)
        _write(self.app, "正在离线比较已核对的局面…\n" + SOURCE_NOTE)
        _set_var(self.app, "status", "本机后台计算中；仍可读取新截图，新局面会使本次结果失效。")

        def work():
            try:
                result = _invoke_node(node, root, board)
                message = {"kind": "advice", "generation": token, "signature": signature, "result": result, "sourceLabel": source_label}
            except subprocess.TimeoutExpired:
                message = {"kind": "advice", "generation": token, "signature": signature, "error": "本机计算超过20秒，已终止，请核对局面后重试。"}
            except Exception as error:
                message = {"kind": "advice", "generation": token, "signature": signature, "error": str(error)}
            state["queue"].put(message)

        threading.Thread(target=work, daemon=True).start()
        state["dialog"] = None
        self.destroy()


def _show_overlay(app):
    state = app._talos_game_update
    existing = state.get("overlay")
    if existing is not None:
        try:
            if existing.winfo_exists():
                state["overlay_text"].set("正在离线计算…")
                existing.deiconify()
                return
        except tk.TclError:
            pass
    window = tk.Toplevel(app.root)
    window.title("当前建议 · 手动执行")
    window.geometry("420x200")
    window.minsize(330, 150)
    window.attributes("-topmost", True)
    text = tk.StringVar(window, "正在离线计算…")
    ttk.Label(window, textvariable=text, wraplength=390, justify="left", font=("Microsoft YaHei UI", 11)).pack(fill="both", expand=True, padx=14, pady=12)
    pin = tk.BooleanVar(window, True)
    ttk.Checkbutton(window, text="置顶", variable=pin, command=lambda: window.attributes("-topmost", pin.get())).pack(anchor="w", padx=14, pady=(0, 8))
    state["overlay"], state["overlay_text"] = window, text
