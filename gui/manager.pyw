"""HomeBot 관리 창 — 봇·워커 켜고 끄기, 로그 보기, 시험 도구를 한 곳에서.

실행: HomeBot 시작.bat (또는 pythonw gui\\manager.pyw)
표준 라이브러리(tkinter)만 씀.
"""
import os, queue, shutil, socket, subprocess, sys, threading, time, urllib.request
import tkinter as tk
from tkinter import ttk, messagebox, simpledialog

HERE = os.path.dirname(os.path.abspath(__file__))
HB = os.path.dirname(HERE)                     # ...\HomeBot\homebot
ROOT = os.path.dirname(HB)                     # ...\HomeBot
CFG = os.path.join(HB, "config.json")
DATA = os.path.join(HB, "data")


# ---- 봇이 꺼져 있을 때 대기열을 DB에서 직접 보고 고침 (봇이 켜져 있으면 봇에게 명령으로 맡김)
class OfflineQueue:
    ACTIVE = ("queued", "generated", "on_switch", "in_home", "ready", "trading")
    MOVABLE = ("queued", "generated")
    KO = {"queued": "대기", "generated": "생성됨", "on_switch": "스위치에 있음", "in_home": "HOME에 있음", "ready": "준비됨", "trading": "교환 중"}

    def __init__(self, path):
        self.path = path

    def _db(self):
        import sqlite3
        c = sqlite3.connect(self.path, timeout=5)
        c.execute("PRAGMA busy_timeout=5000")
        return c

    @staticmethod
    def _key(kj):
        import json
        try:
            k = json.loads(kj or "{}")
            if not k.get("NameKo"): return ""
            g = {0: "♂", 1: "♀"}.get(k.get("Gender", -1), "성별없음")
            return f'{k["NameKo"]} Lv.{k.get("Level", 0)} {g} {k.get("Lang", "")}'
        except Exception:
            return ""

    def _active(self, c, statuses):
        q = ",".join("?" * len(statuses))
        return c.execute(f"SELECT id,status,describe,discord_id,key_json,pool_id,COALESCE(game,'') FROM orders WHERE status IN ({q}) ORDER BY COALESCE(priority,id), id", statuses).fetchall()

    def rows(self):
        if not os.path.exists(self.path): return []
        with self._db() as c:
            out = []
            names = dict(c.execute("SELECT k, v FROM kv WHERE k LIKE 'name:%'").fetchall())
            for i, st, d, u, kj, _, g in self._active(c, self.ACTIVE):
                out.append("\t".join([str(i), self.KO.get(st, st), (d or "").replace("\t", " "), str(u), self._key(kj), "1" if st in self.MOVABLE else "0",
                                       (names.get(f"name:{u}") or "").replace("\t", " "), (g or "").upper()]))
            return out

    def move(self, oid, d):
        with self._db() as c:
            ids = [r[0] for r in self._active(c, self.MOVABLE)]
            if oid not in ids: return "순서를 바꿀 수 없는 상태입니다"
            i = ids.index(oid); j = {"up": i - 1, "down": i + 1, "top": 0, "bottom": len(ids) - 1}.get(d, i)
            if j < 0 or j >= len(ids) or j == i: return "더 옮길 수 없습니다"
            ids.insert(j, ids.pop(i))
            base = min(c.execute("SELECT COALESCE(priority,id) FROM orders WHERE id=?", (x,)).fetchone()[0] for x in ids)
            for k, x in enumerate(ids): c.execute("UPDATE orders SET priority=? WHERE id=?", (base + k * 0.001, x))
            return f"#{oid} 순서 변경"

    def cancel(self, oid, why="운영자가 신청을 취소했습니다"):
        import datetime
        with self._db() as c:
            r = c.execute(f"SELECT status,describe,discord_id,pool_id FROM orders WHERE id=?", (oid,)).fetchone()
            if not r or r[0] not in self.ACTIVE: return f"#{oid}는 진행 중이 아닙니다"
            now = datetime.datetime.now(datetime.timezone.utc).isoformat()
            c.execute("UPDATE orders SET status='cancelled', error=?, updated_at=? WHERE id=?", (why, now, oid))
            if r[3] is not None: c.execute("UPDATE pool SET reserved_by=NULL WHERE id=?", (r[3],))
            msg = f"🚫 #{oid} {r[1]} · {why}" + (". GTS에 맡긴 포켓몬이 있다면 회수해 주세요." if r[0] in ("in_home", "trading") else "")
            c.execute("INSERT INTO kv(k,v) VALUES(?,?) ON CONFLICT(k) DO UPDATE SET v=excluded.v", (f"dm_pending:{oid}", f"{r[2]}\t{msg}"))
            return f"#{oid} 취소 (DM은 봇이 켜지면 보냄)"

    def clear(self, all_):
        with self._db() as c:
            ids = [r[0] for r in self._active(c, self.ACTIVE if all_ else self.MOVABLE + ("on_switch",))]
        for i in ids: self.cancel(i, "운영자가 대기열을 초기화해 신청이 취소됐습니다")
        return f"{len(ids)}건 취소"
PW = os.path.join(HB, "phone_worker")
def worker_url():
    """화면 인식·휴대폰 서비스 주소 (config.json Phone.Url)"""
    try: u = str((load_cfg().get("Phone", {}) or {}).get("Url") or "").strip()
    except Exception: u = ""
    return (u or "http://127.0.0.1:8765").rstrip("/")
NO_WINDOW = 0x08000000 if os.name == "nt" else 0
MAX_ACCTS = 5   # HOME 계정(휴대폰) 최대 수 — 봇·워커와 같게

GUI_SET = os.path.join(HERE, "gui_settings.json")


def load_gui():
    d = {"serial": "127.0.0.1:16384", "user": ""}
    try:
        import json
        with open(GUI_SET, encoding="utf-8") as fp: d.update(json.load(fp))
    except Exception: pass
    return d


def strip_jsonc(t):
    """// 주석과 끝의 쉼표를 지움 (문자열 안은 건드리지 않음)"""
    out = []; i = 0; n = len(t); ins = False
    while i < n:
        c = t[i]
        if ins:
            out.append(c)
            if c == "\\" and i + 1 < n: out.append(t[i + 1]); i += 2; continue
            if c == '"': ins = False
        elif c == '"': ins = True; out.append(c)
        elif c == "/" and i + 1 < n and t[i + 1] == "/":
            while i < n and t[i] != "\n": i += 1
            continue
        else: out.append(c)
        i += 1
    import re
    return re.sub(r",(\s*[}\]])", r"\1", "".join(out))


def load_cfg():
    import json
    if not os.path.exists(CFG):   # 처음: 예시 설정에서 시작 (마법사가 채움)
        smp = os.path.join(HB, "config.sample.json")
        if os.path.exists(smp): shutil.copyfile(smp, CFG)
        else: return {}
    with open(CFG, encoding="utf-8-sig") as fp: return json.loads(strip_jsonc(fp.read()))


def save_cfg(c):
    import json
    try: shutil.copyfile(CFG, CFG + ".bak")
    except Exception: pass
    with open(CFG, "w", encoding="utf-8") as fp: json.dump(c, fp, ensure_ascii=False, indent=2)


# 콘솔 없는 pythonw로 떠도 자식 스크립트는 python.exe로 돌림 (출력을 받아야 하므로)
PY = sys.executable
if PY.lower().endswith("pythonw.exe"):
    cand = PY[:-len("pythonw.exe")] + "python.exe"
    if os.path.exists(cand):
        PY = cand


def child_env():
    env = dict(os.environ)
    env["PYTHONIOENCODING"] = "utf-8"; env["PYTHONUTF8"] = "1"; env["DOTNET_CLI_UI_LANGUAGE"] = "ko"
    env["DOTNET_NOLOGO"] = "1"
    if not env.get("DISCORD_TOKEN") and os.name == "nt":
        try:   # 토큰등록으로 저장한 사용자 환경 변수 (관리 창이 그 전에 떠 있었어도 읽음). 화면에 표시하지 않음.
            import winreg
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as k:
                env["DISCORD_TOKEN"] = winreg.QueryValueEx(k, "DISCORD_TOKEN")[0]
        except Exception:
            pass
    return env


def decode(b):
    for enc in ("utf-8", "cp949"):
        try:
            return b.decode(enc)
        except UnicodeDecodeError:
            pass
    return b.decode("utf-8", "replace")


def kill_tree(p):
    if p and p.poll() is None:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"], capture_output=True, creationflags=NO_WINDOW)
        else:
            p.kill()


