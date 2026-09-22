#!/usr/bin/env python3
"""抓取 XAUAT 上游真实响应，落成 XAUAT.LoginApi 的测试 fixture。

为什么需要这个脚本
------------------
Flask 侧对 CAS 登录页选择器、studentExamInfoVms 正则、AES 加密全部零测试覆盖、
零真实样本（tests/ 里没有任何 authserver/swjw 的 HTML/JSON）。照 Flask 代码逆推写
C# 解析器等于盲写。所以先用真实账号抓一遍，把响应原文连同结构一起冻结下来。

与 Flask 的一致性
-----------------
本脚本刻意复刻 xauat_sso_login.py 与 xauat_client.py 的请求序列，包括：
  - 明文 http 的 authserver、AES-CBC/PKCS7 密码加密、64 字符随机前缀 + 16 字符随机 IV
  - allow_redirects=False 的 CAS POST，以及 loginFromSSO 的跳转链
  - get-data / schedule-table/datum / exam-arrange 三个教务端点
这样抓到的样本就是 .NET 版必须解析的那一份，不会出现"样本对不上实现"。

依赖
----
复用 Flask 项目的虚拟环境即可（requests + cryptography 都在里面）：
    uv run --project /Users/luckyfish/Documents/Project/PythonProjects/xauat_login_flask \
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
lt/execution 的引号形式、exam 页 JS 里是单引号还是双引号）。跑完请人工扫一眼输出。
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

REPO_ROOT = Path(__file__).resolve().parent.parent
FIXTURE_DIR = REPO_ROOT / "XAUAT.LoginApi.Tests" / "TestFixtures"
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
    report("[1/7] GET CAS 登录页")
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
    report("[2/7] POST CAS 表单登录（allow_redirects=False）")
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
    report("[3/7] GET CAS（带 SSO 票据）换取教务会话 cookie —— 复刻 loginFromSSO")
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

    # ---------------------------------------------------------------- 4. 学期
    report("[4/7] GET 课表页（取当前学期 id）")
    table = session.get(f"{BASE_URL}/for-std/course-table", headers=BASE_HEADERS, timeout=(10, 30))
    describe("课表页", table)
    save("course-table.html", redact(table.text, secrets))

    semester_match = re.search(r'selected" value="(.*?)"', table.text)
    semester = semester_match.group(1) if semester_match else None
    report(f"  学期 id 正则 'selected\" value=\"(.*?)\"' 命中: {semester!r}")
    if not semester:
        report("  !! 没匹配到学期 id —— .NET 侧这个正则需要改，请人工看 course-table.html")
    report()

    if not semester:
        report("学期 id 拿不到，后续端点无法查询，提前结束。")
        _flush()
        return 1

    # ---------------------------------------------------------------- 5. 课表 lessonIds
    report("[5/7] GET course-table/get-data")
    data_url = f"{BASE_URL}/for-std/course-table/get-data?bizTypeId=2&semesterId={semester}&dataId="
    data = session.get(data_url, headers=JSON_HEADERS, timeout=(10, 30))
    describe("get-data", data)
    report(f"  Content-Type 含 application/json: {'application/json' in data.headers.get('Content-Type', '')}")
    save("course-table-get-data.json", redact(data.text, secrets))

    lesson_ids: list[Any] = []
    try:
        payload = data.json()
        lesson_ids = payload.get("lessonIds", [])
        report(f"  lessonIds 数量: {len(lesson_ids)}")
        if not lesson_ids:
            report("  !! 响应里没有 lessonIds —— 会话可能已失效，或上游改了结构")
    except ValueError as exc:
        report(f"  !! 不是合法 JSON（{exc}）—— 会话失效时上游会返回 HTML 登录页")
    report()

    # ---------------------------------------------------------------- 6. 课程明细
    if lesson_ids:
        report("[6/7] POST ws/schedule-table/datum")
        detail = session.post(
            f"{BASE_URL}/ws/schedule-table/datum",
            headers=JSON_HEADERS,
            json={"studentId": "null", "lessonIds": lesson_ids},
            timeout=(10, 30),
        )
        describe("schedule-table/datum", detail)
        save("schedule-table-datum.json", redact(detail.text, secrets))
        try:
            result = detail.json().get("result", {})
            report(f"  lessonList 条数: {len(result.get('lessonList', []))}")
            report(f"  scheduleList 条数: {len(result.get('scheduleList', []))}")
            sample = (result.get("scheduleList") or [{}])[0]
            report(f"  首条 scheduleList 键: {list(sample.keys())}")
            report(f"  首条 room 字段类型: {type(sample.get('room')).__name__}")
            report(f"  首条 startTime 字段类型: {type(sample.get('startTime')).__name__}")
        except ValueError as exc:
            report(f"  !! 不是合法 JSON（{exc}）")
        report()
    else:
        report("[6/7] 跳过（没有 lessonIds）")
        report()

    # ---------------------------------------------------------------- 7. 考试
    report("[7/7] GET for-std/exam-arrange")
    exams = session.get(f"{BASE_URL}/for-std/exam-arrange", headers=BASE_HEADERS, timeout=(10, 30))
    describe("考试页", exams)
    save("exam-arrange.html", redact(exams.text, secrets))

    match = re.search(r"var\s+studentExamInfoVms\s*=\s*(\[[\s\S]*?\]);", exams.text)
    report(f"  studentExamInfoVms 正则命中: {match is not None}")
    if match:
        raw = match.group(1)
        report(f"  匹配到的 JS 数组长度: {len(raw)} 字符")
        report(f"  是否含单引号（.NET 侧迁移要注意）: {chr(39) in raw}")
        report(f"  是否含 undefined: {'undefined' in raw}")
        report(f"  是否有尾逗号: {bool(re.search(r',(\s*[}\]])', raw))}")
    else:
        report("  !! 没匹配到 —— .NET 侧这个正则需要改，请人工看 exam-arrange.html")
    report()

    report("=" * 72)
    report(f"完成。样本目录：{FIXTURE_DIR}")
    report("请人工检查：学号/姓名是否还有残留；再确认 cas-login-page.html 里")
    report("pwdEncryptSalt 的 input 形态（有无 value 属性）与 exam 页 JS 的引号风格。")
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
