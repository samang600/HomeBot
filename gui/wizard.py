"""HomeBot 설치 마법사 — 처음 켤 때(또는 설정 탭의 버튼으로) 꼭 필요한 것만 묻고 config.json을 씀.

1) 디스코드: 봇 토큰(윈도우 사용자 환경 변수 DISCORD_TOKEN에만 저장, 파일에 쓰지 않음)·서버 ID·운영자 ID
2) 스위치: IP → sys-botbase(6000)·ftpsrv(21) 연결 확인 → FTP로 스위치 사용자와 게임 세이브 목록을 읽음
3) 세이브 주인(게임 세이브가 있는 사용자)과 다룰 게임, HOME 계정 1~3개(스위치 사용자 + 휴대폰)
표준 라이브러리만 씀 (tkinter, ftplib, socket, subprocess).
"""
import ftplib, os, re, shutil, socket, subprocess, threading
import tkinter as tk
from tkinter import ttk, messagebox

GAMES = [("VL", "스칼렛·바이올렛", ("0100A3D008C5C000", "01008F6008C5E000")),
         ("SH", "소드·실드", ("0100ABF008968000", "01008DB008C2C000")),
         ("BD", "브릴리언트 다이아몬드·샤이닝 펄", ("0100000011D90000", "010018E011D92000")),
         ("GP", "레츠고 피카츄·이브이", ("010003F003A34000", "0100187003A36000")),
         ("PLA", "레전즈 아르세우스", ("01001F5010DFA000",)),
         ("ZA", "레전즈 Z-A", ("0100F43008C44000",))]
HOME_TID = "010015F008C54000"
NO_WINDOW = 0x08000000 if os.name == "nt" else 0


def ftp_scan(ip, user="", pw=""):
    """ftpsrv로 사용자 목록과 사용자마다 있는 세이브(타이틀 ID)를 읽음 → (포트, [(이름, {타이틀ID})])"""
    last = None
    for port in (21, 5000):
        try:
            f = ftplib.FTP(); f.encoding = "utf-8"
            f.connect(ip, port, timeout=6); f.login(user or "anonymous", pw)
            try: f.sendcmd("OPTS UTF8 ON")
            except Exception: pass
            top = [n.strip("/").split("/")[-1] for n in f.nlst("/")]
            save = next((n for n in top if n.rstrip(":").lower() == "save"), None)
            if not save:
                f.quit(); raise RuntimeError("ftpsrv는 켜져 있지만 save: 가 없습니다 → config.ini [Nx] mount_devices=1")
            root = "/" + save.rstrip(":") + ":"
            users = []
            for u in f.nlst(root):
                u = u.strip("/").split("/")[-1]
                if "[" not in u or len(u) < 34: continue
                try: apps = f.nlst(f"{root}/{u}/files")
                except Exception: apps = []
                tids = set(m.upper() for a in apps for m in re.findall(r"\[([0-9A-Fa-f]{16})\]", a))
                users.append((u[:u.rindex(" [")] if " [" in u else u, tids))
            f.quit()
            return port, users
        except (OSError, ftplib.Error) as e:
            last = e
    raise RuntimeError(f"ftpsrv에 연결하지 못했습니다 ({last}). 스위치에 ftpsrv가 깔려 있는지, config.ini(anon=1 또는 user/pass)와 재부팅을 확인하세요")


def port_open(ip, port, timeout=3):
    try:
        with socket.create_connection((ip, port), timeout=timeout): return True
    except OSError:
        return False


def adb_devices():
    """연결된 휴대폰·에뮬레이터 (MuMu 기기 주소도 연결해 봄)"""
    if not shutil.which("adb"): return []
    for p in (16384, 16416, 16448, 16480):
        try: subprocess.run(["adb", "connect", f"127.0.0.1:{p}"], capture_output=True, timeout=4, creationflags=NO_WINDOW)
        except Exception: pass
    try:
        out = subprocess.run(["adb", "devices", "-l"], capture_output=True, text=True, timeout=8, creationflags=NO_WINDOW).stdout
    except Exception:
        return []
    devs = []
    for line in out.splitlines()[1:]:
        p = line.split()
        if len(p) >= 2 and p[1] == "device":
            model = next((x[6:] for x in p if x.startswith("model:")), "")
            devs.append(f"{p[0]}  ({model})" if model else p[0])
    return devs


