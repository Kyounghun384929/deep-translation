# Deep Translation

DeepL처럼 쓰는 Windows 번역기 — 단, 번역은 **내장 로컬 LLM**이 합니다.
인터넷으로 텍스트가 나가지 않는 완전 로컬 번역기입니다.

- **내장 번역 엔진**: LM Studio 없이 동작. 설정에서 모델을 1회 다운로드하면 앱이
  llama.cpp(llama-server)를 직접 기동·관리. 유휴 시(기본 5분) 프로세스를 내려 RAM/VRAM 자동 회수
- 아무 앱에서나 텍스트를 선택하고 **Ctrl+C, C** (빠르게 두 번 복사) → 번역 팝업이 뜸
- **단축키 사용자 지정**: 설정에서 원하는 조합(예: `Alt+Q`, `Ctrl+Shift+T`, `F9`)으로 변경 가능.
  Ctrl+C가 아닌 조합은 누르는 순간 선택 영역을 자동으로 복사해 번역
- 기본 동작: **모든 언어 → 한국어** (원문이 이미 한국어면 → 영어, 설정 변경 가능).
  모델이 첫 줄에 언어 마커를 붙여 실제 번역 언어를 알려주므로, 원문이 이미 대상 언어일 때의 자동 전환이 더 정확
- **팝업에서 대상 언어 즉시 변경**: 번역 창의 언어 드롭다운으로 바꾸면 바로 다시 번역
- **용어집**: 자주 쓰는 용어의 번역을 설정에 등록하면(한 줄에 `원어 = 번역어`) 그 표현을 일관되게 사용
- 번역 결과가 토큰 단위로 실시간 스트리밍 표시
- **반응 속도**: 단축키를 누르면 클립보드 변화를 감지하는 즉시 진행(고정 대기 없이), 모델 목록은 잠시 캐시
- **LLM 중복 호출 방지 (LlmGuard)**: 생성 요청 전역 직렬화(동시에 1개), 동일 요청 캐시 응답(30분/32개),
  연속 호출 냉각 간격. **다시 번역** 버튼(Ctrl+Enter)은 캐시를 무시하고 새로 생성
- 트레이 상주형. **Windows 시작 시 자동 실행 On/Off** — 트레이 우클릭 메뉴에서 즉시 토글 (설정 창에서도 가능, 설치 시 선택도 반영)

## 설치

`dist\DeepTranslation-Setup-1.0.0.exe`를 실행하세요. (.NET 런타임 포함, 별도 설치 불필요)

설치 없이 쓰려면 `dist\DeepTranslation.exe` 단일 파일을 실행해도 됩니다.

## 시작하기

1. 트레이 아이콘 우클릭 → **설정** → 번역 엔진에서 모델을 **다운로드**합니다
   (기본: Qwen3 4B, 2.5GB — 1회만 받으면 됩니다).
2. 텍스트를 선택하고 단축키를 누르면 바로 번역됩니다.

첫 번역 시 llama.cpp 번역 엔진(약 33MB)이 자동으로 다운로드됩니다 (역시 1회).
GPU(Vulkan)로 기동하지 못하면 자동으로 CPU 모드로 전환합니다.
번역이 없으면(기본 5분, 설정 가능) 엔진을 내려 메모리를 완전히 회수하고,
다음 번역 때 자동으로 다시 켭니다 (보통 1~2초).

제공 모델: Qwen3 4B(권장) · Gemma 3 4B · EXAONE 3.5 2.4B(한국어 특화, 비상업 라이선스) ·
HyperCLOVA X SEED 1.5B(경량). 라이선스 조건은 설정 창에 표시됩니다.

## 고급: LM Studio 연동

더 큰 모델을 쓰고 싶으면 설정에서 엔진을 **LM Studio 서버**로 전환할 수 있습니다.

