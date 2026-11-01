"""폰 워커 + 화면 인식 서비스 (표준 라이브러리 HTTP 서버, 의존성: opencv-python numpy).

  python server.py [--port 8765] [--serial <adb 시리얼>] [--no-phone]

엔드포인트 (HomeBot.Core/Workers/PhoneClient.cs 와 짝):
  GET  /ping   → {"ok","phone","name_ocr","busy_since":{"계정":작업 시작 시각}}
  POST /job    {"type":"gts_find_trade|home_close|home_launch|recent_trades|exit","args":{...}} → {"ok","msg","vars","shot"}
  POST /match  multipart(image, template, threshold) → {"found","score","x","y"}
  POST /ocr    multipart(image, region="x,y,w,h") → {"text"}
--no-phone 이면 /match /ocr 만 제공 (스위치 화면 인식 전용).
"""
import argparse, json, os, sys, threading, time, traceback
from email.parser import BytesParser
from email.policy import HTTP
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import vision as V

HOME = None
HOMES = {}        # 계정 번호 → Home (1 = #N1, 2 = #N2). 2는 처음 쓸 때 만듦
USER2 = None
LAST_ACCT = 1
LOCK = threading.Lock()   # 계정 1 작업 잠금
MAX_ACCTS = 5   # HOME 계정(휴대폰) 최대 수 — 봇(Config.MaxAccounts)과 같게
LOCKS = {1: LOCK, **{k: threading.Lock() for k in range(2, MAX_ACCTS + 1)}}   # 계정마다 따로: 두 앱(#N1·#N2)이 따로 창에 떠 있으면 두 계정 작업을 동시에 할 수 있음
BUSY_SINCE = {}   # 계정 번호 → 지금 작업을 시작한 시각 (time.time). /ping 으로 알려 관리 창이 멈춘 워커를 다시 켬
TRADES_LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "trades_done.jsonl")


def log(*a):
    print(*a, flush=True)


def home_of(acct):
    """계정 번호에 맞는 휴대폰 HOME 조작기. 2번 계정은 #N2 (안드로이드 사용자 = --user2, 없으면 HOME이 깔린 다음 사용자)."""
    global USER2
    acct = int(acct or 1)
    if acct <= 1 or HOME is None:
        return HOME
    if acct in HOMES:
        return HOMES[acct]
    from adb import Adb
    from flows import Home
    # 두 번째 계정은 MuMu 두 번째 기기(device-1), 세 번째 계정은 세 번째 기기(device-2)의 Pokémon HOME (기본 사용자).
    # 계정 번호에 맞는 기기만 씀: 설정(Home.Accounts의 Phone)·PHONE{n}_SERIAL에 적은 휴대폰, 없으면 MuMu 기본 포트(device-N = 16384 + 32·N).
    # 다른 계정의 기기를 빌려 쓰면 계정이 뒤바뀐 채 굳음 → 빌리지 않음
    ser = os.environ.get(f"PHONE{acct}_SERIAL", "")
    want_u = int(os.environ[f"PHONE{acct}_USER"]) if os.environ.get(f"PHONE{acct}_USER") else None
    if not ser:
        # 환경 변수가 없으면(server.py를 직접 띄운 경우) config.json의 Home.Accounts에서
        acc = _config_accounts()
        if len(acc) >= acct and acc[acct - 1].get("Phone"):
            ser = acc[acct - 1]["Phone"]
            if want_u is None and acc[acct - 1].get("AndroidUser") is not None: want_u = int(acc[acct - 1]["AndroidUser"])
    sr = ser or f"127.0.0.1:{16384 + 32 * (acct - 1)}"
    used = {(HOME.adb.serial, HOME.adb.user)} | {(h.adb.serial, h.adb.user) for h in HOMES.values()}   # 같은 기기라도 안드로이드 사용자가 다르면 다른 계정
    if (sr, want_u) in used or (not ser and sr in {x for x, _ in used}):
        raise RuntimeError(f"계정 {acct}의 기기 {sr}를 다른 계정이 이미 쓰고 있음 (설정 Home.Accounts의 Phone 확인)")
    a2 = Adb(sr, want_u)
    for attempt in range(2):
        try:
            a2.fit_display(log)
            if HOME.adb.package in a2.run("shell", "pm", "list", "packages", HOME.adb.package, timeout=10):
                log(f"계정 {acct}: 기기 {sr} 의 Pokémon HOME")
                h = Home(a2, log); HOMES[acct] = h
                return h
            log(f"계정 {acct}: 기기 {sr}에 Pokémon HOME이 없음")
            break
        except Exception as e:
            log(f"계정 {acct} 기기 {sr} 연결 실패:", e)
            if attempt == 0 and a2.is_emulator():
                try:
                    import emulator
                    emulator.ensure(a2, log)                     # 그 기기(MuMu device-N)만 켜 봄
                except Exception as e2:
                    log(f"계정 {acct} 기기 {sr} 켜기 실패:", e2)
                    break
            else:
                break
    if acct != 2 or os.environ.get("PHONE2_SAME_DEVICE", "") != "1":
        raise RuntimeError(f"계정 {acct}의 휴대폰 {sr}를 adb로 찾지 못함 (설정 Home.Accounts의 Phone 확인)")
    u = USER2
    if u is None:
        base = HOME.adb.user if HOME.adb.user is not None else 0
        try:
            out = HOME.adb.run("shell", "pm", "list", "users")
            import re
            users = [(int(i), n) for i, n in re.findall(r"UserInfo\{(\d+):([^:}]*)", out)]
            log("안드로이드 사용자:", users)
            named = [i for i, n in users if "N2" in n.replace("#", "").upper()]
            ids = named + sorted(i for i, _ in users if i not in named)
            for i in ids:
                if i <= base: continue
                pk = HOME.adb.run("shell", "pm", "list", "packages", "--user", str(i), HOME.adb.package)
                if HOME.adb.package in pk:
                    u = i; break
        except Exception as e:
            log("두 번째 HOME 사용자 찾기 실패:", e)
        if u is None: u = base + 1
        USER2 = u
    log(f"계정 {acct}: 안드로이드 사용자 {u} (#N2)")
    h = Home(Adb(HOME.adb.serial, u), log)
    HOMES[acct] = h
    if os.environ.get("PHONE_TABS", "front") == "detach":
        _hook(h.adb, acct)
        _hook(HOME.adb, 1)
    return h


