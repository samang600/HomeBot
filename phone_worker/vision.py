"""화면 인식: OpenCV 템플릿 매칭 + OCR (선택).

템플릿 파일: templates/<set>/<name>.png (set = phone | switch).
같은 이름의 <name>.json 이 있으면 {"region":[x,y,w,h], "threshold":0.9} 로 찾는 범위·기준을 좁힐 수 있음.
"""
import json, os
import numpy as np
import cv2

TPL_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "templates")
_cache: dict[str, tuple[np.ndarray, dict]] = {}


def decode(img_bytes: bytes) -> np.ndarray:
    arr = np.frombuffer(img_bytes, np.uint8)
    img = cv2.imdecode(arr, cv2.IMREAD_COLOR)
    if img is None:
        raise ValueError("이미지를 읽지 못함")
    return img


def imread(p, flag=cv2.IMREAD_COLOR):
    """한글 경로(윈도우 사용자 이름 등)에서도 읽음 — cv2.imread는 윈도우에서 비ASCII 경로를 못 읽음"""
    try: return cv2.imdecode(np.fromfile(p, np.uint8), flag)
    except (OSError, ValueError): return None


def imwrite(p, img):
    ok, buf = cv2.imencode(os.path.splitext(p)[1] or ".png", img)
    if ok: buf.tofile(p)
    return ok


def _load(name: str):
    if name in _cache:
        tpl, meta, p, mt = _cache[name]
        try:
            if os.path.getmtime(p) == mt: return tpl, meta
        except OSError: pass
    for s in ("phone", "switch", ""):
        p = os.path.join(TPL_DIR, s, name + ".png") if s else os.path.join(TPL_DIR, name + ".png")
        if os.path.exists(p):
            tpl = imread(p, cv2.IMREAD_COLOR)
            meta = {}
            mp = p[:-4] + ".json"
            if os.path.exists(mp):
                with open(mp, encoding="utf-8") as f:
                    meta = json.load(f)
            _cache[name] = (tpl, meta, p, os.path.getmtime(p))   # 파일이 바뀌면 (게임 로고 등록) 다시 읽음
            return tpl, meta
    raise FileNotFoundError(f"템플릿 없음: {name} (templates/phone 또는 templates/switch 에 {name}.png)")