1. [LM Studio](https://lmstudio.ai/)를 설치하고 채팅용 모델을 하나 내려받습니다.
2. 왼쪽 **개발자(Developer)** 탭에서 로컬 서버를 시작합니다 (기본 주소 `http://localhost:1234`).
3. 모델을 로드합니다. (로드하지 않아도 요청 시 자동 로드(JIT)가 동작하면 사용 가능)

## 사용법

| 동작 | 방법 |
|---|---|
| 번역 팝업 | 텍스트 선택 후 **Ctrl+C, C** (기본값 — 설정에서 임의 조합으로 변경 가능) |
| 단축키 변경 | 설정 → 번역 단축키 입력란 클릭 → 원하는 조합 누르기. 문자·숫자 키는 Ctrl/Alt/Win 조합 필요, F1–F24는 단독 가능. Ctrl+C 조합만 '두 번 누르기' 강제 |
| 다시 번역 | 팝업에서 원문 수정 (0.9초 후 자동 번역), 또는 **다시 번역** 버튼/**Ctrl+Enter** (캐시 무시, 새로 생성) |
| 대상 언어 변경 | 팝업의 "번역 →" 드롭다운 (즉시 재번역, 설정에도 저장됨) |
| 번역문 복사 | 팝업의 **복사** 버튼 |
| 창 닫기 | **Esc**, ✕, 또는 창 밖 클릭 (📌 고정 시 유지) |
| 직접 입력 번역 | 트레이 아이콘 더블클릭 → 원문 입력 |
| 용어집 | 설정 → 용어집에 `원어 = 번역어`를 한 줄에 하나씩 입력 (예: `LM Studio = LM Studio`) |
| 모델 변경/삭제 | 설정 → 번역 엔진에서 모델 선택·다운로드·삭제 (받다 만 파일은 이어받기) |
| 테마 변경 | 설정 → 번역 창 테마 (시스템 기본 / 라이트 / 다크 — 기본값은 Windows 설정 따름) |
| 설정 | 트레이 아이콘 우클릭 → 설정 (번역 엔진, 모델, 대상 언어, 용어집, 테마, 자동 실행 등) |
| 자동 시작 On/Off | 트레이 아이콘 우클릭 → **Windows 시작 시 자동 실행** (체크 표시 = 켜짐, 즉시 반영) |
| 업데이트 | 하루 1회 자동 확인(설정에서 끔 가능) 또는 트레이 우클릭 → **업데이트 확인**. 새 버전이 있으면 GitHub Releases에서 설치 프로그램을 받아 설치합니다 (저장소가 public일 때 동작) |
| 종료 | 트레이 아이콘 우클릭 → 종료 |

LM Studio 모드에서 모델을 지정하지 않으면(기본값: 자동) 서버에 **이미 로드된 모델을 우선** 사용하고,
로드 실패 시 다음 모델로 자동 재시도합니다.

## 빌드

```powershell
# .NET 8 SDK 필요 (winget install Microsoft.DotNet.SDK.8)
.\build.ps1              # dist\DeepTranslation.exe
.\build.ps1 -Installer   # + 설치 프로그램 (Inno Setup 6 필요)
```

개발용 연결 점검 (GUI 없이 번역 파이프라인 검증):

```powershell
.\dist\DeepTranslation.exe --selftest "Hello world" --out result.txt
# 모델 로드 없이 UI/파싱만 검증 (컴퓨터가 바쁠 때):
.\dist\DeepTranslation.exe --selftest --no-llm --out result.txt
```

## 문제 해결

| 증상 | 해결 |
|---|---|
| "모델이 아직 다운로드되지 않았습니다" | 설정 → 번역 엔진에서 선택한 모델을 다운로드 |
| "내장 번역 엔진을 시작하지 못했습니다" | 오류에 표시된 로그(`%LOCALAPPDATA%\DeepTranslation\llama-server.log`) 확인. 대개 메모리 부족 — 더 작은 모델(HyperCLOVA 1.5B) 선택 |
| "LM Studio 서버에 연결할 수 없습니다" | LM Studio 실행 → 개발자 탭에서 서버 시작(Status: Running) 확인 |
| "모델이 응답을 생성하지 못했습니다" | 모델이 메모리에 로드되지 못한 경우 (대개 메모리 부족). LM Studio에서 더 작은 모델을 로드하거나, 설정에서 모델을 직접 지정 |
| 번역이 느림 | 더 작은 모델 사용 권장. 첫 요청은 모델 로드 때문에 오래 걸릴 수 있음 |
| Ctrl+C, C가 안 먹힘 | 트레이 아이콘 우클릭 → 설정에서 단축키 활성화 확인. 관리자 권한 창 위에서는 일반 권한 앱의 훅이 동작하지 않음 |

설정 파일: `%APPDATA%\DeepTranslation\settings.json`
모델·엔진 파일: `%LOCALAPPDATA%\DeepTranslation\models`, `%LOCALAPPDATA%\DeepTranslation\llama`

## 알려진 제한 사항

**스마트 앱 컨트롤(SAC)이 켜진 PC**에서는 내장 번역 엔진(llama-server.exe, 무서명)의 실행이
차단됩니다. SAC은 앱별 예외 등록을 지원하지 않으므로, **LM Studio 모드**(서명된 앱이라 차단되지
않음)를 사용하거나 Windows 설정 → 개인 정보 및 보안 → Windows 보안 → 앱 및 브라우저 컨트롤에서
SAC을 꺼야 합니다. 설정 창과 오류 메시지에서도 같은 안내가 표시됩니다.

## 구조

```
DeepTranslation/            WPF 앱 (.NET 8, C#)
  App.xaml(.cs)             트레이 아이콘, 전역 훅 연결, 셀프테스트
  Services/
    KeyboardHookService.cs  저수준 키보드 훅 — Ctrl+C 두 번 감지
    LmStudioClient.cs       OpenAI 호환 API 클라이언트 (SSE 스트리밍)
    EmbeddedEngine.cs       내장 엔진 — llama-server 기동/폴백/유휴 언로드
    ModelCatalog.cs         내장 GGUF 모델 카탈로그 (4종)
    ModelDownloader.cs      대용량 다운로드 (.part 이어받기)
    TranslationService.cs   모델 자동 선택·재시도, 스트리밍 스로틀, think·마커 필터
    LanguageMaps.cs         한글 감지, 대상 언어 결정, 시스템 프롬프트(언어 마커·용어집)
    MarkerFilter.cs         출력 첫 줄의 언어 마커(@@언어@@) 감지·제거
    ThemeManager.cs         라이트/다크 테마 팔레트 적용 (시스템 설정 추적)
    UpdateChecker.cs        GitHub Releases 새 버전 확인
  Windows/
    TranslationWindow.xaml  번역 팝업 (라이트/다크 테마)
    SettingsWindow.xaml     설정 창
installer/setup.iss         Inno Setup 스크립트
tools/make-icon.ps1         앱 아이콘 생성
build.ps1                   퍼블리시 + 설치 프로그램 빌드
```
