"""HOME 앱 조작 흐름. 각 흐름은 '화면 확인 → 터치' 단계의 나열.

여기 적힌 템플릿 이름(home_*)은 아직 그림이 없는 자리표시자다. record.py로 한 번 손으로 진행하며 찍은 스크린샷에서
해당 화면의 고유한 부분을 잘라 templates/phone/<이름>.png 로 저장하면 동작한다. 좌표가 필요한 터치는 템플릿 중심을 쓴다
(tap_tpl) — 해상도가 바뀌어도 템플릿만 다시 뜨면 됨.

박스·칸 번호는 0부터. HOME 박스 한 칸의 화면 좌표는 grid() 로 계산: templates/phone/box_grid.json 에
{"x0":..,"y0":..,"dx":..,"dy":..} (첫 칸 중심과 칸 간격) 을 기록해 둔다.
"""
import json, os, time
from adb import Adb
import cv2
import vision as V

SKIP_TRADE_ANIM = os.environ.get("SKIP_TRADE_ANIM", "1") != "0"   # 교환 연출 건너뛰기 (0이면 끝까지 기다림)
SHOT_DIR = os.environ.get("SHOT_DIR", os.path.join(os.path.dirname(os.path.abspath(__file__)), "shots"))
GRID_FILE = os.path.join(V.TPL_DIR, "phone", "box_grid.json")


class FlowError(Exception):
    def __init__(self, msg, shot=""):
        super().__init__(msg)
        self.shot = shot


