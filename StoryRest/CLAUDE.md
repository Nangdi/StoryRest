# StoryRest

과학 전시용 Unity 프로젝트. 책자의 ArUco 마커를 카메라로 읽어 프로젝터로 콘텐츠를 투사한다.

## 작업 전 필독

| 문서 | 내용 |
|---|---|
| **`docs/PROJECT_SPEC.md`** | 설치 환경(층별 프로젝터·카메라 구성), 콘텐츠 경로 규칙, 마커 매핑, 확정/미확정 사항 |
| **`docs/ARCHITECTURE.md`** | 위를 어떤 구조로 구현할지. 좌표계, 캘리브레이션, 오브젝트 구조, 구현 순서 |

코드를 만지기 전에 둘 다 읽는다. 결정이 바뀌면 코드보다 문서를 먼저 갱신한다.

**특히 주의할 전제** — 프로젝터는 실제 책자 위에 빛을 쏜다. 화면 속 영상을 보는 구조가 아니다.
그래서 (1) 카메라 영상을 투사하지 않고, (2) 카메라 픽셀 좌표를 그대로 출력 좌표로 쓸 수 없다.
카메라↔프로젝터 호모그래피가 필요하다. 자세한 내용은 `ARCHITECTURE.md` §0~1.

## 주요 경로

| 경로 | 내용 |
|---|---|
| `Assets/ArUcoPages/` | 자체 제작 ArUco 오버레이 패키지 (사용법은 그 안의 `README.md`) |
| `Assets/OpenCVForUnity/` | 마커 검출 기반 (에셋스토어 유료 에셋) |
| `Assets/AVProVideo/` | 영상 재생. **Unity `VideoPlayer` 대신 이걸 쓴다** |
| `Assets/3.Script/` | GameManager, 통신(RS232/TCP), 설정 UI |
| `Assets/StreamingAssets/` | 런타임 설정 JSON, 층별 콘텐츠 폴더 `floor_<N>/` |