def match(img: np.ndarray, name: str, threshold: float | None = None):
    """반환 (found, score, cx, cy). cx,cy = 템플릿 중심의 화면 좌표.
    특수 이름 '@busy:x,y,w,h' = 그 네모 칸에 무언가 있는지(밝기 표준편차가 threshold×100 이상인지) — 빈 박스 칸은 매끈해서 5 안팎."""
    if name.startswith("@busy:"):
        x, y, w, h = (int(v) for v in name[6:].split(","))
        g = cv2.cvtColor(img[y:y + h, x:x + w], cv2.COLOR_BGR2GRAY)
        sd = float(g.std()) if g.size else 0.0
        lim = (threshold if threshold is not None else 0.15) * 100
        return bool(sd >= lim), sd, x + w // 2, y + h // 2
    if name.startswith("@check:"):
        # 놓아주기 여러 마리 고르기 화면의 주황 체크 표시 (칸 오른쪽 위). x,y = 체크 표시 중심. 주황 비율이 threshold 이상이면 체크됨
        x, y = (int(v) for v in name[7:].split(","))
        p = cv2.cvtColor(img[max(0, y - 9):y + 9, max(0, x - 9):x + 9], cv2.COLOR_BGR2HSV)
        if p.size == 0:
            return False, 0.0, x, y
        org = float(((p[..., 0] >= 10) & (p[..., 0] <= 25) & (p[..., 1] > 150) & (p[..., 2] > 200)).mean())
        return bool(org >= (threshold if threshold is not None else 0.3)), org, x, y
    if name.startswith("@text:"):
        # 글자 확인 '@text:x,y,w,h:찾을 글' — 그 네모를 읽어(한·영·일) 찾을 글이 들어 있으면 찾음 (예: HOME 게임 선택 화면의 세이브 주인 이름).
        # 글자색이 배경보다 밝을 수도 있어 그대로·반전 두 번 읽음. threshold = 비슷한 정도 (기본 0.75)
        import difflib, re
        box, needle = name[6:].split(":", 1)
        x, y, w, h = (int(v) for v in box.split(","))
        want = re.sub(r"[\W_]+", "", needle.lower())
        lim = threshold if threshold is not None else 0.75
        best = 0.0
        if want and name_ocr_ready():
            import tesserocr
            from PIL import Image
            g = cv2.resize(cv2.cvtColor(img[y:y + h, x:x + w], cv2.COLOR_BGR2GRAY), None, fx=1.5, fy=1.5, interpolation=cv2.INTER_CUBIC)
            langs = "+".join(l for l in ("kor", "jpn", "eng") if os.path.isfile(os.path.join(TESSDATA, l + ".traineddata"))) or "eng"
            for im2 in (g, 255 - g):
                got = re.sub(r"[\W_]+", "", tesserocr.image_to_text(Image.fromarray(im2), lang=langs, path=TESSDATA, psm=tesserocr.PSM.SINGLE_LINE).lower())
                if want in got: best = 1.0; break
                L = len(want)
                for i in range(0, max(1, len(got) - L + 1)):
                    best = max(best, difflib.SequenceMatcher(None, want, got[i:i + L]).ratio())
                if best >= lim: break
        return bool(best >= lim), best, x + w // 2, y + h // 2
    tpl, meta = _load(name)
    # 템플릿 json의 threshold는 최소값: 부르는 쪽이 더 낮게 줘도 그 아래로는 안 내려감 (비슷한 그림을 잘못 고르지 않게)
    thr = max(threshold, meta["threshold"]) if threshold is not None and "threshold" in meta else (threshold if threshold is not None else meta.get("threshold", 0.85))
    ox = oy = 0
    region = meta.get("region")
    if region:
        x, y, w, h = region
        img = img[y:y + h, x:x + w]
        ox, oy = x, y
    th, tw = tpl.shape[:2]
    if img.shape[0] < th or img.shape[1] < tw:
        return False, 0.0, 0, 0
    res = cv2.matchTemplate(img, tpl, cv2.TM_CCOEFF_NORMED)
    _, mx, _, loc = cv2.minMaxLoc(res)
    return bool(mx >= thr), float(mx), ox + loc[0] + tw // 2, oy + loc[1] + th // 2


