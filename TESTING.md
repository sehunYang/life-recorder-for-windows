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

**배포판(파일 하나짜리 exe)을 확인할 때는** `tools\ffmpeg\` 이 없는 곳으로 옮겨서 실행해야 한다.
저장소 안에서 그냥 돌리면 안에 든 것 대신 `tools\` 쪽을 써서 정작 확인하려던 경로가 안 돈다.

```powershell
dotnet publish src\LifeRecorderWin\LifeRecorderWin.csproj -c Release `
  -p:PublishSingleFile=true -p:EmbedFfmpeg=true -o dist
Copy-Item dist\LifeRecorder.exe "$env:TEMP\lr-test\" -Force   # 저장소 밖으로
```

첫 실행 로그에 `ffmpeg 을 꺼냈습니다: ...` 가 한 번 뜨고, 두 번째 실행부터는 안 떠야 한다
(이미 꺼내 둔 것을 크기로 확인하고 그대로 쓴다).

---

## 1. 캡처가 되는지 (앱 없이 ffmpeg만)

앱을 의심하기 전에 ffmpeg 단독으로 5초 찍어 본다.
앱은 gdigrab 대신 화면을 직접 떠서 ffmpeg 에 넘기지만, 뜨는 방식(BitBlt)이 같아서 여기서 검으면 앱에서도 검다.
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
- 로그: `인코더: h264_amf` (AMD 가 없으면 `인코더 h264_amf 를 이 컴퓨터에서 쓸 수 없어 다음 것을 봅니다` → `인코더: libx264`)
- 로그: `화면 녹화 시작 [home] 5760x2172 → 2880x1086 @2fps, h264_amf, 상한 2262kbps (화면 배율 150%)`
  - 앞이 잡는 크기(물리 픽셀), 뒤가 파일에 들어가는 크기다. 화면 배율에 따라 자동으로 정해진다

**정각 분할**은 다음 정시까지 기다려야 확인된다. 정각이 지나면:

- `work\` 의 파일이 새 이름으로 바뀌고
- 직전 것이 `queue\` 로 옮겨진 뒤 (최대 10초 지연) 업로드된다
- 로그: `세그먼트 완료: pcscreen_...`

기다리기 싫으면 `Config.SegmentSeconds` 를 60으로 바꿔 빌드해서 1분 단위로 본다.

### 3-1. 앞 창 기록

녹화가 도는 동안 어느 창이 앞에 있었는지가 같이 남는지 본다.

```powershell
Get-Content "$env:LOCALAPPDATA\LifeRecorder\index\rawpcapp_$(Get-Date -Format yyyy-MM-dd)_*.jsonl.part" -Tail 10
```

- ON 직후 `"event":"start"` 한 줄, 이어서 지금 앞에 있는 창의 `"event":"focus"` 한 줄
- 다른 창을 클릭하면 **1초 안에** `focus` 줄이 하나 더 (`proc`·`title` 확인)
- 같은 창을 계속 쓰면 줄이 늘지 않아야 한다. 브라우저 탭을 바꾸면 제목이 바뀌어 한 줄 는다
- 크롬·엣지가 앞에 있으면 `focus`에 `"url":"youtube.com/watch?v=…"` 가 붙는다 (스킴 없이). 주소창에 글자를 치는 동안은 새 줄이 나지 않고, 페이지가 열린 뒤 1~2초 안에 난다
- 긴 기사를 열고 스크롤하면 `"event":"scroll","pos":0.31,"view":0.22` 줄이 5% 움직일 때마다 난다. 끝까지 내리면 `pos + view` 가 1에 가깝다. 유튜브 재생 화면처럼 안 내리면 안 난다
- 1분 넘게 손을 떼면 `"event":"idle"`, 마우스를 움직이면 `"event":"active"`
- `Win`+`L` 로 잠그면 `"event":"stop","reason":"잠금 상태"`, 풀면 다시 `start`
- 정각 1분이 지나면 `index\` 의 지난 시간 `rawpcapp_<오늘>_<컴퓨터이름>_h<시>.jsonl.part` 가 `queue\` 의 `pcapp_<오늘>_<컴퓨터이름>_h<시>.jsonl` 로 옮겨지고, 정각 3분 뒤 업로드에서 Drive `app/` 으로 올라간다 (`pcscreentext_` 도 같다)
- 이 판을 깔기 전에 쌓이던 `rawpcapp_<날짜>_<컴퓨터이름>.jsonl.part`(시가 없는 것)는 날이 바뀐 뒤 하루 파일 `pcapp_<날짜>_<컴퓨터이름>.jsonl` 로 올라간다

### 3-2. 화면 글자

```powershell
Get-Content "$env:LOCALAPPDATA\LifeRecorder\index
awpcscreentext_$(Get-Date -Format yyyy-MM-dd)_*.jsonl.part" -Tail 3
```

- ON 직후 `"kind":"service","event":"start"` 한 줄
- 크롬에서 기사를 열면 1~3초 안에 `"kind":"screen","proc":"chrome"` 줄에 본문 문장들이 `nodes[].text` 로 온다. 스크롤하면 새로 보인 문단만 한 줄 더
- 같은 창을 가만히 두면 줄이 늘지 않는다. 메모장에 글을 치는 동안은 안 남고, 멈추면 한 줄
- 비밀번호 칸(브라우저 로그인 폼)은 `nodes` 에 없어야 한다
- 60초 넘게 손을 떼면 읽지 않는다 (줄이 안 는다)
- OFF 하면 `"event":"stop"` 의 `reason` 끝에 `(읽기 N회 · 노드 M · 느린 창 K)`

### 3-3. 가린 창 (Brave · Chrome 시크릿)

일반 Chrome 창, Chrome 시크릿 창(`Ctrl`+`Shift`+`N`), Brave 창을 나란히 띄우고 시크릿 창 위에 메모장을 조금 겹쳐 둔 채 1분 녹화한다.

- 로그: `화면 가림: 창 N개를 검게 칠합니다` (가릴 창 수가 바뀔 때마다 한 줄)
- 영상: 시크릿 창과 Brave 창 자리가 검다. **메모장이 겹친 부분은 메모장이 보인다.** 일반 Chrome 창은 그대로 보인다
- 시크릿 창을 끌고 다녀도 가장자리가 새지 않는다. 새 시크릿 창을 열면 처음 뜬 장부터 검다
- `rawpcapp_`: 시크릿·Brave 창을 앞으로 가져오면 `"event":"focus","proc":"chrome","private":true` — `title`·`url` 이 없어야 한다
- `rawpcscreentext_`: 시크릿·Brave 창이 앞에 있는 동안 `"kind":"screen"` 줄이 늘지 않는다
- 시크릿 창에서 유튜브를 틀면 `media` 줄이 `"app":"Chrome","private":true` 로 제목 없이 난다

---

## 4. 쉬는 조건

| 확인할 것 | 방법 | 기대 |
|---|---|---|
| 세션 잠금 | `Win`+`L` → 다시 로그인 | 트레이 노랑 → 빨강. 로그에 `세션 상태: SessionLock` / `SessionUnlock` |
| 모니터 꺼짐 | 전원 설정에서 화면 끄기를 1분으로 두고 방치 | 로그에 `모니터 전원: 꺼짐` → `켜짐` |
| 절전 | 절전 진입 후 복귀 | 로그에 `전원 상태: Suspend` / `Resume` |
| 모니터 구성 | 모니터 케이블을 빼거나 `Win`+`P` 로 표시 방식 변경 | 로그에 `잡는 영역 변경 ... 세션을 다시 엽니다` |
| 입력 없음 | 5분 넘게 키보드·마우스를 안 건드린다 | 로그에 `입력이 5분 없어 화면 녹화를 쉽니다`, 트레이 노랑, 상태창 `입력 없음 5분`. 마우스를 움직이면 10초 안에 `다시 시작합니다` + 새 세그먼트. `rawpcapp_` 끝에 `stop`(입력 없음)·`start` |
| 영상 보는 중 | 유튜브를 틀고 5분 넘게 손을 뗀다 | **쉬지 않아야 한다.** 로그에 `입력은 없지만 재생 중: Chrome — <영상 제목> — 화면 녹화를 계속합니다`. `rawpcapp_`에 `"event":"media","state":"playing","title":"<영상 제목>"`. 영상을 멈추면 5분 뒤 쉰다 |
| 소리만 | 미디어 세션에 안 붙는 플레이어(mpv 등)로 재생 | 로그에 `소리 남 — 계속합니다`. 20초 연속 소리가 나야 인정된다 (알림음 한 번은 무시) |

쉬는 동안에는 `work\` 파일이 늘지 않고, 풀리면 **새 세그먼트**로 다시 시작한다.
그 사이는 파일이 없는 공백으로 남는다 (의도한 동작).

### 노트북에서만 확인할 것

시작할 때 로그의 `환경:` 줄을 먼저 본다. `배터리 없음`이면 아래는 해당 없다.

```
환경: [school-laptop] 화면 배율 150% · 전원 연결됨 · 회선 일반
```

| 확인할 것 | 방법 | 기대 |
|---|---|---|
| 배터리 업로드 미루기 | 전원 어댑터를 뽑는다 | 상태창이 `업로드 미룸 — 배터리로 도는 중 (NN%)`. **녹화는 계속된다** — `work\` 파일이 계속 커져야 한다 |
| 수동 무시 | 위 상태에서 **지금 업로드** | 제약을 무시하고 바로 올라간다 |
| 다시 꽂으면 | 어댑터를 꽂는다 | 로그에 `전원 상태: 전원 연결됨` 이 뜨고 곧바로 미뤄 뒀던 것이 올라간다 |
| 종량제 | 설정 > 네트워크에서 지금 회선을 **종량제 연결**로 켠다 | 상태창이 `업로드 미룸 — 종량제 회선`. 끄면 2분 안에 다시 올라간다 |

**배터리 잔량으로 녹화를 멈추지는 않는다.** 잔량이 얼마든 녹화는 계속된다.
배터리가 다 되어 노트북이 꺼지면 쓰던 세그먼트 하나를 잃는다 (최대 한 시간).

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
- AMF 면 `ScreenAmfQmin` 을 18로, libx264 면 `ScreenCrf` 를 28~30으로 올린다 (움직임이 많은 구간에서 뭉개진다)
- `ScreenBitsPerPixelPerFrame` 을 낮춘다 (상한만 내린다. 평소 용량은 품질 기준이 정한다)

---

## 8. 로컬을 전부 지우고 처음부터

```powershell
# 앱을 끝낸 뒤
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\LifeRecorder"
```

설정·토큰·대기 중인 파일·수집 기록이 전부 사라진다.
이미 Drive에 올라간 것은 그대로 남는다.
