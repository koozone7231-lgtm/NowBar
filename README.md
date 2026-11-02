# NowBar

Windows 화면 상단 중앙에 떠 있는 캡슐형 위젯입니다. 평소에는 **선택한 항목 하나**만 작게 보여 주고, 마우스를 올리면 **날씨 · 음악 · AI 작업(Claude Code)** 세 탭과 상세 정보·제어 버튼이 아래로 펼쳐집니다.

```
평소:        [ ✦ AI 작업 작업 중 ]
마우스 오버:  [ ☀ 날씨 9° ] | [ ♪ 음악 재생 중 ] | [ ✦ AI 작업 작업 중 ]
              └─ 선택한 탭의 상세 패널 ─────────────────────┘
```

- C# / .NET 8 / WPF (MVVM, CommunityToolkit.Mvvm)
- 날씨: Open-Meteo (API 키 불필요)
- 음악: Windows Global System Media Transport Controls (GSMTC)
- AI 작업: Claude Code 공식 Hooks → 로컬 HTTP 서버(Kestrel, `127.0.0.1` 전용)

---

## 1. 실행 환경

| 항목 | 요구 사항 |
|---|---|
| OS | Windows 10 2004(19041) 이상 / Windows 11 |
| 런타임 (배포 빌드 실행 시) | .NET 8 **Desktop Runtime** + .NET 8 **ASP.NET Core Runtime** (x64) |
| 빌드 시 | .NET 8 SDK |

런타임이 없다면 https://dotnet.microsoft.com/download/dotnet/8.0 에서 두 런타임을 설치하세요.
런타임 설치 없이 실행하려면 아래 "자체 포함 빌드"를 사용하세요.

## 2. 실행 방법

아래 "3. 직접 빌드"의 배포 명령으로 만든 Release 빌드(저장소에는 포함되지 않음):

```bash
publish/NowBar/NowBar.exe
```

- 실행하면 메인 모니터 상단 중앙(위에서 8px)에 캡슐이 나타납니다.
- 작업 표시줄에는 표시되지 않습니다. **시스템 트레이 아이콘**에서 설정 열기 / 숨기기 / 종료를 할 수 있습니다.
- 캡슐을 오른쪽 클릭해도 같은 메뉴가 나옵니다.

### 이전 이름(TOPBAR)에서 업그레이드

NowBar를 처음 실행하면 `%AppData%\Topbar`의 설정·날씨 캐시·Claude 연동 토큰을 `%AppData%\NowBar`로 복사합니다(원본은 남겨 둠).
Claude Code Hook 주소(`127.0.0.1:17432/api/claude-hook`)와 토큰이 그대로라 연동을 다시 설정할 필요가 없습니다.
이전 이름의 자동 실행 항목(`TOPBAR`)이 있으면 `NowBar`로 바꿔 등록합니다.

## 3. 직접 빌드

```bash
dotnet build NowBar.sln -c Release
```

```bash
dotnet test NowBar.sln
```

배포 폴더 만들기 (런타임 필요, 약 25MB):

```bash
dotnet publish src/NowBar.App/NowBar.App.csproj -c Release -r win-x64 --self-contained false -o publish/NowBar
```

자체 포함 빌드 (런타임 불필요, 용량 큼):

```bash
dotnet publish src/NowBar.App/NowBar.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/NowBar-standalone
```

## 4. 사용법

| 동작 | 결과 |
|---|---|
| 평소 (마우스 없음) | 선택한 항목 하나만 표시 (기본 너비 200px, 설정 → "축소 시 너비") |
| 캡슐에 마우스 올리기 | 세 탭이 나타나며 아래로 확장 (약 200ms, EaseOut) |
| 마우스가 패널 밖으로 나감 | 0.5초 후 다시 항목 하나로 축소 (설정에서 변경). 다시 들어오면 축소 취소 |
| 탭 클릭 | 내용 전환 + 축소 시 보여 줄 항목 선택. 마지막 탭은 다음 실행 때도 유지 |
| 📌 고정 버튼 | 마우스가 나가도 확장 유지 |
| `ESC` | 고정 해제 + 축소 (NowBar를 한 번 클릭해 키보드 포커스가 있을 때) |
| 슬라이더 드래그 / 메뉴 열림 | 조작 중에는 닫히지 않음 |

