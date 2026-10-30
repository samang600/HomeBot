"""템플릿 수집용 기록기: 폰 화면이 바뀔 때마다 PNG 저장 + 터치 좌표 로그.

  python record.py [--serial S] [--out record_phone]

사용법: 실행해 두고 HOME 앱에서 GTS 맡기기 → 받기 → 놓아주기(또는 친구 교환)를 손으로 한 번 끝까지 진행.
끝나면 Ctrl+C. out 폴더의 PNG들과 touches.log 를 zip으로 보내 주면 템플릿을 만든다.
"""
import argparse, os, subprocess, threading, time
import numpy as np, cv2
import vision as V
from adb import Adb, ADB


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--serial", default=None)
    ap.add_argument("--out", default="record_phone", help="이 폴더 아래 실행 시각 폴더를 새로 만들어 저장")
    ap.add_argument("--interval", type=float, default=0.7)
    ap.add_argument("--user", type=int, default=None, help="MuMu 복제본의 안드로이드 사용자 번호 (#N1 = 10)")
    a = ap.parse_args()
    adb = Adb(a.serial, a.user)
    a.out = os.path.join(a.out, time.strftime("%Y%m%d_%H%M%S"))
    os.makedirs(a.out, exist_ok=True)
    print("폰:", adb.devices(), "해상도:", adb.size())
    print("HOME 화면 찾음" if adb.find_display() else "HOME 화면을 못 찾음 — #N1 HOME을 켜 두세요", adb.display, adb.sf_display)
    stop = False

    # 터치 로그 (getevent: 좌표는 기기 raw 값 → 화면 좌표 변환은 나중에)
    def touches():
        cmd = [ADB] + (["-s", a.serial] if a.serial else []) + ["shell", "getevent", "-lt"]
        with subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True) as p, open(os.path.join(a.out, "touches.log"), "w") as f:
            for line in p.stdout:
                if stop:
                    p.kill(); break
                if "ABS_MT_POSITION" in line or "BTN_TOUCH" in line:
                    f.write(f"{time.strftime('%H:%M:%S')} {line}")
                    f.flush()
    threading.Thread(target=touches, daemon=True).start()

    prev = None; n = 0
    try:
        while True:
            try:
                b = adb.screenshot()
                img = cv2.imdecode(np.frombuffer(b, np.uint8), cv2.IMREAD_COLOR)
                if img is None:
                    raise RuntimeError(f"이미지 해독 실패 ({len(b)}바이트, 앞부분 {b[:40]!r})")
                small = cv2.resize(img, (90, 160))
                if prev is None or np.mean(np.abs(small.astype(int) - prev.astype(int))) > 2.0:
                    n += 1
                    V.imwrite(os.path.join(a.out, f"{n:04d}_{time.strftime('%H%M%S')}.png"), img)
                    print(f"\r{n}장", end="", flush=True)
                    prev = small
            except Exception as e:
                print("\n캡처 실패:", e)
                try:
                    with open(os.path.join(a.out, "errors.log"), "a", encoding="utf-8") as f:
                        f.write(f"{time.strftime('%H:%M:%S')} {e!r}\n")
                except Exception:
                    pass
            time.sleep(a.interval)
    except KeyboardInterrupt:
        stop = True
        print(f"\n저장: {a.out} ({n}장)")


if __name__ == "__main__":
    main()
