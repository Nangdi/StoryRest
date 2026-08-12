using System;
using System.Collections.Generic;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 카메라 한 대의 캡처 설정. 세트마다 하나씩 갖는다.
    /// </summary>
    [Serializable]
    public class CameraConfig
    {
        // OpenCV VideoCapture 디바이스 인덱스. 세트마다 달라야 한다.
        public int deviceId = 0;

        // 0 = CAP_DSHOW(권장) / 1 = CAP_ANY / 2 = CAP_MSMF
        // 비전카메라 호환성은 DirectShow 가 가장 높다.
        public int backend = 0;

        // 검출용 해상도. 표시 해상도와 무관하며, 낮을수록 CPU 를 아낀다.
        // 층당 PC 한 대가 카메라를 최대 2대 돌리므로 720p 를 기본으로 둔다.
        public int width = 1280;
        public int height = 720;
        public int fps = 30;

        // 거울 효과용이 아니다. ArUco 는 좌우 반전된 마커를 인식하지 못하므로,
        // 카메라가 애초에 거울상을 내보낼 때 바로잡는 용도로만 쓴다.
        public bool flipHorizontal = false;
        public bool flipVertical = false;
    }

    /// <summary>
    /// 카메라 픽셀 → 프로젝터 픽셀 변환(H_camproj)을 결정하는 네 쌍의 대응점.
    ///
    /// 카메라와 프로젝터는 위치도 렌즈도 다른 별개의 장치라, 카메라가 마커를 본 좌표를
    /// 그대로 투사 좌표로 쓸 수 없다. 투사면이 평면이므로 대응점 4쌍이면 변환이 완전히 결정된다.
    /// 자세한 배경은 docs/ARCHITECTURE.md §1.
    /// </summary>
    [Serializable]
    public class CalibrationConfig
    {
        // 출력 해상도를 1 로 보는 정규화 좌표. 프로젝터 해상도가 바뀌어도 값이 유지된다.
        // 가장자리는 렌즈 왜곡이 크고 카메라 시야를 벗어나기 쉬워 안쪽으로 들여 잡는다.
        public Vector2[] projectorPoints = DefaultProjectorPoints();

        // 위 네 점이 카메라 영상의 어디에 찍혔는지(카메라 픽셀). 캘리브레이션이 채운다.
        public Vector2[] cameraPoints = new Vector2[0];

        // 캘리브레이션을 마쳤는지. false 면 항등 변환으로 동작한다(정합은 안 맞지만 화면은 뜬다).
        public bool valid = false;

        public static Vector2[] DefaultProjectorPoints()
        {
            return new[]
            {
                new Vector2(0.15f, 0.15f),
                new Vector2(0.85f, 0.15f),
                new Vector2(0.85f, 0.85f),
                new Vector2(0.15f, 0.85f),
            };
        }

        /// <summary>네 쌍이 모두 채워져 실제로 변환을 세울 수 있는 상태인지.</summary>
        public bool IsUsable =>
            valid
            && projectorPoints != null && projectorPoints.Length == 4
            && cameraPoints != null && cameraPoints.Length == 4;

        public void Reset()
        {
            projectorPoints = DefaultProjectorPoints();
            cameraPoints = new Vector2[0];
            valid = false;
        }
    }

    /// <summary>
    /// 마커 하나를 어디에 얼마나 크게 띄울지. 현장에서 편집모드로 맞춘 값이 저장된다.
    ///
    /// 좌표는 마커 한 변을 1 로 보는 상대값이라 카메라 높이나 줌이 바뀌어도 배치가 유지되고,
    /// 마커 평면 위에서 처리되므로 책자를 돌려도 콘텐츠는 같은 자리에 머문다.
    /// </summary>
    [Serializable]
    public class MarkerConfig
    {
        public int id;
        public bool enabled = true;

        // 콘텐츠 가로폭 = 화면에 잡힌 마커 한 변 길이 x globalScale x scale
        public float scale = 1f;
        public float offsetX = 0f;
        public float offsetY = 0f;

        // 마커를 비뚤게 인쇄·부착했을 때 보정할 각도(도).
        public float rotationOffset = 0f;

        public void ResetPlacement()
        {
            scale = 1f;
            offsetX = 0f;
            offsetY = 0f;
            rotationOffset = 0f;
        }
    }

    /// <summary>
    /// 카메라 1대 + 프로젝터 1대로 이루어진 인터랙션 세트 하나.
    ///
    /// 세트끼리는 완전히 독립이다. 카메라 A 가 본 마커는 프로젝터 A 에만 투사되고,
    /// 같은 마커 ID 라도 세트가 다르면 별개의 보정값을 갖는다(설치 각도가 다르므로).
    /// </summary>
    [Serializable]
    public class SetConfig
    {
        // 로그와 편집모드 HUD 에 표시할 이름. "A", "B" 처럼 짧게 둔다.
        public string name = "A";

        // 이 세트를 쓰는 층. 비워 두면 모든 층에서 쓴다.
        //
        // 층마다 장비 수가 다르므로(1층 카메라 1대, 2·3층 2대) 세트를 전부 적어 두고
        // 여기서 골라 쓴다. Setting.json 의 floor 만 바꾸면 그 층 구성으로 뜬다.
        // 층수로 세트 수를 추론하지 않고 이 목록을 보는 이유는, 나중에 1층에 카메라를
        // 더 달아도 코드가 아니라 이 값만 고치면 되게 하기 위해서다.
        public int[] floors = new int[0];

        // 이 세트가 투사할 Unity 디스플레이 번호. 0 = 주 디스플레이.
        public int displayIndex = 0;

        public CameraConfig camera = new CameraConfig();
        public CalibrationConfig calibration = new CalibrationConfig();

        // 이 세트의 모든 콘텐츠에 공통으로 곱해지는 배율. 현장에서 전체 크기를 한 번에 맞출 때 쓴다.
        public float globalScale = 1f;

        // 이 세트의 모든 콘텐츠에 공통으로 더해지는 위치. 마커별 offset 은 이 값 위에 얹힌다
        // (최종 위치 = globalOffset + markers[].offset).
        //
        // "콘텐츠는 늘 마커 위쪽" 같은 공통 배치를 한 번에 잡고 예외만 개별로 손보라는 값이다.
        // 마커별 값을 일일이 고치지 않으므로 나중에 전체를 다시 밀 수 있다.
        //
        // 단위는 마커 한 변을 1 로 보는 상대값이고, 마커 평면 기준이라 책자를 돌리면 함께 돈다.
        public float globalOffsetX = 0f;
        public float globalOffsetY = 0f;

        public List<MarkerConfig> markers = new List<MarkerConfig>();

        /// <summary>세트 공통 배치를 기본값으로 되돌린다. 마커별 값은 건드리지 않는다.</summary>
        public void ResetGlobalPlacement()
        {
            globalScale = 1f;
            globalOffsetX = 0f;
            globalOffsetY = 0f;
        }

        /// <summary>이 층에서 쓰는 세트인지. floors 가 비어 있으면 모든 층에서 쓴다.</summary>
        public bool IsUsedOnFloor(int floor)
        {
            if (floors == null || floors.Length == 0) return true;
            return Array.IndexOf(floors, floor) >= 0;
        }

        public MarkerConfig Find(int id)
        {
            if (markers == null) return null;

            for (int i = 0; i < markers.Count; i++)
            {
                if (markers[i] != null && markers[i].id == id) return markers[i];
            }
            return null;
        }

        /// <summary>
        /// 설정에 없는 마커가 처음 잡히면 기본값 항목을 만들어 둔다.
        /// 콘텐츠 폴더만 추가하고 바로 편집모드에서 맞출 수 있어야 하므로 json 선편집을 요구하지 않는다.
        /// </summary>
        public MarkerConfig GetOrCreate(int id)
        {
            var found = Find(id);
            if (found != null) return found;

            markers ??= new List<MarkerConfig>();

            var created = new MarkerConfig { id = id };
            markers.Add(created);
            return created;
        }
    }

    /// <summary>
    /// StreamingAssets/aruco.json 의 데이터 구조.
    /// 검출·표시 공통 파라미터는 최상위에, 장비에 딸린 값은 세트 안에 둔다.
    /// </summary>
    [Serializable]
    public class ArUcoConfig
    {
        // OpenCV predefined dictionary 번호. 1 = DICT_4X4_100 (ID 0~99).
        // 인쇄한 마커와 반드시 같아야 한다.
        //
        // 4X4 계열은 같은 바이트 표를 앞에서부터 잘라 쓴다 — DICT_4X4_100 의 0~49 번은
        // DICT_4X4_50 의 것과 비트까지 동일하다. 그래서 50 → 100 으로 넓혀도 이미 인쇄한
        // 마커를 다시 뽑을 필요가 없다(오류정정 비트도 1 로 같다).
        // 다만 마커 간 최소 해밍거리가 4 → 3 으로 줄어 오검출 여유는 조금 좁아진다.
        // 현장에서 엉뚱한 ID 가 튀면 DICT_5X5_100(=5)으로 올린다 — 이때는 전량 재인쇄해야 한다.
        public int dictionaryId = 1;

        // 0 = 즉시 반응(떨림 많음), 1 에 가까울수록 부드럽지만 늦게 따라온다.
        public float smoothing = 0.4f;

        // 마커를 놓쳤을 때 콘텐츠를 몇 초 더 붙잡아 둘지. 손이 스칠 때의 깜빡임을 막는다.
        public float holdSeconds = 0.3f;

        // 원근 격자 분할. 1 이면 대각선을 경계로 그림이 꺾여 보인다. 6 이상 권장.
        public int warpSubdivisions = 10;

        // false 면 원근 없이 화면상 회전·크기만 맞춘다. 마커가 작아 심하게 떨릴 때의 대안.
        public bool perspectiveMapping = true;

        // 세트 하나가 동시에 표시할 최대 마커 수. 0 = 제한 없음.
        public int maxSimultaneous = 0;

        // 세트 하나가 동시에 돌릴 영상 디코더 수. 층당 PC 1대 구성이라 상한을 둔다.
        public int maxConcurrentVideos = 4;

        // 캘리브레이션 전용으로 예약한 마커 ID. 콘텐츠 마커와 겹치면 안 된다.
        // 프로젝터가 이 ID 들을 투사하고 카메라가 그것을 읽어 대응점을 얻는다.
        //
        // 반드시 dictionaryId 가 가진 범위 안이어야 한다. 기본값 DICT_4X4_100 은 0~99 이므로
        // 콘텐츠가 0번부터 늘어난다고 보고 뒤쪽 네 개를 예약해 둔다(콘텐츠에 0~95 가 남는다).
        // 딕셔너리를 더 큰 것으로 바꾸면 이 값도 함께 옮기는 편이 안전하다.
        public int[] calibrationMarkerIds = { 96, 97, 98, 99 };

        // 켜면 에디터에서만 세트들을 한 화면에 좌우로 나눠 그린다. 빌드에서는 무시된다.
        //
        // 기본은 꺼 둔다. 나눠 그리면 세트 하나의 화면 비율이 실제 프로젝터와 달라져
        // (예: 1920x1080 → 960x1080) 정합을 눈으로 맞추는 작업에서 감각이 어긋난다.
        // 에디터에서도 Game 뷰 상단의 Display 드롭다운으로 세트별 화면을 그대로 볼 수 있고,
        // Game 뷰를 두 개 띄우면 동시에 볼 수도 있다. Display.Activate 는 빌드에서만 필요하다.
        public bool editorPreview = false;

        // 카메라 없이도 콘텐츠를 화면에 늘어놓아 재생 상태를 확인한다.
        // 마커 인식과 무관하게 동작하므로, 현장에서 "영상 파일이 제대로 들어갔는지" 만
        // 빠르게 점검할 때 켠다. 전시 중에는 반드시 꺼 둔다.
        public bool debugPreviewContent = false;

        // 편집모드에서 키를 한 번 누를 때 움직이는 양. Shift 를 같이 누르면 줄어든다.
        public float adjustPositionStep = 0.02f;
        public float adjustScaleStep = 0.02f;
        public float adjustRotationStep = 1f;

        // 설치할 수 있는 세트를 전부 적어 두고, 각 세트의 floors 로 층을 고른다.
        // 실제로 만들어지는 세트 수 = 이 층에서 쓰는 항목 수.
        //
        // 기본값은 계획된 설치 구성이다.
        //   1층 — 프로젝터 3대: ArUco 0번        + 키워드 월 1, 2번
        //   2·3층 — 프로젝터 4대: ArUco 0, 1번   + 키워드 월 2, 3번
        public List<SetConfig> sets = new List<SetConfig>
        {
            new SetConfig
            {
                name = "A",
                floors = new[] { 1, 2, 3 },
                displayIndex = 0,
                camera = new CameraConfig { deviceId = 0 },
            },
            new SetConfig
            {
                name = "B",
                floors = new[] { 2, 3 },
                displayIndex = 1,
                camera = new CameraConfig { deviceId = 1 },
            },
        };

        /// <summary>이 층에서 실제로 쓰는 세트만 골라 준다.</summary>
        public List<SetConfig> SetsForFloor(int floor)
        {
            var result = new List<SetConfig>();
            if (sets == null) return result;

            foreach (var set in sets)
            {
                if (set != null && set.IsUsedOnFloor(floor)) result.Add(set);
            }
            return result;
        }

        public bool IsCalibrationMarker(int id)
        {
            if (calibrationMarkerIds == null) return false;
            return Array.IndexOf(calibrationMarkerIds, id) >= 0;
        }

        /// <summary>
        /// 현장에서 손으로 고치다 생기는 실수를 잡는다.
        /// 고칠 수 있는 것은 조용히 고치고, 사람이 결정해야 하는 것만 문제로 보고한다.
        /// </summary>
        public void Validate(List<string> problems, int floor)
        {
            if (sets == null || sets.Count == 0)
            {
                problems.Add("세트가 하나도 정의되지 않았습니다. sets 배열에 최소 1개가 필요합니다.");
                return;
            }

            smoothing = Mathf.Clamp(smoothing, 0f, 0.99f);
            holdSeconds = Mathf.Max(0f, holdSeconds);
            warpSubdivisions = Mathf.Clamp(warpSubdivisions, 1, 32);
            maxSimultaneous = Mathf.Max(0, maxSimultaneous);
            maxConcurrentVideos = Mathf.Max(1, maxConcurrentVideos);

            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                if (set == null)
                {
                    problems.Add($"sets[{i}] 가 비어 있습니다.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(set.name)) set.name = ((char)('A' + i)).ToString();
                set.camera ??= new CameraConfig();
                set.calibration ??= new CalibrationConfig();
                set.markers ??= new List<MarkerConfig>();
                set.globalScale = Mathf.Max(0.05f, set.globalScale);

                // 캘리브레이션이 반쯤 채워진 상태로 남으면 엉뚱한 자리에 투사된다. 아예 무효로 돌린다.
                if (set.calibration.valid && !set.calibration.IsUsable)
                {
                    problems.Add($"세트 '{set.name}' 의 캘리브레이션 대응점이 4쌍이 아닙니다. 다시 보정해야 합니다.");
                    set.calibration.valid = false;
                }

                // 이 층에서 쓰지 않는 세트는 만들어지지 않으므로 충돌 검사에서 뺀다.
                // (세트 B 가 1층에서 쓰는 디스플레이와 겹쳐도, 1층에서 B 가 안 뜨면 문제가 아니다.)
                if (!set.IsUsedOnFloor(floor)) continue;

                // 세트끼리 카메라나 디스플레이를 공유하면 둘 다 정상 동작하지 않는다.
                for (int j = i + 1; j < sets.Count; j++)
                {
                    var other = sets[j];
                    if (other?.camera == null || !other.IsUsedOnFloor(floor)) continue;

                    if (set.camera.deviceId == other.camera.deviceId)
                        problems.Add($"세트 '{set.name}' 와 '{other.name}' 이 같은 카메라(deviceId={set.camera.deviceId})를 씁니다.");

                    if (set.displayIndex == other.displayIndex)
                        problems.Add($"세트 '{set.name}' 와 '{other.name}' 이 같은 디스플레이(displayIndex={set.displayIndex})를 씁니다.");
                }
            }

            if (SetsForFloor(floor).Count == 0)
            {
                problems.Add($"{floor}층에서 쓸 세트가 없습니다. " +
                             $"sets[].floors 에 {floor} 이 들어간 항목이 하나도 없습니다.");
            }
        }
    }
}
