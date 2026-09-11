# 개발 환경 세팅

새 PC 에서 이 저장소를 받아 작업할 때 필요한 절차.

## Unity

**2022.3.14f1** (LTS). 다른 마이너 버전이면 패키지 재임포트가 일어날 수 있다.

## 에셋스토어 유료 에셋 — 저장소에 없다

두 에셋은 **저장소에 포함되어 있지 않다.** 재배포가 라이선스 위반이고,
용량이 커서 그대로는 push 조차 되지 않기 때문이다
(OpenCV for Unity 2.9GB — 그중 WebGL 플러그인만 2GB, 100MB 넘는 파일이 16개).

클론한 직후에는 **컴파일 에러가 난다.** 아래 두 에셋을 임포트하면 해결된다.

| 에셋 | 용도 | 임포트 위치 |
|---|---|---|
| [OpenCV for Unity](https://assetstore.unity.com/packages/tools/integration/opencv-for-unity-21088) | ArUco 마커 검출 | `Assets/OpenCVForUnity/` |
| [AVPro Video](https://assetstore.unity.com/packages/tools/video/avpro-video-ultra-edition-278957) | 영상 재생 | `Assets/AVProVideo/` |

### 임포트할 때 플랫폼 줄이기

이 전시물은 **Windows 전용**이다. 임포트 창에서 아래 플랫폼 플러그인을 빼면
디스크와 임포트 시간을 크게 아낀다(OpenCV 기준 2.9GB → 약 190MB).

```
Assets/OpenCVForUnity/Plugins/
  Windows/     ← 이것만 필요
  WebGL/       ← 2GB. 빼도 된다
  Android/  iOS/  macOS/  VisionOS/  WSA/  Linux/   ← 빼도 된다
```

AVPro 도 마찬가지로 `Runtime/Plugins/Windows/` 만 있으면 된다.

이미 전부 임포트했다면 위 폴더들을 지워도 Windows 빌드에는 영향이 없다.

## Player Settings

- **Active Input Handling**: `Both` 또는 `Input Manager (Old)`
  기존 코드가 `UnityEngine.Input` 을 쓴다. `Input System (New)` 전용으로 두면 런타임 예외가 난다.
- **Run In Background**: 켬. 전시 중 다른 창이 떠도 멈추면 안 된다.

## TextMeshPro

한글은 `TMP Settings` 의 **fallback** 에 등록된 `NotoSansKR-Regular SDF` 가 담당한다
(`Assets/10.Font/defaultFont/`). 이 설정이 없으면 런타임에 만드는 UI 의 한글이 전부 □ 로 나온다.
설정 애셋은 저장소에 포함되어 있으므로 따로 손댈 필요는 없다.

## 실행 전 확인

| 파일 | 확인할 것 |
|---|---|
| `Assets/StreamingAssets/Setting.json` | `floor` 가 이 PC 가 설치될 층인지, `role` 이 이 PC 의 역할인지(아래 표) — **첫 실행 때 복사되는 기본값**. 실제 값은 아래 참고 |
| `Assets/StreamingAssets/aruco.json` | 카메라 `deviceId`, `displayIndex`, 세트 개수 — **첫 실행 때 복사되는 기본값**. 실제 값은 아래 참고 |
| `Assets/StreamingAssets/keywordwall.json` | 키워드 월 `walls[]` 의 디스플레이 번호가 세트와 겹치지 않는지 (`roles` 로 역할별 항목이 나뉘어 있다) |
| `Assets/StreamingAssets/floor_<N>/` | 마커 ID 폴더와 영상, `keywords.txt` |

`aruco.json` 과 `keywordwall.json` 은 없으면 첫 실행 때 기본값으로 만들어진다.

### PC 별 `Setting.json`

| PC | `floor` | `role` | `statsHost` | 비고 |
|---|---|---|---|---|
| 1층 | 1 | `all` | (안 씀) | 카메라 1 + 월 2 를 한 PC 가 맡는다 |
| 2층 ArUco PC | 2 | `aruco` | (안 씀) | 카메라 2 + 프로젝터 2. `statsPort`(기본 5100)를 방화벽에서 열어 둔다 |
| 2층 월 PC | 2 | `wall` | 2층 ArUco PC 의 IP | 프로젝터 2. 랜선으로 ArUco PC 와 연결 |
| 3층 | 3 | 위와 같음 | | |

두 PC 는 서로 없어도 각자 돈다. 월 PC 의 시작 로그에 `[Stats] ArUco PC 연결됨` 이 찍히면 링크가 된 것이고,
`[TCP] 연결/수신 오류` 가 반복되면 IP · 방화벽 · 랜선을 확인한다.

`aruco.json` 과 `Setting.json` 은 실제로는 `persistentDataPath` 에서 읽고 쓴다
(Windows: `%USERPROFILE%\AppData\LocalLow\DefaultCompany\StoryRest\`).
`StreamingAssets` 의 파일은 거기에 아직 파일이 없을 때 한 번 복사되는 원본이라,
한 번 실행한 뒤에는 `StreamingAssets` 쪽을 고쳐도 반영되지 않는다 — `persistentDataPath` 의 파일을 고치거나 지운다.
정확한 경로는 시작 로그의 `설정 파일:` / `층 설정:` 줄에 있다.

콘텐츠 영상(`floor_<N>/<마커ID>/`)은 저장소에 넣지 않는다. 현장에서 직접 복사한다.

## 카메라 없이 확인하기

개발 PC 에 웹캠이 없어도 아래는 확인할 수 있다.

- `aruco.json` 의 `debugPreviewContent` 를 켜면 마커 인식 없이 영상이 화면에 격자로 뜬다.
- 키워드 월은 카메라와 무관하게 동작한다.
- 에디터에서는 Game 뷰 상단의 **Display 드롭다운**으로 세트별·키워드월별 화면을 골라 본다.
