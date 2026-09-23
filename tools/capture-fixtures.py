#!/usr/bin/env python3
"""抓取 XAUAT 上游真实响应，落成 XAUAT.LoginApi 的测试 fixture。

为什么需要这个脚本
------------------
Flask 侧对 CAS 登录页选择器与 AES 加密零测试覆盖、零真实样本（tests/ 里没有任何
authserver 的 HTML）。照 Flask 代码逆推写 C# 解析器等于盲写。所以先用真实账号抓一遍，
把响应原文连同结构一起冻结下来。

与 Flask 的一致性
-----------------
本脚本刻意复刻 xauat_sso_login.py 的请求序列，包括：
  - 明文 http 的 authserver、AES-CBC/PKCS7 密码加密、64 字符随机前缀 + 16 字符随机 IV
  - allow_redirects=False 的 CAS POST，以及 loginFromSSO 的跳转链
这样抓到的样本就是 .NET 版必须解析的那一份，不会出现"样本对不上实现"。

教务侧端点（课表、考试）的抓取随日历功能一起迁到了 XAUAT.EduApi：
那边的解析归它自己的 CourseService / ExamService，不再由本服务解析。

依赖
----
复用 Flask 项目的虚拟环境即可（requests + cryptography 都在里面）：
    uv run --project ../../PythonProjects/xauat_login_flask python tools/capture-fixtures.py <学号> <密码>
        python tools/capture-fixtures.py <学号> <密码>

输出
----
    XAUAT.LoginApi.Tests/TestFixtures/*.html / *.json
以及一份 capture-report.txt，记录每一步的 HTTP 状态、Content-Type、字符集与重定向链——
这几项直接决定 .NET 侧的编码处理与重定向复刻方式。

脱敏
----
脚本会把学号、姓名、cookie 值替换成占位符，但**保留完整的标签结构与属性顺序**：
正则的成败全在这些细节上（比如 pwdEncryptSalt 的 input 是否带 value、
lt/execution 的引号形式）。跑完请人工扫一眼输出。
"""

from __future__ import annotations

import base64
import json
import os
import random
import re
import sys
import urllib.parse
from pathlib import Path
from typing import Any

import requests
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

AUTH_SERVER_URL = "http://authserver.xauat.edu.cn/authserver/login"
SERVICE_URL = "https://swjw.xauat.edu.cn/student/sso/login"
BASE_URL = "https://swjw.xauat.edu.cn/student"

# 与 xauat_sso_login.py 完全一致：49 字符字母表，去掉了易混淆的 I/l/o/0/1/9
AES_CHARS = "ABCDEFGHJKMNPQRSTWXYZabcdefhijkmnprstwxyz2345678"
DEFAULT_SALT = "rjBFAaHsNkKAhpoi"

UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36"
)
BASE_HEADERS = {
    "User-Agent": UA,
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
    "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
    "Accept-Encoding": "gzip, deflate",
    "Connection": "keep-alive",
    "Upgrade-Insecure-Requests": "1",
}
JSON_HEADERS = {**BASE_HEADERS, "Accept": "application/json, text/plain, */*"}

# 与 Flask 的 XAUATParser.extract_exams_from_html 同形的清洗正则，仅用于诊断输出。
# 写成模块级常量而不是内联在 f-string 里：Python 3.12 之前不允许 f-string 的
# 表达式部分出现反斜杠（本项目按 3.10+ 兼容，README 也是这么写的）。
TRAILING_COMMA_RE = re.compile(r",(\s*[}\]])")

REPO_ROOT = Path(__file__).resolve().parent.parent
# 原始抓取物含真实姓名/课程/学籍信息，**绝不能入库**，因此写进 raw/ 子目录（已在 .gitignore）。
# 入库的 fixture 是人工从中脱敏提炼出来的那几个小文件，见 TestFixtures/ 下的注释。
FIXTURE_DIR = REPO_ROOT / "XAUAT.LoginApi.Tests" / "TestFixtures" / "raw"
REPORT_PATH = FIXTURE_DIR / "capture-report.txt"

_report_lines: list[str] = []

def report(line: str = "") -> None:
    print(line)
    _report_lines.append(line)