- 마우스를 올리는 것만으로는 다른 프로그램의 키보드 포커스를 가져오지 않습니다.
- 전체 화면 앱(게임·발표)이 실행 중이면 자동으로 숨깁니다 (설정에서 끌 수 있음).

## 5. 날씨

1. 확장 패널 오른쪽 위 설정 아이콘(⚙) → **날씨** → 지역 검색
2. 결과 목록에서 지역을 클릭하면 바로 적용됩니다.

- 위치는 자동으로 수집하지 않습니다. 사용자가 직접 고른 지역만 사용합니다.
- 한글 검색은 전체 이름이 필요합니다: `서울특별시`, `부산광역시` (짧은 `서울`은 Open-Meteo가 결과를 주지 않음). 영문 `Seoul`, `Busan` 도 됩니다.
- 기본 15분마다 갱신(10~60분 설정 가능). 실패하면 마지막 성공 데이터를 보여 주고 "업데이트 실패 · HH:mm 데이터"로 알려 줍니다.
- 표시 항목: 지역, 업데이트 시간, 아이콘, 기온, 날씨 상태, 체감 온도, 습도, 강수 확률, 오늘 최고/최저.

## 6. 음악 / 동영상

Windows 시스템 미디어 세션을 제공하는 프로그램(예: Spotify, Windows 미디어 플레이어, Chrome/Edge 등 브라우저의 미디어)이 재생하는 정보를 읽고 제어합니다.

- 표시: 앨범 이미지/썸네일, 제목, 아티스트, 재생 프로그램, 상태, 현재/전체 시간, 진행률
- 제어: 이전 곡 · 재생/일시정지 · 다음 곡 · 재생 위치(슬라이더 드래그) · 재생 프로그램 열기(↗)
- 여러 프로그램이 동시에 세션을 제공하면 확장 화면 위쪽에 프로그램 선택 버튼이 나타납니다.
- 프로그램이 지원하지 않는 명령(예: 일부 브라우저의 위치 변경)은 버튼이 비활성화되거나 안내 문구가 표시됩니다.

## 7. Claude Code 연동 (AI 작업 탭)

NowBar는 Claude Code가 **공식 제공하는 Hooks**만 사용합니다. Claude의 내부 사고 과정, 웹 채팅, 브라우저 데이터는 읽지 않습니다.

### 자동 설정 (권장)

설정 → **AI 작업 (Claude Code)** → **[Claude Code 연동 설정]**

1. Claude Code 설정 파일 위치 확인 (`%USERPROFILE%\.claude\settings.json`, `CLAUDE_CONFIG_DIR`이 있으면 그 위치)
2. NowBar 로컬 서버 상태 확인 (`http://127.0.0.1:17432/api/claude-hook`)
3. 인증 토큰 준비 (설치 시 무작위 생성, `%AppData%\NowBar\settings.json`에 보관)
4. **변경 내용 미리보기 창**에서 추가될 항목을 확인하고 승인 (취소하면 파일은 바뀌지 않음)
5. 기존 `settings.json`을 `settings.json.nowbar-backup-날짜-시간`으로 백업
6. 기존 설정과 다른 Hooks는 그대로 두고 NowBar 항목만 병합하여 저장
7. 로컬 수신 테스트
8. 결과 표시

> 이미 실행 중인 Claude Code 세션에는 재시작 후 적용될 수 있습니다.
> **[연결 해제]** 는 NowBar가 추가한 Hook만 제거합니다(마찬가지로 백업 후 승인).

### 등록되는 Hook

`UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PostToolUseFailure`, `PermissionRequest`, `Notification`, `Stop`, `StopFailure`, `SessionEnd` 에 아래 HTTP Hook 하나씩:

```json
{
  "type": "http",
  "url": "http://127.0.0.1:17432/api/claude-hook",
  "timeout": 3,
  "headers": { "Authorization": "Bearer <NowBar 토큰>" }
}
```

