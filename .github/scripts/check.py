#!/usr/bin/env python3
"""在 GitHub Actions runner 上拨测 out/clash.yaml 里的节点，产出只含可用节点的订阅。

原理：runner 有开放网络（本机 VM 直连被网关拦了，测不了），所以测速放在
Actions 里跑：拉起 mihomo，调它的外部控制器对 Auto 组做整组延迟测试
（GET /group/Auto/delay），只把返回了延迟的节点当作可用。

产出（都写到 --out-dir）：
  working.txt        可用节点的原始 URI，一行一个（顺序与 nodes.txt 一致）
  working.b64        working.txt 的 base64（可直接当订阅导入 Shadowrocket）
  clash-working.yaml 只含可用节点的 Clash（mihomo）配置（用控制台 convert 模式重建）
  check.json         本轮测速统计（总数/可用数/按类型/最快的 10 个）

注意：
  - mihomo 的组延迟接口只返回测通了的节点（名 -> 延迟毫秒），没测通的不在
    结果里；如果全部测不通，接口返回 HTTP 504。
  - 任意一环失败（比如本轮 0 个可用）都不让整个工作流失败：发现本身是
    成功的，测速只是附加信息；失败时保留上一轮的产出，只写 check.json。
"""
import argparse
import base64
import json
import os
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

DEFAULT_URL = "http://www.gstatic.com/generate_204"
CONTROLLER = "http://127.0.0.1:9090"


def log(msg):
    print(f"[check] {msg}", flush=True)


