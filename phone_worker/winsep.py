"""MuMu에서 두 HOME 앱(#N1·#N2)이 같은 창의 탭으로 있으면 보이지 않는 탭의 앱이 멈춤(흰 화면).
앱을 켤 때마다, 그 앱이 아직 자기 창으로 떨어져 있지 않고 다른 계정 앱도 켜져 있으면
MuMu 창의 지금 활성 탭(방금 켠 앱)을 마우스로 끌어 따로 창으로 뗌. 결과는 shots/winsep.log.
윈도우 표준 함수(ctypes)만 씀. 실패해도 조용히 넘어감 (봇은 번갈아 모드로 계속 동작)."""
import ctypes, os, time
from ctypes import wintypes

LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "shots", "winsep.log")
IS_WIN = os.name == "nt"


def _log(*a):
    try:
        os.makedirs(os.path.dirname(LOG), exist_ok=True)
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(time.strftime("%H:%M:%S ") + " ".join(str(x) for x in a) + "\n")
    except Exception:
        pass


if IS_WIN:
    u32 = ctypes.windll.user32; k32 = ctypes.windll.kernel32
    try: u32.SetProcessDPIAware()
    except Exception: pass


def _proc_name(hwnd):
    pid = wintypes.DWORD()
    u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    h = k32.OpenProcess(0x1000, False, pid.value)   # PROCESS_QUERY_LIMITED_INFORMATION
    if not h: return ""
    try:
        buf = ctypes.create_unicode_buffer(512); n = wintypes.DWORD(512)
        if k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)): return os.path.basename(buf.value).lower()
    finally:
        k32.CloseHandle(h)
    return ""


def mumu_windows():
    """[(hwnd, title, (l,t,r,b))] 보이는 MuMu 기기 창들."""
    out = []
    if not IS_WIN: return out
    PROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(hwnd, _):
        if not u32.IsWindowVisible(hwnd): return True
        n = u32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 1); u32.GetWindowTextW(hwnd, buf, n + 1)
        r = wintypes.RECT(); u32.GetWindowRect(hwnd, ctypes.byref(r))
        if r.right - r.left < 200 or r.bottom - r.top < 200: return True
        if _proc_name(hwnd) != "mumunxdevice.exe": return True
        out.append((hwnd, buf.value, (r.left, r.top, r.right, r.bottom)))
        return True
    u32.EnumWindows(PROC(cb), 0)
    return out


def _grab(rect):
    from PIL import ImageGrab
    import numpy as np
    return np.array(ImageGrab.grab(bbox=rect, all_screens=True))[:, :, :3]


def _mouse_drag(x1, y1, x2, y2):
    MOVE, DOWN, UP = 0x0001, 0x0002, 0x0004
    u32.SetCursorPos(int(x1), int(y1)); time.sleep(0.15)
    u32.mouse_event(DOWN, 0, 0, 0, 0); time.sleep(0.25)
    steps = 25
    for i in range(1, steps + 1):
        u32.SetCursorPos(int(x1 + (x2 - x1) * i / steps), int(y1 + (y2 - y1) * i / steps)); time.sleep(0.02)
    time.sleep(0.3)
    u32.mouse_event(UP, 0, 0, 0, 0)


def _front(hwnd):
    SW_RESTORE = 9
    try:
        if u32.IsIconic(hwnd): u32.ShowWindow(hwnd, SW_RESTORE)
        HWND_TOPMOST, HWND_NOTOPMOST, F = -1, -2, 0x0001 | 0x0002   # NOSIZE|NOMOVE
        u32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, F)
        u32.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, F)
        u32.SetForegroundWindow(hwnd)
    except Exception:
        pass