def random_string(length: int) -> str:
    return "".join(random.choice(AES_CHARS) for _ in range(length))

def encrypt_aes(plaintext: str, salt: str | None) -> str:
    """复刻 xauat_sso_login.encrypt_aes：AES-CBC/PKCS7，key=salt ascii，iv=随机 16 字符。"""
    if not salt:
        # Flask 的行为：登录页没有 pwdEncryptSalt 时**直接发明文密码**。
        report("  !! 登录页未提供 pwdEncryptSalt —— 将按 Flask 行为发送明文密码")
        return plaintext

    key = salt.encode("utf-8")
    iv = random_string(16).encode("utf-8")
    msg = (random_string(64) + plaintext).encode("utf-8")

    pad_len = 16 - (len(msg) % 16)
    padded = msg + bytes([pad_len]) * pad_len

    encryptor = Cipher(algorithms.AES(key), modes.CBC(iv)).encryptor()
    ciphertext = encryptor.update(padded) + encryptor.finalize()
    return base64.b64encode(ciphertext).decode("ascii")

def extract_login_params(html: str) -> dict[str, Any]:
    """复刻 get_login_params 的 BeautifulSoup 选择器，但只依赖正则，便于和 C# 侧对拍。"""

    def input_value(pattern: str) -> str | None:
        match = re.search(pattern, html, re.IGNORECASE | re.DOTALL)
        return match.group(1) if match else None

    params: dict[str, Any] = {
        "lt": input_value(r'<input[^>]*name=["\']lt["\'][^>]*value=["\']([^"\']*)["\']') or "",
        "execution": input_value(r'<input[^>]*name=["\']execution["\'][^>]*value=["\']([^"\']*)["\']') or "",
        "_eventId": input_value(r'<input[^>]*name=["\']_eventId["\'][^>]*value=["\']([^"\']*)["\']') or "submit",
        "captcha": "",
        "cllt": "userNameLogin",
        "dllt": "generalLogin",
    }

    salt_input = re.search(r'<input[^>]*id=["\']pwdEncryptSalt["\'][^>]*>', html, re.IGNORECASE)
    if salt_input is None:
        report("  未找到 #pwdEncryptSalt 输入框 —— encrypt_salt=None（Flask 会发明文密码）")
        params["encrypt_salt"] = None
    else:
        value = re.search(r'value=["\']([^"\']*)["\']', salt_input.group(0), re.IGNORECASE)
        params["encrypt_salt"] = value.group(1) if value else DEFAULT_SALT
        report(f"  pwdEncryptSalt = {params['encrypt_salt']!r}"
               f"{'（input 无 value 属性，回退默认盐）' if not value else ''}")

    return params

def save(name: str, content: str, note: str = "") -> None:
    FIXTURE_DIR.mkdir(parents=True, exist_ok=True)
    path = FIXTURE_DIR / name
    path.write_text(content, encoding="utf-8")
    report(f"  已保存 {name}（{len(content)} 字符，utf-8）{'  ' + note if note else ''}")

def redact(text: str, secrets: list[str]) -> str:
    """把学号/姓名等替换成占位符，保持结构不变（改的只是文本内容）。"""
    for secret in secrets:
        if secret and len(secret) >= 3:
            text = text.replace(secret, "20239999")
    return text

def describe(label: str, response: requests.Response) -> None:
    ctype = response.headers.get("Content-Type", "<无>")
    report(f"  {label}: HTTP {response.status_code}  Content-Type: {ctype}")
    if response.encoding:
        report(f"      requests 推断编码: {response.encoding}")

