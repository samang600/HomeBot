"""역 GTS 시험: 조건으로 검색해서 결과를 보고 (--dry 면 확인 창에서 취소, 교환 안 함).
  python gts_test.py --name 피츄 --gender 1 --level 20 --lang ES-LA [--dry]
"""
import argparse
from adb import Adb
from flows import Home, FlowError

ap = argparse.ArgumentParser()
ap.add_argument("--serial", default="127.0.0.1:16384"); ap.add_argument("--user", type=int, default=0)
ap.add_argument("--name", default=""); ap.add_argument("--sample", action="store_true", help="기록 때 조건 (Camerupt 수컷 Lv33 ES-LA)"); ap.add_argument("--gender", type=int, default=-1, help="0 수컷 / 1 암컷 / -1 상관없음")
ap.add_argument("--level", type=int, default=0); ap.add_argument("--lang", default="")
ap.add_argument("--dry", action="store_true")
a = ap.parse_args()
if a.sample:
    a.name, a.gender, a.level, a.lang = "Camerupt", 0, 33, "ES-LA"
adb = Adb(a.serial, a.user)
print("HOME 화면:", adb.find_display(), adb.display)
try:
    print("결과:", Home(adb).gts_find_trade(a.name, a.gender, a.level, a.lang, 0, a.dry))
except FlowError as e:
    print("실패:", e, e.shot)
