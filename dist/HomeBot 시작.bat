@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "homebot\data" mkdir "homebot\data"
if exist "homebot\data\.installed" goto run
echo 처음 실행: 필요한 프로그램 - 파이썬, adb, 패키지 - 를 설치합니다. 몇 분 걸립니다...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0설치.ps1"
if not exist "homebot\data\.installed" goto fail
echo.
echo 설치 끝. 이 창을 닫고 'HomeBot 시작.bat'을 한 번 더 실행해 주세요.
pause
exit /b 0

:run
set "PYEXE="
if exist "homebot\data\python.txt" set /p PYEXE=<"homebot\data\python.txt"
if defined PYEXE if exist "%PYEXE%" goto haspy
echo 파이썬을 찾지 못했습니다. homebot\data\.installed 를 지우고 다시 실행하면 설치를 다시 합니다.
pause
exit /b 1

:haspy
set "PYW=%PYEXE:python.exe=pythonw.exe%"
if not exist "%PYW%" set "PYW=%PYEXE%"
start "" "%PYW%" "%~dp0homebot\gui\manager.pyw"
exit /b 0

:fail
echo 설치가 끝나지 않았습니다. 설치.log 를 확인한 뒤 다시 실행하세요.
pause
exit /b 1
