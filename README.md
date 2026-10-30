# HomeBot

디스코드에서 원하는 포켓몬을 문장으로 신청하면, 봇이 합법 개체를 만들어 **포켓몬 HOME GTS**로 교환해 주는 봇입니다.

```
신청자: /교환  이로치 마스카나 ♀ 몬스터볼 6V
  → 봇: 합법 개체 생성 (PKHeX · AutoMod)
  → 스위치 게임 세이브에 넣음 (ftpsrv, 게임을 켜지 않음)
  → 스위치 HOME으로 옮김 (sys-botbase 버튼 + 화면 인식)
  → 휴대폰 HOME 앱 GTS에서 신청자가 맡긴 포켓몬과 교환 (adb + 화면 인식)
```

- 지원 게임: 스칼렛·바이올렛 / 소드·실드 / 브릴리언트 다이아몬드·샤이닝 펄 / 레츠고 피카츄·이브이 / 레전즈 아르세우스 / 레전즈 Z-A
- HOME 계정 1~3개를 동시에 돌림. 게임은 신청자 투표로 바꾸고, 계정이 남으면 다른 게임도 같이 받음(동시 운영)
- 짝 게임(소드·실드 등)은 스위치 세이브로 버전을 알아내고, HOME 게임 선택 화면의 로고도 처음 한 번 알아서 등록
- 윈도우 관리 창(설치 마법사 포함)에서 켜고 끄고, 대기열·설정·도구를 다룸

> ⚠️ 비공식 도구입니다. Nintendo · The Pokémon Company · GAME FREAK와 관계없고, 계정 제재 등 사용에 따른 책임은 사용자에게 있습니다.

## 쓰기 (윈도우)

1. [Releases](../../releases)에서 `HomeBot_범용판_vX.Y.Z.zip`을 받아 압축을 풉니다
2. `HomeBot 시작.bat` 실행 → 처음에 파이썬·adb·패키지 자동 설치 → 한 번 더 실행하면 관리 창과 설치 마법사
3. 자세한 준비물(커스텀 펌웨어 스위치, sys-botbase, ftpsrv 수정판, 휴대폰/MuMu, 디스코드 봇)과 순서는 zip 안의 `사용법.txt`

중요한 조건
- 스위치 언어 한국어, **휴대폰 HOME 앱 언어 English**
- 각 게임 세이브의 박스 1, 각 HOME 계정의 HOME 박스 1은 비워 두기 (봇 작업용)
- ftpsrv는 세이브 쓰기를 고친 판이 필요 ([switch/ftpsrv](switch/ftpsrv))

## 구성

```
HomeBot.Core/     신청 상태기계(Orchestrator), DB, 생성(GenService), 스위치(SwitchWorker: 버튼·화면 + ftpsrv 세이브), 매크로 실행기
HomeBot.Discord/  봇 실행 파일 (슬래시 명령, 채널·투표판, 관리 창 명령 처리)
gui/              관리 창(manager.pyw) · 설치 마법사(wizard.py) — tkinter
phone_worker/     휴대폰 HOME 조작 + 스위치/휴대폰 화면 인식 서버 (server.py, OpenCV · Tesseract)
macros/           스위치 HOME 매크로 (home_open / save / close / discard)
dist/             배포 zip에 들어가는 시작·설치 스크립트, 사용법
switch/ftpsrv/    ftpsrv 세이브 쓰기 수정 패치
tools/pack.sh     배포 zip 만들기
```

## 소스로 빌드

생성 엔진(PKHeX · AutoMod 한국어판)은 [PKHeX-Mobile-KR](https://github.com/samang600/PKHeX-Mobile-KR)의 것을 서브모듈로 씁니다.

```bash
git clone --recursive https://github.com/samang600/HomeBot
cd HomeBot
# (이미 받았다면) git submodule update --init
dotnet build HomeBot.Discord -c Release                  # .NET 10 SDK
tools/pack.sh v1.1.0 path/to/ftpsrv_savefix.zip          # 배포 zip (win-x64, self-contained)
```

관리 창·워커는 파이썬 3.10+ (`pip install opencv-python numpy tesserocr Pillow`)와 adb가 필요합니다.

## 라이선스와 사용한 오픈소스

GPL-3.0 ([LICENSE](LICENSE)). 다음 프로젝트를 사용합니다:
[PKHeX](https://github.com/kwsch/PKHeX) · [PKHeX-Plugins / AutoMod](https://github.com/architdate/PKHeX-Plugins) ·
[Discord.Net](https://github.com/discord-net/Discord.Net) · [ftpsrv](https://github.com/ITotalJustice/ftpsrv) ·
[sys-botbase](https://github.com/olliz0r/sys-botbase) · [ADBKeyboard](https://github.com/senzhk/ADBKeyBoard) ·
[Tesseract](https://github.com/tesseract-ocr/tessdata) 언어 데이터 · OpenCV.
화면 인식용 템플릿 그림은 게임·앱 화면의 작은 조각이며 그 저작권은 각 권리자에게 있습니다.
