# 템플릿

- `phone/<이름>.png` : HOME 폰 앱 화면 조각 (flows.py 의 expect/tap_tpl 이름과 같게)
- `switch/<이름>.png` : 스위치 화면 조각 (macros/*.json 의 expect 이름과 같게, pixelPeek JPEG 기준 1280x720)
- `<이름>.json` (선택) : `{"region":[x,y,w,h], "threshold":0.9}` — 찾을 범위와 기준
- `phone/box_grid.json` : HOME 박스 첫 칸 중심과 칸 간격 `{"x0":..,"y0":..,"dx":..,"dy":..,"cols":6}`
- `phone/box_no_region.json` : 박스 번호 글자 영역 `{"region":[x,y,w,h]}` (박스 여러 개일 때만)

## 만드는 법
1. `python record.py` 를 켜 두고 HOME 앱에서 흐름을 손으로 한 번 진행 → `record_phone/` 에 PNG가 쌓임
2. 각 화면에서 "그 화면에만 있는 부분"(버튼 글자·아이콘)을 잘라 위 이름으로 저장 (`python make_template.py 0012_… 420 1180 260 90 home_gts_deposit`)
3. 탭할 대상은 그 버튼 자체를 템플릿으로 (중심을 탭함)

## 템플릿 이름
- 폰 (templates/phone, HOME 앱 언어 English): flows.py 의 expect/tap_tpl 에서 쓰는 이름 그대로 (ph_*.png)
- 스위치 (templates/switch, 스위치 언어 한국어): macros/*.json 의 expect 이름 (home_*.png, sw_*.png)
- 게임 로고 (templates/switch/home_gs_<버전>.png): 봇이 처음 HOME에 들어갈 때 없는 것은 알아서 잘라 둠 (관리 창 [게임 로고 자동/직접 등록])