def set_user_env(name, value):
    """윈도우 사용자 환경 변수 (setx). 다음에 켜는 프로그램부터 적용 — 관리 창은 레지스트리에서 바로 읽음"""
    if os.name != "nt": return False
    r = subprocess.run(["setx", name, value], capture_output=True, creationflags=NO_WINDOW)
    return r.returncode == 0


class Wizard(tk.Toplevel):
    def __init__(self, master, cfg, save_cfg, on_done=None):
        super().__init__(master)
        self.title("HomeBot 설치 마법사"); self.geometry("880x760"); self.transient(master)
        self.cfg, self.save_cfg, self.on_done = cfg, save_cfg, on_done
        self.users = []          # [(이름, {타이틀ID})]
        self.ftp_port = (cfg.get("Switch", {}) or {}).get("FtpPort", 21) or 21
        d, s = cfg.setdefault("Discord", {}), cfg.setdefault("Switch", {})
        cv = tk.Canvas(self, highlightthickness=0); sb = ttk.Scrollbar(self, command=cv.yview)
        body = ttk.Frame(cv, padding=14); body.bind("<Configure>", lambda e: cv.configure(scrollregion=cv.bbox("all")))
        cv.create_window((0, 0), window=body, anchor="nw"); cv.configure(yscrollcommand=sb.set)
        cv.pack(side="left", fill="both", expand=True); sb.pack(side="right", fill="y")
        self.bind("<MouseWheel>", lambda e: cv.yview_scroll(-int(e.delta / 120) or (-1 if e.delta > 0 else 1), "units"))
        ttk.Label(body, text="아래로 스크롤해 5번까지 채운 뒤 맨 아래 [💾 저장하고 닫기]를 누르세요.", foreground="#06c").pack(anchor="w")
        def sec(t): ttk.Label(body, text=t, font=("Malgun Gothic", 11, "bold")).pack(anchor="w", pady=(12, 4))
        def note(t): ttk.Label(body, text=t, foreground="#666", wraplength=700, justify="left").pack(anchor="w")
        def row(label, var, show="", width=40):
            f = ttk.Frame(body); f.pack(fill="x", pady=2)
            ttk.Label(f, text=label, width=24).pack(side="left")
            e = ttk.Entry(f, textvariable=var, width=width, show=show); e.pack(side="left"); return f

        sec("1. 디스코드")
        note("디스코드 개발자 포털에서 봇을 만들고(Bot → Reset Token), 서버에 초대하세요 (OAuth2 → bot + applications.commands, 권한: 채널 관리·메시지 보내기·파일 첨부).\n"
             "서버 ID·운영자 ID는 디스코드 설정 → 고급 → 개발자 모드를 켠 뒤 서버/내 이름을 오른쪽 클릭 → ID 복사.")
        self.token = tk.StringVar(); self.guild = tk.StringVar(value=str(d.get("GuildId", "") or "")); self.admin = tk.StringVar(value=", ".join(str(x) for x in d.get("AdminIds", []) if x))
        row("봇 토큰 (이미 했으면 비움)", self.token, show="*")
        row("서버 ID", self.guild); row("운영자 ID (쉼표로 여러 명)", self.admin)
        note("토큰은 이 PC의 사용자 환경 변수(DISCORD_TOKEN)에만 저장하고 설정 파일에는 쓰지 않습니다. 신청 채널은 봇이 '포켓몬 교환' 분류에 게임마다 만듭니다.")

        sec("2. 스위치")
        note("커스텀 펌웨어 스위치에 sys-botbase(버튼·화면)와 ftpsrv(세이브 읽고 쓰기, 쓰기가 고쳐진 판)가 있어야 합니다.\n"
             "ftpsrv: SD 카드 config/ftpsrv/config.ini에 anon = 1, [Nx] mount_devices = 1, save_writable = 1 → 재부팅.")
        self.ip = tk.StringVar(value=s.get("IP", "")); self.fuser = tk.StringVar(value=s.get("FtpUser", "")); self.fpass = tk.StringVar(value=s.get("FtpPass", ""))
        row("스위치 IP", self.ip); row("ftpsrv 사용자 (anon=1이면 비움)", self.fuser); row("ftpsrv 비밀번호", self.fpass, show="*")
        bf = ttk.Frame(body); bf.pack(fill="x", pady=4)
        ttk.Button(bf, text="연결 확인 · 사용자 읽기", command=self.scan).pack(side="left")
        self.scan_lbl = ttk.Label(bf, text="", foreground="#666", wraplength=520, justify="left"); self.scan_lbl.pack(side="left", padx=8)

        sec("3. 게임 세이브 주인과 다룰 게임")
        note("봇이 포켓몬을 넣을 게임 세이브가 있는 스위치 사용자. 각 게임 박스 1은 비워 두세요 (레츠고는 맨 윗줄이 파티라 둘째 줄부터 씀).")
        f = ttk.Frame(body); f.pack(fill="x", pady=2)
        ttk.Label(f, text="세이브 주인 (스위치 사용자 이름)", width=24).pack(side="left")
        self.owner = tk.StringVar(value=s.get("SaveUser", "")); self.owner_cb = ttk.Combobox(f, textvariable=self.owner, width=30); self.owner_cb.pack(side="left")
        self.owner_cb.bind("<<ComboboxSelected>>", lambda e: self.refresh_games())
        self.owner_hint = ttk.Label(body, text="2번 [연결 확인]을 누르면 목록이 채워집니다. 스위치 사용자 이름을 직접 쳐도 됩니다.", foreground="#666"); self.owner_hint.pack(anchor="w")
        gf = ttk.Frame(body); gf.pack(fill="x", pady=4)
        self.gvars = {}
        have = set(s.get("Games") or [])
        for i, (code, name, _) in enumerate(GAMES):
            v = tk.BooleanVar(value=code in have or (not have and code == "VL"))   # 아직 읽기 전엔 하나만 (연결 확인 뒤 세이브에 맞춰 바뀜)
            ttk.Checkbutton(gf, text=name, variable=v).grid(row=i // 2, column=i % 2, sticky="w", padx=(0, 24))
            self.gvars[code] = v

        sec("4. HOME 계정 (1~3개)")
        note("계정마다 'HOME에 로그인된 스위치 사용자' + '같은 HOME 계정으로 로그인한 휴대폰(MuMu 기기 또는 실제 폰)'을 짝지어요. 깡계(무료) HOME 계정이면 됩니다.\n"
             "휴대폰 HOME 앱 언어는 English로 두세요 (봇이 영어 화면으로 인식). ★ 각 HOME 계정의 HOME 박스 1은 비워 두세요 — 봇 작업용이라 다른 개체는 놓아줍니다.\n"
             "실제 폰은 USB 디버깅(또는 무선 디버깅)을 켜고 연결하면 목록에 나옵니다. 워커가 켜져 있는 동안 화면이 1080x1920으로 바뀌고, 끄면 되돌립니다.")
        ttk.Button(body, text="휴대폰 찾기", command=self.find_phones).pack(anchor="w", pady=2)
        self.accts = []
        old = (cfg.get("Home", {}) or {}).get("Accounts", []) or []
        for k in range(3):
            fr = ttk.LabelFrame(body, text=f"계정 {k + 1}" + (" (안 씀이면 비움)" if k else ""), padding=6); fr.pack(fill="x", pady=3)
            su, ph, au = tk.StringVar(), tk.StringVar(), tk.StringVar()
            if k < len(old):
                su.set(str(old[k].get("SwitchUser", ""))); ph.set(old[k].get("Phone", "")); au.set("" if old[k].get("AndroidUser") is None else str(old[k]["AndroidUser"]))
            ttk.Label(fr, text="스위치 사용자").grid(row=0, column=0, sticky="w")
            c1 = ttk.Combobox(fr, textvariable=su, width=26); c1.grid(row=0, column=1, padx=4)   # 목록은 [연결 확인] 뒤에 채워짐, 순서 번호를 직접 쳐도 됨
            ttk.Label(fr, text="휴대폰").grid(row=0, column=2, sticky="w")
            c2 = ttk.Combobox(fr, textvariable=ph, width=30); c2.grid(row=0, column=3, padx=4)
            ttk.Label(fr, text="앱 사용자 번호").grid(row=1, column=2, sticky="w")
            ttk.Entry(fr, textvariable=au, width=8).grid(row=1, column=3, sticky="w", padx=4)
            ttk.Label(fr, text="(MuMu 앱 복제본처럼 다른 안드로이드 사용자에 깔린 HOME만, 보통 비움)", foreground="#666").grid(row=2, column=2, columnspan=2, sticky="w")
            self.accts.append((su, ph, au, c1, c2))

        sec("5. 봇 트레이너 (생성 포켓몬의 어버이)")
        t = s.setdefault("Trainer", {})
        self.ot, self.tid, self.sid = tk.StringVar(value=t.get("OT", "홈봇")), tk.StringVar(value=str(t.get("TID", 123456))), tk.StringVar(value=str(t.get("SID", 1234)))
        row("어버이 이름 (한글 6자까지)", self.ot); row("TID", self.tid); row("SID", self.sid)

        ttk.Button(body, text="💾 저장하고 닫기", command=self.finish).pack(anchor="w", pady=16)
        self.after(200, lambda: self.find_phones(quiet=True))

    # ---- 동작
    def scan(self):
        ip = self.ip.get().strip()
        if not ip: self.scan_lbl.config(text="스위치 IP를 넣으세요", foreground="#c00"); return
        self.scan_lbl.config(text="확인 중…", foreground="#666"); self.update()
        msgs = ["sys-botbase(6000): " + ("연결됨" if port_open(ip, 6000) else "연결 안 됨 — sys-botbase 설치·와이파이 확인")]
        try:
            self.ftp_port, self.users = ftp_scan(ip, self.fuser.get().strip(), self.fpass.get())
            msgs.append(f"ftpsrv({self.ftp_port}): 사용자 {len(self.users)}명")
            ok = True
        except Exception as e:
            msgs.append(str(e)); ok = False
        self.scan_lbl.config(text="\n".join(msgs), foreground="#080" if ok and "안 됨" not in msgs[0] else "#c00")
        if not ok: return
        names = [n for n, _ in self.users]
        owners = [n for n, t in self.users if any(x in t for _, _, ids in GAMES for x in ids)]
        self.owner_cb.config(values=owners or names); self.owner_hint.config(text="(게임 세이브가 있는 사용자만 목록에)")
        if self.owner.get() not in (owners or names): self.owner.set((owners or names)[0] if (owners or names) else "")
        homes = [f"{i}: {n}" for i, (n, t) in enumerate(self.users) if HOME_TID in t]
        if not homes:   # HOME을 한 번도 켜지 않은 사용자는 HOME 세이브가 없음 → 전체를 보여 줌
            homes = [f"{i}: {n}" for i, (n, _) in enumerate(self.users)]
            self.scan_lbl.config(text=self.scan_lbl.cget("text") + "\nHOME 세이브가 있는 사용자가 없습니다 — 스위치에서 각 계정으로 Pokémon HOME을 한 번 켜 주세요")
        for su, _, _, c1, _ in self.accts:
            c1.config(values=[""] + homes)
            if su.get().isdigit():   # 예전 설정의 숫자 → 목록 이름
                su.set(next((h for h in homes if h.startswith(su.get() + ":")), su.get()))
        if homes and not any(a[0].get() for a in self.accts): self.accts[0][0].set(homes[0])
        self.refresh_games()

    def refresh_games(self):
        tids = next((t for n, t in self.users if n == self.owner.get()), None)
        if tids is None: return
        for code, _, ids in GAMES: self.gvars[code].set(any(x in tids for x in ids))

    def find_phones(self, quiet=False):
        threading.Thread(target=lambda: (lambda d: self.after(0, self._phones_found, d, quiet))(adb_devices()), daemon=True).start()

    def _phones_found(self, devs, quiet):
        if not self.winfo_exists(): return
        for _, ph, _, _, c2 in self.accts: c2.config(values=[""] + devs)
        if not quiet:
            messagebox.showinfo("HomeBot", ("찾은 기기:\n" + "\n".join(devs)) if devs else "adb에 보이는 기기가 없습니다. MuMu를 켜거나, 폰의 USB 디버깅을 켜고 연결한 뒤 '허용'을 눌러 주세요.", parent=self)

    def finish(self):
        c = self.cfg; d, s = c.setdefault("Discord", {}), c.setdefault("Switch", {})
        try:
            d["GuildId"] = int(self.guild.get().strip() or 0)
            d["AdminIds"] = [int(x) for x in re.split(r"[,\s]+", self.admin.get().strip()) if x]
        except ValueError:
            messagebox.showerror("HomeBot", "서버 ID·운영자 ID는 숫자여야 합니다", parent=self); return
        if not d["GuildId"]: messagebox.showerror("HomeBot", "서버 ID를 넣어 주세요", parent=self); return
        if not d["AdminIds"] and not messagebox.askyesno("HomeBot", "운영자 ID가 비어 있어요. 운영자 ID가 없으면 봇의 오류 알림·투표 기록 DM을 받을 수 없습니다. 그래도 저장할까요?", parent=self): return
        d.setdefault("TokenEnv", "DISCORD_TOKEN"); d.setdefault("ChannelId", 0)
        if self.token.get().strip():
            if not set_user_env(d["TokenEnv"], self.token.get().strip()):
                messagebox.showwarning("HomeBot", "토큰을 저장하지 못했습니다 (윈도우에서만 됨)", parent=self)
            os.environ[d["TokenEnv"]] = self.token.get().strip()
            self.token.set("")
        s["IP"] = self.ip.get().strip(); s["Port"] = s.get("Port", 6000) or 6000; s["FtpPort"] = self.ftp_port
        s["FtpUser"] = self.fuser.get().strip(); s["FtpPass"] = self.fpass.get()
        s["SaveUser"] = self.owner.get().strip()
        names = [n for n, _ in self.users]
        if not s["SaveUser"]: messagebox.showerror("HomeBot", "세이브 주인 스위치 사용자 이름을 넣어 주세요", parent=self); return
        if s["SaveUser"] in names: s["GameUserIndex"] = names.index(s["SaveUser"])
        s["Games"] = [code for code, _, _ in GAMES if self.gvars[code].get()]
        if not s["Games"]: messagebox.showerror("HomeBot", "다룰 게임을 하나 이상 고르세요", parent=self); return
        if s.get("Game") not in s["Games"]: s["Game"] = s["Games"][0]
        s.setdefault("TransferBox", 1); s.setdefault("MacroDir", "macros")
        accts = []
        for su, ph, au, _, _ in self.accts:
            if not su.get().strip() and not ph.get().strip(): continue
            raw = su.get().strip(); names = [n for n, _ in self.users]
            m = re.match(r"(\d+)", raw)
            idx = int(m.group(1)) if m else (names.index(raw) if raw in names else None)   # "1: p2" / "1" / 이름
            if idx is None or not ph.get().strip():
                messagebox.showerror("HomeBot", "HOME 계정마다 스위치 사용자(순서 번호 또는 목록에서 고른 이름)와 휴대폰을 둘 다 넣으세요", parent=self); return
            a = {"SwitchUser": idx, "Phone": ph.get().split()[0]}
            if au.get().strip().isdigit(): a["AndroidUser"] = int(au.get().strip())
            accts.append(a)
        if not accts: messagebox.showerror("HomeBot", "HOME 계정을 하나 이상 정하세요", parent=self); return
        c.setdefault("Home", {})["Accounts"] = accts; c["Home"].setdefault("Mode", "free")
        s["HomeUserIndex"] = accts[0]["SwitchUser"]
        t = s.setdefault("Trainer", {}); t["OT"] = self.ot.get().strip()[:6] or "홈봇"
        try: t["TID"] = int(self.tid.get()); t["SID"] = int(self.sid.get())
        except ValueError: pass
        t.setdefault("Gender", 0); t.setdefault("Language", "ko")
        c.setdefault("Phone", {})["Enabled"] = True; c["Phone"].setdefault("Url", "http://127.0.0.1:8765")
        c.setdefault("DataDir", "data"); c.setdefault("DefaultRoute", "home_gts")
        self.save_cfg(c)
        messagebox.showinfo("HomeBot", "저장했습니다. [▶ 전체 시작]을 누르면 봇이 디스코드에 신청 채널과 투표 채널을 만듭니다.", parent=self)
        self.destroy()
        if self.on_done: self.on_done()