class Proc:
    """프로그램 하나 (봇·워커·도구). 출력은 줄 단위로 queue에."""
    def __init__(self, name, args, cwd, out_q):
        self.name, self.args, self.cwd, self.q = name, args, cwd, out_q
        self.p = None; self.started = 0.0; self.extra_env = {}

    def running(self):
        return self.p is not None and self.p.poll() is None

    def start(self):
        self._stopped_by_user = False                   # 사용자가 직접 멈춘 것만 자동 재시작하지 않음
        if self.running():
            return
        self.q.put((self.name, f"──── {time.strftime('%H:%M:%S')} 시작: {' '.join(self.args)}\n"))
        try:
            self.p = subprocess.Popen(self.args, cwd=self.cwd, env={**child_env(), **self.extra_env}, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                      stdin=subprocess.DEVNULL, creationflags=NO_WINDOW)
        except OSError as e:
            self.p = None; self._stopped_by_user = True
            self.q.put((self.name, f"──── 실행 실패: {e} ({self.args[0]})\n")); return
        self.started = time.time()
        threading.Thread(target=self._pump, args=(self.p,), daemon=True).start()

    def _pump(self, p):
        for line in iter(p.stdout.readline, b""):
            self.q.put((self.name, decode(line).replace("\r", "")))
        code = p.wait()
        self.q.put((self.name, f"──── {time.strftime('%H:%M:%S')} 끝남 (코드 {code})\n"))

    def stop(self):
        self._stopped_by_user = True; kill_tree(self.p)


class BotProc(Proc):
    """봇은 'shutdown' 명령으로 먼저 끄게 함 → 상태 채널을 '꺼짐'으로 바꾸고 종료. 12초 안에 안 꺼지면 강제 종료."""
    def stop(self):
        self._stopped_by_user = True
        if not self.running(): return
        try:
            os.makedirs(DATA, exist_ok=True)
            with open(os.path.join(DATA, "cmd.txt"), "a", encoding="utf-8") as fp: fp.write("shutdown\n")
        except Exception: pass
        p = self.p
        def later():
            try: p.wait(timeout=12)
            except Exception: self._kill(p)
        threading.Thread(target=later, daemon=True).start()

    def stop_now(self):
        self._stopped_by_user = True; self._kill(self.p)

    @staticmethod
    def _kill(p):
        """강제 종료. 봇이 못 읽은 'shutdown'이 cmd.txt에 남으면 다음에 켜자마자 꺼지므로 지움."""
        was = p is not None and p.poll() is None
        kill_tree(p)
        if was:
            try: os.remove(os.path.join(DATA, "cmd.txt"))
            except Exception: pass


