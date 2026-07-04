# Deep Translation

DeepL처럼 쓰는 Windows 번역기 — 단, 번역은 **LM Studio의 로컬 LLM**이 합니다.
인터넷으로 텍스트가 나가지 않는 완전 로컬 번역기입니다.

- 아무 앱에서나 텍스트를 선택하고 **Ctrl+C, C** (빠르게 두 번 복사) → 번역 팝업이 뜸
- **단축키 사용자 지정**: 설정에서 원하는 조합(예: `Alt+Q`, `Ctrl+Shift+T`, `F9`)으로 변경 가능.
  Ctrl+C가 아닌 조합은 누르는 순간 선택 영역을 자동으로 복사해 번역
- 기본 동작: **모든 언어 → 한국어** (원문이 이미 한국어면 → 영어, 설정 변경 가능)
- 번역 결과가 토큰 단위로 실시간 스트리밍 표시
- 트레이 상주형. 설치 후 Windows 시작 시 자동 실행 옵션 지원

## 설치

`dist\DeepTranslation-Setup-1.0.0.exe`를 실행하세요. (.NET 런타임 포함, 별도 설치 불필요)

설치 없이 쓰려면 `dist\DeepTranslation.exe` 단일 파일을 실행해도 됩니다.

## 준비물: LM Studio

1. [LM Studio](https://lmstudio.ai/)를 설치하고 채팅용 모델을 하나 내려받습니다.
2. 왼쪽 **개발자(Developer)** 탭에서 로컬 서버를 시작합니다 (기본 주소 `http://localhost:1234`).
3. 모델을 로드합니다. (로드하지 않아도 요청 시 자동 로드(JIT)가 동작하면 사용 가능)

## 사용법

| 동작 | 방법 |
|---|---|
| 번역 팝업 | 텍스트 선택 후 **Ctrl+C, C** (기본값 — 설정에서 임의 조합으로 변경 가능) |
| 단축키 변경 | 설정 → 번역 단축키 입력란 클릭 → 원하는 조합 누르기. 문자·숫자 키는 Ctrl/Alt/Win 조합 필요, F1–F24는 단독 가능. Ctrl+C 조합만 '두 번 누르기' 강제 |
| 다시 번역 | 팝업에서 원문 수정 (0.9초 후 자동 번역) 또는 **Ctrl+Enter** |
| 번역문 복사 | 팝업의 **복사** 버튼 |
| 창 닫기 | **Esc**, ✕, 또는 창 밖 클릭 (📌 고정 시 유지) |
| 직접 입력 번역 | 트레이 아이콘 더블클릭 → 원문 입력 |
| 설정 | 트레이 아이콘 우클릭 → 설정 (서버 주소, 모델, 대상 언어, 자동 실행 등) |
| 종료 | 트레이 아이콘 우클릭 → 종료 |

모델을 지정하지 않으면(기본값: 자동) 서버에 **이미 로드된 모델을 우선** 사용하고,
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
```

## 문제 해결

| 증상 | 해결 |
|---|---|
| "LM Studio 서버에 연결할 수 없습니다" | LM Studio 실행 → 개발자 탭에서 서버 시작(Status: Running) 확인 |
| "모델이 응답을 생성하지 못했습니다" | 모델이 메모리에 로드되지 못한 경우 (대개 메모리 부족). LM Studio에서 더 작은 모델을 로드하거나, 설정에서 모델을 직접 지정 |
| 번역이 느림 | 더 작은 모델 사용 권장. 첫 요청은 모델 로드 때문에 오래 걸릴 수 있음 |
| Ctrl+C, C가 안 먹힘 | 트레이 아이콘 우클릭 → 설정에서 단축키 활성화 확인. 관리자 권한 창 위에서는 일반 권한 앱의 훅이 동작하지 않음 |

설정 파일: `%APPDATA%\DeepTranslation\settings.json`

## 구조

```
DeepTranslation/            WPF 앱 (.NET 8, C#)
  App.xaml(.cs)             트레이 아이콘, 전역 훅 연결, 셀프테스트
  Services/
    KeyboardHookService.cs  저수준 키보드 훅 — Ctrl+C 두 번 감지
    LmStudioClient.cs       OpenAI 호환 API 클라이언트 (SSE 스트리밍)
    TranslationService.cs   모델 자동 선택·재시도, think 블록 필터
    LanguageMaps.cs         한글 감지, 대상 언어 결정, 시스템 프롬프트
  Windows/
    TranslationWindow.xaml  다크 테마 번역 팝업
    SettingsWindow.xaml     설정 창
installer/setup.iss         Inno Setup 스크립트
tools/make-icon.ps1         앱 아이콘 생성
build.ps1                   퍼블리시 + 설치 프로그램 빌드
```