def match_all(img: np.ndarray, name: str, threshold: float | None = None, gap: int = 40):
    """같은 템플릿이 여러 번 보이는 위치들 (중심 좌표 목록, 위→아래)."""
    if name.startswith("@text:"):
        # 글자 확인 '@text:x,y,w,h:찾을 글' — 그 네모를 읽어(한·영·일) 찾을 글이 들어 있으면 찾음 (예: HOME 게임 선택 화면의 세이브 주인 이름).
        # 글자색이 배경보다 밝을 수도 있어 그대로·반전 두 번 읽음. threshold = 비슷한 정도 (기본 0.75)
        import difflib, re
        box, needle = name[6:].split(":", 1)
        x, y, w, h = (int(v) for v in box.split(","))
        want = re.sub(r"[\W_]+", "", needle.lower())
        lim = threshold if threshold is not None else 0.75
        best = 0.0
        if want and name_ocr_ready():
            import tesserocr
            from PIL import Image
            g = cv2.resize(cv2.cvtColor(img[y:y + h, x:x + w], cv2.COLOR_BGR2GRAY), None, fx=1.5, fy=1.5, interpolation=cv2.INTER_CUBIC)
            langs = "+".join(l for l in ("kor", "jpn", "eng") if os.path.isfile(os.path.join(TESSDATA, l + ".traineddata"))) or "eng"
            for im2 in (g, 255 - g):
                got = re.sub(r"[\W_]+", "", tesserocr.image_to_text(Image.fromarray(im2), lang=langs, path=TESSDATA, psm=tesserocr.PSM.SINGLE_LINE).lower())
                if want in got: best = 1.0; break
                L = len(want)
                for i in range(0, max(1, len(got) - L + 1)):
                    best = max(best, difflib.SequenceMatcher(None, want, got[i:i + L]).ratio())
                if best >= lim: break
        return bool(best >= lim), best, x + w // 2, y + h // 2
    tpl, meta = _load(name)
    # 템플릿 json의 threshold는 최소값: 부르는 쪽이 더 낮게 줘도 그 아래로는 안 내려감 (비슷한 그림을 잘못 고르지 않게)
    thr = max(threshold, meta["threshold"]) if threshold is not None and "threshold" in meta else (threshold if threshold is not None else meta.get("threshold", 0.85))
    res = cv2.matchTemplate(img, tpl, cv2.TM_CCOEFF_NORMED)
    th, tw = tpl.shape[:2]
    ys, xs = np.where(res >= thr)
    pts = []
    for y, x in sorted(zip(ys, xs)):
        if all(abs(y - py) > gap or abs(x - px) > gap for px, py in pts):
            pts.append((int(x), int(y)))
    return [(x + tw // 2, y + th // 2) for x, y in pts]


def orange_buttons(img: np.ndarray, min_w: int = 200):
    """HOME의 주황색 버튼(OK·검색 등) 위치들 (중심 좌표, 위→아래). 대화창의 확인 버튼을 찾을 때 씀."""
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
    m = cv2.inRange(hsv, (12, 110, 170), (30, 255, 255))
    n, _, stats, cent = cv2.connectedComponentsWithStats(m)
    out = []
    for i in range(1, n):
        x, y, w, h, a = stats[i]
        if w >= min_w and 50 <= h <= 170 and a > w * h * 0.6:
            out.append((int(cent[i][0]), int(cent[i][1])))
    return sorted(out, key=lambda p: p[1])


def find_any(img: np.ndarray, names: list[str]):
    for n in names:
        f, s, x, y = match(img, n)
        if f:
            return n, s, x, y
    return None, 0.0, 0, 0


# ---- OCR: 설치된 것 중 하나를 씀 (없으면 빈 문자열)
_ocr = None


def _get_ocr():
    global _ocr
    if _ocr is not None:
        return _ocr
    try:
        from paddleocr import PaddleOCR  # pip install paddleocr paddlepaddle
        inst = PaddleOCR(use_angle_cls=False, lang="korean", show_log=False)
        _ocr = ("paddle", inst)
    except Exception:
        try:
            import pytesseract  # pip install pytesseract + tesseract-ocr-kor
            _ocr = ("tess", pytesseract)
        except Exception:
            _ocr = ("none", None)
    return _ocr


def ocr(img: np.ndarray, region=None) -> str:
    if region:
        x, y, w, h = region
        img = img[y:y + h, x:x + w]
    kind, inst = _get_ocr()
    if kind == "paddle":
        r = inst.ocr(img, cls=False)
        lines = []
        for page in r or []:
            for item in page or []:
                lines.append(item[1][0])
        return " ".join(lines)
    if kind == "tess":
        g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
        g = cv2.resize(g, None, fx=2, fy=2, interpolation=cv2.INTER_CUBIC)
        return inst.image_to_string(g, lang="kor+eng", config="--psm 7").strip()
    try:
        # paddle·pytesseract가 없으면 이름 읽기용 tesserocr(영어 데이터)로: 숫자·영문 위주 (스위치 HOME 아래 정보 칸의 레벨·능력치)
        import tesserocr
        from PIL import Image
        g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
        g = cv2.resize(g, None, fx=2, fy=2, interpolation=cv2.INTER_CUBIC)
        return " ".join(tesserocr.image_to_text(Image.fromarray(g), lang="eng", path=TESSDATA, psm=tesserocr.PSM.SPARSE_TEXT).split())
    except Exception:
        return ""


# ---- 포켓몬 이름 읽기 (Tesseract · tesserocr 패키지, 언어 데이터는 phone_worker/tessdata)
TESSDATA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tessdata")
if os.name == "nt" and not TESSDATA.isascii():   # tesseract는 윈도우에서 한글 경로를 못 읽음 → 영문 경로로 복사해 씀
    import shutil
    _t = os.path.join(os.environ.get("PROGRAMDATA") or "C:\\ProgramData", "HomeBot", "tessdata")
    try:
        os.makedirs(_t, exist_ok=True)
        for _f in os.listdir(TESSDATA):
            _s, _d = os.path.join(TESSDATA, _f), os.path.join(_t, _f)
            if not os.path.exists(_d) or os.path.getsize(_d) != os.path.getsize(_s):
                shutil.copyfile(_s, _d + ".tmp"); os.replace(_d + ".tmp", _d)
        TESSDATA = _t
    except OSError:
        pass
# 포켓몬 언어 → Tesseract 언어. 이름 하나만 읽으므로 그 언어 데이터 + 영어
TESS_LANG = {"KOR": "kor", "JPN": "jpn", "CHS": "chi_sim", "CHT": "chi_tra",
             "ENG": "eng", "FRE": "fra", "FRA": "fra", "GER": "deu", "DEU": "deu", "ITA": "ita",
             "SPA": "spa", "ES-ES": "spa", "ES-LA": "spa"}


_ocr_ready = None


def name_ocr_ready() -> bool:
    """처음 한 번만 확인하고 기억 (tesserocr를 처음 불러올 때 몇 초 걸려 /ping이 늦어지지 않게)."""
    global _ocr_ready
    if _ocr_ready is None:
        try:
            import tesserocr  # noqa: F401
            _ocr_ready = os.path.isfile(os.path.join(TESSDATA, "eng.traineddata"))
        except Exception:
            _ocr_ready = False
    return _ocr_ready


def ocr_title(img: np.ndarray, region) -> str:
    """제목 한 줄 읽기 (한·일·영 함께, 밝은 글자·어두운 글자 둘 다): HOME 게임 선택 화면 왼쪽 칸의 게임 이름. 두 읽기를 ' | '로 이어 줌."""
    import tesserocr
    from PIL import Image
    x, y, w, h = region
    g = cv2.cvtColor(img[y:y + h, x:x + w], cv2.COLOR_BGR2GRAY)
    g = cv2.resize(g, None, fx=1.5, fy=1.5, interpolation=cv2.INTER_CUBIC)
    langs = "+".join(l for l in ("kor", "jpn", "eng") if os.path.isfile(os.path.join(TESSDATA, l + ".traineddata"))) or "eng"
    outs = []
    for im2 in (g, 255 - g):
        try:
            outs.append(" ".join(tesserocr.image_to_text(Image.fromarray(im2), lang=langs, path=TESSDATA, psm=tesserocr.PSM.SINGLE_LINE).split()))
        except Exception:
            outs.append("")
    return " | ".join(outs)


def template_exists(name: str) -> bool:
    name = os.path.basename(name or "")
    return bool(name) and any(os.path.isfile(os.path.join(TPL_DIR, d, name + ".png")) for d in ("switch", "phone"))


def save_logo(img: np.ndarray, name: str, region) -> str:
    """region 안에서 배경(테두리 색)과 다른 부분을 로고로 잘라 templates/switch/<name>.png + .json(region·threshold) 저장. 예전 그림은 .old.png"""
    import json, shutil
    name = os.path.basename(name or "")
    if not name:
        raise ValueError("템플릿 이름이 없음")
    if img.shape[:2] != (720, 1280):
        img = cv2.resize(img, (1280, 720))
    x, y, w, h = region
    reg = img[y:y + h, x:x + w]
    border = np.concatenate([reg[0], reg[-1], reg[:, 0], reg[:, -1]])
    bg = np.median(border, axis=0)
    ys, xs = np.where(np.abs(reg.astype(int) - bg).sum(axis=2) > 60)
    if len(xs) < 300:
        raise RuntimeError("로고를 찾지 못함 (게임 선택 화면에서 커서가 게임에 있는지 확인)")
    x0, x1 = max(0, xs.min() - 4), min(w, xs.max() + 5)
    y0, y1 = max(0, ys.min() - 4), min(h, ys.max() + 5)
    if x1 - x0 < 60 or y1 - y0 < 30:
        raise RuntimeError("잘린 로고가 너무 작음")
    d = os.path.join(TPL_DIR, "switch"); os.makedirs(d, exist_ok=True)
    out = os.path.join(d, name + ".png")
    if os.path.exists(out):
        shutil.copyfile(out, out[:-4] + ".old.png")
    with open(out[:-4] + ".json", "w", encoding="utf-8") as fp:
        json.dump({"region": [x, y, w, h], "threshold": 0.85}, fp)
    ok, buf = cv2.imencode(".png", reg[y0:y1, x0:x1])
    buf.tofile(out)
    _cache.pop(name, None)
    return out


def ocr_name(img: np.ndarray, region, lang_code: str) -> str:
    """'What do you want to do with / <이름>?' 창에서 포켓몬 이름만 읽음. 이름이 짧으면 한 줄로 붙어 나오므로
    창 글 전체를 블록으로 읽은 뒤 'with' 뒤를 이름으로 씀. 그 포켓몬의 언어 + 영어 데이터로 읽음."""
    import re
    import tesserocr
    from PIL import Image
    x, y, w, h = region
    g = cv2.cvtColor(img[y:y + h, x:x + w], cv2.COLOR_BGR2GRAY)
    g = cv2.resize(g, None, fx=2, fy=2, interpolation=cv2.INTER_CUBIC)
    lang = TESS_LANG.get((lang_code or "").upper(), "eng")
    if not os.path.isfile(os.path.join(TESSDATA, lang + ".traineddata")):
        lang = "eng"
    langs = lang if lang == "eng" else lang + "+eng"
    text = tesserocr.image_to_text(Image.fromarray(g), lang=langs, path=TESSDATA, psm=tesserocr.PSM.SINGLE_BLOCK)
    text = " ".join(text.split())
    m = re.search(r"(?i)\bwith\b(.*)$", text)
    name = (m.group(1) if m else text).strip()
    return name.rstrip("?？ ").strip()


def ocr_text(img: np.ndarray, region, lang_code: str = "ENG", psm: str = "SPARSE_TEXT", scale: float = 1.5) -> str:
    """영역 글 전체를 읽음 (요약 화면용). lang_code는 TESS_LANG 키, 영어는 항상 같이."""
    import tesserocr
    from PIL import Image
    x, y, w, h = region
    g = cv2.cvtColor(img[y:y + h, x:x + w], cv2.COLOR_BGR2GRAY)
    if scale != 1:
        g = cv2.resize(g, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC)
    lang = TESS_LANG.get((lang_code or "").upper(), "eng")
    if not os.path.isfile(os.path.join(TESSDATA, lang + ".traineddata")):
        lang = "eng"
    langs = lang if lang == "eng" else lang + "+eng"
    return tesserocr.image_to_text(Image.fromarray(g), lang=langs, path=TESSDATA, psm=getattr(tesserocr.PSM, psm))


def gender_mark(img: np.ndarray, region=(100, 10, 460, 105)) -> int:
    """요약 화면 맨 위 이름 옆 성별 표시 색으로 성별: 0=수컷(파랑) 1=암컷(빨강) -1=모름(없음)."""
    x, y, w, h = region
    r = img[y:y + h, x:x + w].astype(int)
    b, g, rr = r[..., 0], r[..., 1], r[..., 2]
    blue = int(((b > 170) & (rr < 110) & (b - rr > 80)).sum())
    red = int(((rr > 180) & (b < 150) & (g < 130) & (rr - g > 70)).sum())
    if blue > 250 and red < 80: return 0
    if red > 250 and blue < 80: return 1
    return -1