class WorkerProc(Proc):
    """워커를 강제로 끄면 워커가 바꾼 실제 휴대폰 화면 크기(1080x1920)가 그대로 남으므로 여기서 되돌림"""
    phones = []
    def stop(self):
        self._stopped_by_user = True; kill_tree(self.p)
        def reset():
            for ser in self.phones:
                if ser.startswith(("127.0.0.1", "localhost", "emulator-")): continue
                for what in ("size", "density"):
                    try: subprocess.run(["adb", "-s", ser, "shell", "wm", what, "reset"], capture_output=True, timeout=8, creationflags=NO_WINDOW)
                    except Exception: pass
        threading.Thread(target=reset, daemon=True).start()


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("HomeBot 관리")
        self.geometry("1180x760"); self.minsize(900, 560)
        try: self.iconbitmap(default="")
        except Exception: pass
        self.q = queue.Queue()
        exe = os.path.join(HB, "bot", "HomeBot.Discord.exe")   # 배포판: 미리 빌드한 실행 파일 (.NET 설치 불필요)
        dotnet_run = [exe] if os.path.exists(exe) else ["dotnet", "run", "-c", "Release", "--project", os.path.join(HB, "HomeBot.Discord"), "--"]
        self.bot = BotProc("봇", dotnet_run + [CFG], HB, self.q)
        self.gs = load_gui()
        self.worker = WorkerProc("워커", [], PW, self.q)
        self._worker_args()
        self.tool = None
        self.dotnet_run = dotnet_run
        self.auto = tk.BooleanVar(value=True)
        self._build()
        self.after(200, self._drain)
        self.after(1000, self._tick)
        self.protocol("WM_DELETE_WINDOW", self._close)
        if "--start" in sys.argv: self.after(1500, self.start_all)
        else:
            try: need = not load_cfg().get("Discord", {}).get("GuildId") or not load_cfg().get("Home", {}).get("Accounts")
            except Exception: need = True
            if need: self.after(600, self.open_wizard)   # 봇이 관리 창을 다시 켤 때 (gui_restart) 바로 전체 시작

    def report_callback_exception(self, exc, val, tb):
        # pythonw에는 콘솔이 없어 버튼 오류가 그냥 사라짐 → 창으로 보여 주고 error.log에 남김
        import traceback
        err = "".join(traceback.format_exception(exc, val, tb))
        try:
            with open(os.path.join(HERE, "error.log"), "a", encoding="utf-8") as fp: fp.write(time.strftime("%F %T\n") + err + "\n")
        except Exception: pass
        messagebox.showerror("HomeBot 오류", err[-1200:])

    # ---------- 화면
    def _build(self):
        st = ttk.Style(self)
        try: st.theme_use("vista" if os.name == "nt" else "clam")
        except Exception: pass
        st.configure("Big.TButton", padding=(10, 6))

        top = ttk.Frame(self, padding=8); top.pack(fill="x")
        self.lamps = {}
        for key, label in (("bot", "봇"), ("worker", "워커"), ("mumu", "휴대폰"), ("discord", "디스코드")):
            f = ttk.Frame(top); f.pack(side="left", padx=(0, 18))
            c = tk.Canvas(f, width=14, height=14, highlightthickness=0); c.pack(side="left")
            dot = c.create_oval(2, 2, 12, 12, fill="#999", outline="")
            ttk.Label(f, text=label, font=("Malgun Gothic", 10, "bold")).pack(side="left", padx=(4, 2))
            v = ttk.Label(f, text="확인 중", foreground="#666"); v.pack(side="left")
            self.lamps[key] = (c, dot, v)
        ttk.Checkbutton(top, text="꺼지면 자동으로 다시 켜기", variable=self.auto).pack(side="right")

        bar = ttk.Frame(self, padding=(8, 0, 8, 6)); bar.pack(fill="x")
        ttk.Button(bar, text="▶ 전체 시작", style="Big.TButton", command=self.start_all).pack(side="left")
        ttk.Button(bar, text="■ 전체 중지", style="Big.TButton", command=self.stop_all).pack(side="left", padx=4)
        ttk.Separator(bar, orient="vertical").pack(side="left", fill="y", padx=8)
        ttk.Button(bar, text="봇 시작", command=lambda: self.bot.start()).pack(side="left")
        ttk.Button(bar, text="봇 중지", command=lambda: self.bot.stop()).pack(side="left", padx=2)
        ttk.Button(bar, text="워커 시작", command=lambda: self.worker.start()).pack(side="left", padx=(8, 0))
        ttk.Button(bar, text="워커 중지", command=lambda: self.worker.stop()).pack(side="left", padx=2)
        bar = ttk.Frame(self, padding=(8, 0, 8, 6)); bar.pack(fill="x")   # 둘째 줄: 운영 버튼 (창이 좁아도 잘리지 않게)
        ttk.Button(bar, text="⏸ 일시정지", command=lambda: self._cmd("pause")).pack(side="left")
        ttk.Button(bar, text="▶ 재개", command=lambda: self._cmd("resume")).pack(side="left", padx=2)
        ttk.Button(bar, text="🔁 게임 전환", command=self._switch_game).pack(side="left", padx=(8, 0))
        # 한 버튼으로 마감 ↔ 다시 받기 (창이 좁아 두 버튼이면 '신청 받기'가 잘려 안 보였음)
        self.intake_btn = ttk.Button(bar, text="🚫 신청 마감", command=self._toggle_intake); self.intake_btn.pack(side="left", padx=(8, 0))
        try: _off = bool(load_cfg().get("Switch", {}).get("ScreenOff", False))
        except Exception: _off = False
        self.screen_btn = ttk.Button(bar, text="💡 스위치 화면 켜기" if _off else "🌙 스위치 화면 끄기", command=self._toggle_screen); self.screen_btn.pack(side="left", padx=2)
        self.hold_btn = ttk.Button(bar, text="⏸ 스위치 쉬기", command=self._toggle_hold); self.hold_btn.pack(side="left", padx=2)
        self.hybrid_btn = ttk.Button(bar, text="🔀 동시 운영", command=self._toggle_hybrid); self.hybrid_btn.pack(side="left", padx=(8, 0))
        # 셋째 줄: 지금 상태 한눈에 (게임·버전, 같이 받는 게임, 계정 배정, 신청 상태, 진행 수)
        strip = ttk.Frame(self, padding=(10, 0, 10, 6)); strip.pack(fill="x")
        self.state_lbl = ttk.Label(strip, text="봇이 꺼져 있습니다", font=("Malgun Gothic", 10), foreground="#333", wraplength=1120, justify="left")
        self.state_lbl.pack(anchor="w")
        self.state = {}

        nb = ttk.Notebook(self); nb.pack(fill="both", expand=True, padx=8, pady=(0, 8))
        self.status_txt = self._text_tab(nb, "상태")
        qf = ttk.Frame(nb, padding=8); nb.add(qf, text="대기열")
        self._queue_tab(qf)
        self.logs = {"봇": self._text_tab(nb, "봇 로그"), "워커": self._text_tab(nb, "워커 로그")}
        tools = ttk.Frame(nb, padding=8); nb.add(tools, text="시험·도구")
        self._tools(tools); self.tools_tab = tools
        sf = ttk.Frame(nb, padding=12); nb.add(sf, text="설정")
        self._settings_tab(sf)
        self.logs["도구"] = self.tool_txt
        self.nb = nb

    # ---------- 대기열
    def _queue_tab(self, f):
        cols = ("id", "game", "st", "what", "name", "user", "key")
        tv = ttk.Treeview(f, columns=cols, show="headings", selectmode="browse", height=6)
        for c, t, w in (("id", "번호", 60), ("game", "게임", 110), ("st", "상태", 120), ("what", "신청 포켓몬", 280), ("name", "디스코드 이름", 170), ("user", "사용자 ID", 160), ("key", "맡길 포켓몬", 200)):
            tv.heading(c, text=t); tv.column(c, width=w, anchor="w")
        # 버튼·안내를 먼저 아래에 붙이고 표가 남은 공간을 씀 (창이 작아도 버튼이 가려지지 않게)
        ttk.Label(f, text="회색 줄은 이미 스위치·HOME에 올라가 순서를 바꿀 수 없는 신청입니다. 취소하면 사용자에게 DM이 갑니다.", foreground="#666").pack(side="bottom", anchor="w", pady=(4, 0))
        b = ttk.Frame(f); b.pack(side="bottom", fill="x", pady=(8, 0))
        qsb = ttk.Scrollbar(f, orient="vertical", command=tv.yview); tv.configure(yscrollcommand=qsb.set)
        qsb.pack(side="right", fill="y"); tv.pack(fill="both", expand=True)
        tv.tag_configure("fixed", foreground="#888")
        for text, d in (("⏫ 맨 위로", "top"), ("▲ 위로", "up"), ("▼ 아래로", "down"), ("⏬ 맨 아래로", "bottom")):
            ttk.Button(b, text=text, command=lambda d=d: self._q_move(d)).pack(side="left", padx=2)
        ttk.Separator(b, orient="vertical").pack(side="left", fill="y", padx=8)
        ttk.Button(b, text="🗑 선택 신청 취소", command=self._q_cancel).pack(side="left", padx=2)
        ttk.Button(b, text="✅ 교환 완료로 처리", command=self._q_done).pack(side="left", padx=2)
        ttk.Button(b, text="↩ 다시 넣기", command=self._q_requeue).pack(side="left", padx=2)
        ttk.Button(b, text="✉ DM 보내기", command=self._q_dm).pack(side="left", padx=2)
        b2 = ttk.Frame(f); b2.pack(side="bottom", fill="x", pady=(4, 0), before=b)   # 둘째 줄: 초기화 (좁은 창에서도 잘리지 않게)
        ttk.Button(b2, text="대기열 초기화 (대기 중만)", command=lambda: self._q_clear(False)).pack(side="left", padx=2)
        ttk.Button(b2, text="전체 초기화 (교환 중 포함)", command=lambda: self._q_clear(True)).pack(side="left", padx=2)
        self.qtv = tv; self._qsig = None
        self.oq = OfflineQueue(os.path.join(DATA, "homebot.db"))

    def _q_sel(self):
        s = self.qtv.selection()
        return s[0] if s else None

    def _offline(self, fn, *a):
        """봇이 꺼져 있을 때 DB를 직접 고치고 결과를 보여 줌"""
        try: res = fn(*a)
        except Exception as e: res = f"실패: {e}"
        self.q.put(("봇", f"[관리 창 · 봇 꺼짐] {res}\n"))
        self._qsig = None; self._q_refresh(self.oq.rows())
        return res

    def _q_move(self, d):
        i = self._q_sel()
        if not i: return
        if "fixed" in self.qtv.item(i, "tags"):
            messagebox.showinfo("HomeBot", "스위치·HOME에 올라간 신청은 순서를 바꿀 수 없습니다"); return
        if self.bot.running(): self._cmd(f"move:{i}:{d}")
        else: self._offline(self.oq.move, int(i), d)

    def _q_cancel(self):
        i = self._q_sel()
        if not i: return
        on = self.bot.running()
        if messagebox.askyesno("HomeBot", f"#{i} 신청을 취소할까요? " + ("사용자에게 DM이 갑니다." if on else "봇이 꺼져 있어 DM은 봇이 다시 켜질 때 보내요.")):
            why = simpledialog.askstring("취소 사유", "신청자에게 보낼 취소 사유 (비우면 기본 문구)", parent=self) or ""
            if on: self._cmd(f"cancelmsg:{i}:{why}" if why.strip() else f"cancel:{i}")
            else: self._offline(self.oq.cancel, int(i), why.strip() or "운영자가 신청을 취소했습니다")

    def _q_dm(self):
        i = self._q_sel()
        if not i: return
        if not self.bot.running(): messagebox.showinfo("HomeBot", "봇이 켜져 있을 때만 쓸 수 있습니다"); return
        s = simpledialog.askstring("DM 보내기", f"#{i} 신청자에게 보낼 메시지", parent=self)
        if s and messagebox.askyesno("HomeBot", f"#{i} 신청자에게 이 DM을 보낼까요?\n\n{s}"): self._cmd(f"dm:{i}:{s}")

    def _q_done(self):
        i = self._q_sel()
        if not i: return
        if not self.bot.running():
            messagebox.showinfo("HomeBot", "봇이 켜져 있을 때만 쓸 수 있습니다 (완료 DM을 보내야 해서)"); return
        if messagebox.askyesno("HomeBot", f"#{i} 신청을 교환 완료로 처리할까요?\n실제로 교환이 끝났는데 봇이 모르고 계속 찾는 경우에만 쓰세요. 사용자에게 완료 DM이 갑니다."):
            self._cmd(f"done:{i}")

    def _q_requeue(self):
        i = self._q_sel()
        if not i: return
        if not self.bot.running():
            messagebox.showinfo("HomeBot", "봇이 켜져 있을 때만 쓸 수 있습니다"); return
        if messagebox.askyesno("HomeBot", f"#{i} 신청의 포켓몬을 게임 박스에 다시 넣을까요?\n스위치·HOME에 올렸던 포켓몬이 사라졌을 때(직접 옮기거나 놓아준 경우)만 쓰세요."):
            self._cmd(f"requeue:{i}")

    def _q_clear(self, all_):
        msg = "교환 중인 것을 포함해 모든 신청을 취소할까요?" if all_ else "대기 중인 신청을 모두 취소할까요? (지금 교환 중인 것은 그대로)"
        if messagebox.askyesno("HomeBot", msg):
            if self.bot.running(): self._cmd("clear_all" if all_ else "clear")
            else: self._offline(self.oq.clear, all_)

    def _q_refresh(self, rows):
        sig = tuple(rows)
        if sig == self._qsig: return
        self._qsig = sig; sel = self._q_sel()
        self.qtv.delete(*self.qtv.get_children())
        for r in rows:
            p = r.split("\t")
            if len(p) < 6: continue
            name = p[6] if len(p) > 6 else ""; game = p[7] if len(p) > 7 else ""; game = self.GAME_NAMES.get(game, game)
            self.qtv.insert("", "end", iid=p[0], values=("#" + p[0], game, p[1], p[2], name, p[3], p[4]), tags=() if p[5] == "1" else ("fixed",))
        if sel and self.qtv.exists(sel): self.qtv.selection_set(sel)

    # ---------- 설정
    FIELDS = [
        ("스위치", [("Switch.IP", "스위치 IP"), ("Switch.Port", "sys-botbase 포트 (보통 6000)"), ("Switch.ScreenOff", "스위치 화면"),
                  ("Switch.Games", "다룰 게임 (여러 개 고르기)"), ("Switch.Game", "시작 게임 (처음 켤 때만)")]),
        ("세이브 (ftpsrv)", [("Switch.FtpPort", "ftpsrv 포트 (기본 21)"), ("Switch.FtpUser", "ftpsrv 사용자 (anon=1이면 비움)"),
                ("Switch.FtpPass", "ftpsrv 비밀번호 (anon=1이면 비움)"), ("Switch.SaveUser", "세이브 주인 스위치 사용자 이름"), ("Switch.TransferBox", "넣고 옮기는 박스 (1부터)")]),
        ("봇 트레이너", [("Switch.Trainer.OT", "어버이 이름 (한글 6자까지)"), ("Switch.Trainer.TID", "TID"), ("Switch.Trainer.SID", "SID")]),
        ("디스코드", [("Discord.GuildId", "서버 ID"), ("Discord.ChannelId", "신청 채널 ID (0 = 봇이 만듦)"), ("Discord.AdminIds", "운영자 ID (쉼표로 여러 명)")]),
        ("제한", [("Limits.PerUserDaily", "한 사람 하루 신청 수"), ("Limits.PerUserActive", "한 사람 동시 진행 신청 수"), ("Limits.ClaimTimeoutSec", "GTS 검색 제한 시간 (초)"),
                ("Limits.GenSeconds", "생성 제한 시간 (초)"), ("Limits.MoveBatchMax", "한 번에 HOME으로 옮길 수")]),
        ("게임 전환 투표", [("Vote.Enabled", "전환 방식"), ("Vote.Needed", "투표 인원 (명)"), ("Vote.CooldownHours", "전환 뒤 다음 투표까지 (시간)"),
                ("Vote.ChannelId", "투표 채널 ID (0 = 봇이 새로 만듦)")]),
        ("게임 동시 운영 (HOME 계정이 2개 이상일 때)", [("Hybrid.Enabled", "동시 운영"), ("Hybrid.Priority", "같이 열 게임 우선순위 (쉼표, 앞이 먼저: ZA, VL, SH, PLA)"),
                ("Hybrid.PerAccountPerHour", "계정 하나가 한 시간에 처리하는 건수 (기준값)"), ("Hybrid.OpenAfterMinutes", "계정이 남는 상태가 이 분 이어지면 다른 게임 열기"),
                ("Hybrid.CloseAfterMinutes", "계정이 모자란 상태가 이 분 이어지면 같이 연 게임 마감"), ("Hybrid.NoticeMinutes", "열기 몇 분 전에 채널에 예고"),
                ("Hybrid.MinOpenMinutes", "연 게임은 최소 이 분은 유지 (밀린 신청 20건 이상이면 바로 마감)")]),
        ("테스트 서버 (선택)", [("Discord.TestOnly", "테스트 전용 모드"), ("Discord.TestGuildId", "테스트 서버 ID (0 = 안 씀)")]),
    ]

    # 직접 입력 대신 골라서 넣는 칸: 키 → [(보이는 글, 저장 값)]
    CHOICES = {
        "Switch.ScreenOff": [("켜 둠", False), ("봇이 도는 동안 끔 (배터리·발열 절약)", True)],
        "Switch.Game": [("스칼렛·바이올렛 (VL)", "VL"), ("소드·실드 (SH)", "SH"), ("BDSP (BD)", "BD"), ("레츠고 피카츄·이브이 (GP)", "GP"),
                        ("레전즈 아르세우스 (PLA)", "PLA"), ("레전즈 Z-A (ZA)", "ZA")],
        "Vote.Enabled": [("투표 — 투표 채널에서 정한 인원이 모이면 바꿈", True), ("투표 끔 — [🔁 게임 전환]으로만", False)],
        "Discord.TestOnly": [("끔 — 본 서버·테스트 서버 둘 다 받음", False), ("켬 — 테스트 서버에서만 받음", True)],
        "Hybrid.Enabled": [("켬 — 계정이 남으면 다른 게임도 같이 받음", True), ("끔 — 계정 모두 한 게임만", False)],
        "Limits.MoveBatchMax": [("1마리씩", 1), ("6마리 (1줄)", 6), ("12마리 (2줄)", 12), ("18마리 (3줄)", 18), ("24마리 (4줄)", 24), ("30마리 (5줄, 박스 하나 전부 · 레츠고는 24)", 30)],
    }

    def _settings_tab(self, f):
        bf = ttk.Frame(f); bf.pack(side="bottom", fill="x", pady=(8, 0))   # 저장 버튼은 늘 보이게 아래에 고정
        area = ttk.Frame(f); area.pack(fill="both", expand=True)
        inner = self._scroll_area(area)
        self.svars = {}
        for title, items in self.FIELDS:
            lf = ttk.LabelFrame(inner, text=title, padding=8); lf.pack(fill="x", pady=4, padx=2)
            for r, (key, label) in enumerate(items):
                ttk.Label(lf, text=label, width=44).grid(row=r, column=0, sticky="w", pady=2)
                v = tk.StringVar()
                if key == "Switch.Games":   # 여러 개 고르는 드롭다운 (체크 메뉴)
                    mb = ttk.Menubutton(lf, width=36); mb.grid(row=r, column=1, sticky="w", pady=2)
                    m = tk.Menu(mb, tearoff=False); mb["menu"] = m
                    checks = {c: tk.BooleanVar() for c in self.GAME_NAMES}
                    def sync_from_checks(v=v, checks=checks): v.set(", ".join(c for c, b in checks.items() if b.get()))
                    def sync_to_checks(*_, v=v, checks=checks, mb=mb):
                        have = [x.strip().upper() for x in v.get().split(",") if x.strip()]
                        for c, b in checks.items(): b.set(c in have)
                        mb.config(text=" · ".join(self.GAME_NAMES[c] for c in self.GAME_NAMES if c in have) or "고르세요 ▾")
                    for c, name in self.GAME_NAMES.items():
                        m.add_checkbutton(label=name, variable=checks[c], command=sync_from_checks)
                    v.trace_add("write", sync_to_checks)
                elif key in self.CHOICES:
                    ttk.Combobox(lf, textvariable=v, width=38, state="readonly", values=[l for l, _ in self.CHOICES[key]]).grid(row=r, column=1, sticky="w", pady=2)
                else:
                    ttk.Entry(lf, textvariable=v, width=40, show="*" if key.endswith("Pass") else "").grid(row=r, column=1, sticky="w", pady=2)
                self.svars[key] = v
        ttk.Button(bf, text="💾 저장", style="Big.TButton", command=self._save_settings).pack(side="left")
        ttk.Button(bf, text="다시 불러오기", command=self._load_settings).pack(side="left", padx=6)
        ttk.Button(bf, text="🧭 설치 마법사", command=self.open_wizard).pack(side="left", padx=6)
        ttk.Label(bf, text="저장하면 config.json.bak 에 이전 설정이 남습니다. 봇·워커는 다시 시작해야 적용됩니다.", foreground="#666").pack(side="left", padx=8)
        self._load_settings()

    @staticmethod
    def _get(d, path):
        for k in path.split("."):
            d = d.get(k, {}) if isinstance(d, dict) else {}
        return "" if d == {} else d

    def _load_settings(self):
        try: c = load_cfg()
        except Exception as e: messagebox.showerror("HomeBot", f"config.json을 읽지 못했습니다: {e}"); return
        for key, v in self.svars.items():
            if key.startswith("gui."): v.set(str(self.gs.get(key[4:], ""))); continue
            val = self._get(c, key)
            if key in self.CHOICES:
                opts = self.CHOICES[key]
                if val == "": val = {"Discord.TestOnly": False, "Vote.Enabled": True, "Hybrid.Enabled": True}.get(key, opts[0][1])
                if key == "Hybrid.Enabled":   # 관리 명령(hybrid:on/off)으로 바꾼 값이 설정보다 우선
                    hv = self._kv("hybrid")
                    if hv in ("0", "1"): val = hv == "1"
                if key == "Vote.Enabled":   # 관리 명령(vote:on/off)으로 바꾼 값이 설정보다 우선 → 그 값을 보여 줌
                    vm = self._kv("vote_mode")
                    if vm in ("0", "1"): val = vm == "1"
                if key == "Switch.Game": val = str(val).upper()
                hit = next((l for l, x in opts if x == val or str(x).lower() == str(val).lower()), None)
                if hit is None and key == "Limits.MoveBatchMax":   # 예전 값(5·10·15·20 등)은 가장 가까운 줄 단위로 보여 줌 (저장하면 그 값으로)
                    try: hit = min(opts, key=lambda o: (abs(o[1] - int(val)), -o[1]))[0]
                    except (TypeError, ValueError): hit = None
                v.set(hit or opts[0][0]); continue
            if val == "" and key == "Switch.TransferBox": val = 1
            if val == "" and key == "Switch.FtpPort": val = 21
            if val == "" and key.startswith("Hybrid."):
                val = {"Hybrid.Priority": "ZA, VL, SH, PLA", "Hybrid.PerAccountPerHour": 28, "Hybrid.OpenAfterMinutes": 15, "Hybrid.CloseAfterMinutes": 3, "Hybrid.NoticeMinutes": 3, "Hybrid.MinOpenMinutes": 30}.get(key, "")
            v.set(", ".join(str(x) for x in val) if isinstance(val, list) else str(val))

    def _save_settings(self):
        try: c = load_cfg()
        except Exception as e: messagebox.showerror("HomeBot", f"config.json을 읽지 못했습니다: {e}"); return
        ints = {"Switch.Port", "Switch.FtpPort", "Switch.TransferBox", "Switch.Trainer.TID", "Switch.Trainer.SID",
                "Discord.GuildId", "Discord.ChannelId", "Limits.PerUserDaily", "Limits.ClaimTimeoutSec", "Limits.GenSeconds",
                "Limits.MoveBatchMax", "Discord.TestGuildId", "Limits.PerUserActive", "Vote.Needed", "Vote.ChannelId",
                "Hybrid.PerAccountPerHour", "Hybrid.OpenAfterMinutes", "Hybrid.CloseAfterMinutes", "Hybrid.NoticeMinutes", "Hybrid.MinOpenMinutes"}
        import ipaddress
        try:
            ipaddress.ip_address(self.svars["Switch.IP"].get().strip())
        except ValueError:
            messagebox.showerror("HomeBot", "스위치 IP 형식이 올바르지 않습니다 (예: 192.168.1.100)"); return
        for key, v in self.svars.items():
            raw = v.get().strip()
            if key.startswith("gui."): self.gs[key[4:]] = raw; continue
            if key in self.CHOICES:
                val = next((x for l, x in self.CHOICES[key] if l == raw), self.CHOICES[key][0][1])
                d = c; ks = key.split(".")
                for k in ks[:-1]: d = d.setdefault(k, {})
                d[ks[-1]] = val; continue
            try:
                if key in ints: val = int(raw or 0)
                elif key in ("Switch.Games", "Hybrid.Priority"): val = [x.upper() for x in raw.replace(" ", "").split(",") if x]
                elif key == "Discord.AdminIds": val = [int(x) for x in raw.replace(" ", "").split(",") if x]
                elif key == "Vote.CooldownHours": val = float(raw or 2)
                elif key == "Discord.TestOnly": val = raw.lower() in ("true", "1", "켬", "yes")
                else: val = raw
            except ValueError:
                messagebox.showerror("HomeBot", f"'{key}' 값은 숫자여야 합니다"); return
            d = c; ks = key.split(".")
            for k in ks[:-1]: d = d.setdefault(k, {})
            d[ks[-1]] = val
        save_cfg(c)
        # 전환 방식: 봇이 기억한 값(vote_mode)도 같이 맞춤 — 켜져 있으면 명령으로, 꺼져 있으면 DB에 바로
        vote_on = bool(self._get(c, "Vote.Enabled") if self._get(c, "Vote.Enabled") != "" else True)
        if self.bot.running(): self._cmd("vote:on" if vote_on else "vote:off", quiet=True)
        else: self._set_kv("vote_mode", "1" if vote_on else "0")
        hy = self._get(c, "Hybrid.Enabled"); hy_on = True if hy == "" else bool(hy)
        if self.bot.running(): self._cmd("hybrid:on" if hy_on else "hybrid:off", quiet=True)
        else: self._set_kv("hybrid", "1" if hy_on else "0")
        # 시작 게임을 바꿨으면 봇이 기억한 게임도 같이 바꿈 (봇이 꺼져 있을 때만 — 켜져 있으면 [🔁 게임 전환]을 써야 스위치도 같이 바뀜)
        g = str(self._get(c, "Switch.Game") or "").upper()
        if g and not self.bot.running():
            try:
                import sqlite3, datetime
                with sqlite3.connect(os.path.join(DATA, "homebot.db"), timeout=5) as db:
                    db.execute("CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT)")
                    old = (db.execute("SELECT v FROM kv WHERE k='game'").fetchone() or [""])[0]
                    if old != g:
                        db.execute("INSERT INTO kv(k,v) VALUES('game',?) ON CONFLICT(k) DO UPDATE SET v=excluded.v", (g,))
                        db.execute("INSERT INTO kv(k,v) VALUES('game_since',?) ON CONFLICT(k) DO UPDATE SET v=excluded.v", (datetime.datetime.now(datetime.timezone.utc).isoformat(),))
            except Exception as e:
                messagebox.showwarning("HomeBot", f"봇이 기억한 게임을 바꾸지 못했습니다: {e}")
        import json
        with open(GUI_SET, "w", encoding="utf-8") as fp: json.dump(self.gs, fp, ensure_ascii=False, indent=2)
        self._worker_args()
        if (self.bot.running() or self.worker.running()) and messagebox.askyesno("HomeBot", "저장했습니다. 봇과 워커를 지금 다시 시작할까요?"):
            self.stop_all(); self.after(2000, self.start_all)
        else:
            messagebox.showinfo("HomeBot", "저장했습니다")

    def _kv(self, k):
        try:
            import sqlite3
            with sqlite3.connect(os.path.join(DATA, "homebot.db"), timeout=5) as db:
                r = db.execute("SELECT v FROM kv WHERE k=?", (k,)).fetchone()
                return r[0] if r else ""
        except Exception: return ""

    def _set_kv(self, k, v):
        try:
            import sqlite3
            with sqlite3.connect(os.path.join(DATA, "homebot.db"), timeout=5) as db:
                db.execute("CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT)")
                db.execute("INSERT INTO kv(k,v) VALUES(?,?) ON CONFLICT(k) DO UPDATE SET v=excluded.v", (k, v))
        except Exception as e: messagebox.showwarning("HomeBot", f"봇 기록을 바꾸지 못했습니다: {e}")

    def _scroll_area(self, parent):
        """세로로 스크롤되는 칸 (창이 작거나 화면 배율이 커도 아래 버튼까지 닿게). 마우스 휠은 그 칸 위에 있을 때만"""
        canvas = tk.Canvas(parent, highlightthickness=0); sb = ttk.Scrollbar(parent, orient="vertical", command=canvas.yview)
        inner = ttk.Frame(canvas)
        inner.bind("<Configure>", lambda e: canvas.configure(scrollregion=canvas.bbox("all")))
        win = canvas.create_window((0, 0), window=inner, anchor="nw"); canvas.configure(yscrollcommand=sb.set)
        canvas.bind("<Configure>", lambda e: canvas.itemconfigure(win, width=e.width))   # 안쪽 칸 폭을 창에 맞춤
        sb.pack(side="right", fill="y"); canvas.pack(side="left", fill="both", expand=True)
        def wheel(e):
            if canvas.yview() != (0.0, 1.0): canvas.yview_scroll(int(-e.delta / 120) or (-1 if e.delta > 0 else 1), "units")
        canvas.bind("<Enter>", lambda e: canvas.bind_all("<MouseWheel>", wheel))
        canvas.bind("<Leave>", lambda e: canvas.unbind_all("<MouseWheel>"))
        return inner

    def _text_tab(self, nb, title):
        f = ttk.Frame(nb); nb.add(f, text=title)
        t = tk.Text(f, wrap="word", font=("Consolas", 10), bg="#111418", fg="#d8dee9", insertbackground="#fff", relief="flat", padx=8, pady=6, height=8)   # height는 최소 크기 (창에 맞춰 늘어남)
        sb = ttk.Scrollbar(f, command=t.yview); t.configure(yscrollcommand=sb.set)
        sb.pack(side="right", fill="y"); t.pack(fill="both", expand=True)
        t.tag_configure("err", foreground="#ff7b72"); t.tag_configure("ok", foreground="#7ee787"); t.tag_configure("sep", foreground="#8b949e")
        return t

    def _tools(self, f):
        lwrap = ttk.Frame(f, width=300); lwrap.pack(side="left", fill="y", padx=(0, 10)); lwrap.pack_propagate(False)
        left = self._scroll_area(lwrap)
        def sec(text): ttk.Label(left, text=text, font=("Malgun Gothic", 10, "bold")).pack(anchor="w", pady=(10, 2))
        def btn(text, cmd): ttk.Button(left, text=text, width=26, command=cmd).pack(anchor="w", pady=1)
        sec("운영 (봇이 켜져 있을 때)")
        btn("📢 신청 채널에 공지 보내기", self.t_announce)
        btn("📷 스위치 화면 보기", self.t_sw_shot)
        btn("🎮 게임 로고 자동 등록 (HOME에서)", self.t_logo_scan)
        btn("🎮 게임 로고 직접 등록 (화면에서)", self.t_logo)
        btn("🔁 전환 투표 초기화", lambda: messagebox.askyesno("HomeBot", "전환 투표 수를 0으로 되돌릴까요?") and self._cmd("vote:reset"))
        btn("🎮 스위치 버튼 보내기 (고급)", self.t_sw_keys)
        btn("📱 휴대폰 워커 다시 켜기", lambda: self._cmd("worker_restart"))
        sec("생성")
        btn("생성 시험 (문장 입력)", self.t_gen)
        sec("스위치  (봇을 끄고 쓰세요)")
        btn("스위치 화면 기록 시작", self.t_rec_switch)
        sec("휴대폰 (HOME 계정 1)")
        btn("GTS 검색 시험 (교환 안 함)", self.t_gts)
        btn("휴대폰 화면 기록 시작", self.t_rec_phone)
        btn("화면 목록 확인", self.t_displays)
        btn("휴대폰 연결 확인", self.t_emu)
        btn("⌨ 한글 입력기 설치 (모든 계정 폰)", lambda: self._run_tool("입력기", [PY, "-u", "ime_setup.py", "install"], PW))
        sec("도구 중지")
        btn("■ 실행 중인 도구 멈추기", self.t_stop)
        sec("설정·폴더")
        btn("🧭 설치 마법사 (토큰·스위치·계정)", self.open_wizard)
        btn("설정 파일 열기 (config.json)", lambda: self._open(CFG, notepad=True))
        btn("데이터 폴더 열기", lambda: self._open(DATA))
        btn("실패 화면 폴더 열기", lambda: self._open(os.path.join(PW, "shots")))
        right = ttk.Frame(f); right.pack(side="left", fill="both", expand=True)
        ttk.Label(right, text="도구 출력").pack(anchor="w")
        t = tk.Text(right, wrap="word", font=("Consolas", 10), bg="#111418", fg="#d8dee9", relief="flat", padx=8, pady=6, height=8)
        sb = ttk.Scrollbar(right, command=t.yview); t.configure(yscrollcommand=sb.set)
        sb.pack(side="right", fill="y"); t.pack(fill="both", expand=True)
        t.tag_configure("err", foreground="#ff7b72"); t.tag_configure("ok", foreground="#7ee787"); t.tag_configure("sep", foreground="#8b949e")
        self.tool_txt = t

    def _worker_args(self):
        """HOME 계정 1의 휴대폰은 인자로, 2·3은 환경 변수로 (config.json Home.Accounts). 없으면 관리 창 설정"""
        try: accts = (load_cfg().get("Home", {}) or {}).get("Accounts") or []
        except Exception: accts = []
        ser, usr = self.gs.get("serial", "127.0.0.1:16384"), self.gs.get("user", "")
        env = {}
        if accts:
            ser = accts[0].get("Phone") or ser
            usr = "" if accts[0].get("AndroidUser") is None else str(accts[0]["AndroidUser"])
            for k, a in enumerate(accts[1:MAX_ACCTS], start=2):
                if a.get("Phone"): env[f"PHONE{k}_SERIAL"] = a["Phone"]
                if a.get("AndroidUser") is not None: env[f"PHONE{k}_USER"] = str(a["AndroidUser"])
        self.gs["serial"], self.gs["user"] = ser, usr
        port = worker_url().rsplit(":", 1)[-1]
        self.worker.args = [PY, "-u", "server.py", "--serial", ser] + (["--user", usr] if str(usr).strip() else []) + (["--port", port] if port.isdigit() else [])
        self.worker.extra_env = env
        self.worker.phones = [ser] + [a.get("Phone", "") for a in accts[1:MAX_ACCTS] if a.get("Phone")]

    def open_wizard(self):
        try:
            sys.path.insert(0, HERE); import wizard
            w = wizard.Wizard(self, load_cfg(), save_cfg, on_done=lambda: (self._worker_args(), self._load_settings()))
            w.lift(); w.focus_force()
        except Exception as e:
            messagebox.showerror("HomeBot", f"설치 마법사를 열지 못했습니다: {e}")

    # ---------- 동작
    def start_all(self):
        self.worker.start()
        self.after(4000, self.bot.start)   # 워커가 먼저 떠야 봇이 화면 인식에 연결

    def stop_all(self):
        self.bot.stop(); self.worker.stop()

    def _toggle_intake(self):
        if not self.bot.running(): return self._cmd("")
        if self._kv("intake_closed") == "1":
            if messagebox.askyesno("HomeBot", "지금은 신청을 마감한 상태예요. 새 신청을 다시 받을까요?"): self._cmd("intake:open"); self.intake_btn.config(text="🚫 신청 마감")
        elif messagebox.askyesno("HomeBot", "새 신청을 그만 받을까요?\n지금 대기열에 있는 신청까지만 처리합니다."):
            self._cmd("intake:close"); self.intake_btn.config(text="✅ 신청 다시 받기")

    def _toggle_screen(self):
        # 스위치 화면(백라이트)만 끄고 켬. 게임·조작·화면 인식은 그대로. 설정에도 저장해 봇을 다시 켜도 유지
        try: c = load_cfg(); off = not bool(c.get("Switch", {}).get("ScreenOff", False)); c.setdefault("Switch", {})["ScreenOff"] = off; save_cfg(c)
        except Exception as e: messagebox.showerror("HomeBot", f"설정 저장 실패: {e}"); return
        self._cmd("screen:off" if off else "screen:on", quiet=True)
        self.screen_btn.config(text="💡 스위치 화면 켜기" if off else "🌙 스위치 화면 끄기")
        if "Switch.ScreenOff" in getattr(self, "svars", {}):
            opts = self.CHOICES["Switch.ScreenOff"]; self.svars["Switch.ScreenOff"].set(next(l for l, x in opts if x == off))

    def _toggle_hold(self):
        if not self.bot.running(): return self._cmd("")
        # 스위치만 쉬게 함 (휴대폰 GTS 교환은 계속) — 스위치를 잠깐 다른 용도로 쓸 때
        if self._kv("switch_hold") == "1":
            self._cmd("switch:resume"); self.hold_btn.config(text="⏸ 스위치 쉬기")
        elif messagebox.askyesno("HomeBot", "스위치를 쉬게 할까요?\n지금 하던 작업만 마치고 쉬어요. 휴대폰 GTS 교환은 계속돼요.\n(새 신청도 막으려면 [🚫 신청 마감]을 같이 누르세요)"):
            self._cmd("switch:hold"); self.hold_btn.config(text="▶ 스위치 다시 쓰기")

    GAME_NAMES = {"VL": "스칼렛·바이올렛", "SH": "소드·실드", "BD": "BDSP", "GP": "레츠고 피카츄·이브이", "PLA": "레전즈 아르세우스", "ZA": "레전즈 Z-A"}
    VER_NAMES = {"SL": "스칼렛", "VL": "바이올렛", "SW": "소드", "SH": "실드", "BD": "브릴리언트 다이아몬드", "SP": "샤이닝 펄",
                 "GP": "레츠고 피카츄", "GE": "레츠고 이브이", "PLA": "레전즈 아르세우스", "ZA": "레전즈 Z-A"}

    def _toggle_hybrid(self):
        if not self.bot.running(): return self._cmd("")
        st = self.state or {}
        if st.get("accounts", 1) < 2:
            messagebox.showinfo("HomeBot", "HOME 계정이 하나라 게임을 나눠 받을 수 없어요. (계정이 2개 이상이면 남는 계정으로 다른 게임을 같이 받습니다)"); return
        if st.get("hybrid"):
            if messagebox.askyesno("HomeBot", "게임 동시 운영을 끌까요?\n같이 받던 게임은 마감하고, 받은 신청을 끝낸 뒤 계정을 기본 게임으로 모아요."):
                self._cmd("hybrid:off"); self.hybrid_btn.config(text="🔀 동시 운영 켜기")
        elif messagebox.askyesno("HomeBot", "게임 동시 운영을 켤까요?\n기본 게임 신청이 적어 계정이 남으면 다른 게임(우선순위: 설정)을 같이 받고, 신청이 많아지면 다시 모아요."):
            self._cmd("hybrid:on"); self.hybrid_btn.config(text="🔀 동시 운영 끄기")

    def _state_line(self, st):
        if not st: return "봇이 켜지는 중입니다" if self.bot.running() else "봇이 꺼져 있습니다. [▶ 전체 시작]을 누르세요."
        g = f"🎮 지금 게임: {st.get('gameKo', '')}"
        if st.get("verKo") and st.get("verKo") != st.get("gameKo"): g += f" ({st['verKo']})"
        if st.get("accounts", 1) > 1: g += f" · 계정 {','.join(str(a) for a in st.get('mainAccts', []))}"
        parts = [g]
        for sd in st.get("sides") or []:
            parts.append(f"🔀 같이 받는 중: {sd['ko']} 계정 {','.join(str(a) for a in sd.get('accts', []))}" + (" (마감 중)" if sd.get("closing") else ""))
        if st.get("accounts", 1) > 1: parts.append("동시 운영 " + ("켬" if st.get("hybrid") else "끔"))
        if st.get("switchRequested"): parts.append(f"🔁 {st.get('switchToKo')}로 바꾸는 중 (받은 신청을 끝낸 뒤)")
        elif st.get("voteMode"):
            parts.append(f"투표 {st.get('voteOpenAt')}부터" if st.get("voteOpenAt") else f"전환 투표 {st.get('votes', 0)}/{st.get('voteNeeded', 0)}")
        if st.get("preOrder"): parts.append(f"{self.GAME_NAMES.get(st['preOrder'], st['preOrder'])} 미리 신청 받는 중")
        parts.append("⏸ 일시정지" if st.get("paused") else ("🚫 신청 마감" if st.get("intakeClosed") else "✅ 신청 받는 중"))
        if st.get("switchHold"): parts.append("스위치 쉬는 중")
        parts.append(f"진행 {st.get('active', 0)}건" + (f" (다른 게임 보류 {st['heldOther']}건)" if st.get("heldOther") else "") + f" · 최근 1시간 교환 {st.get('tradesHour', 0)}건")
        line = "  |  ".join(parts)
        if st.get("lastError"): line += f"\n⚠ {st['lastError']}"
        return line

    def _switch_game(self):
        if not self.bot.running(): return self._cmd("")
        try: games = [g.upper() for g in (load_cfg().get("Switch", {}).get("Games") or [])] or list(self.GAME_NAMES)
        except Exception: games = list(self.GAME_NAMES)
        st = self.state or {}; cur = st.get("game"); sides = {s["game"]: s for s in st.get("sides") or []}
        w = tk.Toplevel(self); w.title("게임 바꾸기"); w.transient(self); w.grab_set(); w.resizable(False, False)
        ttk.Label(w, text=f"지금 게임: {st.get('gameKo', '?')}\n바꿀 게임을 고르세요. 새 신청은 바로 막고, 받은 신청이 모두 끝나면 그 게임으로 바뀌어요 (봇을 다시 켜지 않음).", padding=12).pack()
        f = ttk.Frame(w, padding=(12, 0, 12, 12)); f.pack()
        def pick(code): w.destroy(); self._cmd(f"game:switch:{code}")
        for code in games:
            txt = self.GAME_NAMES.get(code, code)
            if code == cur: txt += " (지금)"
            elif code in sides: txt += " (같이 받는 중)"
            ttk.Button(f, text=txt, command=lambda c=code: pick(c), state="disabled" if code == cur else "normal").pack(side="left", padx=3)

    def _cmd(self, c, quiet=False):
        if not self.bot.running():
            if not quiet: messagebox.showinfo("HomeBot", "봇이 꺼져 있습니다")
            return
        os.makedirs(DATA, exist_ok=True)
        with open(os.path.join(DATA, "cmd.txt"), "a", encoding="utf-8") as fp: fp.write(c + "\n")

    def _run_tool(self, name, args, cwd, need_bot_off=False):
        if self.tool and self.tool.running():
            messagebox.showwarning("HomeBot", "다른 도구가 실행 중입니다. 먼저 멈추세요."); return
        if need_bot_off and self.bot.running():
            if not messagebox.askyesno("HomeBot", "스위치 연결은 하나만 쓸 수 있어 봇을 꺼야 합니다. 봇을 끄고 진행할까요?"): return
            self.bot.stop()
            try: self.bot.p.wait(timeout=12)
            except Exception: self.bot.stop_now()
        self.tool = Proc("도구", args, cwd, self.q)
        self.tool.start()
        self.nb.select(self.tools_tab)

    def t_gen(self):
        s = simpledialog.askstring("생성 시험", "신청 문장 (예: 피카츄 암컷 이로치 6V)", parent=self)
        if s: self._run_tool("생성", self.dotnet_run + ["gen", s, CFG], HB)

    def t_rec_switch(self):
        self._run_tool("스위치 기록", self.dotnet_run + ["record-switch", CFG], HB, need_bot_off=True)

    def t_gts(self):
        name = simpledialog.askstring("GTS 검색 시험", "GTS에서 찾아볼 포켓몬 영어 이름 (예: Pikachu). 교환은 하지 않고 검색 화면까지만 가 봅니다.", parent=self)
        if not name: return
        self._run_tool("GTS 시험", [PY, "-u", "gts_test.py", "--serial", self.gs["serial"], "--name", name.strip(), "--dry"] + (["--user", str(self.gs["user"])] if str(self.gs.get("user", "")).strip() else []), PW)

    def t_rec_phone(self):
        self._run_tool("휴대폰 기록", [PY, "-u", "record.py", "--serial", self.gs["serial"], "--out", os.path.join(DATA, "record_phone")] + (["--user", str(self.gs["user"])] if str(self.gs.get("user", "")).strip() else []), PW)

    def t_displays(self):
        self._run_tool("화면 목록", [PY, "-u", "displays.py", os.path.join(DATA, "displays"), self.gs["serial"]], PW)

    def t_emu(self):
        phones = [p for p in getattr(self.worker, "phones", []) or [self.gs["serial"]] if p]
        code = ("import subprocess as s\n"
                f"for p in {phones!r}:\n"
                "    if ':' in p: print(s.run(['adb','connect',p],capture_output=True,text=True).stdout.strip())\n"
                "print(s.run(['adb','devices','-l'],capture_output=True,text=True).stdout)\n"
                f"for p in {phones!r}: print(p, s.run(['adb','-s',p,'shell','wm','size'],capture_output=True,text=True).stdout.strip())\n")
        self._run_tool("에뮬레이터", [PY, "-u", "-c", code], PW)

    def t_announce(self):
        if not self.bot.running(): messagebox.showinfo("HomeBot", "봇이 꺼져 있습니다"); return
        s = simpledialog.askstring("공지", "모든 게임의 신청 채널에 올릴 글 (줄바꿈은 \\n)", parent=self)
        if s and messagebox.askyesno("HomeBot", f"이 글을 신청 채널에 올릴까요?\n\n{s.replace(chr(92) + 'n', chr(10))}"):
            self._cmd("announce:" + s.replace("\n", "\\n"))

    def t_sw_shot(self):
        if not self.bot.running(): messagebox.showinfo("HomeBot", "봇이 꺼져 있습니다"); return
        p = os.path.join(DATA, "shots", "switch_now.jpg")
        before = os.path.getmtime(p) if os.path.exists(p) else 0
        self._cmd("sw_shot")
        def wait(n=0):
            if os.path.exists(p) and os.path.getmtime(p) > before: self._open_file(p)
            elif n < 20: self.after(500, lambda: wait(n + 1))
            else: messagebox.showwarning("HomeBot", "스위치 화면을 받지 못했습니다 (스위치 연결 확인)")
        self.after(500, wait)

    GS_TPL = {"SL": "home_gs_scarlet", "VL": "home_gs_violet", "SW": "home_gs_sw", "SH": "home_gs_sh", "BD": "home_gs_bd", "SP": "home_gs_sp",
              "GP": "home_gs_gp", "GE": "home_gs_ge", "PLA": "home_gs_la", "ZA": "home_gs_za"}

    def t_logo_scan(self):
        if not self.bot.running(): return self._cmd("")
        if messagebox.askyesno("HomeBot", "다루는 게임 중 로고 그림이 없는 게임이 있으면, 봇이 HOME 계정 1로 게임 선택 화면에 들어가 로고를 잘라 둡니다.\n(지금 하던 HOME 작업이 끝난 뒤 시작, 몇 분 걸림) 진행할까요?"):
            self._cmd("logo:scan"); self.nb.select(self.logs["봇"].master)

    def t_logo(self):
        """HOME 게임 선택 화면 왼쪽의 게임 로고를 내 스위치 화면에서 잘라 등록 (스칼렛·소드·펄·이브이나 다른 언어 로고일 때)"""
        if not self.bot.running(): messagebox.showinfo("HomeBot", "봇이 켜져 있어야 스위치 화면을 받을 수 있어요"); return
        st = self.state or {}
        cur = st.get("ver") or st.get("game") or ""
        code = simpledialog.askstring("게임 로고 직접 등록",
            "보통은 봇이 처음 HOME에 들어갈 때 알아서 등록합니다. 자동 등록이 안 됐을 때만 쓰세요.\n\n"
            "1) [⏸ 스위치 쉬기]를 누르고 '쉬는 중'이 될 때까지 기다리세요\n"
            "2) 조이콘으로 HOME → 사용자 → '연동할 게임 소프트웨어를 선택' 화면까지 가서\n   커서를 봇이 쓸 게임에 두세요 (왼쪽에 그 게임 로고가 크게 보이는 상태)\n"
            "3) 아래에 버전 코드를 넣고 확인: " + ", ".join(f"{k}={v}" for k, v in self.VER_NAMES.items()), initialvalue=cur, parent=self)
        code = (code or "").strip().upper()
        if code not in self.GS_TPL: return
        p = os.path.join(DATA, "shots", "switch_now.jpg")
        before = os.path.getmtime(p) if os.path.exists(p) else 0
        self._cmd("sw_shot")
        def wait(n=0):
            if os.path.exists(p) and os.path.getmtime(p) > before:
                self.after(700, lambda: self._save_logo(p, code))   # 봇이 다 쓸 때까지 잠깐 기다림
            elif n < 20: self.after(500, lambda: wait(n + 1))
            else: messagebox.showwarning("HomeBot", "스위치 화면을 받지 못했습니다")
        self.after(500, wait)

    def _save_logo(self, shot, code):
        try:
            import cv2, numpy as np, json
            img = cv2.imdecode(np.fromfile(shot, np.uint8), cv2.IMREAD_COLOR)
            if img is None: raise RuntimeError("스위치 화면 그림을 읽지 못했어요. 한 번 더 해 보세요")
            img = cv2.resize(img, (1280, 720)) if img.shape[:2] != (720, 1280) else img
            x, y, w, h = 50, 170, 580, 260                      # 게임 선택 화면 왼쪽 정보 칸 (매크로가 로고를 찾는 범위)
            reg = img[y:y + h, x:x + w]
            border = np.concatenate([reg[0], reg[-1], reg[:, 0], reg[:, -1]])
            bg = np.median(border, axis=0)
            ys, xs = np.where(np.abs(reg.astype(int) - bg).sum(axis=2) > 60)
            if len(xs) < 300: raise RuntimeError("로고를 찾지 못했어요. 게임 선택 화면에서 커서가 게임에 있는지 확인하세요")
            x0, x1 = max(0, xs.min() - 4), min(w, xs.max() + 5); y0, y1 = max(0, ys.min() - 4), min(h, ys.max() + 5)
            if x1 - x0 < 60 or y1 - y0 < 30: raise RuntimeError("잘린 로고가 너무 작아요. 화면을 확인하세요")
            out = os.path.join(PW, "templates", "switch", self.GS_TPL[code] + ".png")
            if os.path.exists(out): shutil.copyfile(out, out[:-4] + ".old.png")
            with open(out[:-4] + ".json", "w", encoding="utf-8") as fp: json.dump({"region": [x, y, w, h], "threshold": 0.85}, fp)
            ok, buf = cv2.imencode(".png", reg[y0:y1, x0:x1]); buf.tofile(out)   # png를 나중에 (화면 인식이 png가 바뀌면 다시 읽음)
            self._open_file(out)
            messagebox.showinfo("HomeBot", f"{self.VER_NAMES[code]} 로고를 등록했어요 (열린 그림이 게임 로고인지 확인).\n"
                                "조이콘으로 HOME을 닫고 [▶ 스위치 다시 쓰기]를 누르세요. 예전 그림은 .old.png로 남겨 뒀어요.")
        except Exception as e:
            messagebox.showerror("HomeBot", f"로고 등록 실패: {e}")

    def t_sw_keys(self):
        if not self.bot.running(): messagebox.showinfo("HomeBot", "봇이 꺼져 있습니다"); return
        s = simpledialog.askstring("스위치 버튼", "누를 버튼을 쉼표로 (예: B,B,HOME / A,wait1000,DDOWN)\n봇이 일하는 중에 누르면 진행이 꼬일 수 있으니 일시정지한 뒤에 쓰세요.", parent=self)
        if s: self._cmd("sw_keys:" + s.replace(" ", ""))

    def _open_file(self, p):
        try:
            if os.name == "nt": os.startfile(p)
        except Exception as e: messagebox.showerror("HomeBot", str(e))

    def t_stop(self):
        if self.tool and self.tool.running():
            self.tool.stop()

    def _open(self, path, notepad=False):
        if notepad:
            subprocess.Popen(["notepad", path]); return
        os.makedirs(path, exist_ok=True)
        if os.name == "nt": os.startfile(path)

    # ---------- 주기 확인
    def _ping(self):
        return self._ping_info() is not None

    def _ping_info(self):
        """워커 /ping 응답(JSON dict). 응답 없으면 None."""
        import json
        try:
            with urllib.request.urlopen(worker_url() + "/ping", timeout=1) as r:
                if r.status != 200: return None
                try: return json.loads(r.read().decode("utf-8", "ignore")) or {}
                except Exception: return {}
        except Exception: return None

    def _lamp(self, key, color, text):
        c, dot, v = self.lamps[key]; c.itemconfigure(dot, fill=color); v.configure(text=text)

    def _tick(self):
        threading.Thread(target=self._check, daemon=True).start()
        self.after(1500, self._tick)

    def _check(self):
        bot_on = self.bot.running(); w_on = self.worker.running(); info = self._ping_info(); ping = info is not None
        self._watchdog(info)
        mumu = None
        if shutil.which("adb"):
            try:
                r = subprocess.run(["adb", "-s", self.gs["serial"], "shell", "getprop", "sys.boot_completed"], capture_output=True, timeout=4, creationflags=NO_WINDOW)
                mumu = r.stdout.strip() == b"1"
            except Exception: mumu = False
        status = ""
        try:
            p = os.path.join(DATA, "status.txt")
            if os.path.exists(p) and time.time() - os.path.getmtime(p) < 15:
                with open(p, encoding="utf-8") as fp: status = fp.read()
        except Exception: pass
        st = {}
        try:
            sp = os.path.join(DATA, "state.json")
            if bot_on and os.path.exists(sp) and time.time() - os.path.getmtime(sp) < 15:
                import json as _j
                with open(sp, encoding="utf-8") as fp: st = _j.load(fp)
        except Exception: st = self.state if bot_on else {}   # 쓰는 도중 읽혔으면 지난 값 유지
        self.state = st
        rows = []
        try:
            qp = os.path.join(DATA, "queue.tsv")
            if bot_on and os.path.exists(qp) and time.time() - os.path.getmtime(qp) < 15:
                with open(qp, encoding="utf-8") as fp: rows = [l for l in fp.read().splitlines() if l]
            elif not bot_on:
                rows = self.oq.rows()                       # 봇이 꺼져 있으면 DB에서 바로 읽음
        except Exception: pass
        self.after(0, self._show, bot_on, w_on, ping, mumu, status)
        self.after(0, self._q_refresh, rows)

    WORKER_HANG = 600   # 한 계정 작업이 이만큼(초) 넘게 안 끝나면 워커가 멈춘 것으로 보고 다시 켬

    def _watchdog(self, info):
        """워커 작업이 10분 넘게 안 끝나면 (잠금이 풀리지 않아 다음 작업도 다 막힘) 워커를 강제로 끔 → 아래 자동 재시작이 다시 켬."""
        try:
            busy = (info or {}).get("busy_since") or {}
            if not busy or not self.worker.running(): return
            acct, since = max(((k, float(v)) for k, v in busy.items()), key=lambda z: time.time() - z[1])
            if time.time() - since > self.WORKER_HANG:
                self.q.put((self.worker.name, f"──── {time.strftime('%H:%M:%S')} 워커 작업이 {int((time.time() - since) // 60)}분째 안 끝남 (계정 {acct}) → 워커를 강제로 끄고 다시 켭니다\n"))
                kill_tree(self.worker.p)                     # _stopped_by_user는 그대로(False) → 자동 재시작
        except Exception: pass

    def _show(self, bot_on, w_on, ping, mumu, status):
        try:
            st = self.state or {}
            closed = st.get("intakeClosed") if st else self._kv("intake_closed") == "1"
            hold = st.get("switchHold") if st else self._kv("switch_hold") == "1"
            self.intake_btn.config(text="✅ 신청 다시 받기" if closed else "🚫 신청 마감")
            self.hold_btn.config(text="▶ 스위치 다시 쓰기" if hold else "⏸ 스위치 쉬기")
            self.hybrid_btn.config(text=("🔀 동시 운영 끄기" if st.get("hybrid") else "🔀 동시 운영 켜기") if st else "🔀 동시 운영")
            self.state_lbl.config(text=self._state_line(st), foreground="#a00" if st.get("lastError") else "#333")
        except Exception: pass
        self._lamp("bot", "#2ea043" if bot_on else "#8b949e", "실행 중" if bot_on else "꺼짐")
        self._lamp("worker", "#2ea043" if ping else ("#d29922" if w_on else "#8b949e"), "응답함" if ping else ("켜는 중" if w_on else "꺼짐"))
        self._lamp("mumu", "#8b949e" if mumu is None else ("#2ea043" if mumu else "#f85149"), "adb 없음" if mumu is None else ("켜짐" if mumu else "응답 없음"))
        disc = "Connected" in status
        self._lamp("discord", "#2ea043" if disc else "#8b949e", "연결됨" if disc else ("확인 중" if bot_on else "-"))
        self.status_txt.configure(state="normal"); self.status_txt.delete("1.0", "end")
        self.status_txt.insert("end", status or ("봇이 꺼져 있습니다. [▶ 전체 시작]을 누르세요." if not bot_on else "봇이 켜지는 중입니다"))
        self.status_txt.configure(state="disabled")
        if self.auto.get():
            for p in (self.worker, self.bot):
                if p.p is not None and not p.running() and time.time() - p.started > 30 and not getattr(p, "_stopped_by_user", False):
                    self.q.put((p.name, f"──── 꺼져서 다시 켭니다\n")); p.start()

    def _drain(self):
        try:
            for _ in range(400):
                name, line = self.q.get_nowait()
                t = self.logs.get(name)
                if t is None: continue
                tag = "sep" if line.startswith("────") else ("err" if any(k in line for k in ("오류", "실패", "Error", "Exception", "error")) else ("ok" if any(k in line for k in ("완료", "성공", "연결됨")) else None))
                t.insert("end", line, tag)
                if int(t.index("end-1c").split(".")[0]) > 4000: t.delete("1.0", "500.0")
                t.see("end")
        except queue.Empty:
            pass
        self.after(200, self._drain)

    def _close(self):
        if (self.bot.running() or self.worker.running()) and not messagebox.askyesno("HomeBot", "창을 닫으면 봇과 워커도 함께 꺼집니다. 닫을까요?"):
            return
        for p in (self.worker, self.tool):
            if p: p.stop()
        if self.bot.running():
            self.bot.stop()
            try: self.bot.p.wait(timeout=12)
            except Exception: self.bot.stop_now()
        self.destroy()


if __name__ == "__main__":
    try:
        App().mainloop()
    except Exception:
        import traceback
        err = traceback.format_exc()
        with open(os.path.join(HERE, "error.log"), "w", encoding="utf-8") as fp: fp.write(err)
        try: messagebox.showerror("HomeBot 관리 오류", err[-1500:])
        except Exception: pass
