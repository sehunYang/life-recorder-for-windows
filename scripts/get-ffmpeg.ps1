# ffmpeg 정적 빌드를 tools\ffmpeg\ 로 받아 둔다.
# 바이너리는 저장소에 넣지 않는다 (용량 + GPL 재배포). 개발자와 사용자가 각자 한 번 실행한다.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'tools\ffmpeg'
$exe  = Join-Path $dest 'ffmpeg.exe'

if (Test-Path $exe) {
    Write-Host "already present: $exe"
    & $exe -version | Select-Object -First 1
    exit 0
}

# BtbN 정적 GPL 빌드. libx264 와 gdigrab 이 들어 있다.
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip'
$zip = Join-Path $env:TEMP 'ffmpeg-liferecorder.zip'
$tmp = Join-Path $env:TEMP 'ffmpeg-liferecorder-extract'

Write-Host "downloading: $url"
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -UseBasicParsing $url -OutFile $zip

if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
Expand-Archive -Path $zip -DestinationPath $tmp -Force

$src = Get-ChildItem -Path $tmp -Filter 'ffmpeg.exe' -Recurse | Select-Object -First 1
if (-not $src) { throw 'ffmpeg.exe not found inside the archive' }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $src.FullName $exe -Force

Remove-Item -Recurse -Force $tmp
Remove-Item -Force $zip

Write-Host "installed: $exe"
& $exe -version | Select-Object -First 1