def http_get(url, timeout):
    req = urllib.request.Request(url, headers={"User-Agent": "proxynodehub-check/1.0"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.status, r.read()


def wait_controller(proc, timeout=60):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if proc.poll() is not None:
            return False
        try:
            status, _ = http_get(CONTROLLER + "/version", 3)
            if status == 200:
                return True
        except Exception:
            pass
        time.sleep(1)
    return False


def direct_ok(url):
    """不经过任何代理，直接访问测试地址，看它本身通不通。"""
    try:
        status, _ = http_get(url, 10)
        return 200 <= status < 300
    except Exception:
        return False


def ip_fallback(url):
    """把测试地址的域名解析成真实 IP，拼一个不用 DNS 的版本（防 runner DNS 抖动）。"""
    try:
        host = urllib.parse.urlparse(url).hostname
        if not host:
            return None
        ip = socket.gethostbyname(host)
        return f"http://{ip}/generate_204"
    except Exception:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True, help="待测的 clash.yaml（控制台产出）")
    ap.add_argument("--names", required=True, help="node-names.json（代理名 -> 原始节点 URI）")
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--mihomo", default="./mihomo")
    ap.add_argument("--converter", default="console/bin/Release/net8.0/ProxyNodeHub.Console.dll",
                    help="控制台 dll，用 convert 模式重建 clash-working.yaml")
    ap.add_argument("--group", default="Auto")
    ap.add_argument("--url", default=DEFAULT_URL)
    ap.add_argument("--timeout", type=int, default=5000, help="单个节点测速超时（毫秒）")
    args = ap.parse_args()

    if not os.path.isfile(args.config):
        log(f"找不到 {args.config}，跳过测速。")
        return 0
    if not os.path.isfile(args.names):
        log(f"找不到 {args.names}，无法把代理名对应回原始节点，跳过测速。")
        return 0
    with open(args.names, encoding="utf-8") as f:
        name_to_uri = json.load(f)
    total = len(name_to_uri)
    if total == 0:
        log("clash.yaml 里没有节点，跳过测速。")
        return 0

    # 先确认测试地址本身是通的，否则全部节点都会被误判为不通。
    test_url = args.url
    test_url_ok = direct_ok(test_url)
    if not test_url_ok:
        fb = ip_fallback(test_url)
        if fb and direct_ok(fb):
            log(f"测试地址 {test_url} 直连不通，改用 IP 版 {fb}。")
            test_url = fb
            test_url_ok = True
        else:
            log(f"警告：测试地址 {test_url} 直连不通，本轮结果可能全是失败。")

    # 拉起 mihomo（日志写临时文件，不进 out/，免得被提交）。
    mihomo_log = "/tmp/mihomo-check.log"
    log(f"启动 mihomo（{args.config}），等控制器就绪……")
    with open(mihomo_log, "w", encoding="utf-8") as lf:
        proc = subprocess.Popen([args.mihomo, "-f", args.config],
                                stdout=lf, stderr=subprocess.STDOUT)
    delays = {}
    try:
        if not wait_controller(proc):
            log("mihomo 控制器 60 秒没起来，跳过测速。")
            try:
                with open(mihomo_log, encoding="utf-8") as lf:
                    log("mihomo 日志尾部：\n" + "".join(lf.readlines()[-15:]))
            except Exception:
                pass
            return 0
        delay_url = (f"{CONTROLLER}/group/{urllib.parse.quote(args.group, safe='')}/delay"
                     f"?url={urllib.parse.quote(test_url, safe='')}&timeout={args.timeout}")
        log(f"整组测速中（{total} 个节点，单个超时 {args.timeout}ms）……")
        t0 = time.time()
        try:
            status, body = http_get(delay_url, 900)
            delays = json.loads(body.decode("utf-8"))
            if not isinstance(delays, dict):
                delays = delays.get("proxies", {}) if isinstance(delays, dict) else {}
            log(f"测速接口返回 {len(delays)} 个可用节点，用时 {time.time() - t0:.0f}s。")
        except urllib.error.HTTPError as e:
            if e.code == 504:
                log("测速接口返回 504：全部节点都没测通。")
            else:
                log(f"测速接口返回 HTTP {e.code}：{e.read()[:200]!r}。")
            delays = {}
        except Exception as e:
            log(f"测速请求失败：{e}。")
            delays = {}
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()

    working_names = [n for n, d in delays.items() if isinstance(d, (int, float)) and d > 0]
    working_uris = [name_to_uri[n] for n in name_to_uri if n in delays and delays[n] > 0]
    by_type = {}
    for uri in working_uris:
        by_type[uri.split("://", 1)[0].lower()] = by_type.get(uri.split("://", 1)[0].lower(), 0) + 1
    fastest = sorted(
        ({"name": n, "delay_ms": delays[n],
          "type": name_to_uri[n].split("://", 1)[0].lower()}
         for n in working_names),
        key=lambda x: x["delay_ms"])[:10]

    stats = {
        "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "test_url": test_url,
        "test_url_ok": test_url_ok,
        "timeout_ms": args.timeout,
        "total": total,
        "working": len(working_uris),
        "failed": total - len(working_uris),
        "by_type": by_type,
        "fastest": fastest,
    }
    os.makedirs(args.out_dir, exist_ok=True)
    with open(os.path.join(args.out_dir, "check.json"), "w", encoding="utf-8") as f:
        json.dump(stats, f, ensure_ascii=False, indent=2)
        f.write("\n")

    if not working_uris:
        log(f"本轮 0/{total} 个节点可用，未写 working.txt / clash-working.yaml（保留上一轮）。")
        return 0

    working_txt = "\n".join(working_uris) + "\n"
    with open(os.path.join(args.out_dir, "working.txt"), "w", encoding="utf-8") as f:
        f.write(working_txt)
    with open(os.path.join(args.out_dir, "working.b64"), "w", encoding="utf-8") as f:
        f.write(base64.b64encode(working_txt.encode("utf-8")).decode("ascii") + "\n")

    # 用控制台 convert 模式把可用节点重建成 Clash 配置（保证格式与 clash.yaml 一致）。
    working_path = os.path.join(args.out_dir, "working.txt")
    clash_working = os.path.join(args.out_dir, "clash-working.yaml")
    if os.path.isfile(args.converter):
        try:
            r = subprocess.run(["dotnet", args.converter, "convert", working_path, clash_working],
                               capture_output=True, text=True, timeout=300)
        except (OSError, subprocess.TimeoutExpired) as e:
            log(f"警告：重建 clash-working.yaml 失败（{e}），working.txt / working.b64 已写好。")
            r = None
        if r is not None:
            if r.returncode == 0:
                log(r.stdout.strip())
            else:
                log(f"警告：重建 clash-working.yaml 失败（{r.stderr.strip()[:200]}），"
                    "working.txt / working.b64 已写好。")
    else:
        log(f"警告：找不到控制台 {args.converter}，没写 clash-working.yaml；"
            "working.txt / working.b64 已写好。")

    log(f"完成：{len(working_uris)}/{total} 个节点可用（按类型 {by_type}）。")
    for item in fastest[:5]:
        log(f"  最快：{item['delay_ms']}ms {item['type']} {item['name'][:40]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
