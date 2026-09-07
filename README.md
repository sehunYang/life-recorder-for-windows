# Life Recorder for Windows

컴퓨터 화면을 통째로 기록해 **Google Drive에 쌓아 두는 윈도우 앱.**
ON을 누르면 트레이에 앉아 **모니터 전체를 한 화면으로** 계속 녹화하고,
정각마다 파일을 끊어 자동으로 올린다. OFF를 누르기 전까지 멈추지 않도록 설계했다.

[안드로이드 앱](https://github.com/sehunYang/life-recorder-for-android)의 화면 기록 부분만 데스크톱으로 옮긴 것이다.
**같은 Google Drive 폴더(`LifeRecorder/screen`)에 그대로 올라간다.** 폴더가 늘어나지 않는다.
폰 화면과 PC 화면이 한 폴더에서 시간순으로 섞이고, 파일 이름 접두어로만 갈린다.

여기서는 **화면만** 담는다. 소리(마이크·시스템 사운드)는 담지 않는다.

컴퓨터 여러 대에 깔아도 된다. 각 컴퓨터에 짧은 이름(`home`, `school`)을 정해 두면
파일 이름 끝에 붙어 갈린다. 설치는 [다른 컴퓨터에 설치하기](#다른-컴퓨터에-설치하기) 참고.

---

## ⚠️ 먼저 읽을 것

화면에 뜬 것은 전부 담긴다. 내가 보낸 메시지뿐 아니라 **상대가 보낸 메시지, 남의 이름과 전화번호,
비밀번호 입력창, 사내 문서, 열려 있던 은행 화면**이 그대로 프레임에 들어간다.

- **회사·학교 PC에서는 쓰지 말 것.** 대개 취업규칙이나 정보보안 규정 위반이고, 담기는 자료의
  소유자가 본인이 아니다.
- 화상회의 화면을 담으면 **상대의 얼굴과 발언이 영상으로 남는다.** 소리를 담지 않아도 마찬가지다.
- 담긴 파일은 본인의 Drive에만 올라간다. 서버도 계정도 없고 개발자를 포함한 제3자에게 가는 것은 없다.
  그래도 **Drive 계정이 털리면 화면 기록이 통째로 털린다는 뜻**이기도 하다. 2단계 인증을 켜 둘 것.

이 저장소는 코드를 제공할 뿐이고, **사용에 따른 법적 책임은 전적으로 사용자에게 있다**(LICENSE 참고).
보관 범위와 기간은 스스로 정할 것.

---

## 무엇을 어떻게 모으는가

| 항목 | 방식 |
|---|---|
| 화면 | gdigrab으로 **가상 데스크톱 전체**(모니터를 전부 이어 붙인 한 프레임)를 **논리 해상도의 75%** 크기로, 초당 2장. H.264 CRF 26이라 정지 화면일 땐 용량이 거의 안 늘어난다 |
| 크기 기준 | Windows 화면 배율을 읽어 자동으로 정한다. 150% 화면이면 물리 픽셀의 0.5배, 100% 화면이면 0.75배다. **배율이 다른 컴퓨터끼리 글자 크기가 같아지도록** 논리 해상도를 기준으로 잡았다. 셋 다 실측해서 한글·코드 판독에 차이가 없는 것을 확인하고 가장 싼 것을 골랐다 |
| 컴퓨터 이름 | 올라가는 파일 이름 끝에 붙는다(`_home`). 처음 실행할 때 한 번 정하고, 정하기 전에는 녹화가 시작되지 않는다 |
| 분할 | 벽시계 정각(HH:00:00) 기준. 첫 세그먼트만 짧고 이후는 1시간 단위. ffmpeg의 segment 먹서가 키프레임에서 끊는다 |
| 쉬는 때 | 세션 잠금·모니터 꺼짐·절전 동안에는 담을 화면이 없어 쉰다. 풀리면 바로 다시 시작한다. 그 구간은 파일이 없는 공백으로 남는다 |
| 업로드 | Drive 재개 가능 업로드(8MB 청크). 크기·MD5 검증 후 로컬 삭제. 세그먼트가 닫힐 때마다 + 30분마다 |
| 수집 기록 | 올린 파일을 하루치 JSONL(`pcindex_yyyy-MM-dd.jsonl`)로 `LifeRecorder/index`에 남긴다. 보관 기간이 지나 원본을 지워도 무엇이 언제 수집됐는지는 남는다 |
| 생존 | 트레이 상주 + 로그인 시 자동 시작. ffmpeg이 죽으면 10초 → 30초 → 1분 → 5분 간격으로 계속 다시 붙는다 |

로컬 저장 위치: `%LOCALAPPDATA%\LifeRecorder\`

```
work\   pcscreen_2026-09-07_13-00-00.mp4   ffmpeg이 지금 쓰는 중
queue\  pcscreen_2026-09-07_12-00-00.mp4   완성돼 업로드를 기다리는 것
index\  rawpcindex_2026-09-07.jsonl.part   오늘치 수집 기록
logs\   liferecorder-2026-09-07.log        2주치
```

업로더는 `queue\`만 본다. 쓰는 중인 파일이 올라갈 일이 없다.

### 안드로이드 앱과 한 폴더를 쓰는 방법

```
LifeRecorder/
  screen/   screen_2026-09-07_13-00-00.mp4          ← 폰 (안드로이드 앱)
            pcscreen_2026-09-07_13-00-00_home.mp4   ← 집 PC
            pcscreen_2026-09-07_13-00-00_school.mp4 ← 학교 PC
  index/    index_2026-09-07.jsonl                  ← 폰
            pcindex_2026-09-07_home.jsonl           ← 집 PC
            pcindex_2026-09-07_school.jsonl         ← 학교 PC
```

앞은 `pc` 접두어로 폰과 갈리고, 뒤는 컴퓨터 이름으로 PC끼리 갈린다.
시각이 이름보다 앞에 있어서 다 섞어 놓아도 이름순 정렬이 곧 시간순이다.

안드로이드 쪽 `IndexRestore`는 `index_`로 시작하는 파일만 읽으므로
서로의 수집 기록을 건드리지 않는다. 자세한 규칙은 [DESIGN.md](DESIGN.md)에 있다.

---

## 시작하기

### 1. 빌드

.NET 8 SDK가 필요하다.

```powershell
git clone https://github.com/sehunYang/life-recorder-for-windows.git
cd life-recorder-for-windows
.\scripts\get-ffmpeg.ps1                       # tools\ffmpeg\ffmpeg.exe 를 받는다
dotnet build src\LifeRecorderWin\LifeRecorderWin.csproj -c Release
```

단일 exe로 묶으려면:

```powershell
dotnet publish src\LifeRecorderWin\LifeRecorderWin.csproj -c Release -p:PublishSingleFile=true -o dist
Copy-Item tools\ffmpeg\ffmpeg.exe dist\           # exe 옆에 두면 앱이 찾는다
```

ffmpeg 바이너리는 저장소에 넣지 않는다(용량 + GPL 재배포). 이 앱은 ffmpeg을
**별개 프로세스로 실행**할 뿐 링크하지 않으므로 앱 자체는 MIT 그대로다.

> `dotnet build`로 만든 exe를 그냥 실행하면 **.NET 8 데스크톱 런타임**이 있어야 한다.
> 없으면 아무 로그도 남기지 않고 "런타임을 설치하세요" 대화상자만 뜬다.
> 런타임을 깔기 싫으면 위의 `PublishSingleFile` 쪽을 쓴다 (런타임이 exe 안에 들어간다).

### 2. Google Drive 연결

안드로이드 앱은 패키지명 + 서명 SHA-1로 클라이언트를 등록해서 앱에 아무것도 넣지 않았지만,
데스크톱 앱은 OAuth 클라이언트를 직접 만들어 넣어야 한다.

1. [Cloud Console](https://console.cloud.google.com/apis/credentials)에서 **안드로이드 앱과 같은 프로젝트**를 고른다
   (Drive API가 이미 켜져 있다. 새 프로젝트라면 Drive API부터 사용 설정할 것)
2. **사용자 인증 정보 만들기 → OAuth 클라이언트 ID → 애플리케이션 유형: 데스크톱 앱**
3. 만들어진 **클라이언트 ID**와 **클라이언트 보안 비밀**을 복사
4. 앱을 실행하고 **Google 계정 연결** → 두 값을 붙여넣기 → 브라우저에서 동의

리디렉션은 `http://127.0.0.1:<임시포트>`로 이 PC 안에서만 오간다.
리프레시 토큰은 **DPAPI로 이 PC의 이 사용자 계정에 묶어** `credentials.bin`에 넣는다.
파일을 복사해 다른 PC에 붙여도 풀리지 않는다.

> **스코프가 안드로이드와 다르다.** 폰은 `drive.file`(자기가 만든 파일만)을 쓰지만,
> 그 스코프로는 다른 OAuth 클라이언트가 만든 `LifeRecorder` 폴더가 **보이지 않아**
> 같은 이름의 폴더를 루트에 하나 더 만들게 된다. 기존 폴더에 그대로 넣기 위해
> 데스크톱은 전체 `drive` 스코프를 쓴다. 바꾸려면 `Config.OAuthScope`.

> **테스트 모드면 7일마다 다시 연결해야 한다.** OAuth 동의 화면의 게시 상태가
> "테스트"이면 Google이 리프레시 토큰을 7일 만에 만료시킨다. 앱은 이때 연결이 끊긴 것을
> 알아채고 상태창에 알리며, 그동안 녹화는 계속되고 파일은 로컬에 쌓인다.
> 매주 다시 누르기 싫으면 동의 화면의 게시 상태를 **"프로덕션"으로 바꾼다.**
> 심사를 받지 않아도 본인 계정은 "고급 → 계속"으로 통과할 수 있고, 그러면 만료되지 않는다.

### 3. 실행

`LifeRecorder.exe`를 실행하면 트레이에 앉는다.

**처음 실행하면 창이 뜨고 컴퓨터 이름을 묻는다.** 짧게 `home`, `school` 같은 것을 넣고 저장한다.
이 이름이 올라가는 파일 끝에 붙어 어느 컴퓨터 화면인지 가른다.
정하기 전에는 녹화가 시작되지 않는다 — 컴퓨터 두 대가 같은 이름으로 올리면
`pcindex_<날짜>.jsonl`이 매일 충돌하기 때문이다.

아이콘 색이 상태다.

| 색 | 뜻 |
|---|---|
| 🔴 빨강 | 기록 중 |
| 🟡 노랑 | 잠금·모니터 꺼짐·절전으로 쉬는 중, 또는 다시 붙는 중 |
| ⚫ 회색 | 꺼짐 |

**Windows 시작할 때 자동으로 실행**을 켜 두면 로그인할 때마다 트레이에만 뜨고,
저장된 ON/OFF 상태를 그대로 이어받는다. 창을 닫아도 앱은 트레이에 남는다.
끝내려면 트레이 메뉴에서 **끝내기**.

---

## 다른 컴퓨터에 설치하기

한 대에서 만든 것을 통째로 옮기면 된다. **받는 쪽에는 .NET도 ffmpeg도 깔 필요가 없고,
관리자 권한도 필요 없다.** 자립형(self-contained) exe라 런타임이 안에 들어 있다.

### 1) 만드는 컴퓨터에서 — 꾸러미 만들기

```powershell
.\scripts\get-ffmpeg.ps1
dotnet publish src\LifeRecorderWin\LifeRecorderWin.csproj -c Release -p:PublishSingleFile=true -o dist
Copy-Item tools/ffmpeg/ffmpeg.exe dist/
Remove-Item dist/*.pdb
Compress-Archive -Path dist\* -DestinationPath LifeRecorder-win-x64.zip -Force
```

`dist\`에 남는 것은 두 개뿐이다. 압축하면 약 116MB다.

```
LifeRecorder.exe   약 154MB (.NET 런타임이 안에 들어 있다)
ffmpeg.exe         약 139MB
```

### 2) 옮기기 — GitHub 릴리스

저장소가 비공개라 zip을 저장소에 넣을 수는 없다(파일 100MB 제한). **릴리스에 첨부한다.**

```powershell
gh release create v1.0.0 LifeRecorder-win-x64.zip --title "v1.0.0" --notes "첫 배포"
```

받는 컴퓨터에서:

```powershell
gh auth login                                   # 처음 한 번만
gh release download v1.0.0 --repo sehunYang/life-recorder-for-windows
Expand-Archive LifeRecorder-win-x64.zip -DestinationPath "$env:LOCALAPPDATA\Programs\LifeRecorder"
```

`gh`를 깔 수 없으면 브라우저로 GitHub에 로그인해서 릴리스 페이지에서 직접 내려받아도 된다.
USB로 옮겨도 똑같다 — 두 파일이 한 폴더에 같이 있기만 하면 된다.

> 어디에 두든 상관없지만 `Program Files`는 피한다. 관리자 권한이 필요하고, 이 앱은 필요 없다.
> `%LOCALAPPDATA%\Programs\LifeRecorder` 가 무난하다.

### 3) 받는 컴퓨터에서 — 처음 켤 때

1. `LifeRecorder.exe` 실행
   - "Windows의 PC 보호" 경고가 뜨면 **추가 정보 → 실행**. 서명하지 않은 exe라 그렇다
2. **컴퓨터 이름**을 넣고 저장 (`home`, `school` 처럼 짧게). 컴퓨터마다 **다르게** 정할 것
3. **Google 계정 연결** → 클라이언트 ID·보안 비밀 붙여넣기 → 브라우저에서 동의
   - **두 컴퓨터가 같은 클라이언트 ID·보안 비밀을 쓰면 된다.** 새로 만들 필요 없다
   - 계정 연결은 컴퓨터마다 한 번씩 해야 한다. 토큰이 그 PC의 그 사용자 계정에 묶여 있어서
     (DPAPI) `credentials.bin`을 복사해 봐야 풀리지 않는다
4. **Windows 시작할 때 자동으로 실행** 체크
5. **기록 시작 (ON)**

### 4) 업데이트

새 버전을 만들어 릴리스에 올리고, 받는 쪽에서 앱을 끝낸 뒤 exe만 덮어쓴다.
설정·토큰·아직 못 올린 파일은 `%LOCALAPPDATA%\LifeRecorder\`에 따로 있어서 그대로 남는다.

---

## 여러 대에서 쓸 때 알아 둘 것

- **컴퓨터 이름을 반드시 다르게 정할 것.** 같으면 `pcindex_<날짜>.jsonl`이 매일 충돌한다.
  Drive는 같은 이름 파일을 그냥 두 개 만들어 버려서 어느 쪽 것인지 알 수 없게 된다.
  나중에 이름을 바꿔도 **이미 올라간 파일은 예전 이름 그대로** 남는다.
- **화면 크기 설정은 컴퓨터마다 알아서 맞춰진다.** 화면 배율을 읽어 계산하므로
  배율이 다른 컴퓨터에서도 글자 크기가 같게 나온다. 손댈 것이 없다.
- **두 대를 동시에 켜 둬도 된다.** 시간이 겹치는 기록이 각각 남는다.
- 로컬에 쌓이는 파일과 설정은 컴퓨터마다 따로다. 옮기거나 합칠 필요 없다.

### 학교·회사 컴퓨터라면

화면에 뜬 것은 전부 담긴다. 학생 명단, 성적, 상담 기록, 남의 개인정보가 화면에 있었다면
그것도 그대로 영상으로 남아 개인 Google Drive에 올라간다. 기관 규정과 개인정보 보호 의무를
먼저 확인하는 편이 좋다. 판단은 사용자 몫이고, 이 저장소는 코드를 제공할 뿐이다(LICENSE 참고).

기술적으로 걸릴 수 있는 것들:

| 증상 | 이유와 대처 |
|---|---|
| exe 실행이 막힌다 | AppLocker/SmartScreen. 서명이 없어서다. IT 정책이면 우회하지 말 것 |
| 화면이 검게 나온다 | 원격 데스크톱(RDP) 세션이거나 보호 콘텐츠. gdigrab의 한계다 |
| 로그인할 때마다 초기화된다 | 로밍 프로필/복원 정책이 걸린 PC. `%LOCALAPPDATA%`가 유지되지 않으면 매번 다시 연결해야 한다 |


## 용량 감각

정지 화면에서는 비트를 거의 쓰지 않는다. 아래는 **모니터 세 대(물리 5760×2172, 배율 150%)** 에서
터미널 출력이 계속 흐르는 **바쁜 화면**을 40초씩 실측한 값이라, 실제로는 이보다 적다.

| `ScreenTargetLogicalScale` | 이 컴퓨터의 프레임 | 시간당 | 하루 8시간 | 판독 |
|---|---|---|---|---|
| **0.75 (기본)** | 2880×1086 | **563MB** | 4.5GB | 한글·코드 모두 읽힘 |
| 1.0 | 3840×1448 | 930MB | 7.4GB | 화면에서 보이는 그대로 |
| (물리 원본) | 5760×2172 | 1.9GB | 15GB | 위와 눈에 띄는 차이 없음 |

논리 해상도가 다르면 픽셀 수에 대략 비례해 바뀐다. 컴퓨터가 두 대면 두 배가 아니라,
**각 컴퓨터가 켜져 있는 시간만큼** 든다.

더 줄이려면 [`Config.cs`](src/LifeRecorderWin/Config.cs)에서 `ScreenCrf`를 28~30으로 올린다
(움직임이 많은 구간에서만 뭉개진다). 실측으로 CRF 26 → 28이 대략 20% 줄었다.

---

## 알려진 한계

- **전체화면 DirectX 게임과 DRM 보호 영상은 검게 나올 수 있다.** gdigrab은 화면 DC를 BitBlt로
  긁는 방식이라 하드웨어 오버레이로 합성되는 것을 놓친다. 일반 창·브라우저·문서는 정상이다.
  전부 담고 싶으면 `Config.cs`의 인코더 쪽을 ddagrab(Desktop Duplication) 기반으로 바꿔야 하는데,
  ddagrab은 모니터를 하나씩만 잡아서 여러 대를 붙이려면 필터 그래프를 따로 짜야 한다.
- **잠금·화면 꺼짐 동안은 공백이다.** 담을 화면이 없어 의도한 것이다.
- **모니터를 붙이거나 빼면 그 자리에서 세그먼트가 갈린다.** ffmpeg이 입력 크기를 도중에 못 바꿔서
  세션을 다시 연다. 1~2초 공백이 생긴다.
- **강제 종료(작업 관리자, 정전)되면 쓰던 세그먼트 하나가 사라진다.** mp4는 닫힐 때 `moov`가 쓰이는데
  그게 없으면 재생이 안 된다. 다음 실행에서 검사해 버린다. 안드로이드도 같은 이유로 같게 동작한다.
- Wi-Fi 전용 / 충전 중에만 같은 업로드 제약은 데스크톱에서 뜻이 없어 뺐다.

---

## 설정 바꾸기

해상도·fps·비트레이트·세그먼트 길이·업로드 주기·Drive 폴더는 전부
[`Config.cs`](src/LifeRecorderWin/Config.cs) 한 곳에서 바꾼다.

화면 크기는 `ScreenTargetLogicalScale`(논리 해상도 대비, 기본 0.75)로 정한다.
배율 계산을 무시하고 물리 픽셀 대비 배율을 직접 박고 싶으면 `ScreenScaleOverride`를 쓴다.

하드웨어 인코더를 쓰고 싶으면 `ScreenEncoder`를 `h264_nvenc`(NVIDIA) / `h264_qsv`(인텔) /
`h264_amf`(AMD)로 바꾼다. 초당 2장이라 libx264로도 CPU를 거의 안 쓴다.

---

## 테스트

[TESTING.md](TESTING.md)에 손으로 확인하는 순서를 적어 뒀다.

## 라이선스

MIT. [LICENSE](LICENSE) 참고.