- `SessionStart`는 Claude Code가 HTTP Hook을 지원하지 않는 이벤트라 사용하지 않습니다. 세션은 첫 이벤트를 받을 때 표시됩니다.
- NowBar는 항상 **빈 본문의 200** 으로 응답하므로 Claude의 동작(권한 결정 등)에 영향을 주지 않습니다. NowBar가 꺼져 있으면 연결 실패로 처리되며 Claude 작업은 그대로 진행됩니다.
- 사용자 설정에 `allowedHttpHookUrls` 허용 목록이 있다면 NowBar URL도 허용해야 합니다 (설정 도우미가 경고를 표시합니다).

### 수동 설정

자동 설정을 쓰지 않으려면 **[수동 설정용 JSON 복사]** 로 위 형식의 JSON을 복사해 `settings.json`의 `hooks`에 직접 병합하세요. 복사본에는 토큰이 들어 있으니 공유하지 마세요.

### 상태 표시

| 상태 | 의미 | 바뀌는 이벤트 |
|---|---|---|
| 대기 중 (IDLE) | 진행 중인 작업 없음 / 세션 종료 | `SessionEnd` |
| 작업 중 (WORKING) | 요청 처리 중 | `UserPromptSubmit`, `PostToolUse` |
| 도구 실행 중 (TOOL_RUNNING) | 파일 수정·명령 실행 등 | `PreToolUse` |
| 응답 필요 (WAITING) | 승인·입력 대기 | `PermissionRequest`, `Notification(permission_prompt 등)` |
| 완료 (COMPLETED) | 응답 완료 | `Stop` |
| 오류 (ERROR) | API 오류로 중단 | `StopFailure` |

- 확장 화면: 연결 상태, 프로젝트 이름(cwd의 폴더명), 작업 요약(선택), 최근 활동(예: "파일 수정 중 · MainWindow.xaml"), 시작 시간, 경과 시간, 최근 알림.
- 진행률은 측정할 수 없으므로 퍼센트를 만들지 않고 상태 텍스트로만 표시합니다.
- 여러 Claude Code 세션은 `session_id`로 구분되어 프로젝트 이름 버튼으로 전환할 수 있습니다.

### 개인정보 · 보안

- 서버는 `127.0.0.1`에만 바인딩되고, 토큰이 일치하는 요청만 받습니다(고정 시간 비교). 웹 페이지발 요청(`Origin` 헤더)은 거부합니다.
- 프롬프트·명령어·파일 내용·대화 내역은 **저장하지 않고 외부로 보내지 않습니다**. 파일 도구의 경우 파일 이름(경로 제외)만 표시합니다.
- "작업 요약 표시"는 기본 꺼짐입니다. 켜면 프롬프트 앞부분을 한 줄로 줄이고(기본 40자) 비밀값처럼 보이는 문자열을 가린 뒤 **메모리에만** 보관합니다. 끄면 즉시 지웁니다.
- 진단 로그(`nowbar.log`)에는 이벤트 이름과 도구 이름만 기록됩니다.

### Hooks를 쓸 수 없을 때

설정 → **수동 작업 등록**에서 작업 이름을 입력해 시작/완료를 직접 표시할 수 있습니다.

## 8. 설정 항목

| 구분 | 항목 |
|---|---|
| 일반 | Windows 시작 시 자동 실행, 자동 확장, 항상 위, 전체 화면 앱에서 숨기기, 자동 축소 지연, 상단 여백 / 가로 위치, 캡슐 크기, 확장 패널 크기 |
| 디자인 | 다크/밝은 모드, 배경 불투명도, 모서리 둥글기, 애니메이션 속도, 강조 색상 |
| 날씨 | 지역 검색/선택, 온도 단위, 업데이트 주기 |
| 음악 | 현재 세션 자동 선택, 세션 선택 버튼 표시, 앨범 이미지 표시 |
| AI | 연결 상태, 연동 설정/테스트/해제, 토큰 재생성, 포트, 작업 요약·알림·세션 표시 |

자동 실행은 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 의 `NowBar` 값으로 등록됩니다(관리자 권한 불필요).

## 9. 데이터 위치

| 파일 | 내용 |
|---|---|
| `%AppData%\NowBar\settings.json` | 모든 설정, 마지막 탭, 지역, 인증 토큰 |
| `%AppData%\NowBar\weather-cache.json` | 마지막으로 성공한 날씨 |
| `%AppData%\NowBar\nowbar.log` | 오류/진단 로그 (1MB 초과 시 교체) |

