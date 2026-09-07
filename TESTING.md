# 손으로 확인하는 순서

자동 테스트는 없다. 화면 캡처·전원 이벤트·Drive 업로드는 실제 PC에서만 뜻이 있어서
아래 순서를 손으로 훑는다. 무슨 일이 있었는지는 전부 로그에 남는다.

```
%LOCALAPPDATA%\LifeRecorder\logs\liferecorder-<날짜>.log
```

트레이 메뉴 → **로그 폴더 열기** 로도 갈 수 있다.

---

## 0. 준비

```powershell
.\scripts\get-ffmpeg.ps1
dotnet build src\LifeRecorderWin\LifeRecorderWin.csproj -c Release
```

ffmpeg이 제대로 받아졌는지:

```powershell
.\tools\ffmpeg\ffmpeg.exe -version          # libx264 가 있어야 한다
.\tools\ffmpeg\ffmpeg.exe -hide_banner -encoders | Select-String libx264
```

---

## 1. 캡처가 되는지 (앱 없이 ffmpeg만)

앱을 의심하기 전에 ffmpeg 단독으로 5초 찍어 본다.
`<L> <T> <W> <H>` 는 가상 데스크톱 값이다:

```powershell
Add-Type -AssemblyName System.Windows.Forms
$v = [System.Windows.Forms.SystemInformation]::VirtualScreen
"$($v.Left) $($v.Top) $($v.Width) $($v.Height)"
```

```powershell
.\tools\ffmpeg\ffmpeg.exe -f gdigrab -framerate 2 `
  -offset_x <L> -offset_y <T> -video_size <W>x<H> -i desktop `
  -t 5 -c:v libx264 -preset veryfast -crf 26 -pix_fmt yuv420p test.mp4
```

- 재생해서 **모니터가 전부 한 화면에 들어 있는지** 확인한다.
- 전체화면 게임이나 DRM 영상이 검게 나오는 것은 gdigrab의 알려진 한계다(README 참고).
- 세로 크기가 홀수면 앱은 1픽셀 깎는다. 여기서 직접 돌릴 때는 짝수로 맞춰 줄 것.

---

## 2. 처음 실행

`LifeRecorder.exe` 실행 → 창이 뜨고 트레이에 회색 점.

1. **컴퓨터 이름**을 넣고 저장 (`home`, `school` 처럼 짧게)
   - 넣기 전에는 상태가 `컴퓨터 이름을 정해 주세요` 이고 **녹화가 시작되지 않아야 한다**
   - 로그에 `컴퓨터 이름: home`
   - 컴퓨터가 두 대 이상이면 **서로 다르게** 정할 것. 같으면 `pcindex_<날짜>.jsonl` 이 매일 충돌한다
2. **Google 계정 연결** → 클라이언트 ID / 보안 비밀 입력 → 브라우저에서 동의
   - 두 컴퓨터가 **같은** 클라이언트 ID·보안 비밀을 써도 된다. 연결만 각자 한 번씩 한다
3. 상태창에 `Drive 연결됨` 이 뜨는지
4. 로그에 `Google 계정 연결 완료`

안 되면 자주 걸리는 것:

