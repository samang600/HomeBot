"""ADBKeyboard 설치·지정 / 되돌리기 (HOME에서 포켓몬 이름을 한글로 검색할 때 필요).
  python ime_setup.py install    config.json의 HOME 계정 휴대폰마다 ADBKeyboard 설치 후 입력기로 지정
  python ime_setup.py restore    이전 입력기로 되돌리기
  (뒤에 기기 주소와 안드로이드 사용자 번호를 주면 그 기기만: python ime_setup.py install 127.0.0.1:16384 10)
"""
import json, os, re, subprocess, sys
from adb import ADB

IME = "com.android.adbkeyboard/.AdbIME"
HERE = os.path.dirname(os.path.abspath(__file__))
PREV = os.path.join(HERE, "ime_prev.json")


def targets():
    if len(sys.argv) >= 3:
        return [(sys.argv[2], sys.argv[3] if len(sys.argv) >= 4 else "0")]
    try:
        with open(os.path.join(HERE, "..", "config.json"), encoding="utf-8-sig") as fp:
            t = fp.read()
        t = re.sub(r'("(?:\\.|[^"\\])*")|//[^\n]*', lambda m: m.group(1) or "", t)   # 문자열 밖의 // 주석
        t = re.sub(r",(\s*[}\]])", r"\1", t)
        acc = json.loads(t).get("Home", {}).get("Accounts") or []
        out = [(a["Phone"], str(a.get("AndroidUser") if a.get("AndroidUser") is not None else 0)) for a in acc if a.get("Phone")]
        if out: return out
    except Exception as e:
        print("config.json을 읽지 못함:", e)
    return [("127.0.0.1:16384", "0")]


def sh(serial, *x):
    p = subprocess.run([ADB, "-s", serial, "shell", *x], capture_output=True)
    out = (p.stdout + p.stderr).decode(errors="ignore").strip()
    print(" ".join(x), "->", out or f"(코드 {p.returncode})")
    return out


def load_prev():
    try:
        with open(PREV, encoding="utf-8") as fp: return json.load(fp)
    except Exception: return {}


def install(serial, user):
    print(f"\n== {serial} (사용자 {user}) ==")
    subprocess.run([ADB, "connect", serial], capture_output=True)
    r = subprocess.run([ADB, "-s", serial, "install", "-r", os.path.join(HERE, "ADBKeyboard.apk")], capture_output=True, text=True)
    print((r.stdout + r.stderr).strip())
    if user != "0": sh(serial, "pm", "install-existing", "--user", user, "com.android.adbkeyboard")
    prev = sh(serial, "settings", "--user", user, "get", "secure", "default_input_method")
    if prev and "adbkeyboard" not in prev:
        d = load_prev(); d[f"{serial}|{user}"] = prev
        with open(PREV, "w", encoding="utf-8") as fp: json.dump(d, fp)
    sh(serial, "ime", "enable", "--user", user, IME)
    sh(serial, "ime", "set", "--user", user, IME)
    if "adbkeyboard" not in sh(serial, "settings", "--user", user, "get", "secure", "default_input_method"):
        en = sh(serial, "settings", "--user", user, "get", "secure", "enabled_input_methods")   # ime 명령이 막힌 경우: 설정 값을 직접 씀
        if IME not in en:
            sh(serial, "settings", "--user", user, "put", "secure", "enabled_input_methods", (en + ":" if en and en != "null" else "") + IME)
        sh(serial, "settings", "--user", user, "put", "secure", "default_input_method", IME)
    print("입력기 =", sh(serial, "settings", "--user", user, "get", "secure", "default_input_method"))


def restore(serial, user):
    prev = load_prev().get(f"{serial}|{user}")
    if not prev: print(f"{serial}: 저장된 이전 입력기가 없습니다"); return
    sh(serial, "ime", "set", "--user", user, prev)
    sh(serial, "settings", "--user", user, "put", "secure", "default_input_method", prev)
    print("되돌림:", prev)


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd not in ("install", "restore"): print(__doc__); sys.exit(1)
    for s, u in targets():
        (install if cmd == "install" else restore)(s, u)
