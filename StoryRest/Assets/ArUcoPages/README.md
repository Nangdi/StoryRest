# ArUco Pages

책 페이지마다 붙인 ArUco 마커를 카메라로 읽어, **그 페이지 면 위에 이미지를 눕혀** 띄우는 전시용 패키지.

마커 네 꼭짓점으로 호모그래피를 세우기 때문에 책을 기울이거나 넘기면 이미지도 같이 기울어진다.
카메라 캘리브레이션(체커보드 촬영)이 필요 없다.

---

## 필요한 것

| | |
|---|---|
| Unity | 2022.3 이상 |
| [OpenCV for Unity](https://assetstore.unity.com/packages/tools/integration/opencv-for-unity-21088) | 에셋스토어 유료. **패키지에 포함되지 않으므로 따로 임포트해야 한다.** |
| 카메라 | 아무 USB 웹캠. DirectShow 로 여는 구조라 UVC 미지원 비전카메라도 대체로 붙는다. |
| 입력 | 구 Input Manager (`UnityEngine.Input`) 사용 |

OpenCV for Unity 를 먼저 임포트하지 않으면 `ArUcoPages.asmdef` 가 참조를 찾지 못해 컴파일 에러가 난다.

> **Input System 만 쓰는 프로젝트 주의**
> `Project Settings > Player > Active Input Handling` 이 `Input System Package (New)` 로만 되어 있으면
> 키 입력(`C`, `F1` 등)에서 런타임 예외가 난다. `Both` 로 바꾸면 그대로 동작한다.

## 설치

1. OpenCV for Unity 임포트
2. `ArUcoPages.unitypackage` 임포트 (또는 `ArUcoPages` 폴더째 복사)
3. 빈 GameObject 하나에 **`ArUcoPageOverlay`** 컴포넌트 추가

끝이다. 캔버스, 검출기, 편집모드 UI 는 전부 런타임에 붙으므로 인스펙터 연결 작업이 없다.
Main Camera 도 필요 없다(ScreenSpaceOverlay 캔버스).

## 이미지 넣기

`StreamingAssets/Images/image_<마커ID>.png`

- 파일을 넣기만 하면 그 페이지가 자동으로 생긴다. 설정 파일을 미리 손볼 필요 없다.
- png / jpg / jpeg 지원. 투명 png 를 쓰면 카메라 영상 위에 자연스럽게 얹힌다.
- 폴더명과 접두어는 `aruco.json` 의 `imageFolder`, `imagePrefix` 로 바꿀 수 있다.
- 특정 마커만 다른 파일명을 쓰려면 해당 마커 항목의 `"image"` 에 직접 적는다.

## 마커 인쇄

Unity 메뉴 **`Tools > ArUco 마커 생성`**

딕셔너리·ID 범위·여백·ID 각인을 정해 png 로 뽑는다.
인쇄한 마커의 딕셔너리와 `aruco.json` 의 `dictionaryId` 가 같아야 인식된다.

> 딕셔너리를 모르는 마커가 있으면, 후보만 잡히고 인식이 안 될 때
> 21종 딕셔너리를 자동으로 훑어서 "어느 것으로 바꾸라"고 콘솔에 알려준다.

## 조작

| 키 | 기능 |
|---|---|
| `C` | 카메라 영상 켜기/끄기 (꺼도 마커 이미지는 그대로 뜬다) |
| `F1` | 편집모드 진입/종료 |

편집모드 안에서:

| 키 | 기능 |
|---|---|
| `Tab` / `Shift+Tab` | 조정할 마커 선택 |
| `← → ↑ ↓` | 위치 |
| `+` `-` | 크기 |
| `[` `]` | 회전 |
| `PageUp` / `PageDown` | 전체 배율 |
| `P` | 원근 켜고 끄기 |
| `D` | 마커 검출 테두리 |
| `F5` | 이미지 다시 읽기 |
| `Backspace` | 이 마커 초기화 |
| `Enter` | 즉시 저장 (평소엔 1.5초 후 자동 저장) |
| `Shift` 병행 | 미세조정 (1/5 폭) |

값은 마커별로 `aruco.json` 에 저장된다. 빌드 후에도 `<게임이름>_Data/StreamingAssets/` 에서 같은 방식으로 조정된다.

## 설정 (`StreamingAssets/aruco.json`)

파일이 없으면 첫 실행 때 기본값으로 만들어진다.

```json
{
    "camera": { "deviceId": 0, "backend": 0, "width": 0, "height": 0, "fps": 0,
                "flipHorizontal": false, "flipVertical": false },
    "dictionaryId": 0,
    "autoScanIntervalFrames": 60,
    "imageFolder": "Images",
    "imagePrefix": "image_",
    "showCameraOnStart": true,
    "perspectiveMapping": true,
    "warpSubdivisions": 10,
    "globalScale": 1.0,
    "smoothing": 0.4,
    "holdSeconds": 0.3,
    "maxSimultaneous": 0,
    "markers": []
}
```

| 키 | 설명 |
|---|---|
| `camera.backend` | 0 = DSHOW(권장) / 1 = ANY / 2 = MSMF |
| `camera.flipHorizontal` | **거울 효과용이 아니다.** ArUco 는 좌우 반전된 마커를 인식하지 못한다. 카메라가 애초에 거울상을 내보낼 때 바로잡는 용도로만 쓴다. |
| `dictionaryId` | 0 = DICT_4X4_50 |
| `perspectiveMapping` | `false` 로 두면 원근 없이 화면상 회전·크기만 맞춘다 |
| `warpSubdivisions` | 원근 격자 분할. 1이면 대각선에서 그림이 꺾인다. 6 이상 권장 |
| `smoothing` | 0 = 즉시 반응(떨림), 1에 가까울수록 부드럽지만 늦음 |
| `maxSimultaneous` | 동시 표시 상한. 0 = 무제한, 1 = 가장 크게 잡힌 것만 |

## 다른 설정 관리자와 합치기

패키지는 기본적으로 `aruco.json` 을 직접 읽고 쓴다.
프로젝트에 이미 설정 관리자가 있다면 `ArUcoConfigStore` 에 끼워 인스턴스를 공유시킬 수 있다.

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
static void Install()
{
    // null 을 돌리면 패키지 기본 파일 로더가 처리한다.
    // (관리자의 Awake 순서가 보장되지 않으므로 null 을 돌려도 안전해야 한다.)
    ArUcoConfigStore.Loader = () => MyManager.instance?.arucoJson;
    ArUcoConfigStore.Saver  = config => MyManager.instance.SaveArUcoJson();
}
```

## 구조

| 파일 | 역할 |
|---|---|
| `ArUcoPageOverlay` | 씬에 붙이는 유일한 컴포넌트. 화면 구성과 배치 |
| `ArUcoMarkerTracker` | 카메라 캡처 + 마커 검출 + 꼭짓점 스무딩 |
| `ArUcoHomography` | 마커 네 꼭짓점 → 페이지 평면 (사영변환 닫힌 해) |
| `ArUcoWarpedImage` | 사각형이 아닌 면에 텍스처를 그리는 UI 그래픽 |
| `ArUcoImageLibrary` | 이미지 로딩·캐시·동적 페이지 스캔 |
| `ArUcoAdjustMode` | 편집모드 |
| `ArUcoConfigStore` | 설정 입출력 (호스트 프로젝트 연동 지점) |

## 한계

- **평면까지만 따라간다.** 이미지는 마커 평면과 함께 기울어질 뿐, 넘어가는 종이처럼 *휘지는* 않는다. 곡면까지 하려면 페이지당 마커가 여러 개 필요하다.
- **마커가 작을수록 흔들린다.** 작은 마커의 네 점으로 큰 페이지를 외삽하므로 각도 오차가 증폭된다. 3cm 보다 5cm 로 인쇄하는 편이 훨씬 안정적이다. 그래도 심하면 `smoothing` 을 올리거나 `P` 로 원근을 끈다.
- 타입에 네임스페이스를 두지 않았다. 모든 공개 타입이 `ArUco` 접두어를 갖고 있어 충돌 가능성은 낮다.

## 인쇄 팁

- 무광 용지 (광택지는 반사로 인식률이 떨어진다)
- 한 변 3~5cm
- **마커 주변 흰 여백을 자르지 말 것** — 여백이 검출의 일부다