class Home:
    def __init__(self, adb: Adb, log=print):
        self.adb = adb
        self.log = log
        self.vars = {}

    # ---- 기본 동작
    def shot(self):
        t = time.time()
        img = self.adb.capture()
        self._shot_n = getattr(self, "_shot_n", 0) + 1; self._shot_t = getattr(self, "_shot_t", 0.0) + time.time() - t
        if img is not None and img.size and float(img.std()) < 1.0:
            # 완전히 검은 화면: MuMu는 앱마다 화면(디스플레이)이 따로라, 앱을 다시 켜면 번호가 바뀌어 옛 화면을 찍고 있을 수 있음
            # → 화면 번호를 다시 찾고 한 번 더 찍음 (안 그러면 계속 검은 화면만 보고 'GTS로 못 감'이 됨)
            self.adb.sf_display = None; self.adb.display = None
            try: self.adb.find_display()
            except Exception: pass
            img = self.adb.capture()
        return img

    def save_shot(self, img, tag):
        os.makedirs(SHOT_DIR, exist_ok=True)
        import cv2
        p = os.path.join(SHOT_DIR, f"{time.strftime('%Y%m%d_%H%M%S')}_{tag}.png")
        V.imwrite(p, img)
        return p

    def dismiss_error(self, img):
        """'Error / A communication error has occurred' 같은 오류 창이면 주황 OK를 눌러 닫음. 닫았으면 True."""
        if img is None:
            return False
        if V.match(img, "ph_error")[0]:
            btns = V.orange_buttons(img)
            x, y = btns[-1] if btns else (540, 1330)
        else:
            # 'Communication Error' 등 제목이 다른 오류 창: 가운데 주황 버튼 하나 + 위쪽 글에 'error'가 있으면 오류 창
            btns = [b for b in V.orange_buttons(img) if abs(b[0] - 540) < 90]
            if not btns:
                return False
            x, y = btns[-1]
            try:
                txt = V.ocr_text(img, (60, max(0, y - 560), 960, 480), "ENG").lower()
            except Exception:
                return False
            if "error" not in txt and "communication" not in txt:
                return False
        self.log(f"오류 창 → OK ({x},{y})"); self.save_shot(img, "error_dialog")
        self.tap(x, y, 1.5)
        return True

    def expect(self, name, timeout=10.0, interval=0.12):
        """템플릿이 보일 때까지 기다림. 못 보면 FlowError (스크린샷 저장). 중간에 통신 오류 창이 뜨면 닫고 FlowError(통신 오류)."""
        t0 = time.time()
        img = None
        while time.time() - t0 < timeout:
            img = self.shot()
            f, s, x, y = V.match(img, name)
            if f:
                return x, y
            if name != "ph_error" and self.dismiss_error(img):
                raise FlowError("통신 오류 창이 떠서 OK를 누름 (처음부터 다시)", "")
            time.sleep(interval)
        p = self.save_shot(img, "fail_" + name) if img is not None else ""
        raise FlowError(f"화면이 '{name}'이 아님", p)

    def wait_tap(self, name, timeout=10.0, settle=0.2, stable=True):
        """버튼이 보이면 바로 누름. stable=True면 연속 두 장에서 같은 자리일 때 (미끄러져 들어오는 중에 누르지 않게)."""
        t0 = time.time(); last = None; img = None
        while time.time() - t0 < timeout:
            img = self.shot()
            f, s, x, y = V.match(img, name)
            if f:
                if not stable or (last and abs(last[0] - x) <= 6 and abs(last[1] - y) <= 6 and time.time() - last[2] >= 0.25):
                    self.adb.tap(x, y); time.sleep(settle); return x, y
                if not (last and abs(last[0] - x) <= 6 and abs(last[1] - y) <= 6):
                    last = (x, y, time.time())
            else:
                last = None
        p = self.save_shot(img, "fail_" + name) if img is not None else ""
        raise FlowError(f"화면이 '{name}'이 아님", p)

    def wait_gone(self, name, timeout=2.0):
        """템플릿이 사라질 때까지 (창이 닫혔는지). 사라지면 True."""
        t0 = time.time()
        while time.time() - t0 < timeout:
            if not V.match(self.shot(), name)[0]:
                return True
        return False

    def tap_tpl(self, name, timeout=10.0, settle=0.8):
        x, y = self.expect(name, timeout)
        self.adb.tap(x, y)
        time.sleep(settle)
        return x, y

    def tap(self, x, y, settle=0.8):
        self.adb.tap(x, y)
        time.sleep(settle)

    def seen(self, name):
        f, *_ = V.match(self.shot(), name)
        return f

    # ---- HOME 박스 칸 좌표
    def grid(self, slot):
        with open(GRID_FILE, encoding="utf-8") as f:
            g = json.load(f)
        cols = g.get("cols", 6)
        return g["x0"] + (slot % cols) * g["dx"], g["y0"] + (slot // cols) * g["dy"]

    def goto_box(self, box):
        """현재 박스 번호를 OCR로 읽어 목표 박스까지 좌우 화살표. 깡계는 박스 1개라 할 일 없음."""
        if box <= 0:
            return
        for _ in range(250):
            img = self.shot()
            cur = V.ocr(img, self._box_no_region()) if self._box_no_region() else ""
            n = "".join(ch for ch in cur if ch.isdigit())
            if n and int(n) - 1 == box:
                return
            self.tap_tpl("home_box_next", 3)
        raise FlowError(f"박스 {box + 1}로 이동 실패")

    def _box_no_region(self):
        p = os.path.join(V.TPL_DIR, "phone", "box_no_region.json")
        if os.path.exists(p):
            with open(p, encoding="utf-8") as f:
                return json.load(f)["region"]
        return None

    # ---- 공통: 앱 열고 메인까지
    def open_main(self):
        self.adb.launch()
        time.sleep(3)
        for _ in range(6):   # 공지·업데이트 팝업 닫기
            if self.seen("home_main"):
                return
            for n in ("home_popup_close", "home_popup_ok", "home_title_tap"):
                if self.seen(n):
                    self.tap_tpl(n, 2)
                    break
            else:
                time.sleep(2)
        self.expect("home_main", 30)

    def open_boxes(self):
        self.open_main()
        self.tap_tpl("home_menu_pokemon")
        self.expect("home_boxes", 30)

    # ---- GTS
    def gts_deposit(self, box, slot, species_name, gender, level_min, level_max):
        """박스 칸의 포켓몬을 GTS에 맡기고 원하는 포켓몬 조건을 설정."""
        self.open_main()
        self.tap_tpl("home_menu_trade")
        self.tap_tpl("home_trade_gts")
        self.expect("home_gts_main", 20)
        self.tap_tpl("home_gts_deposit")
        self.expect("home_boxes", 20)
        self.goto_box(box)
        x, y = self.grid(slot)
        self.tap(x, y)
        self.tap_tpl("home_gts_select_this")        # '이 포켓몬을 맡긴다'
        # 원하는 포켓몬: 종 이름 검색 (한글 입력은 adb input text가 안 되므로 자판 템플릿 또는 클립보드 방식 필요)
        self.tap_tpl("home_gts_want_species")
        self._type_korean(species_name)
        self.tap_tpl("home_gts_search_result_first")
        if gender in (0, 1):
            self.tap_tpl("home_gts_want_gender")
            self.tap_tpl("home_gts_gender_male" if gender == 0 else "home_gts_gender_female")
        self.tap_tpl("home_gts_want_level")
        self.tap_tpl(f"home_gts_level_{level_min}_{level_max}")   # 레벨 구간 버튼 템플릿 (예: home_gts_level_1_10)
        self.tap_tpl("home_gts_deposit_confirm")
        self.expect("home_gts_deposited", 30)
        return {"deposited": "1"}

    def gts_collect(self):
        """맡긴 포켓몬이 교환됐는지 확인. 됐으면 받은 포켓몬을 받아 박스로. 반환 vars: traded=1/0."""
        self.open_main()
        self.tap_tpl("home_menu_trade")
        self.tap_tpl("home_trade_gts")
        self.expect("home_gts_main", 20)
        if self.seen("home_gts_traded_badge"):
            self.tap_tpl("home_gts_traded_badge")
            self.tap_tpl("home_gts_receive")
            self.expect("home_gts_received", 30)
            self.tap_tpl("home_popup_ok", 5)
            return {"traded": "1"}
        return {"traded": "0"}

    def gts_withdraw(self):
        self.open_main()
        self.tap_tpl("home_menu_trade")
        self.tap_tpl("home_trade_gts")
        self.expect("home_gts_main", 20)
        self.tap_tpl("home_gts_my_deposit")
        self.tap_tpl("home_gts_withdraw")
        self.tap_tpl("home_popup_ok", 5)
        return {}

    # ---- 친구 교환 (프리미엄)
    def friend_trade(self, box, slot, friend_name):
        self.open_main()
        self.tap_tpl("home_menu_trade")
        self.tap_tpl("home_trade_friend")
        self.expect("home_friend_list", 20)
        # 친구 목록에서 이름 찾기: 목록을 넘기며 OCR
        if not self._find_in_list(friend_name):
            raise FlowError(f"친구 목록에서 '{friend_name}'을 찾지 못함")
        self.tap_tpl("home_friend_trade_request")
        self.expect("home_friend_partner_ready", 180)   # 상대 수락 대기
        self.expect("home_boxes", 30)
        self.goto_box(box)
        x, y = self.grid(slot)
        self.tap(x, y)
        self.tap_tpl("home_trade_offer")
        self.expect("home_trade_both_ready", 180)
        self.tap_tpl("home_trade_confirm")
        self.expect("home_trade_done", 90)
        self.tap_tpl("home_popup_ok", 5)
        return {}

    def _find_in_list(self, name):
        for _ in range(30):
            img = self.shot()
            txt = V.ocr(img)
            if name and name in txt:
                # 이름 위치는 OCR 결과 좌표가 필요 → 단순화: 템플릿 'home_friend_row'의 첫 행 탭 후 이름 재확인
                return True
            self.adb.swipe(540, 1600, 540, 900, 400)
            time.sleep(0.8)
        return False

    # ---- 놓아주기
    def release(self, slots):
        self.open_boxes()
        done = 0
        for box, slot in slots:
            self.goto_box(box)
            x, y = self.grid(slot)
            self.tap(x, y)
            if not self.seen("home_slot_menu"):      # 빈 칸이면 메뉴가 안 뜸
                self.adb.back(); time.sleep(0.5)
                continue
            self.tap_tpl("home_slot_release")
            self.tap_tpl("home_release_confirm")
            self.tap_tpl("home_release_confirm2", 5)   # '정말로?' 두 번째 확인이 있으면
            self.tap_tpl("home_popup_ok", 5)
            done += 1
        return {"released": str(done)}


    # ================= 역 GTS: 사용자가 올린 '열쇠 포켓몬'을 찾아 봇 포켓몬(HOME 1-1)과 교환 =================
    # 좌표는 MuMu 1080x1920 기준 (기록 20261024_011719)
    ROW0 = 470; ROW_DY = 120            # 고르기 창: '상관없다' 줄과 줄 간격
    LANG_ROW = {"ENG": 1, "ES-ES": 2, "ES-LA": 3, "FRA": 4, "DEU": 5, "ITA": 6, "JPN": 7, "KOR": 8, "CHS": 9, "CHT": 10}
    LANG_ALIAS = {"en": "ENG", "es": "ES-ES", "es-es": "ES-ES", "es-la": "ES-LA", "es419": "ES-LA", "fr": "FRA", "de": "DEU",
                  "it": "ITA", "ja": "JPN", "jp": "JPN", "ko": "KOR", "kr": "KOR", "영어": "ENG", "스페인어": "ES-ES",
                  "프랑스어": "FRA", "독일어": "DEU", "이탈리아어": "ITA", "일본어": "JPN", "한국어": "KOR",
                  "zh": "CHS", "zh-hans": "CHS", "zh-hant": "CHT", "chs": "CHS", "cht": "CHT", "중국어간체": "CHS", "중국어번체": "CHT"}

    def to_gts_main(self, timeout=90):
        """어느 화면에 있든 GTS 첫 화면까지. 앱이 꺼져 있으면 켬."""
        t0 = time.time(); launched = False; black_since = None
        self._black_relaunched = False; icon_tried = False; load_extra = 0.0
        while time.time() - t0 < timeout + load_extra:
            img = self.shot()
            if img is not None and img.size and float(img.std()) < 1.0:
                # 계속 완전히 검은 화면: PC 화면이 꺼지거나 잠겨 MuMu가 그리기를 멈춘 경우가 많음 → 기다려도 소용없으니 바로 알림
                black_since = black_since or time.time()
                if time.time() - black_since > 10 and not getattr(self, "_black_relaunched", False):
                    # 먼저 HOME 앱을 껐다 켜 봄 (앱 창이 검게 굳는 경우가 있음: 22:22 사례)
                    self.log("휴대폰 화면이 검게만 보임 → HOME 앱을 다시 켬")
                    self._black_relaunched = True
                    self.close(); time.sleep(2); self.adb.launch(); time.sleep(8)
                    self.adb.sf_display = None; self.adb.display = None
                    black_since = time.time(); continue
                if time.time() - black_since > 20 and not icon_tried:
                    # 다시 켜도 단색: am start로 켜면 앱이 가로(0번 화면)에 떠 그려지지 않는 경우 → 바탕화면 아이콘으로 다시 켬
                    icon_tried = True
                    self.log("다시 켜도 화면이 단색 → 바탕화면 아이콘으로 다시 켬")
                    self.close(); time.sleep(2)
                    if self.adb.launch_from_icon(self.log):
                        time.sleep(10); black_since = time.time(); continue
                if time.time() - black_since > 20:
                    raise FlowError("휴대폰 화면이 검게만 보임 (PC 화면 꺼짐·잠금으로 MuMu가 멈췄을 수 있음)", self.save_shot(img, "fail_black"))
                time.sleep(2); continue
            black_since = None; self._black_relaunched = False
            if self.dismiss_error(img):
                continue                                     # 통신 오류 창 → OK (보통 타이틀로 돌아감)
            if self._is_loading(img):
                # 'Now loading… Master data now downloading…' (HOME 데이터 업데이트 직후 등): 실패로 치지 않고 기다림 (최대 3분 더)
                if load_extra < 180:
                    load_extra = min(180.0, load_extra + 2.5)
                time.sleep(2.0); continue
            n, _, x, y = V.find_any(img, ["ph_gts_main", "ph_trade_menu", "ph_room", "ph_box_select", "ph_form", "ph_confirm_ok", "ph_popup"])
            if n == "ph_gts_main":
                return
            if n == "ph_confirm_ok":
                self.tap(318, 1635, 1.5); continue           # 교환 확인 창 → '취소' (주황 OK를 누르면 안 됨)
            if n == "ph_popup":
                self.adb.back(); time.sleep(1.0); continue   # 고르기 창 닫기
            if n == "ph_trade_menu":
                self.tap(x, y, 0.8); continue
            if n == "ph_room":
                self.tap(200, 66, 0.8); continue           # 'Trades' 탭
            if n in ("ph_box_select", "ph_form"):
                self.tap(540, 1800, 1.5); continue          # 아래 '돌아가기'
            if not launched and time.time() - getattr(self, "_fg_checked", 0) > 6:
                self._fg_checked = time.time()                  # dumpsys가 느려서 몇 초에 한 번만 확인
                if "pokemonhome" not in self.adb.foreground():
                    self.adb.launch(); launched = True; time.sleep(5); continue
            # 시작 화면의 대화창('장시간 조작하지 않아…', 통신 오류 등)은 주황색 버튼을 눌러 닫고 계속
            btns = V.orange_buttons(img)
            if btns:
                x, y = btns[-1]
                self.log(f"대화창 버튼 누름 ({x},{y})"); self.save_shot(img, "dialog")
                self.tap(x, y, 2.0); continue
            if self._green_back(img):
                # 아래 가운데 초록 동그라미 '돌아가기' 버튼이 있는 화면 (GTS 글 상세 카드 등 — 원하는 포켓몬이 다른 글을 닫은 뒤 남음) → 눌러서 뒤로
                self.tap(540, 1800, 1.2); continue
            self.tap(540, 1000, 0.6)                         # 타이틀·공지 화면은 화면 터치로 넘김
        raise FlowError("HOME GTS 화면으로 가지 못함", self.save_shot(self.shot(), "fail_to_gts"))

    _LOAD_TPL = None

    @staticmethod
    def _green_back(img):
        """화면 아래 가운데(540,1800)에 HOME의 초록 동그라미 돌아가기 버튼이 있는지"""
        try:
            import cv2
            h = cv2.cvtColor(img[1760:1840, 500:580], cv2.COLOR_BGR2HSV)
            return float(((h[..., 0] >= 55) & (h[..., 0] <= 95) & (h[..., 1] > 120) & (h[..., 2] > 120)).mean()) > 0.6
        except Exception:
            return False

    def _is_loading(self, img):
        """HOME 로딩 화면('Now loading...')인지: 흰 글자만 남겨 템플릿과 비교 (배경 무늬가 움직여도 됨)."""
        try:
            import cv2, numpy as np
            if Home._LOAD_TPL is None:
                p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "templates", "phone", "ph_loading_bin.png")
                Home._LOAD_TPL = V.imread(p, cv2.IMREAD_GRAYSCALE)
            tpl = Home._LOAD_TPL
            if tpl is None or img is None or img.shape[0] < 900 or img.shape[1] < 900: return False
            g = cv2.cvtColor(img[600:900, 200:900], cv2.COLOR_BGR2GRAY)
            reg = ((g > 235).astype(np.uint8)) * 255
            return float(cv2.matchTemplate(reg, tpl, cv2.TM_CCOEFF_NORMED).max()) >= 0.55
        except Exception:
            return False

    def _pick(self, row_y, k):
        """고르기 창에서 k번째 줄 (0 = Any). 창에 9줄(0~8)이 보이고, 그 아래는 끝까지 내린 뒤 고름.
        k == 0 이면 기본값(Any) 그대로라 누르지 않음 — 성별 없는 포켓몬은 Gender 칸이 눌리지 않아 창이 안 뜸."""
        if k <= 0:
            return
        self.tap(540, row_y, 0.1)
        self.expect("ph_popup", 5)
        time.sleep(0.25)                                          # 창이 올라오는 움직임이 끝날 때까지
        for attempt in range(2):
            if k <= 8:
                self.tap(540, self.ROW0 + self.ROW_DY * k, 0.1)
            else:
                for _ in range(2):
                    self.adb.swipe(540, 1350, 540, 500, 400); time.sleep(0.3)
                time.sleep(0.6)                                   # 튕김이 가라앉을 때까지
                self.tap(540, 485 + self.ROW_DY * (k - 2), 0.1)  # 끝까지 내리면 맨 위가 2번째 줄
            if self.wait_gone("ph_popup", 2.0):
                time.sleep(0.15)
                return
            self.log(f"고르기 창이 안 닫힘 (줄 {k}) → 다시")
        p = self.save_shot(self.shot(), "fail_pick")
        raise FlowError(f"고르기 창에서 {k}번째 줄을 고르지 못함", p)

    def _ensure_check(self, off_tpl, want_on, x, y):
        is_off = self.seen(off_tpl)
        if is_off == want_on:
            self.tap(x, y, 0.4)

    NAME_REGION = (60, 580, 960, 260)      # "What do you want to do with <이름>?" 창의 글 전체 (이름이 짧으면 한 줄, 길면 두 줄)
    DIALOG_CLOSE = (540, 1310)

    def _close_dialog(self):
        """포켓몬 메뉴 창의 X 버튼을 찾아 누름 (창 높이가 이름 길이에 따라 달라 위치를 그때그때 찾음)."""
        ok, _, x, y = V.match(self.shot(), "ph_dialog_close")
        px, py = (x, y) if ok else self.DIALOG_CLOSE
        self.tap(px, py, 0.1)
        self.wait_gone("ph_send_menu", 1.5)

    @staticmethod
    def _norm(t):
        import unicodedata
        t = unicodedata.normalize("NFKC", t or "")
        return "".join(ch for ch in t if ch.isalnum()).lower()

    def _occupied(self, img, max_slots=30):
        """GTS 박스 고르기 화면에서 포켓몬이 있는 칸 번호들 (빈 칸은 매끈한 배경이라 밝기 변화가 거의 없음)."""
        g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
        out = []
        for i in range(max_slots):
            x, y = self.grid(i)
            if y > 1720 or x > 1040:
                break
            cell = g[max(0, y - 60):y + 60, max(0, x - 60):x + 60]
            if cell.size and float(cell.std()) > 3:     # 빈 칸은 1 안팎. 작은 포켓몬(꽃이나 등)은 9 정도라 10이면 놓침 (#934)
                out.append(i)
        return out

    def _pick_by_name(self, nick, nick_lang, max_slots=30, nth=0, total=1, fp=None):
        """GTS 박스 고르기 화면에서 포켓몬이 있는 칸을 모두 눌러 메뉴 창의 이름을 읽은 뒤, 봇 포켓몬(nick)과 맞는 칸 하나를 골라
        'Offer as a trade' 위치를 돌려줌. nick은 '닉네임|영어종이름'처럼 후보 여러 개 가능. 정확히 같은 이름을 먼저, 없으면 비슷한 이름이
        하나뿐일 때만 고름 (피츄/피카츄처럼 헷갈리면 고르지 않고 실패)."""
        import difflib
        wants = [self._norm(w) for w in str(nick).split("|") if self._norm(w)]
        if not wants:
            raise FlowError("내보낼 포켓몬 이름이 없어 고를 수 없음")
        shot0 = self.shot()
        slots = self._occupied(shot0)
        if not slots:
            raise FlowError("GTS 박스 화면에 포켓몬이 보이지 않음", self.save_shot(shot0, "fail_name"))
        if total == 1 and len(slots) == 1:
            # GTS 박스는 상대가 원하는 종류만 보여 줌 + 봇 개체도 하나 → 이름을 읽지 않고 바로 그 칸 (한 건에 몇 초 절약)
            i = slots[0]
            sx, sy = self.grid(i)
            self.tap(sx, sy, 0.1)
            x, y = self.expect("ph_send_menu", 5)
            time.sleep(0.15)
            self.log(f"화면에 한 마리뿐 → 이름 확인 없이 {i + 1}번째 칸")
            return x, y
        seen = []   # (slot, text, bx, by)
        for i in slots:
            sx, sy = self.grid(i)
            self.tap(sx, sy, 0.1)
            got = None
            t0 = time.time()
            while time.time() - t0 < 3:
                img = self.shot()
                ok, _, bx, by = V.match(img, "ph_send_menu")
                if ok:
                    got = (img, bx, by); break
            if not got:
                # 창이 안 뜸 (내보낼 수 없는 포켓몬 등) → 다른 창이 떠 있으면 닫고 다음 칸
                self.log(f"{i + 1}번 칸: 메뉴 창이 뜨지 않음 → 건너뜀")
                self._close_dialog()
                continue
            img, bx, by = got
            text = V.ocr_name(img, self.NAME_REGION, nick_lang)
            seen.append((i, text, bx, by))
            self._close_dialog()                                    # X로 닫고 다음 칸
        norm = [(i, self._norm(t), t, bx, by) for i, t, bx, by in seen]
        exact = [e for e in norm if e[1] in wants]
        if not exact:
            # 창 문장을 통째로 읽다 'with' 등이 깨져 앞에 군더더기가 붙는 경우 (예: 'What do you want to 001 킬라플로르') → 이름이 들어 있으면 같은 것으로 봄
            exact = [e for e in norm if any(len(w) >= 2 and w in e[1] for w in wants)]
        if total > 1 and fp:
            # 같은 이름의 봇 개체가 여럿: 후보마다 요약(Summary)을 열어 레벨·성별·능력치·기술·어버이·ID로 봇이 만든 개체를 직접 확인
            cand = exact
            if len(cand) < total:
                cand = [e for e in norm if max(difflib.SequenceMatcher(None, w, e[1]).ratio() for w in wants) >= 0.6]
            if len(cand) < total and len(norm) > len(cand):
                # 이름으로 찾은 칸이 봇 개체 수보다 적음 (한 칸 이름을 못 읽음 등, #2498 사례: 다른 신청 개체 하나만 확인하고 실패)
                # GTS 박스엔 상대가 원하는 종류만 보이므로 보이는 칸 전부를 요약 화면으로 확인
                self.log(f"같은 이름 후보 {len(cand)}칸 < 봇 개체 {total}마리 (읽은 이름: {', '.join(e[2] for e in norm)}) → 보이는 칸 전부 요약 화면으로 확인")
                cand = norm
            if not cand and norm:
                # 이름을 못 읽음 (글자가 깨져 특수문자만 읽힘 등). GTS 박스엔 상대가 원하는 종류만 보이므로 보이는 칸 전부를 요약 화면으로 확인
                self.log(f"같은 이름 후보를 이름으로 못 찾음 (읽은 이름: {', '.join(e[2] for e in norm)}) → 보이는 칸 전부 요약 화면으로 확인")
                cand = norm
            if not cand:
                raise FlowError("같은 이름 후보를 찾지 못함 (메뉴 창이 뜬 칸 없음)", self.save_shot(self.shot(), "fail_name"))
            chosen = self._pick_by_summary(cand, fp, nick_lang)
        elif total > 1:
            # 같은 이름의 봇 개체가 여럿: HOME 칸 순서 = 화면 순서이므로 nth번째를 고름. 보이는 수가 봇 개체 수와 다르면
            # (받은 포켓몬이 같은 이름이거나, 상대 조건 때문에 일부가 안 보이거나, 이름을 잘못 읽음) 엉뚱한 걸 보낼 수 있어 고르지 않음
            cand = exact
            if len(cand) != total:
                fuzzy = [e for e in norm if max(difflib.SequenceMatcher(None, w, e[1]).ratio() for w in wants) >= 0.75]
                if len(fuzzy) == total:
                    cand = fuzzy
            if len(cand) != total or not (0 <= nth < total):
                read = ", ".join(f"{e[0] + 1}:{e[2]}" for e in norm) or "없음"
                raise FlowError(f"같은 이름 봇 개체 {total}마리 중 {nth + 1}번째를 고르려 했지만 화면에 맞는 칸이 {len(cand)}개 (읽은 이름: {read})", self.save_shot(self.shot(), "fail_name"))
            cand = sorted(cand, key=lambda e: e[0])
            chosen = cand[nth]
            self.log(f"같은 이름 {total}마리 중 {nth + 1}번째 칸을 고름")
        elif len(exact) == 1:
            chosen = exact[0]
        elif len(exact) > 1 and fp:
            # 기록상 한 마리인데 같은 이름이 여럿 보임 (취소된 신청의 남은 개체·받은 포켓몬 등) → 요약으로 봇 개체를 확인
            self.log(f"같은 이름 '{exact[0][2]}' {len(exact)}칸 → 요약 화면으로 확인")
            chosen = self._pick_by_summary(exact, fp, nick_lang)
        elif len(exact) > 1:
            raise FlowError(f"같은 이름 '{exact[0][2]}'이 {len(exact)}칸 있어 어느 것인지 알 수 없음", self.save_shot(self.shot(), "fail_name"))
        else:
            scored = []
            for e in norm:
                sc = max(difflib.SequenceMatcher(None, w, e[1]).ratio() for w in wants)
                scored.append((sc, e))
            scored.sort(key=lambda z: -z[0])
            # GTS 박스는 상대가 원하는 종류만 보여 주므로, 한 마리만 보이면 그게 봇 포켓몬 (글자 하나 잘못 읽어도: 갑주무사→감주무사)
            if scored and len(scored) == 1 and total == 1 and len(slots) == 1:
                # GTS 박스는 상대가 원하는 종류만 보여 주고, 봇 개체도 한 마리뿐 → 이름을 못 읽어도(외국어 닉네임 등) 그게 봇 포켓몬
                chosen = scored[0][1]
                self.log(f"화면에 한 마리뿐이라 고름: '{chosen[2]}' ({scored[0][0]:.2f}) ← 찾는 이름 {nick}")
            elif scored and len(scored) == 1 and scored[0][0] >= 0.5:
                chosen = scored[0][1]
                self.log(f"한 마리뿐이라 고름: '{chosen[2]}' ({scored[0][0]:.2f}) ← 찾는 이름 {nick}")
            elif scored and scored[0][0] >= 0.75 and (len(scored) == 1 or scored[0][0] - scored[1][0] >= 0.2):
                chosen = scored[0][1]
                self.log(f"비슷한 이름으로 고름: '{chosen[2]}' ({scored[0][0]:.2f}) ← 찾는 이름 {nick}")
            elif fp and norm:
                # 이름을 못 읽음 (한글 닉네임 OCR 실패 등). GTS 박스엔 상대가 원하는 종류만 보이므로 보이는 칸들을 요약 화면으로 확인해 봇 개체를 고름
                self.log(f"이름으로 못 고름 (읽은 이름: {', '.join(e[2] for e in norm)}) → 요약 화면으로 확인")
                chosen = self._pick_by_summary(norm, fp, nick_lang)
            else:
                read = ", ".join(f"{e[0] + 1}:{e[2]}" for e in norm) or "없음"
                raise FlowError(f"GTS 박스에서 '{nick}'을 찾지 못함 (읽은 이름: {read})", self.save_shot(self.shot(), "fail_name"))
        i = chosen[0]
        sx, sy = self.grid(i)
        self.tap(sx, sy, 0.1)
        x, y = self.expect("ph_send_menu", 5)
        time.sleep(0.15)
        self.log(f"봇 포켓몬 찾음: {i + 1}번째 '{chosen[2]}' (찾는 이름 '{nick}')")
        return x, y

    SUMMARY_DY = 160            # 메뉴 창에서 'Summary' 버튼 = 'Offer as a trade' 버튼 아래 이만큼
    SUMMARY_CLOSE = (540, 1800)  # 요약 화면 아래 X

    def _read_summary(self, nick_lang):
        """지금 열린 요약 화면을 읽음 → (머리글, 능력치·기술, 어버이·ID 부분, 성별)."""
        time.sleep(0.8)
        img = self.shot()
        head = V.ocr_text(img, (0, 0, 1080, 150), nick_lang)
        body = V.ocr_text(img, (0, 1060, 1080, 520), "ENG")
        gender = V.gender_mark(img)
        self.adb.swipe(540, 1500, 540, 800, 300); time.sleep(1.0)
        img2 = self.shot()
        low = V.ocr_text(img2, (0, 500, 1080, 1200), nick_lang)
        return head, body, low, gender, img

    def _summary_score(self, head, body, low, gender, fp):
        import re, difflib
        s = 0; why = []
        m = re.search(r"Lv\.?\s*(\d{1,3})", head)
        if m:
            if int(m.group(1)) == int(fp.get("lv", -1)): s += 3; why.append("레벨")
            else: s -= 3; why.append(f"레벨다름({m.group(1)})")
        nums = set(re.findall(r"\d+", body))
        st = [str(v) for v in fp.get("stats", [])]
        hit = sum(1 for v in st if v in nums)
        s += hit; why.append(f"능력치{hit}/{len(st)}")
        bn = self._norm(body)
        mv = 0
        for name in fp.get("moves", []):
            n = self._norm(name)
            if not n: continue
            if n in bn: mv += 1; continue
            L = len(n)
            if any(difflib.SequenceMatcher(None, n, bn[i:i + L]).ratio() >= 0.75 for i in range(0, max(1, len(bn) - L + 1))): mv += 1
        s += mv; why.append(f"기술{mv}")
        tid = str(fp.get("tid", ""))
        if tid and tid in re.findall(r"\d{4,7}", low.replace("/", "")): s += 1; why.append("ID")
        ot = self._norm(fp.get("ot", ""))
        if ot:
            ln = self._norm(low)
            L = len(ot)
            if ot in ln or any(difflib.SequenceMatcher(None, ot, ln[i:i + L]).ratio() >= 0.5 for i in range(0, max(1, len(ln) - L + 1))): s += 1; why.append("어버이")
        g = int(fp.get("gender", -1))
        if g in (0, 1) and gender in (0, 1):
            if g == gender: s += 2; why.append("성별")
            else: s -= 3; why.append("성별다름")
        return s, " ".join(why)

    def _pick_by_summary(self, cand, fp, nick_lang):
        """후보 칸마다 메뉴 → Summary → 읽고 점수 → X. 가장 잘 맞는 하나가 확실할 때만 고름 (아니면 실패)."""
        scores = []
        for e in cand:
            i = e[0]
            sx, sy = self.grid(i)
            self.tap(sx, sy, 1.2)
            x, y = self.expect("ph_send_menu", 5)
            self.tap(x, y + self.SUMMARY_DY, 1.5)
            time.sleep(0.5)
            if V.match(self.shot(), "ph_send_menu")[0]:
                self._close_dialog()
                raise FlowError("요약(Summary) 화면이 열리지 않음", self.save_shot(self.shot(), "fail_summary_open"))
            head, body, low, gender, img = self._read_summary(nick_lang)
            if "lv" not in head.lower():
                self.log(f"요약 머리글을 못 읽음: {head!r}")
            sc, why = self._summary_score(head, body, low, gender, fp)
            self.log(f"요약 {i + 1}번 칸: {sc}점 ({why})")
            scores.append((sc, e, why))
            self.tap(*self.SUMMARY_CLOSE, 1.2)            # 요약 닫기 → 메뉴 창으로 돌아옴
            self._close_dialog()                          # 메뉴 창 닫기
        scores.sort(key=lambda z: -z[0])
        best = scores[0]
        second = scores[1][0] if len(scores) > 1 else -99
        if best[0] >= 5 and best[0] - second >= 3:
            pass
        elif best[0] >= 12 and second >= best[0] - 2:
            # 둘 다 봇 개체 정보와 거의 다 맞음 = 같은 조건으로 만든 똑같은 개체 (같은 조건 신청이 둘) → 어느 쪽을 보내도 같음
            self.log(f"요약 점수가 둘 다 높음({best[0]}·{second}) → 같은 조건 개체로 보고 {best[1][0] + 1}번 칸")
            return best[1]
        if best[0] >= 5 and best[0] - second >= 3:
            self.log(f"요약으로 봇 개체 확인: {best[1][0] + 1}번 칸 ({best[0]}점, 다음 {second}점)")
            return best[1]
        detail = ", ".join(f"{z[1][0] + 1}번 {z[0]}점({z[2]})" for z in scores)
        raise FlowError(f"요약으로도 봇 개체를 확실히 고르지 못함: {detail}", self.save_shot(self.shot(), "fail_summary"))

    def gts_find_trade(self, species_name, gender=-1, level=0, lang="", slot=0, dry=False, fresh=False, nick="", nick_lang="", nth=0, total=1, fp=None):
        """GTS '포켓몬을 찾는다'에서 사용자가 올린 포켓몬(종·성별·레벨·언어)을 찾아 HOME 박스 slot 칸과 교환.
        반환 vars: found(검색 결과 수), traded=1/0.  dry=True 이면 확인 창에서 취소."""
        if not species_name or any(ord(c) > 127 for c in species_name):
            raise FlowError(f"HOME(영어)에 입력할 수 없는 이름: {species_name}")
        T = [("시작", time.time())]; self._shot_n = 0; self._shot_t = 0.0
        def mark(n): T.append((n, time.time()))
        def dump(res):
            try:
                ts = " ".join(f"{n}+{b - a:.1f}" for (_, a), (n, b) in zip(T, T[1:]))
                with open(os.path.join(SHOT_DIR, "timing.log"), "a", encoding="utf-8") as f:
                    f.write(f"{time.strftime('%H:%M:%S')} {species_name} 합계 {T[-1][1] - T[0][1]:.1f}초 · {ts} · {res} · 캡처 {self._shot_n}장 평균 {self._shot_t / max(1, self._shot_n):.2f}초\n")
            except Exception: pass
        if fresh:                                           # 스위치에서 막 옮긴 뒤: 새로 켜야 박스가 최신
            self.close(); time.sleep(1.5)
            self.adb.launch(); time.sleep(4)
        self.to_gts_main()
        # 버튼이 미끄러져 들어오는 중에 누르면 옆 버튼('Wanted Pokémon')이 눌림 → 연속 두 장에서 같은 자리일 때 바로 누름
        mark("GTS첫화면")
        self.wait_tap("ph_gts_main", 10, 0.1)
        self.expect("ph_form", 10)
        mark("검색창")
        # 1) 원하는 포켓몬 이름 (HOME 언어 = English, 전체 이름을 치면 후보 첫 줄이 그 포켓몬)
        self.tap(600, 234, 0.8)
        self.type_text(species_name)
        time.sleep(1.0)
        self.tap(540, 340, 0.2)
        try:
            self.expect("ph_form_ready", 5)                 # 이름이 맞게 들어가야 검색 버튼이 주황색이 됨
        except FlowError as e:
            raise FlowError(f"이름 '{species_name}'을 고르지 못함", e.shot)
        mark("이름입력")
        # 2) 성별 / 레벨 구간 / 언어
        self._pick(370, {0: 1, 1: 2}.get(int(gender), 0))
        self._pick(510, (int(level) - 1) // 10 + 1 if level else 0)
        code = self.LANG_ALIAS.get(str(lang).lower(), str(lang).upper())
        self._pick(930, self.LANG_ROW.get(code, 0))
        # 3) 전설·환상 포함 켬, '가지고 있지 않은 포켓몬' 포함 끔 (봇이 가진 포켓몬을 원하는 글만)
        self._ensure_check("ph_legend_off", True, 140, 1125)
        self._ensure_check("ph_own_off", False, 140, 1340)
        x, y = self.expect("ph_form_ready", 5)
        mark("조건")
        self.tap(x, y, 0.3)
        # 4) 결과: 카드가 뜨거나 '찾는 포켓몬이 없다' 창이 뜰 때까지
        t0 = time.time(); cards = []; none = False
        while time.time() - t0 < 25:
            img = self.shot()
            cards = V.match_all(img, "ph_card")
            if cards: break
            if V.match(img, "ph_no_result")[0]:
                none = True; break
            time.sleep(0.1)
        mark("결과")
        if not cards:
            dump("없음")
            p = self.save_shot(self.shot(), "gts_none")
            self.tap(540, 1272, 1.5)                        # 'Back' (창이 없으면 체크칸을 건드리지만 다음 검색 때 다시 맞춤)
            self.tap(540, 1800, 1.5)                        # 검색 화면에서 돌아가기
            return {"found": "0", "traded": "0", "shot": p}
        if len(cards) > 1:
            p = self.save_shot(self.shot(), "gts_many")
            self.tap(540, 1800, 1.0)
            return {"found": str(len(cards)), "traded": "0", "shot": p}
        cx, cy = cards[0]
        self.tap(cx + 270, cy - 90, 0.1)
        x, y = self.expect("ph_confirm_ok", 10)
        time.sleep(0.25)                                    # 창이 다 뜬 뒤 위치를 다시 잼
        img = self.shot()
        f, _, x2, y2 = V.match(img, "ph_confirm_ok")
        if f: x, y = x2, y2
        detail = self.save_shot(img, "gts_detail")
        mark("상세")
        if dry:
            self.tap(318, 1635, 1.0)
            return {"found": "1", "traded": "0", "shot": detail}
        self.tap(x, y, 0.1)
        # 박스 고르기 화면 대신 "You don't have a Pokémon that your partner wants." 창이 뜰 수 있음
        # = 이 글이 원하는 포켓몬이 봇 포켓몬과 다름 (다른 사람의 같은 조건 글이거나, 신청자가 '원하는 포켓몬'을 잘못 설정) → 봇 오류가 아님
        t_box = time.time(); img_b = None
        while True:
            img_b = self.shot()
            if V.match(img_b, "ph_box_select")[0]:
                break
            btns = V.orange_buttons(img_b)
            if btns:
                txt = ""
                try: txt = V.ocr_text(img_b, (60, 600, 960, 600), "ENG", psm="SINGLE_BLOCK", scale=1.0).lower()
                except Exception: pass
                if "partner" in txt or "wants" in txt or "don't have" in txt or "dont have" in txt:
                    shotp = self.save_shot(img_b, "want_mismatch")
                    self.tap(*btns[-1], 1.0)
                    raise FlowError("WANT_MISMATCH: 찾은 GTS 글이 원하는 포켓몬이 봇 포켓몬과 다름 (원하는 포켓몬 설정 확인 필요)", shotp)
            if self.dismiss_error(img_b):
                raise FlowError("통신 오류 창이 떠서 OK를 누름 (처음부터 다시)", "")
            if time.time() - t_box > 20:
                raise FlowError("화면이 'ph_box_select'이 아님", self.save_shot(img_b, "fail_ph_box_select"))
            time.sleep(0.12)
        mark("박스")
        time.sleep(0.3)                                     # 칸 그림이 다 그려진 뒤 빈 칸 판단
        if nick and V.name_ocr_ready():
            x, y = self._pick_by_name(nick, nick_lang, nth=nth, total=total, fp=fp)       # 이름을 읽어 봇 포켓몬을 골라냄 (화면 순서가 HOME 칸 순서와 다름)
        else:
            if slot > 0:
                raise FlowError("이름 인식 없이 2번째 이후 칸은 고를 수 없음 (화면 순서가 달라 다른 포켓몬이 나갈 수 있음)")
            sx, sy = self.grid(0)                           # 이름 인식이 없으면 예전처럼 첫 칸 (한 마리씩일 때만 안전)
            self.tap(sx, sy, 1.5)
            try:
                x, y = self.expect("ph_send_menu", 6)
            except FlowError as e:
                raise FlowError("봇 포켓몬을 내보낼 수 없음 (상대가 원하는 조건과 안 맞을 수 있음)", e.shot)
        mark("개체고름")
        self.tap(x, y, 0.2)
        if SKIP_TRADE_ANIM:
            # 교환 연출을 기다리지 않음. 단, 반드시 '교환 버튼 → 연출 시작'을 확인한 뒤에만 앱을 끔.
            # 연출 시작 = 박스/보내기 메뉴가 사라지고, 오류·확인 창도 아닌 화면이 연속 2번 보일 때.
            # 캡처가 빨라져 장 수 대신 시간으로 봄: 아는 화면이 아닌 상태가 1.2초 넘게 이어지고 3장 이상일 때
            t0 = time.time(); seen = 0; img = None; since = None; started = False
            while time.time() - t0 < 20:
                img = self.shot()
                if self.dismiss_error(img):
                    raise FlowError("교환 버튼 뒤 통신 오류 창 → OK 누름 (교환 안 된 것으로 보고 다시 검색)", "")
                n, *_ = V.find_any(img, ["ph_send_menu", "ph_box_select", "ph_confirm_ok", "ph_dialog_close", "ph_no_result"])
                if n: seen = 0; since = None
                else:
                    seen += 1; since = since or time.time()
                if seen >= 3 and time.time() - since >= 1.2:
                    started = True; break
                time.sleep(0.1)
            if not started:
                raise FlowError("교환 버튼 뒤 연출로 넘어가지 않음 (앱을 끄지 않고 둠)", self.save_shot(img if img is not None else self.shot(), "no_trade_anim"))
            anim = self.save_shot(img, "trade_anim")       # 나중에 확인용 (연출 화면이 맞는지)
            self.log("교환 연출 시작 확인 → HOME 바로 재시작")
            mark("연출확인")
            # anim_test.txt 의 숫자만큼은 앱을 끄지 않고 (재 보니 연출 27초 < 재시작 35초라 기본으로 연출을 봄) 연출이 끝나 GTS·Trades 화면으로 돌아올 때까지 잼 (재시작보다 빠른지 비교)
            tf = os.path.join(os.path.dirname(os.path.abspath(__file__)), "anim_test.txt")
            try: left = int(open(tf, encoding="utf-8").read().strip() or 0)
            except Exception: left = 0
            if left > 0:
                try: open(tf, "w", encoding="utf-8").write(str(left - 1))
                except Exception: pass
                t1 = time.time(); back = False
                while time.time() - t1 < 120:
                    img = self.shot()
                    if self.dismiss_error(img):
                        back = False; break                  # 연출 뒤 오류 창: 교환은 이미 됨 → 닫고 아래에서 앱을 다시 켬
                    n, *_ = V.find_any(img, ["ph_gts_main", "ph_trade_menu", "ph_room"])
                    if n:
                        back = True; break
                    btns = V.orange_buttons(img)
                    if btns and time.time() - t1 > 5:
                        self.tap(*btns[-1], 0.3)
                    time.sleep(0.2)
                mark("연출끝까지" if back else "연출120초넘음")
                dump("교환(연출 끝까지 봄)")
                if back:
                    return {"found": "1", "traded": "1", "shot": detail, "anim": anim}
                self.close(); time.sleep(0.5); self.adb.launch()
                return {"found": "1", "traded": "1", "shot": detail, "anim": anim}
            dump("교환")
            self.close(); time.sleep(0.5); self.adb.launch()
            return {"found": "1", "traded": "1", "shot": detail, "anim": anim}
        # 교환 연출이 끝나면 Trades 첫 화면(또는 GTS 화면)으로 돌아옴
        t0 = time.time()
        while time.time() - t0 < 180:
            n, *_ = V.find_any(self.shot(), ["ph_trade_menu", "ph_gts_main"])
            if n:
                return {"found": "1", "traded": "1", "shot": detail}
            time.sleep(2)
        raise FlowError("교환 뒤 Trades 화면으로 돌아오지 않음", self.save_shot(self.shot(), "fail_after_trade"))

    def relaunch(self):
        """휴대폰 HOME을 껐다가 새로 켜 두기만 함 (첫 화면까지 기다리지 않음)."""
        self.close(); time.sleep(1.5)
        self.adb.launch()
        return {}

    def close(self):
        """휴대폰 HOME 종료 (스위치 HOME과 같은 계정이라 동시에 못 씀)."""
        try: self.adb.force_stop()
        except Exception as e: self.log("HOME 종료 실패:", e)
        return {}

    def type_text(self, s):
        """한글 입력: ADBKeyboard (#N1 사용자에 설치·지정 필요)."""
        if all(ord(c) < 128 for c in s):
            self.adb.text(s); return
        if not self.adb.ime_text(s):
            raise FlowError("한글 입력기(ADBKeyboard)가 준비되지 않았습니다")

    # ---- 한글 입력 (adb input text는 한글 불가)
    def _type_korean(self, s):
        """1순위: 클립보드 앱(예: 'Clipper')에 넣고 붙여넣기. 2순위: 화면 자판 템플릿. 둘 다 준비 전이면 FlowError."""
        try:
            # adb shell am broadcast -a clipper.set -e text '...'  (Clipper 앱 설치 시)
            self.adb.run("shell", "am", "broadcast", "-a", "clipper.set", "-e", "text", s)
            self.adb.key(279)   # KEYCODE_PASTE
            time.sleep(0.8)
            return
        except Exception:
            raise FlowError("한글 입력 수단이 없습니다 (Clipper 앱 또는 자판 템플릿 필요)")
