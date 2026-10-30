"""한글 없이 이름을 넣을 수 있는지 시험: 영어 이름·도감 번호를 넣어 보고, '원하는 포켓몬으로 찾는다' 화면도 찍음.
결과 사진: homebot/data/probe/"""
import os, time, cv2
from adb import Adb
from flows import Home
import vision as V
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "data", "probe")
os.makedirs(OUT, exist_ok=True)
adb = Adb("127.0.0.1:16384", 10); h = Home(adb)
print("HOME 화면:", adb.find_display(), adb.display)
def snap(tag):
    V.imwrite(os.path.join(OUT, f"{time.strftime('%H%M%S')}_{tag}.png"), h.shot()); print("찍음", tag)
def to_form():
    h.to_gts_main(); x, y = h.expect("ph_gts_main", 10); h.tap(x, y, 1.5); h.expect("ph_form", 10)
for word in ("Pichu", "172", "pichu"):
    to_form()
    h.tap(548, 263, 1.5); snap(f"{word}_0_keyboard")
    adb.text(word); time.sleep(1.5); snap(f"{word}_1_typed")
    h.tap(1004, 1862, 1.5); snap(f"{word}_2_ok")
    h.tap(540, 1800, 1.5); h.tap(540, 1800, 1.5)   # 돌아가기
h.to_gts_main()
h.tap(705, 1620, 2.5); snap("want_search_0")       # '원하는 포켓몬으로 찾는다'
h.tap(157, 435, 2.0); snap("want_search_1")
h.tap(540, 1800, 1.5); snap("want_search_2")
print("끝")