| 증상 | 원인 |
|---|---|
| `redirect_uri_mismatch` | 클라이언트 유형이 "데스크톱 앱"이 아니다. 웹 애플리케이션으로 만들면 루프백이 안 된다 |
| `access_denied` | OAuth 동의 화면의 테스트 사용자에 본인 계정이 없다 |
| 리프레시 토큰이 안 옴 | 이미 동의한 적이 있어 Google이 건너뛴 것. [계정 권한](https://myaccount.google.com/permissions)에서 앱 접근을 지우고 다시 |
| 7일 뒤 연결이 끊김 | 동의 화면이 "테스트" 상태다. "프로덕션"으로 바꾼다 (README 참고) |

---

## 3. 녹화가 도는지

**기록 시작 (ON)** 을 누른다.

```powershell
# 지금 쓰고 있는 세그먼트
Get-ChildItem "$env:LOCALAPPDATA\LifeRecorder\work"

# 완성돼 업로드를 기다리는 것
Get-ChildItem "$env:LOCALAPPDATA\LifeRecorder\queue"
```

- 트레이가 **빨강**, 상태창이 `기록 중`, `2880x1086 · 2fps`
- `work\` 에 `pcscreen_<시각>_<컴퓨터이름>.mp4` 하나가 생기고 **크기가 계속 는다**
- 로그: `화면 녹화 시작 [home] 5760x2172 → 2880x1086 @2fps, 상한 2262kbps (화면 배율 150%)`
  - 앞이 잡는 크기(물리 픽셀), 뒤가 파일에 들어가는 크기다. 화면 배율에 따라 자동으로 정해진다

**정각 분할**은 다음 정시까지 기다려야 확인된다. 정각이 지나면:

- `work\` 의 파일이 새 이름으로 바뀌고
- 직전 것이 `queue\` 로 옮겨진 뒤 (최대 10초 지연) 업로드된다
- 로그: `세그먼트 완료: pcscreen_...`

기다리기 싫으면 `Config.SegmentSeconds` 를 60으로 바꿔 빌드해서 1분 단위로 본다.

---

## 4. 쉬는 조건

| 확인할 것 | 방법 | 기대 |
|---|---|---|
| 세션 잠금 | `Win`+`L` → 다시 로그인 | 트레이 노랑 → 빨강. 로그에 `세션 상태: SessionLock` / `SessionUnlock` |
| 모니터 꺼짐 | 전원 설정에서 화면 끄기를 1분으로 두고 방치 | 로그에 `모니터 전원: 꺼짐` → `켜짐` |
| 절전 | 절전 진입 후 복귀 | 로그에 `전원 상태: Suspend` / `Resume` |
| 모니터 구성 | 모니터 케이블을 빼거나 `Win`+`P` 로 표시 방식 변경 | 로그에 `잡는 영역 변경 ... 세션을 다시 엽니다` |

쉬는 동안에는 `work\` 파일이 늘지 않고, 풀리면 **새 세그먼트**로 다시 시작한다.
그 사이는 파일이 없는 공백으로 남는다 (의도한 동작).

### 노트북에서만 확인할 것

시작할 때 로그의 `환경:` 줄을 먼저 본다. `배터리 없음`이면 아래는 해당 없다.

```
환경: [school-laptop] 화면 배율 150% · 전원 연결됨 · 회선 일반
```

| 확인할 것 | 방법 | 기대 |
|---|---|---|
| 배터리 업로드 미루기 | 전원 어댑터를 뽑는다 | 상태창이 `업로드 미룸 — 배터리로 도는 중 (NN%)`. **녹화는 계속된다** |
| 수동 무시 | 위 상태에서 **지금 업로드** | 제약을 무시하고 바로 올라간다 |
| 다시 꽂으면 | 어댑터를 꽂는다 | 2분 안에 미뤄 뒀던 것이 몰아서 올라간다 |
| 종량제 | 설정 > 네트워크에서 지금 회선을 **종량제 연결**로 켠다 | 상태창이 `업로드 미룸 — 종량제 회선`. 끄면 다시 올라간다 |
| 배터리 부족 | 잔량이 20% 아래가 될 때까지 배터리로 쓴다 | 녹화가 멈추고 상태창에 `쉬는 중 — 배터리 NN% — 20% 아래라 멈춤`. 마지막 세그먼트는 정상적으로 닫혀 `queue\` 로 간다 |

기다리기 싫으면 `Config.BatteryStopPercent` 를 90 같은 값으로 바꿔 빌드해서 본다.

---

## 5. 업로드

**지금 업로드** 를 누른다.

- Drive의 `LifeRecorder/screen` 에 `pcscreen_..._<컴퓨터이름>.mp4` 가 뜬다.
  **폴더가 새로 생기면 안 된다.** 루트에 `LifeRecorder` 가 두 개면 스코프 문제다(README 참고)
- 올라간 파일은 `queue\` 에서 사라진다 (크기·MD5 검증 후 삭제)
- 로그: `업로드 pcscreen_... 0/12345678` → `올림 pcscreen_... → 1AbC...`

**중간에 끊어도 이어 올라가는지:** 큰 파일이 올라가는 동안 랜선을 뽑았다 꽂는다.
다음 시도에서 `업로드 ... <이어받는 offset>/<전체>` 로 0이 아닌 지점부터 시작해야 한다.

**수집 기록:** 날이 바뀐 뒤 `queue\` 에 `pcindex_<어제>_<컴퓨터이름>.jsonl` 이 생기고 업로드된다.
Drive의 `index/` 에서 안드로이드의 `index_...` 와 나란히 보이면 정상이다.

---

## 6. 죽어도 살아나는지

| 확인할 것 | 방법 | 기대 |
|---|---|---|
| ffmpeg이 죽었을 때 | 작업 관리자에서 `ffmpeg.exe` 강제 종료 | 10초 뒤 자동 재시작. 로그에 `화면 녹화가 예기치 않게 끝났습니다` → `10초 뒤 ... 다시 시도` |
| 미완성 세그먼트 | 위와 같이 죽인 직후 `work\` 확인 | 다음 시작 때 `moov` 없는 파일을 버린다. 로그에 `이전 세션 정리: 살림 N개, 버림 M개` |
| 앱 강제 종료 | 작업 관리자에서 `LifeRecorder.exe` 종료 후 다시 실행 | 저장된 ON 상태를 이어받아 바로 녹화 시작. 로그에 `저장된 상태가 ON 이라 ...` |
| 재부팅 | 자동 시작을 켜 두고 재부팅 | 로그인 후 트레이에만 뜨고 녹화가 이어진다 |
| 두 번 실행 | exe를 두 번 실행 | "이미 실행 중입니다" 안내 후 두 번째가 종료 |

---

## 7. 용량이 얼마나 느는지

한 시간 돌린 뒤 `queue\` 또는 Drive에서 세그먼트 하나의 크기를 본다.
평소 작업 패턴에서 시간당 몇 MB인지를 알아야 Drive 용량을 가늠할 수 있다.

너무 크면 `Config.cs` 에서:

- `ScreenTargetLogicalScale` 을 `0.5` 로 (해상도를 줄인다 — 글씨 판독이 나빠진다)
- `ScreenCrf` 를 28~30으로 올린다 (움직임이 많은 구간에서 뭉개진다)
- `ScreenBitsPerPixelPerFrame` 을 낮춘다 (상한만 내린다. 평소 용량은 CRF 가 정한다)

---

## 8. 로컬을 전부 지우고 처음부터

```powershell
# 앱을 끝낸 뒤
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\LifeRecorder"
```

설정·토큰·대기 중인 파일·수집 기록이 전부 사라진다.
이미 Drive에 올라간 것은 그대로 남는다.
