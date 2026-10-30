"""MuMu 화면(디스플레이) 목록과 각 화면 캡처를 저장 → 어느 번호가 HOME 앱인지 확인용."""
import os, re, subprocess, sys
from adb import ADB, Adb

out = sys.argv[1] if len(sys.argv) > 1 else "displays"
serial = sys.argv[2] if len(sys.argv) > 2 else "127.0.0.1:16384"
os.makedirs(out, exist_ok=True)
a = Adb(serial)
log = open(os.path.join(out, "displays.txt"), "w", encoding="utf-8")
def sh(*args):
    p = subprocess.run([ADB, "-s", serial] + list(args), capture_output=True, timeout=30)
    return p.stdout, p.stderr
for cmd in (["shell", "dumpsys", "SurfaceFlinger", "--display-id"], ["shell", "cmd", "display", "get-displays"], ["shell", "dumpsys", "window", "displays"]):
    o, e = sh(*cmd)
    log.write("### " + " ".join(cmd) + "\n" + o.decode(errors="ignore")[:6000] + e.decode(errors="ignore")[:500] + "\n")
o, e = sh("exec-out", "screencap", "-p")
i = o.find(b"\x89PNG")
log.write("### screencap 앞부분\n" + o[:max(i, 0)][:2000].decode(errors="ignore") + "\n")
ids = set(re.findall(rb"Display (\d{1,20})", o[:max(i, 0)] if i > 0 else b""))
ids |= set(re.findall(rb"Display (\d{1,20})", sh("shell", "dumpsys", "SurfaceFlinger", "--display-id")[0]))
log.write(f"### 찾은 디스플레이: {ids}\n")
for d in sorted(ids) or [b"0"]:
    d = d.decode()
    o, e = sh("exec-out", "screencap", "-d", d, "-p")
    j = o.find(b"\x89PNG")
    log.write(f"display {d}: {len(o)}바이트, png={j >= 0} {e.decode(errors='ignore')[:200]}\n")
    if j >= 0:
        open(os.path.join(out, f"display_{d}.png"), "wb").write(o[j:])
log.close()
print("저장:", out)