def separate(acct, other_running):
    """acct 앱이 따로 창이 아니고 다른 계정 앱도 켜져 있으면, 활성 탭(방금 켠 이 앱)을 끌어 뗌. 뗐으면 True."""
    if not IS_WIN: return False
    try:
        tag = f"#N{acct}"
        wins = mumu_windows()
        _log(f"계정 {acct} 확인: 다른 앱 {'켜짐' if other_running else '꺼짐'} · 창 {[(t, rc) for _, t, rc in wins]}")
        if any(tag in t for _, t, _ in wins):
            return False                                   # 이미 따로 창
        if not other_running:
            return False                                   # 다른 앱이 없으면 탭이어도 그려짐
        # 본 창: 따로 떨어진 앱 창(#N…)이 아닌 MuMu 창 중 가장 큰 것
        mains = [w for w in wins if "#N" not in w[1]] or wins
        if not mains:
            _log("MuMu 창을 못 찾음", wins); return False
        hwnd, title, (l, t, r, b) = max(mains, key=lambda w: (w[2][2] - w[2][0]) * (w[2][3] - w[2][1]))
        _front(hwnd); time.sleep(0.4)
        # 탭 줄: 창 위쪽 띠. 활성 탭은 흰색(밝기 ~255), 나머지는 회색 → 가장 오른쪽의 흰 구간(창 조절 버튼 영역 앞)을 활성 탭으로 봄
        strip_h = max(24, int((b - t) * 0.035))
        img = _grab((l, t, r, t + strip_h))
        import numpy as np
        try:
            from PIL import Image
            Image.fromarray(img).save(os.path.join(os.path.dirname(LOG), "winsep_strip.png"))
        except Exception: pass
        # 활성 탭 = 탭 줄에서 가장 밝은 부분 (탭 줄 위·아래 몇 줄 평균으로 글자 영향을 줄임)
        # 글자가 없는 탭 위쪽 띠(높이 28%)와 아래쪽 띠(85%)가 둘 다 흰색(>240)인 곳 = 활성 탭 (나머지 탭은 회색 ~220)
        g = img.astype(np.float32).mean(axis=2)
        ya, yb = int(strip_h * 0.28), min(strip_h - 1, int(strip_h * 0.85))
        bright = (g[ya] > 240) & (g[yb] > 240)
        w = r - l
        segs = []; s0 = None
        for x in range(int(w * 0.6)):                     # 오른쪽 40%는 창 버튼들
            if bright[x] and s0 is None: s0 = x
            if (not bright[x] or x == int(w * 0.6) - 1) and s0 is not None:
                if x - s0 >= 12: segs.append((s0, x))
                s0 = None
        _log(f"계정 {acct}: 본 창 '{title}' {l},{t},{r},{b} · 흰 구간 {segs}")
        if not segs:
            return False
        sx0, sx1 = segs[-1]
        gx = l + sx0 + max(6, (sx1 - sx0) // 3); gy = t + strip_h // 2   # 탭 왼쪽(아이콘·글자 쪽)을 잡음 (× 피함)
        # 내려놓을 곳: 본 창 오른쪽 바깥 (화면 안)
        sw = u32.GetSystemMetrics(78) or u32.GetSystemMetrics(0)
        dx = min(r + 250, sw - 200) if r + 250 < sw else max(200, l - 250)
        dy = t + 150
        _mouse_drag(gx, gy, dx, dy)
        time.sleep(1.5)
        ok = any(tag in tt for _, tt, _ in mumu_windows())
        _log(f"계정 {acct}: 탭 끌어 떼기 ({gx},{gy}) → ({dx},{dy}) · {'성공' if ok else '실패'}")
        return ok
    except Exception as e:
        _log("떼기 오류:", repr(e))
        return False


def _mouse_click(x, y):
    DOWN, UP = 0x0002, 0x0004
    u32.SetCursorPos(int(x), int(y)); time.sleep(0.1)
    u32.mouse_event(DOWN, 0, 0, 0, 0); time.sleep(0.05); u32.mouse_event(UP, 0, 0, 0, 0)


def ensure_visible(acct):
    """이 계정 앱이 따로 창이 아니고 본 창의 탭으로 있는데, 본 창에서 'Android Device' 탭(맨 앞)이 활성이면
    (= 이 앱 탭이 숨어 그리기가 멈춤) 두 번째 탭을 눌러 앞으로. 눌렀으면 True."""
    if not IS_WIN: return False
    try:
        import numpy as np
        tag = f"#N{acct}"
        wins = mumu_windows()
        if any(tag in t for _, t, _ in wins):
            return False
        mains = [w for w in wins if "#N" not in w[1]]
        if not mains: return False
        hwnd, title, (l, t, r, b) = max(mains, key=lambda w: (w[2][2] - w[2][0]) * (w[2][3] - w[2][1]))
        strip_h = max(24, int((b - t) * 0.035))
        img = _grab((l, t, r, t + strip_h))
        g = img.astype(np.float32).mean(axis=2)
        ya, yb = int(strip_h * 0.28), min(strip_h - 1, int(strip_h * 0.85))
        bright = (g[ya] > 240) & (g[yb] > 240)
        w = r - l; lim = int(w * 0.6)
        segs = []; s0 = None
        for x in range(lim):
            if bright[x] and s0 is None: s0 = x
            if (not bright[x] or x == lim - 1) and s0 is not None:
                if x - s0 >= 12: segs.append((s0, x))
                s0 = None
        if not segs: return False
        a0, a1 = segs[0]
        if a0 > 40:                                   # 활성 탭이 맨 앞(Android Device)이 아님 → 앱 탭이 보이는 중
            return False
        # 맨 앞 탭이 활성: 그 오른쪽 탭(이 앱)을 누름
        _front(hwnd); time.sleep(0.3)
        cx = l + a1 + 40; cy = t + strip_h // 2
        _mouse_click(cx, cy); time.sleep(0.8)
        _log(f"계정 {acct}: 본 창에서 'Android Device' 탭이 앞이라 앱 탭을 누름 ({cx},{cy})")
        return True
    except Exception as e:
        _log("탭 앞으로 오류:", repr(e))
        return False