def _hook(adb, acct):
    """앱을 켠 뒤: 다른 계정 앱도 켜져 있으면 이 앱 탭을 따로 창으로 뗌 (두 앱이 동시에 그려지게)."""
    def after():
        try:
            import winsep
            other = USER2 if acct == 1 else (HOME.adb.user if HOME.adb.user is not None else 0)
            winsep.separate(acct, other in adb.running_users())
        except Exception as e:
            log("탭 떼기 실패:", e)
    adb.after_launch = after


def _config_accounts():
    """config.json의 Home.Accounts (주석 있는 JSON도 읽음). 없으면 []"""
    try:
        import json, re
        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "config.json"), encoding="utf-8-sig") as fp: t = fp.read()
        t = re.sub(r'("(?:\\.|[^"\\])*")|//[^\n]*', lambda m: m.group(1) or "", t)
        t = re.sub(r",(\s*[}\]])", r"\1", t)
        return (json.loads(t).get("Home", {}) or {}).get("Accounts") or []
    except Exception:
        return []


MEM_LIMIT_MB = int(os.environ.get("WORKER_MEM_LIMIT_MB", "2500"))   # 워커 메모리가 이만큼 넘으면 작업이 끝난 뒤 스스로 다시 켬
_restarting = False


def mem_mb():
    """(이 워커가 쓰는 메모리 MB, 컴퓨터 남은 메모리 MB). Windows가 아니거나 실패하면 (0, 0)."""
    try:
        import ctypes
        from ctypes import wintypes

        class PMC(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD), ("PeakWorkingSetSize", ctypes.c_size_t),
                        ("WorkingSetSize", ctypes.c_size_t), ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                        ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t), ("PrivateUsage", ctypes.c_size_t)]

        class MS(ctypes.Structure):
            _fields_ = [("dwLength", wintypes.DWORD), ("dwMemoryLoad", wintypes.DWORD), ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                        ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong), ("ullTotalVirtual", ctypes.c_ulonglong),
                        ("ullAvailVirtual", ctypes.c_ulonglong), ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
        pmc = PMC(); pmc.cb = ctypes.sizeof(PMC)
        k32 = ctypes.WinDLL("kernel32"); psapi = ctypes.WinDLL("psapi")
        # 핸들은 64비트: restype/argtypes를 안 정하면 int(32비트)로 잘려 실패하고 0MB로 나옴
        k32.GetCurrentProcess.restype = wintypes.HANDLE
        psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD]
        psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
        k32.GlobalMemoryStatusEx.argtypes = [ctypes.c_void_p]
        psapi.GetProcessMemoryInfo(k32.GetCurrentProcess(), ctypes.byref(pmc), pmc.cb)
        ms = MS(); ms.dwLength = ctypes.sizeof(MS); k32.GlobalMemoryStatusEx(ctypes.byref(ms))
        return int(pmc.PrivateUsage // 1048576), int(ms.ullAvailPageFile // 1048576)
    except Exception:
        return 0, 0


def ram_free_mb():
    try:
        import ctypes
        from ctypes import wintypes

        class MS(ctypes.Structure):
            _fields_ = [("dwLength", wintypes.DWORD), ("dwMemoryLoad", wintypes.DWORD), ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                        ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong), ("ullTotalVirtual", ctypes.c_ulonglong),
                        ("ullAvailVirtual", ctypes.c_ulonglong), ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
        ms = MS(); ms.dwLength = ctypes.sizeof(MS); ctypes.WinDLL("kernel32").GlobalMemoryStatusEx(ctypes.byref(ms))
        return f"{ms.ullAvailPhys // 1048576}/{ms.ullTotalPhys // 1048576}"
    except Exception:
        return "?"


def restart_when_idle(why):
    """모든 계정 작업이 끝나길 기다렸다가 종료 → 관리 창이 다시 켬 (워커 다시 켜기와 같음)."""
    global _restarting
    if _restarting: return
    _restarting = True
    def _bye():
        got = [k for k in sorted(LOCKS) if LOCKS[k].acquire(timeout=600)]
        log(f"{why} → 워커를 다시 켭니다" + ("" if len(got) == len(LOCKS) else " (끝나지 않는 작업이 있어 기다리지 않음)"))
        os._exit(0)
    threading.Thread(target=_bye, daemon=True).start()


class H(BaseHTTPRequestHandler):
    def _json(self, code, obj):
        b = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        try:
            self.wfile.write(b)
        except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
            pass   # 상대(관리 창 상태 확인 등)가 먼저 끊음 — 무시

    def log_message(self, *a):
        pass

    def do_GET(self):
        if self.path == "/ping":
            used, free = mem_mb()
            return self._json(200, {"ok": True, "phone": HOME is not None, "name_ocr": V.name_ocr_ready(),
                                    "busy_since": {str(k): v for k, v in BUSY_SINCE.items() if v}, "mem_mb": used, "free_mb": free})
        self._json(404, {"ok": False})

    def _form(self):
        """multipart/form-data 또는 JSON → dict. 파일 필드는 bytes, 나머지는 str."""
        ctype = self.headers.get("Content-Type", "")
        n = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(n)
        if ctype.startswith("multipart/form-data"):
            msg = BytesParser(policy=HTTP).parsebytes(b"Content-Type: " + ctype.encode() + b"\r\n\r\n" + body)
            out = {}
            for part in msg.iter_parts():
                name = part.get_param("name", header="content-disposition")
                if name is None:
                    continue
                payload = part.get_payload(decode=True)
                out[name] = payload if part.get_filename() else payload.decode("utf-8", "ignore")
            return out
        return json.loads(body or b"{}")

    def do_POST(self):
        try:
            if self.path == "/match":
                f = self._form()
                img = V.decode(f["image"])
                thr = float(f.get("threshold", "0.85"))
                found, score, x, y = V.match(img, f.get("template", ""), thr)
                return self._json(200, {"found": found, "score": round(score, 3), "x": x, "y": y})
            if self.path == "/ocr":
                f = self._form()
                img = V.decode(f["image"])
                region = f.get("region", "")
                reg = [int(v) for v in region.split(",")] if region else None
                if f.get("mode") == "title":
                    return self._json(200, {"text": V.ocr_title(img, reg)})
                return self._json(200, {"text": V.ocr(img, reg)})
            if self.path == "/logo":
                # 게임 로고 템플릿: check=1 이면 있는지만, 아니면 화면의 region 안에서 로고를 잘라 templates/switch/<name>.png 로 저장
                f = self._form()
                name = f.get("name", "")
                if f.get("check"):
                    return self._json(200, {"exists": V.template_exists(name)})
                img = V.decode(f["image"])
                reg = [int(v) for v in f.get("region", "50,170,580,260").split(",")]
                try:
                    return self._json(200, {"ok": True, "path": V.save_logo(img, name, reg)})
                except Exception as e:
                    return self._json(200, {"ok": False, "msg": str(e)})
            if self.path == "/job":
                if HOME is None:
                    return self._json(200, {"ok": False, "msg": "폰 워커가 --no-phone 으로 떠 있습니다"})
                req = self._form()
                t, a = req.get("type"), req.get("args", {})
                if t == "recent_trades":
                    # 봇이 교환 도중 꺼졌다 켜졌을 때: 그사이 끝난 교환이 있었는지 (작업 잠금 없이 기록 파일만 읽음)
                    since = time.time() - float(a.get("minutes", 30)) * 60   # (기록의 acct는 봇이 걸러 씀)
                    rows = []
                    try:
                        with open(TRADES_LOG, "rb") as fp:                # 파일이 계속 커지므로 끝 64KB만 읽음 (몇 시간치 기록이면 충분)
                            fp.seek(0, 2); size = fp.tell()
                            fp.seek(max(0, size - 65536))
                            tail = fp.read().decode("utf-8", "ignore").splitlines()
                            if size > 65536: tail = tail[1:]              # 잘린 첫 줄은 버림
                        for line in tail:
                            try:
                                r = json.loads(line)
                                if r.get("t", 0) >= since: rows.append(r)
                            except Exception: pass
                    except FileNotFoundError: pass
                    return self._json(200, {"ok": True, "msg": "", "vars": {"list": json.dumps(rows, ensure_ascii=False)}, "shot": ""})
                if t == "exit":
                    # 워커 다시 켜기: 지금 하는 작업이 끝나길 기다렸다가(LOCK) 종료 → 관리 창이 30초 안에 다시 켬 (바뀐 코드 적용)
                    restart_when_idle("워커 종료 요청")
                    return self._json(200, {"ok": True, "msg": "종료 예약", "vars": {}, "shot": ""})
                acct_req = int(a.get("acct", 1) or 1)
                if acct_req not in LOCKS:
                    return self._json(200, {"ok": False, "msg": f"계정 번호 {acct_req}는 지원하지 않음 (1~{MAX_ACCTS})", "vars": {}, "shot": ""})
                with LOCKS[acct_req]:
                    BUSY_SINCE[acct_req] = time.time()
                    try:
                        return self._job(t, a)
                    finally:
                        BUSY_SINCE.pop(acct_req, None)
                        used, free = mem_mb()
                        if used >= MEM_LIMIT_MB:
                            restart_when_idle(f"워커 메모리 {used}MB (기준 {MEM_LIMIT_MB}MB, 컴퓨터 남은 메모리 {free}MB)")
            self._json(404, {"ok": False})
        except Exception as e:
            traceback.print_exc()
            self._json(500, {"ok": False, "msg": str(e)})

    def _job(self, t, a):
        """한 계정의 작업 하나 (그 계정 잠금 안에서)."""
        log("작업:", t, a)
        try:
            HM = home_of(a.get("acct", 1))
        except Exception as e:
            return self._json(200, {"ok": False, "msg": "계정 준비 실패: " + str(e), "vars": {}, "shot": ""})
        global LAST_ACCT
        acct_now = int(a.get("acct", 1) or 1)
        PAR = os.environ.get("PHONE_TABS", "front") == "detach"   # 기본: 탭을 떼지 않고 매번 그 앱을 앞으로 (번갈아 모드, 안정적)
        if PAR and t not in ("home_close",) and HM is not None:
            try:
                import winsep
                winsep.ensure_visible(acct_now)
            except Exception as e:
                log("탭 확인 실패:", e)
        if t not in ("home_close", "home_launch") and HM is not None:
            # MuMu는 보이지 않는 탭의 앱을 멈춰 둠(흰 화면) → 매번 이 계정 앱의 탭을 앞으로 가져온 뒤 작업 (사용자가 탭을 눌러 바꿔도 맞게)
            try:
                if not PAR: HM.adb.bring_front()   # 떼기 모드에서는 쓰지 않음 (떼어 둔 창을 본 창 탭으로 다시 붙임)
            except Exception as e:
                log("앱 앞으로 가져오기 실패:", e)
        if t not in ("home_close",): LAST_ACCT = acct_now
        try:
            if HM.adb.is_emulator():
                import emulator
                emulator.ensure(HM.adb, log)
            elif HM.adb.serial not in HM.adb.devices():
                raise RuntimeError("휴대폰이 응답하지 않습니다 (USB·무선 디버깅 연결과 '허용' 확인)")
        except Exception as e:
            log("휴대폰 확인 실패:", e)
            return self._json(200, {"ok": False, "msg": "휴대폰 준비 실패: " + str(e), "vars": {}, "shot": ""})
        try:
            if t == "gts_find_trade":
                v = HM.gts_find_trade(a.get("species_name", ""), int(a.get("gender", -1)), int(a.get("level", 0)), a.get("lang", ""), int(a.get("slot", 0)), str(a.get("dry", "")).lower() in ("1", "true"), str(a.get("fresh", "")).lower() in ("1", "true"), a.get("nick", ""), a.get("nick_lang", ""), int(a.get("nth", 0)), int(a.get("total", 1)), (json.loads(a["fp"]) if a.get("fp") else None))
                if (v or {}).get("traded") == "1":
                    # 교환 완료를 파일에도 남김 (봇이 응답을 못 받고 꺼졌어도 다시 켜진 뒤 알 수 있게)
                    try:
                        with open(TRADES_LOG, "a", encoding="utf-8") as fp:
                            fp.write(json.dumps({"t": time.time(), "species_name": a.get("species_name", ""), "level": int(a.get("level", 0)), "lang": a.get("lang", ""), "gender": int(a.get("gender", -1)), "nick": a.get("nick", ""), "acct": int(a.get("acct", 1) or 1)}, ensure_ascii=False) + "\n")
                    except Exception as e: log("교환 기록 실패:", e)
            elif t == "home_close":
                v = HM.close()
            elif t == "home_launch":
                v = HM.relaunch()
            else:
                return self._json(200, {"ok": False, "msg": "모르는 작업: " + str(t)})
            return self._json(200, {"ok": True, "msg": "", "vars": {k: str(x) for k, x in (v or {}).items()}, "shot": ""})
        except Exception as e:
            shot = getattr(e, "shot", "")
            log("실패:", e)
            return self._json(200, {"ok": False, "msg": str(e), "vars": {}, "shot": shot})


def main():
    global HOME
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--serial", default=None)
    ap.add_argument("--user", type=int, default=None, help="MuMu 앱 복제본의 안드로이드 사용자 번호 (#N1 = 10)")
    ap.add_argument("--user2", type=int, default=None, help="두 번째 HOME 계정 앱(#N2)의 안드로이드 사용자 번호 (없으면 자동으로 찾음)")
    ap.add_argument("--no-phone", action="store_true")
    a = ap.parse_args()
    global USER2
    USER2 = a.user2 if a.user2 is not None else (int(os.environ["PHONE_USER2"]) if os.environ.get("PHONE_USER2") else None)
    if not a.no_phone:
        from adb import Adb
        from flows import Home
        adb = Adb(a.serial, a.user)
        if ":" in a.serial:
            try: import subprocess; from adb import ADB as _ADB; subprocess.run([_ADB, "connect", a.serial], capture_output=True, timeout=15)   # 무선·에뮬레이터 주소면 먼저 연결
            except Exception: pass
        devs = [d for d in adb.devices() if d == a.serial]
        if not devs and adb.is_emulator():
            try:
                import emulator
                emulator.ensure(adb, log); devs = [d for d in adb.devices() if d == a.serial]
            except Exception as e:
                log("에뮬레이터 켜기 실패:", e)
        if not devs:
            log(f"adb devices 에 {a.serial} 이(가) 없습니다. USB 디버깅을 켜고 연결하거나(실제 폰), MuMu를 켜세요 (--no-phone 으로 인식 서비스만 띄울 수 있음)")
            sys.exit(1)
        adb.fit_display(log)
        log("폰:", devs, "해상도:", adb.size())
        try:
            adb.stay_awake()
        except Exception:
            pass
        HOME = Home(adb, log)
    def _memlog():
        # 1분마다 워커·컴퓨터 메모리를 shots/mem.log에 (메모리 부족 오류가 워커 탓인지 컴퓨터 전체 탓인지 가리려고)
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "shots", "mem.log")
        while True:
            try:
                used, free = mem_mb()
                with open(path, "a", encoding="utf-8") as fp: fp.write(f"{time.strftime('%m-%d %H:%M:%S')} worker={used}MB free_commit={free}MB free_ram={ram_free_mb()}MB busy={sorted(BUSY_SINCE)}\n")
            except Exception: pass
            time.sleep(60)
    threading.Thread(target=_memlog, daemon=True).start()
    threading.Thread(target=V.name_ocr_ready, daemon=True).start()   # 이름 인식 준비 여부를 미리 확인해 둠
    srv = ThreadingHTTPServer(("127.0.0.1", a.port), H)
    log(f"워커 서비스 http://127.0.0.1:{a.port}  (phone={'on' if HOME else 'off'})")
    srv.serve_forever()


if __name__ == "__main__":
    main()
