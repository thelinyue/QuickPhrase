"""通知工具商店归档已发布版本；可独立重试，不构建或修改 GitHub Release。"""
import argparse
import json
import os
import re
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, file, code, message, headers, new_url):
        raise RuntimeError("商店接口发生重定向，请将 TOOL_STORE_URL 配置为最终 HTTPS 根地址")


class MissingStatus(Exception):
    pass


def version(value):
    if not isinstance(value, str) or not re.fullmatch(r"v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", value):
        raise RuntimeError("版本必须是正式 SemVer，例如 v1.2.3")
    return tuple(map(int, value.removeprefix("v").split(".")))


class Store:
    def __init__(self, root, key, app_id):
        parsed = urllib.parse.urlsplit(root)
        if parsed.scheme != "https" or not parsed.hostname or parsed.username is not None or parsed.password is not None or parsed.query or parsed.fragment:
            raise RuntimeError("请配置 TOOL_STORE_URL 为无凭据、查询和片段的公网 HTTPS 商店根地址")
        if not key or not re.fullmatch(r"[0-9a-f]{64}", key):
            raise RuntimeError("请配置本工具的 TOOL_STORE_API_KEY Secret")
        if not re.fullmatch(r"[a-z0-9-]{1,64}", app_id):
            raise RuntimeError("软件标识无效")
        self.base = root.rstrip("/") + "/api/integrations/v1/" + app_id + "/releases"
        self.key = key
        self.opener = urllib.request.build_opener(NoRedirect())

    def request(self, method, release_id=None, payload=None, deadline=None):
        address = self.base if release_id is None else self.base + "/" + str(release_id)
        data = None if payload is None else json.dumps(payload).encode("utf-8")
        for attempt in range(5):
            remaining = 30 if deadline is None else deadline - time.monotonic()
            if remaining <= 0:
                raise RuntimeError("等待商店同步超过 30 分钟；GitHub Release 已发布，可单独重试同步")
            request = urllib.request.Request(address, data=data, method=method, headers={
                "Authorization": "Bearer " + self.key, "Content-Type": "application/json", "Accept": "application/json",
            })
            try:
                with self.opener.open(request, timeout=min(30, remaining)) as response:
                    content = response.read(1024 * 1024 + 1)
                    if len(content) > 1024 * 1024:
                        raise RuntimeError("商店响应过大")
                    result = json.loads(content)
                    if not isinstance(result, dict):
                        raise RuntimeError("商店返回格式无效")
                    return result
            except urllib.error.HTTPError as error:
                error.close()
                if error.code == 404 and method == "GET":
                    raise MissingStatus() from None
                if error.code != 429 and error.code < 500:
                    raise RuntimeError(f"商店拒绝请求（HTTP {error.code}），请检查地址、密钥和软件配置") from None
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                pass
            except (ValueError, UnicodeError):
                raise RuntimeError("商店返回格式无效") from None
            if attempt == 4:
                raise RuntimeError("商店暂时不可达或正在限流，有限重试后仍失败；可单独重试商店同步任务")
            pause = min(2 ** attempt, 10)
            if deadline is not None:
                pause = min(pause, max(0, deadline - time.monotonic()))
            time.sleep(pause)


def synchronize(store, release_id, tag):
    expected = version(tag)
    deadline = time.monotonic() + 30 * 60
    payload = {"release_id": release_id, "tag": tag}
    result = store.request("POST", payload=payload, deadline=deadline)
    while time.monotonic() < deadline:
        if result.get("release_id") != release_id or result.get("tag") != tag:
            raise RuntimeError("商店返回了其他 Release 的同步结果，已停止验收")
        state = result.get("status")
        if state in ("succeeded", "superseded"):
            current = version(result.get("current_version"))
            if current < expected or (state == "superseded" and current == expected):
                raise RuntimeError("商店当前可用版本与同步结果不一致")
            return result
        if state == "failed":
            reason = str(result.get("message", "请查看商店同步详情"))[:500]
            raise RuntimeError(f"商店下载、验签或提交失败：{reason}；可单独重试，GitHub Release 已保留")
        if state not in ("pending", "running"):
            raise RuntimeError("商店返回未知同步状态")
        time.sleep(min(10, max(0, deadline - time.monotonic())))
        try:
            result = store.request("GET", release_id=release_id, deadline=deadline)
        except MissingStatus:
            result = store.request("POST", payload=payload, deadline=deadline)
    raise RuntimeError("等待商店同步超过 30 分钟；GitHub Release 已发布，可单独重试同步")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app-id", required=True)
    args = parser.parse_args()
    store = Store(os.environ.get("TOOL_STORE_URL", ""), os.environ.get("TOOL_STORE_API_KEY", ""), args.app_id)
    tag = os.environ.get("TOOL_STORE_TAG", "")
    version(tag)
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise RuntimeError("缺少有效的 GITHUB_REPOSITORY")
    command = ["gh", "api", f"repos/{repository}/releases/tags/{urllib.parse.quote(tag, safe='')}"]
    fetched = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
    if fetched.returncode:
        raise RuntimeError("读取正式 Release 失败，请检查标签和 GitHub 权限")
    release = json.loads(fetched.stdout)
    if release.get("draft") is not False or release.get("prerelease") is not False or release.get("tag_name") != tag or type(release.get("id")) is not int or release["id"] <= 0:
        raise RuntimeError("只允许同步已经发布且标签一致的正式 Release")
    result = synchronize(store, release["id"], tag)
    message = f"GitHub 正式版 {tag} 已发布；工具商店 {result['status']}，当前可用 {result['current_version']}。"
    print(message)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as output:
            output.write(message + "\n")


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, ValueError, OSError) as error:
        message = str(error)
        for name in ("TOOL_STORE_API_KEY", "GH_TOKEN"):
            secret = os.environ.get(name)
            if secret:
                message = message.replace(secret, "[已隐藏]")
        print("工具商店同步失败：" + message)
        raise SystemExit(1)
