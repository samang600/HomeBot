"""폰 워커 + 화면 인식 서비스 (표준 라이브러리 HTTP 서버, 의존성: opencv-python numpy).

  python server.py [--port 8765] [--serial <adb 시리얼>] [--no-phone]

엔드포인트 (HomeBot.Core/Workers/PhoneClient.cs 와 짝):
  GET  /ping
  POST /job    {"type":"gts_find_trade|gts_deposit|gts_collect|gts_withdraw|friend_trade|release","args":{...}} → {"ok","msg","vars","shot"}
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
    # 계정 번호에 맞는 포트(device-N = 16384 + 32·N)를 먼저 시험하고, 다른 계정이 이미 쓰는 기기는 건너뜀
    ser = os.environ.get(f"PHONE{acct}_SERIAL", "")
    want_u = int(os.environ[f"PHONE{acct}_USER"]) if os.environ.get(f"PHONE{acct}_USER") else None
    if not ser:
        # 환경 변수가 없으면(server.py를 직접 띄운 경우) config.json의 Home.Accounts에서
        acc = _config_accounts()
        if len(acc) >= acct and acc[acct - 1].get("Phone"):
            ser = acc[acct - 1]["Phone"]
            if want_u is None and acc[acct - 1].get("AndroidUser") is not None: want_u = int(acc[acct - 1]["AndroidUser"])
    mumu = [16384 + 32 * k for k in range(MAX_ACCTS + 1)]   # MuMu 기기 device-0.. 의 adb 포트
    ports = [16384 + 32 * (acct - 1)] + [p for p in mumu[1:] if p != 16384 + 32 * (acct - 1)]
    cands = [ser] if ser else [f"127.0.0.1:{p}" for p in ports]   # 아무 기록도 없으면 MuMu 기본 포트 순서로
    used = {(HOME.adb.serial, HOME.adb.user)} | {(h.adb.serial, h.adb.user) for h in HOMES.values()}   # 같은 기기라도 안드로이드 사용자가 다르면 다른 계정
    for sr in cands:
        if (sr, want_u) in used or (not ser and sr in {x for x, _ in used}): continue   # 자동으로 찾을 땐 이미 쓰는 기기는 건너뜀
        try:
            a2 = Adb(sr, want_u)
            a2.fit_display(log)
            if HOME.adb.package in a2.run("shell", "pm", "list", "packages", HOME.adb.package, timeout=10):
                log(f"계정 {acct}: 기기 {sr} 의 Pokémon HOME")
                h = Home(a2, log); HOMES[acct] = h
                return h
        except Exception as e:
            log(f"계정 {acct} 기기 {sr} 연결 실패:", e)
    if acct != 2 or os.environ.get("PHONE2_SAME_DEVICE", "") != "1":
        raise RuntimeError(f"계정 {acct}의 휴대폰을 adb로 찾지 못함 (설정 Home.Accounts의 Phone 확인)")
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
            return self._json(200, {"ok": True, "phone": HOME is not None, "name_ocr": V.name_ocr_ready()})
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
                        with open(TRADES_LOG, encoding="utf-8") as fp:
                            for line in fp:
                                try:
                                    r = json.loads(line)
                                    if r.get("t", 0) >= since: rows.append(r)
                                except Exception: pass
                    except FileNotFoundError: pass
                    return self._json(200, {"ok": True, "msg": "", "vars": {"list": json.dumps(rows, ensure_ascii=False)}, "shot": ""})
                if t == "exit":
                    # 워커 다시 켜기: 지금 하는 작업이 끝나길 기다렸다가(LOCK) 종료 → 관리 창이 30초 안에 다시 켬 (바뀐 코드 적용)
                    def _bye():
                        for k in sorted(LOCKS): LOCKS[k].acquire()   # 모든 계정 작업이 끝나길 기다림
                        log("워커 종료 요청 → 다시 켜짐")
                        os._exit(0)
                    threading.Thread(target=_bye, daemon=True).start()
                    return self._json(200, {"ok": True, "msg": "종료 예약", "vars": {}, "shot": ""})
                acct_req = int(a.get("acct", 1) or 1)
                if acct_req not in LOCKS:
                    return self._json(200, {"ok": False, "msg": f"계정 번호 {acct_req}는 지원하지 않음 (1~{MAX_ACCTS})", "vars": {}, "shot": ""})
                with LOCKS[acct_req]:
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
                        if t == "gts_deposit":
                            v = HM.gts_deposit(int(a["box"]), int(a["slot"]), a.get("species_name", ""), int(a.get("gender", -1)), int(a.get("level_min", 1)), int(a.get("level_max", 10)))
                        elif t == "gts_find_trade":
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
                        elif t == "gts_collect":
                            v = HM.gts_collect()
                        elif t == "gts_withdraw":
                            v = HM.gts_withdraw()
                        elif t == "friend_trade":
                            v = HM.friend_trade(int(a["box"]), int(a["slot"]), a.get("friend", ""))
                        elif t == "release":
                            v = HM.release([(int(s["box"]), int(s["slot"])) for s in a.get("slots", [])])
                        else:
                            return self._json(200, {"ok": False, "msg": "모르는 작업: " + str(t)})
                        return self._json(200, {"ok": True, "msg": "", "vars": {k: str(x) for k, x in (v or {}).items()}, "shot": ""})
                    except Exception as e:
                        shot = getattr(e, "shot", "")
                        log("실패:", e)
                        return self._json(200, {"ok": False, "msg": str(e), "vars": {}, "shot": shot})
            self._json(404, {"ok": False})
        except Exception as e:
            traceback.print_exc()
            self._json(500, {"ok": False, "msg": str(e)})


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
    threading.Thread(target=V.name_ocr_ready, daemon=True).start()   # 이름 인식 준비 여부를 미리 확인해 둠
    srv = ThreadingHTTPServer(("127.0.0.1", a.port), H)
    log(f"워커 서비스 http://127.0.0.1:{a.port}  (phone={'on' if HOME else 'off'})")
    srv.serve_forever()


if __name__ == "__main__":
    main()