def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        print("用法: python tools/capture-fixtures.py <学号> <密码>", file=sys.stderr)
        return 2

    username, password = sys.argv[1], sys.argv[2]
    FIXTURE_DIR.mkdir(parents=True, exist_ok=True)
    # 学号与密码都不该出现在 fixture 里；姓名只能用页面内容反推，留待人工复查
    secrets = [username, password]

    report("=" * 72)
    report("XAUAT.LoginApi 上游样本抓取")
    report("=" * 72)
    report()

    session = requests.Session()

    # ---------------------------------------------------------------- 1. CAS 登录页
    report("[1/3] GET CAS 登录页")
    login_url = f"{AUTH_SERVER_URL}?service={SERVICE_URL}"
    page = session.get(login_url, headers=BASE_HEADERS, timeout=(30, 60))
    describe("登录页", page)
    report(f"  重定向链长度: {len(page.history)}")
    report(f"  session cookies: {[c.name for c in session.cookies]}")

    save("cas-login-page.html", redact(page.text, secrets))

    params = extract_login_params(page.text)
    report(f"  lt         = {'<空>' if not params['lt'] else params['lt'][:16] + '…'}")
    report(f"  execution  = {'<空>' if not params['execution'] else params['execution'][:16] + '…'}")
    report(f"  _eventId   = {params['_eventId']}")
    report()

    # ---------------------------------------------------------------- 2. CAS 表单登录
    report("[2/3] POST CAS 表单登录（allow_redirects=False）")
    form = {
        "username": username,
        "password": encrypt_aes(password, params["encrypt_salt"]),
        "lt": params["lt"],
        "execution": params["execution"],
        "_eventId": "submit",
        "captcha": "",
        "cllt": "userNameLogin",
        "dllt": "generalLogin",
        "rememberMe": "true",
    }
    response = session.post(
        login_url, data=form, headers=BASE_HEADERS, allow_redirects=False, timeout=(30, 60)
    )
    describe("CAS POST", response)
    report(f"  Location: {response.headers.get('Location', '<无>')}")
    report(f"  本次响应 Set-Cookie: {[c.name for c in response.cookies]}")

    sso_cookies = [c for c in response.cookies]
    report(f"  SSO 票据 cookie: {[c.name for c in sso_cookies]}")
    report()

    if response.status_code == 200:
        report("  !! 返回 200 而非 302：Flask 用 body 里是否含「退出」/「logout」判定成功。")
        save("cas-login-response.html", redact(response.text, secrets))
        report("  已保存 cas-login-response.html 供核对这个启发式判定")
        report()

    # ---------------------------------------------------------------- 3. SSO -> edu cookie
    report("[3/3] GET CAS（带 SSO 票据）换取教务会话 cookie —— 复刻 loginFromSSO")
    sso_cookie_header = "; ".join(f"{c.name}={c.value}" for c in sso_cookies)
    if not sso_cookie_header:
        sso_cookie_header = "; ".join(f"{c.name}={c.value}" for c in session.cookies)
    report(f"  携带 cookie 名: {[p.split('=')[0] for p in sso_cookie_header.split('; ')]}")

    hop = session.get(
        f"{AUTH_SERVER_URL}?service={urllib.parse.quote(SERVICE_URL, safe='')}",
        headers={**BASE_HEADERS, "Cookie": sso_cookie_header},
        allow_redirects=True,
        timeout=(30, 60),
    )
    describe("SSO 换取", hop)
    report(f"  重定向链（{len(hop.history)} 跳）—— .NET 侧要照这个顺序捕获 Set-Cookie：")
    for index, item in enumerate(hop.history):
        names = [c.name for c in item.cookies]
        report(f"      hop[{index}] {item.status_code} {item.url}")
        report(f"               Set-Cookie: {names}")
    report(f"      最终 {hop.status_code} {hop.url}  体长 {len(hop.text)}")
    report("  注意：Flask 取的是 history[1]（swjw 校验跳）的 cookie；请核对上面的 hop 序号。")
    report()

    report("=" * 72)
    report(f"完成。样本目录：{FIXTURE_DIR}")
    report("原始样本写在 TestFixtures/raw/ 下，该目录已在 .gitignore 中——")
    report("里面有真实姓名/课程/学籍信息，**不要**提交。")
    report("入库的 fixture 请从中人工脱敏提炼（保留结构、替换文本），见")
    report("XAUAT.LoginApi.Tests/TestFixtures/ 下各文件的头部注释。")
    report("=" * 72)
    _flush()
    return 0


def _flush() -> None:
    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text("\n".join(_report_lines) + "\n", encoding="utf-8")

if __name__ == "__main__":
    try:
        sys.exit(main())
    finally:
        _flush()
