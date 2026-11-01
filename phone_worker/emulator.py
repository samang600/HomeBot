"""MuMu 지킴이: 에뮬레이터가 꺼졌거나 튕겼으면 'Android Device #0'을 다시 켜고, 부팅 뒤 광고를 닫음.

MuMuManager.exe 위치는 자동으로 찾음 (못 찾으면 환경 변수 MUMU_MANAGER 에 전체 경로를 넣으세요).
"""
import glob, json, os, subprocess, time

VM_INDEX = os.environ.get("MUMU_VM", "0")
_mgr = None


def manager():
    global _mgr
    if _mgr and os.path.exists(_mgr):
        return _mgr
    env = os.environ.get("MUMU_MANAGER")
    if env and os.path.exists(env):
        _mgr = env; return _mgr
    pats = []
    for d in "CDEFG":
        for pf in ("Program Files", "Program Files (x86)", ""):
            base = f"{d}:\\{pf}\\Netease" if pf else f"{d}:\\Netease"
            pats += [base + "\\MuMu*\\**\\MuMuManager.exe"]
    for p in pats:
        hits = glob.glob(p, recursive=True)
        if hits:
            _mgr = hits[0]; return _mgr
    return None


def _mm(*args, timeout=60):
    m = manager()
    if not m:
        raise RuntimeError("MuMuManager.exe를 찾지 못함 (환경 변수 MUMU_MANAGER 에 경로를 넣으세요)")
    p = subprocess.run([m, *args], capture_output=True, timeout=timeout)
    return (p.stdout or b"").decode("utf-8", "ignore")


def _vm_of(adb):
    try: return str(max(0, (int(str(getattr(adb, 'port_serial', None) or adb.serial).split(":")[1]) - 16384) // 32))
    except Exception: return VM_INDEX


def android_started(vm=None):
    try:
        out = _mm("info", "-v", vm or VM_INDEX, timeout=20)
        j = json.loads(out[out.index("{"):])
        return bool(j.get("is_android_started")), j
    except Exception:
        return None, {}


def online(adb):
    try:
        if adb.serial and ":" in adb.serial:
            subprocess.run([adb._base()[0], "connect", adb.serial], capture_output=True, timeout=10)
        out = adb.run("shell", "getprop", "sys.boot_completed", timeout=10).strip()
        return out == "1"
    except Exception:
        return False


def close_ads(adb, log=print):
    """부팅 직후 바탕화면(0번 화면)에 뜨는 광고·공지 창 닫기: 뒤로 가기를 몇 번 누름."""
    for _ in range(3):
        try: adb.run("shell", "input", "-d", "0", "keyevent", "4")
        except Exception: pass
        time.sleep(1.0)
    try: adb.run("shell", "input", "-d", "0", "keyevent", "3")   # 홈
    except Exception: pass
    log("부팅 뒤 광고 닫기 시도 (뒤로 가기)")


def ensure(adb, log=print, boot_timeout=240):
    """기기가 응답하면 그대로. 아니면 MuMu를 켜고 부팅을 기다림."""
    if online(adb):
        return False
    vm = _vm_of(adb)
    log("에뮬레이터 응답 없음 → MuMu 기기 #%s 다시 켬" % vm)
    started, _ = android_started(vm)
    if not started:
        try: _mm("control", "-v", vm, "launch", timeout=60)
        except Exception as e: log("MuMu 실행 명령 실패:", e)
    t0 = time.time()
    while time.time() - t0 < boot_timeout:
        if online(adb):
            break
        time.sleep(5)
    else:
        raise RuntimeError("MuMu가 제한 시간 안에 켜지지 않음")
    time.sleep(10)
    close_ads(adb, log)
    adb.display = adb.sf_display = None
    log("에뮬레이터 다시 켜짐")
    return True


if __name__ == "__main__":
    print("MuMuManager:", manager())
    print("상태:", android_started())
