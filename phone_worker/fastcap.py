"""MuMu 전용 고속 화면 캡처 (external_renderer_ipc.dll). adb screencap(0.6초)보다 수십 배 빠름.
처음 쓸 때 여러 조합(설치 경로·디스플레이 번호·뒤집힘)을 시험해, adb로 찍은 화면과 같은 그림이 나오는 조합만 씀.
조사 결과는 shots/fastcap.log 에 남김. 안 되면 None을 돌려주고 adb 캡처를 그대로 씀."""
import ctypes, os, time, glob

LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "shots", "fastcap.log")
ROOTS = [r"C:\Program Files\Netease\MuMuPlayer\nx_device\15.0", r"C:\Program Files\Netease\MuMuPlayer",
         r"C:\Program Files\Netease\MuMuPlayer-12.0", r"C:\Program Files\Netease\MuMu Player 12"]


def _log(*a):
    try:
        os.makedirs(os.path.dirname(LOG), exist_ok=True)
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(time.strftime("%H:%M:%S ") + " ".join(str(x) for x in a) + "\n")
    except Exception:
        pass


def _find_dlls():
    out = []
    for r in ROOTS:
        for pat in (r"shell\sdk\external_renderer_ipc.dll", r"shell\external_renderer_ipc.dll", r"sdk\external_renderer_ipc.dll",
                    r"**\external_renderer_ipc.dll"):
            try:
                for p in glob.glob(os.path.join(r, pat), recursive="**" in pat):
                    if p not in out: out.append(p)
            except Exception:
                pass
        if out: break
    return out


class FastCap:
    def __init__(self, instance=0):
        self.instance = instance
        self.dll = None; self.h = 0; self.disp = None; self.flip = True; self.rot = None; self.ok = False
        self.buf = None; self.wh = (0, 0)

    def _load(self):
        dlls = _find_dlls()
        _log("DLL 후보:", dlls or "없음")
        for p in dlls:
            try:
                os.add_dll_directory(os.path.dirname(p))
            except Exception:
                pass
            try:
                d = ctypes.CDLL(p)
                d.nemu_connect.restype = ctypes.c_int; d.nemu_connect.argtypes = [ctypes.c_wchar_p, ctypes.c_int]
                d.nemu_disconnect.argtypes = [ctypes.c_int]
                d.nemu_capture_display.restype = ctypes.c_int
                d.nemu_capture_display.argtypes = [ctypes.c_int, ctypes.c_uint, ctypes.c_int, ctypes.POINTER(ctypes.c_int),
                                                   ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_ubyte)]
                try:
                    d.nemu_get_display_id.restype = ctypes.c_int
                    d.nemu_get_display_id.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int]
                except Exception:
                    pass
                self.dll = d
                # 설치 경로 후보: DLL 위로 몇 단계
                cands = []
                q = os.path.dirname(p)
                for _ in range(5):
                    cands.append(q); q = os.path.dirname(q)
                for root in cands + ROOTS:
                    h = d.nemu_connect(root, self.instance)
                    _log("connect", root, self.instance, "→", h)
                    if h:
                        self.h = h; return True
            except Exception as e:
                _log("DLL 로드 실패", p, repr(e))
        return False

    def _size(self, disp):
        w = ctypes.c_int(0); hh = ctypes.c_int(0)
        r = self.dll.nemu_capture_display(self.h, disp, 0, ctypes.byref(w), ctypes.byref(hh), None)
        return r, w.value, hh.value

    def grab_raw(self, disp):
        import numpy as np
        r, w, h = self._size(disp)
        if r != 0 or w <= 0 or h <= 0:
            return None
        if self.wh != (w, h):
            self.buf = (ctypes.c_ubyte * (w * h * 4))(); self.wh = (w, h)
        wi = ctypes.c_int(w); hi = ctypes.c_int(h)
        r = self.dll.nemu_capture_display(self.h, disp, w * h * 4, ctypes.byref(wi), ctypes.byref(hi), self.buf)
        if r != 0:
            return None
        return np.frombuffer(self.buf, np.uint8).reshape(h, w, 4)

    def setup(self, adb_img, package="jp.pokemon.pokemonhome"):
        """adb로 찍은 화면(BGR, 1080 폭)과 비교해 맞는 디스플레이 번호를 찾음."""
        import numpy as np, cv2
        if not self.dll and not self._load():
            _log("사용 불가: DLL 또는 연결 실패"); return False
        ids = []
        if hasattr(self.dll, "nemu_get_display_id"):
            for app in range(0, 4):
                try:
                    v = self.dll.nemu_get_display_id(self.h, package.encode(), app)
                    _log("get_display_id app", app, "→", v)
                    if v >= 0 and v not in ids: ids.append(v)
                except Exception as e:
                    _log("get_display_id 오류", repr(e)); break
        for v in range(0, 12):
            if v not in ids: ids.append(v)
        ref = cv2.resize(cv2.cvtColor(adb_img, cv2.COLOR_BGR2GRAY), (108, 192)).astype(np.float32)
        best = None
        for d in ids:
            t = time.time()
            raw = self.grab_raw(d)
            dt = time.time() - t
            if raw is None:
                continue
            rots = [None] if raw.shape[0] >= raw.shape[1] else [cv2.ROTATE_90_CLOCKWISE, cv2.ROTATE_90_COUNTERCLOCKWISE]   # 가로로 잡힌 화면(#N2 등)은 돌려 봄
            for rot in rots:
                for flip in (True, False):
                    img = self._conv(raw, flip, rot)
                    g = cv2.resize(cv2.cvtColor(img, cv2.COLOR_BGR2GRAY), (108, 192)).astype(np.float32)
                    c = float(np.corrcoef(ref.ravel(), g.ravel())[0, 1]) if g.std() > 1 else 0.0
                    _log(f"display {d} flip={flip} rot={rot} 크기={raw.shape[1]}x{raw.shape[0]} 상관={c:.3f} {dt * 1000:.0f}ms")
                    if best is None or c > best[0]:
                        best = (c, d, flip, rot)
        if best and best[0] >= 0.9:
            _, self.disp, self.flip, self.rot = best; self.ok = True
            _log(f"사용: display {self.disp} flip={self.flip} rot={self.rot} (상관 {best[0]:.3f})")
            return True
        _log("맞는 화면을 못 찾음 → adb 캡처 사용", best)
        return False

    @staticmethod
    def _conv(raw, flip, rot):
        import cv2
        img = cv2.cvtColor(raw, cv2.COLOR_RGBA2BGR)
        if flip: img = cv2.flip(img, 0)
        if rot is not None: img = cv2.rotate(img, rot)
        return img

    def grab(self):
        """BGR 이미지 (원래 크기). 실패하면 None."""
        import cv2
        if not self.ok:
            return None
        raw = self.grab_raw(self.disp)
        if raw is None:
            return None
        return self._conv(raw, self.flip, self.rot)
