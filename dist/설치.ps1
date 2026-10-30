# HomeBot 설치 — 파이썬 3.12, adb, 파이썬 패키지 (봇 본체는 이미 빌드돼 있어 .NET은 필요 없음)
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Start-Transcript -Path (Join-Path $root '설치.log') -Force | Out-Null
function Step($t) { Write-Host "`n==== $t ====" -ForegroundColor Cyan }
function RefreshPath { $env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User') }
function Has($cmd) { [bool](Get-Command $cmd -ErrorAction SilentlyContinue) }
$wg = @('--exact','--silent','--accept-source-agreements','--accept-package-agreements','--disable-interactivity')
if (-not (Has winget)) { Write-Host 'winget이 없습니다. Microsoft Store에서 "앱 설치 관리자"를 설치한 뒤 다시 실행하세요.' -ForegroundColor Red; Stop-Transcript | Out-Null; exit 1 }

Step '1/3 Python 3.12'
$py = $null
foreach ($c in @('py -3.12','python')) { try { $v = Invoke-Expression "$c --version" 2>$null; if ($v -match 'Python 3\.1[0-9]') { $py = $c; break } } catch {} }
if (-not $py) { winget install --id Python.Python.3.12 @wg; RefreshPath; $py = if (Has py) { 'py -3.12' } else { 'python' } }
Invoke-Expression "$py --version"

Step '2/3 Android adb'
if (-not (Has adb)) {
  winget install --id Google.PlatformTools @wg; RefreshPath
  if (-not (Has adb)) {
    $c = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter adb.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { [Environment]::SetEnvironmentVariable('Path', [Environment]::GetEnvironmentVariable('Path','User') + ';' + $c.DirectoryName, 'User'); RefreshPath }
  }
}
adb version

Step '3/3 파이썬 패키지 (화면 인식·이름 인식)'
Invoke-Expression "$py -m pip install --user --upgrade opencv-python numpy tesserocr Pillow"
Invoke-Expression "$py -c `"import cv2, tesserocr, tkinter; print('확인 완료')`""
if ($LASTEXITCODE -ne 0) { Write-Host "`n설치가 끝나지 않았습니다 (인터넷 연결 확인 후 'HomeBot 시작.bat'을 다시 실행). 로그: 설치.log" -ForegroundColor Red; Stop-Transcript | Out-Null; exit 1 }
New-Item -ItemType Directory -Force -Path (Join-Path $root 'homebot\data') | Out-Null
# 패키지를 넣은 바로 그 파이썬으로 관리 창을 켜도록 경로를 남김 (여러 버전이 깔린 PC 대비)
$exe = (Invoke-Expression "$py -c `"import sys; print(sys.executable)`"").Trim()
[IO.File]::WriteAllText((Join-Path $root 'homebot\data\python.txt'), $exe, (New-Object Text.UTF8Encoding $false))   # BOM 없이 (bat의 set /p가 읽음)

$cfg = Join-Path $root 'homebot\config.json'
if (-not (Test-Path $cfg)) { Copy-Item (Join-Path $root 'homebot\config.sample.json') $cfg }
Set-Content -Path (Join-Path $root 'homebot\data\.installed') -Value (Get-Date) -Force
Write-Host "`n설치 끝. 이 창을 닫고 'HomeBot 시작.bat'을 실행하세요." -ForegroundColor Green
Stop-Transcript | Out-Null