설정 파일이 손상되면 `settings.json.corrupt-*` 로 옮기고 기본값으로 시작합니다. 저장은 임시 파일 → 교체 방식이라 갑작스러운 종료에도 깨지지 않습니다.

## 10. 프로젝트 구조

```
NowBar.sln
src/NowBar.App/
  App.xaml(.cs)            시작/종료, 단일 실행, 전역 예외 처리, 트레이
  MainWindow.xaml(.cs)     상단 캡슐 창: 위치·애니메이션·마우스 오버·항상 위
  Views/                   WeatherView, MusicView, AIActivityView, SettingsView/Window, HooksApprovalWindow
  ViewModels/              Main, Weather, Music, AIActivity, Settings
  Models/                  AppSettings, WeatherData, MediaSessionData, ClaudeSession
  Services/                WeatherService, MediaService, ClaudeHookServer, ClaudeSessionTracker,
                           ClaudeHooksSetupService, SettingsService, StartupService, ThemeService, TrayIconService
  Helpers/                 위치/애니메이션/Win32/변환기/로거
  Controls/Icon.cs         24×24 선 아이콘 렌더러
  Resources/               Styles.xaml, Icons/Icons.xaml, nowbar.ico
tests/NowBar.Tests/        xUnit 테스트
publish/NowBar/            Release 배포 빌드
```

개발용 시작 옵션: `--tab weather|music|ai` (시작 탭), `--pinned` (확장·고정 상태로 시작).

## 11. 검증 결과

| 항목 | 결과 | 확인 방법 |
|---|---|---|
| 상단 중앙 캡슐 표시, 세 탭 구분 | ✅ | 실제 실행 후 화면 캡처 |
| 마우스 오버 확장 / 지연 축소 / 패널 이동 중 유지 | ✅ | 커서 이동 자동화 + 화면 픽셀 검사 |
| 탭 클릭 전환 | ✅ | 실행 확인 |
| 날씨 실제 데이터 | ✅ | Open-Meteo 실호출 (통합 테스트 + 화면) |
| 인터넷 끊김 시 종료 안 됨 / 캐시 표시 | ✅ | 프록시 차단으로 오프라인 재현 |
| 실제 미디어 세션 정보 표시 | ✅ | 별도 프로세스(SMTC 등록 플레이어)로 확인 |
| 재생/일시정지, 이전/다음, 재생 위치 | ✅ | 외부 플레이어가 명령 수신 로그 확인 |
| Claude Code 이벤트 → 로컬 서버 → UI 반영 | ✅ | 실제 `claude -p` 실행 (임시 설정 파일 사용) |
| 기존 Claude 설정 보존 / 백업 / 해제 | ✅ | 단위 테스트 + 설정 창 버튼 UI 자동화 |
| 설정 유지(재실행) | ✅ | 단위 테스트 (저장→재로드) |
| 시작 프로그램 등록 | ✅ (로직) | 테스트용 값 이름으로 레지스트리 등록/해제 확인 |
| Release 빌드 | ✅ | 경고 0, 오류 0 / `dotnet test` 33개 통과 |
| 유휴 리소스 | CPU 약 0.03%, 메모리 약 91MB(private) | 안정화 후 20초 측정 |

### 알려진 제한

- **메인 모니터 전용**입니다. 보조 모니터 배치, 모니터별 DPI가 다른 환경은 지원하지 않습니다.
- `ESC`는 NowBar 창이 키보드 포커스를 가진 경우에만 동작합니다(마우스 오버만으로 포커스를 빼앗지 않기 위한 설계). 고정 버튼으로도 해제할 수 있습니다.
- Claude Code에서 작업을 `Esc`로 중단하면 `Stop` 이벤트가 오지 않아 "작업 중"으로 남을 수 있습니다. 10분간 이벤트가 없으면 안내 문구가 표시되고, 12시간이 지나면 정리됩니다.
- 미디어 정보·제어 범위는 각 프로그램의 Windows 미디어 세션 지원 수준에 따라 다릅니다. 일부 앱은 재생 위치 보고가 늦어, NowBar가 이동 직후 위치를 자체적으로 유지합니다.
- 밝은 모드는 동일한 브러시 교체 방식으로 구현되어 있으나, 화면 캡처 검증은 다크 모드에서만 했습니다.
