"""SonettoHere 启动器 —— 后端包装器（launcher 自有文件，不参与项目代码）。

目的：以「不改动项目代码」的方式运行 main.py，同时给启动器提供一条优雅关闭通道。

关闭路径：启动器向本进程 stdin 写入 ``shutdown``（或直接关闭 stdin，表示启动器已退出），
本包装器就通过 uvicorn 官方推荐的程序化方式（``Server.should_exit = True``）触发关闭，
于是 FastAPI lifespan 的 shutdown 分支会正常执行 —— MCP 连接、长期记忆、边缘灯等
资源都会走它们自己的收尾逻辑，而不是被强杀。

原理：包装器把 ``uvicorn.run`` 替换成一个「捕获 Server 实例后再运行」的版本，
然后照常 ``import main; main.main()``。main.py 本身一行都不用改，
它调用 uvicorn.run 时拿到的仍然是一个正常工作的 server。

用法（工作目录须为项目根）：
    .venv\\Scripts\\python.exe <本文件> <项目根目录>
"""

import os
import sys
import threading
import time

_CTRL_COMMANDS = {"shutdown", "exit", "quit"}


def _bootstrap_path() -> str:
    root = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()
    root = os.path.abspath(root)
    if root not in sys.path:
        sys.path.insert(0, root)
    os.chdir(root)
    return root


PROJECT_ROOT = _bootstrap_path()

import uvicorn  # noqa: E402  — 必须在 sys.path 就绪后导入

_holder: dict = {}
_real_run = uvicorn.run


def _controlled_run(app, **kwargs):
    """捕获 uvicorn.Server 实例，让启动器可以从外部触发优雅关闭。"""
    server = uvicorn.Server(uvicorn.Config(app, **kwargs))
    _holder["server"] = server
    print("[launcher] 后端控制通道就绪", flush=True)
    server.run()


uvicorn.run = _controlled_run


def _request_shutdown() -> None:
    """等待 server 实例出现（可能还在启动阶段），然后请求优雅关闭。"""
    deadline = time.monotonic() + 120.0
    while time.monotonic() < deadline:
        server = _holder.get("server")
        if server is not None:
            server.should_exit = True
            return
        time.sleep(0.1)
    print("[launcher] 等待 server 实例超时，无法优雅关闭", flush=True)


def _watch_control_channel() -> None:
    reason = "控制通道关闭"
    try:
        for line in sys.stdin:
            if line.strip().lower() in _CTRL_COMMANDS:
                reason = "收到关闭指令"
                break
    except Exception as exc:  # stdin 异常同样按关闭处理
        reason = f"控制通道异常 ({exc.__class__.__name__})"

    print(f"[launcher] {reason}，开始优雅关闭后端", flush=True)
    _request_shutdown()


def main() -> None:
    threading.Thread(target=_watch_control_channel, daemon=True).start()

    import main as project_main  # noqa: PLC0415 — 项目入口，路径已就绪

    project_main.main()
    print("[launcher] 后端进程已正常退出", flush=True)


if __name__ == "__main__":
    main()
