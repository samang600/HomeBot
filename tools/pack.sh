#!/usr/bin/env bash
# 배포 zip 만들기: 봇을 win-x64로 빌드하고 시작·설치 스크립트, 관리 창, 워커, 매크로, ftpsrv와 함께 묶음
#   사용: tools/pack.sh v1.1.0 [ftpsrv_savefix.zip 경로]
#   필요: .NET 10 SDK, zip, deps/PKHeX-Mobile-KR (git submodule update --init)
set -euo pipefail
VER="${1:?버전을 주세요 (예: v1.1.0)}"
FTPZIP="${2:-}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/out"; STAGE="$OUT/stage/HomeBot"
rm -rf "$OUT/stage"; mkdir -p "$STAGE/homebot" "$STAGE/스위치에 넣을 것"

dotnet publish "$ROOT/HomeBot.Discord/HomeBot.Discord.csproj" -c Release -r win-x64 --self-contained true -o "$STAGE/homebot/bot"
cp "$ROOT/dist/HomeBot 시작.bat" "$ROOT/dist/설치.ps1" "$ROOT/dist/사용법.txt" "$STAGE/"
cp "$ROOT/config.sample.json" "$STAGE/homebot/"
cp -r "$ROOT/macros" "$ROOT/gui" "$ROOT/phone_worker" "$STAGE/homebot/"
find "$STAGE" -name "__pycache__" -prune -exec rm -rf {} +
rm -rf "$STAGE/homebot/phone_worker/shots" "$STAGE/homebot/gui/gui_settings.json"
if [ -n "$FTPZIP" ]; then cp "$FTPZIP" "$STAGE/스위치에 넣을 것/ftpsrv_savefix.zip"
else echo "주의: ftpsrv_savefix.zip 없이 묶음 (switch/ftpsrv/README.md 참고해 빌드한 zip을 두 번째 인자로)"; fi

cd "$OUT/stage"; rm -f "$OUT/HomeBot_$VER.zip"
zip -qr "$OUT/HomeBot_$VER.zip" HomeBot
echo "만듦: $OUT/HomeBot_$VER.zip"
