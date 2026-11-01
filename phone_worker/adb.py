"""ADB 래퍼: 루팅 없이 되는 것만 (screencap, input tap/swipe/keyevent)."""
import subprocess, time, os, re

ADB = os.environ.get("ADB", "adb")


class Adb:
    def __init__(self, serial: str | None = None, user: int | None = None):
        self.serial = serial
        self.user = user      # MuMu 앱 복제본(#N1 등)은 안드로이드 다른 사용자(10, 11…)로 깔려 있음
        self.display = None   # 입력용 논리 번호 (input -d)
        self.sf_display = None  # 캡처용 SurfaceFlinger 번호 (screencap -d)
        self.package = "jp.pokemon.pokemonhome"
        self.after_launch = None   # 앱을 새로 켠 뒤 부를 함수 (MuMu 탭을 따로 창으로 떼기)
        self.scale = 1.0      # 실제 화면 폭 / 1080. 템플릿·좌표는 1080x1920 기준이라 캡처는 늘리고 터치는 줄임
        if serial and ":" in serial:
            try: subprocess.run([ADB, "connect", serial], capture_output=True, timeout=10)
            except Exception: pass

    def _base(self):
        return [ADB] + (["-s", self.serial] if self.serial else [])

    def run(self, *args, timeout=30, binary=False):
        p = subprocess.run(self._base() + list(args), capture_output=True, timeout=timeout)
        if p.returncode != 0:
            raise RuntimeError(f"adb {' '.join(args)}: {p.stderr.decode(errors='ignore').strip()}")
        return p.stdout if binary else p.stdout.decode(errors="ignore")

    def devices(self):
        out = self.run("devices")
        return [l.split("\t")[0] for l in out.splitlines()[1:] if "\tdevice" in l]

    def find_display(self, package=None):
        """MuMu는 앱마다 따로 화면(디스플레이)을 띄움 → HOME 앱이 떠 있는 화면 번호를 찾음."""
        package = package or self.package
        acts = self.run("shell", "dumpsys", "activity", "activities")
        cur = None; logical = None
        tag = (f"u{self.user} {package}" if self.user is not None else package)
        for l in acts.splitlines():
            m = re.search(r"Display #(\d+)", l)
            if m: cur = int(m.group(1))
            if tag in l and cur is not None:
                logical = cur; break
        if logical is None:
            return False
        sf = None
        for l in self.run("shell", "cmd", "display", "get-displays").splitlines():
            m = re.search(r"displayId (\d+),", l); u = re.search(r'uniqueId "local:(\d+)"', l)
            if m and u and int(m.group(1)) == logical:
                sf = u.group(1)
        self.display, self.sf_display = logical, sf
        return True

    def screenshot(self) -> bytes:
        """PNG 바이트. 디스플레이가 여러 개면 HOME 화면을 찾아 그 화면만 찍음."""
        if self.sf_display is None:
            try: self.find_display()
            except Exception: pass
        b = self.run(*(["exec-out", "screencap"] + (["-d", self.sf_display] if self.sf_display else []) + ["-p"]), binary=True)
        i = b.find(b"\x89PNG")
        if i < 0:
            self.sf_display = None  # 앱을 다시 켜면 번호가 바뀔 수 있음 → 다음에 다시 찾기
            raise RuntimeError(f"PNG 아님 ({len(b)}바이트): {b[:120]!r}")
        return self._normalize(b[i:])

    def capture(self):
        """화면을 numpy(BGR, 1080 폭)로. MuMu 고속 캡처가 되면 그것, 아니면 adb raw, 그것도 안 되면 PNG."""
        fc = getattr(self, "fast", None)
        if fc is not None and fc.ok and not getattr(self, "_fast_dirty", False):
            try:
                img = fc.grab()
                if img is not None and float(img.std()) >= 1.0:
                    return self._fit(img)
            except Exception:
                pass
            self._fast_dirty = True
        img = self._capture_adb()
        if os.environ.get("FASTCAP", "1") != "0" and self.is_emulator() and img is not None and float(img.std()) >= 1.0 \
                and (fc is None or getattr(self, "_fast_dirty", False)) and time.time() - getattr(self, "_fast_try", 0) > 20:
            self._fast_try = time.time()
            try:
                if fc is None:
                    import fastcap
                    inst = 0
                    try: inst = max(0, (int(str(self.serial).split(":")[1]) - 16384) // 32)
                    except Exception: pass
                    fc = self.fast = fastcap.FastCap(inst)
                fc.ok = False
                if fc.setup(img):
                    self._fast_dirty = False
            except Exception as e:
                try:
                    import fastcap; fastcap._log("설정 오류", repr(e))
                except Exception: pass
        return img

    def _capture_adb(self):
        import numpy as np, cv2
        if self.sf_display is None:
            try: self.find_display()
            except Exception: pass
        if getattr(self, "_raw_ok", True):
            try:
                b = self.run(*(["exec-out", "screencap"] + (["-d", self.sf_display] if self.sf_display else [])), binary=True)
                if len(b) > 16:
                    w = int.from_bytes(b[0:4], "little"); h = int.from_bytes(b[4:8], "little")
                    n = w * h * 4
                    if 100 < w < 5000 and 100 < h < 5000 and len(b) >= n + 12:
                        hdr = len(b) - n                     # 안드로이드 버전에 따라 머리말 12 또는 16바이트
                        img = np.frombuffer(b, np.uint8, n, hdr).reshape(h, w, 4)
                        fmt = int.from_bytes(b[8:12], "little")      # 1·2 = RGBA/RGBX, 5 = BGRA
                        img = cv2.cvtColor(img, cv2.COLOR_BGRA2BGR if fmt == 5 else cv2.COLOR_RGBA2BGR)
                        return self._fit(img)
                self.sf_display = None
                raise RuntimeError(f"raw 캡처 실패 ({len(b)}바이트): {b[:80]!r}")
            except (RuntimeError, subprocess.TimeoutExpired):
                raise                                        # adb 오류·시간 초과는 일시적 → raw를 끄지 않고 그대로 알림
            except Exception:
                self._raw_ok = False                         # 이 기기에서 raw가 안 되면 PNG로
        img = cv2.imdecode(np.frombuffer(self.screenshot(), np.uint8), cv2.IMREAD_COLOR)
        return img

    def _fit(self, img):
        import cv2
        h, w = img.shape[:2]
        if w == 1080:
            self.scale = 1.0; return img
        self.scale = w / 1080.0
        return cv2.resize(img, (1080, int(round(h / self.scale))), interpolation=cv2.INTER_LINEAR)

    def _normalize(self, png: bytes) -> bytes:
        """MuMu 창 크기에 따라 HOME 화면이 900x1600 등으로 바뀜 → 1080 폭으로 맞춰 돌려줌."""
        try:
            import cv2, numpy as np
            img = cv2.imdecode(np.frombuffer(png, np.uint8), cv2.IMREAD_COLOR)
            if img is None: return png
            h, w = img.shape[:2]
            if w == 1080 or w <= 0:
                self.scale = 1.0; return png
            self.scale = w / 1080.0
            img = cv2.resize(img, (1080, int(round(h / self.scale))), interpolation=cv2.INTER_CUBIC)
            ok, enc = cv2.imencode(".png", img)
            return enc.tobytes() if ok else png
        except Exception:
            return png

    def _s(self, v):
        return str(int(round(v * self.scale)))

    def _input(self, *args):
        d = ["-d", str(self.display)] if self.display is not None else []
        self.run("shell", "input", *d, *args)

    def tap(self, x, y):
        self._input("tap", self._s(x), self._s(y))

    def swipe(self, x1, y1, x2, y2, ms=300):
        self._input("swipe", self._s(x1), self._s(y1), self._s(x2), self._s(y2), str(int(ms)))

    def key(self, code):
        self._input("keyevent", str(code))

    def back(self):
        self.key(4)

    def text(self, s: str):
        # 한글은 input text로 안 들어감 → 숫자·영문만. 한글 입력은 클립보드 방식 필요
        # 원격 셸에서 그대로 들어가게 작은따옴표로 감쌈 (공백은 %s)
        q = "'" + s.replace(" ", "%s").replace("'", "'\\''") + "'"
        self._input("text", q)

    def ime_text(self, s: str) -> bool:
        """ADBKeyboard로 유니코드 입력 (base64 방송이라 한글·특수문자 그대로)."""
        import base64
        b = base64.b64encode(s.encode("utf-8")).decode()
        u = ["--user", str(self.user)] if self.user is not None else []
        out = self.run("shell", "am", "broadcast", *u, "-a", "ADB_INPUT_B64", "--es", "msg", b)
        return "result=0" in out or "Broadcast completed" in out

    def launch(self, package="jp.pokemon.pokemonhome", activity="com.unity3d.player.UnityPlayerActivity"):
        args = ["shell", "am", "start"] + (["--user", str(self.user)] if self.user is not None else []) + ["-n", f"{package}/{activity}"]
        self.run(*args)
        self.display = self.sf_display = None  # 새로 뜬 화면 번호는 다음 캡처 때 찾음
        self._call_after_launch()
        self._fast_dirty = True                # 고속 캡처도 화면 번호를 다시 확인

    def launch_from_icon(self, log=print):
        """MuMu 바탕화면(0번 화면)의 앱 아이콘을 눌러 켬. am start로 켜면 가끔 앱이 따로 세로 창을 못 받고 0번 화면(가로)에 떠서
        검게만 보이는데(10-25 11:32 사례), 아이콘으로 켜면 정상 세로 창이 뜸. 아이콘 위치는 launcher_icons.json (화면 비율)."""
        import json
        p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "launcher_icons.json")
        try: icons = json.load(open(p, encoding="utf-8"))
        except Exception: icons = {}
        rel = icons.get(str(self.serial)) or next((v for k, v in icons.items() if str(self.serial).endswith(":" + k.split(":")[-1])), None)
        if not rel:
            log("아이콘 위치 기록 없음 (launcher_icons.json):", self.serial); return False
        try:
            out = self.run("shell", "wm", "size")
            import re
            m = re.findall(r"(\d+)x(\d+)", out)
            w, h = (int(m[-1][0]), int(m[-1][1])) if m else (1920, 1080)
        except Exception:
            w, h = 1920, 1080
        x, y = int(rel[0] * max(w, h)), int(rel[1] * min(w, h))   # 바탕화면은 가로
        self.run("shell", "input", "-d", "0", "keyevent", "3"); time.sleep(1.5)
        self.run("shell", "input", "-d", "0", "tap", str(x), str(y))
        log(f"바탕화면 아이콘으로 HOME 켬 ({x},{y})")
        self.display = self.sf_display = None; self._fast_dirty = True
        return True

    def bring_front(self, package="jp.pokemon.pokemonhome", activity="com.unity3d.player.UnityPlayerActivity"):
        """이미 켜진 앱을 맨 앞으로 (MuMu는 보이지 않는 탭의 앱은 그리기를 멈춤 → 쓰기 전에 그 탭을 앞으로). 화면 번호는 그대로 둠."""
        args = ["shell", "am", "start"] + (["--user", str(self.user)] if self.user is not None else []) + ["-n", f"{package}/{activity}"]
        out = self.run(*args)
        if "brought to the front" not in out and "Starting" in out:
            self.display = self.sf_display = None; self._fast_dirty = True
            self._call_after_launch()                # 꺼져 있던 앱이 새로 켜짐
        return "brought to the front" in out or "Starting" in out

    def _call_after_launch(self):
        if self.after_launch:
            try:
                time.sleep(2.5)                      # MuMu가 새 탭을 띄울 시간
                self.after_launch()
            except Exception:
                pass

    def running_users(self, package="jp.pokemon.pokemonhome"):
        """HOME 앱이 켜져 있는 안드로이드 사용자 번호들 (ps의 u10_a123 → 10)."""
        import re
        try: out = self.run("shell", "ps", "-A", "-o", "USER,NAME")
        except Exception: return set()
        us = set()
        for l in out.splitlines():
            if package in l:
                m = re.match(r"\s*u(\d+)_", l)
                us.add(int(m.group(1)) if m else 0)
        return us

    def force_stop(self, package="jp.pokemon.pokemonhome"):
        self.run("shell", "am", "force-stop", *(["--user", str(self.user)] if self.user is not None else []), package)

    def foreground(self) -> str:
        out = self.run("shell", "dumpsys", "activity", "activities")
        for l in out.splitlines():
            if "mResumedActivity" in l or "topResumedActivity" in l:
                return l.strip()
        return ""

    def size(self):
        out = self.run("shell", "wm", "size")  # Physical size: 1080x2400
        w, h = out.split(":")[-1].strip().split("x")
        return int(w), int(h)

    def is_emulator(self) -> bool:
        s = (self.serial or "")
        return s.startswith(("127.0.0.1", "localhost", "emulator-"))

    def fit_display(self, log=print):
        """실제 휴대폰: 화면을 MuMu와 같은 1080x1920·밀도 280으로 맞춤 (인식 그림·터치 좌표가 그 기준).
        워커가 끝나면 원래대로 되돌림. 에뮬레이터는 그대로."""
        if self.is_emulator():
            return
        try:
            w, h = self.size()
            if (w, h) != (1080, 1920):
                self.run("shell", "wm", "size", "1080x1920")
                self.run("shell", "wm", "density", "280")
                log(f"휴대폰 {self.serial}: 화면을 1080x1920으로 맞춤 (원래 {w}x{h}, 워커를 끄면 되돌림)")
                import atexit
                atexit.register(self.reset_display)
        except Exception as e:
            log(f"휴대폰 {self.serial} 화면 맞추기 실패:", e)

    def reset_display(self):
        try:
            self.run("shell", "wm", "size", "reset"); self.run("shell", "wm", "density", "reset")
        except Exception:
            pass

    def stay_awake(self):
        self.run("shell", "svc", "power", "stayon", "usb")
