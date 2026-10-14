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

        // 카메라가 내보낼 픽셀 포맷 4글자. 빈 값이면 드라이버 기본값을 쓴다.
        //
        // 지정하지 않으면 대부분의 UVC 카메라가 무압축 YUY2 를 고르는데, USB 대역폭 때문에
        // 720p 에서 10fps 까지 떨어진다. 캡처는 워커 스레드에서 돌지만(→ ARCHITECTURE §6)
        // 마커 반응 속도는 그대로 카메라 fps 를 따라가므로 MJPG 로 열어 둔다.
        public string fourcc = "MJPG";

        // 거울 효과용이 아니다. ArUco 는 좌우 반전된 마커를 인식하지 못하므로,
        // 카메라가 애초에 거울상을 내보낼 때 바로잡는 용도로만 쓴다.
        public bool flipHorizontal = false;
        public bool flipVertical = false;
    }

    /// <summary>
    /// 카메라 픽셀 → 프로젝터 픽셀 변환(H_camproj)을 결정하는 대응점.
    ///
    /// 카메라와 프로젝터는 위치도 렌즈도 다른 별개의 장치라, 카메라가 마커를 본 좌표를
    /// 그대로 투사 좌표로 쓸 수 없다. 투사면이 평면이므로 대응점 4쌍이면 변환이 결정되지만,
    /// 카메라 렌즈의 휨까지 잡으려면 점을 더 찍는다(격자 보정). 자세한 배경은 docs/ARCHITECTURE.md §1.
    /// </summary>
    [Serializable]
    public class CalibrationConfig
    {
        // 보정 자리 사각형의 네 귀퉁이(좌상 → 우상 → 우하 → 좌하). 출력 해상도를 1 로 보는 정규화 좌표.
        // 자동 보정은 이 네 점에 마커를 쏘고, 수동 보정은 이 사각형을 gridSize × gridSize 로 나눈 점을 짚는다.
        // 책자가 놓이는 영역을 덮도록 현장에서 옮긴다 — 보정은 점들이 둘러싼 안쪽에서 가장 정확하다.
        public Vector2[] projectorPoints = DefaultProjectorPoints();

        // 예전 4점 보정의 카메라 값. 아래 pair* 가 비어 있을 때만 projectorPoints 와 짝지어 쓴다.
        public Vector2[] cameraPoints = new Vector2[0];

        // 수동 보정에서 짚을 격자 한 변의 점 수. 2 = 네 귀퉁이만(예전 방식), 3 = 9점, 4 = 16점.
        // 점이 많을수록 카메라 렌즈의 휨이 위치마다 고르게 잡힌다.
        public int gridSize = 3;

        public const int MinGridSize = 2;
        public const int MaxGridSize = 5;

        // 자동 보정에서 투사할 마커의 화면상 크기(짧은 변 기준 비율, 흰 여백 포함).
        public float autoMarkerSize = 0.18f;

        public const float MinAutoMarkerSize = 0.04f;
        public const float MaxAutoMarkerSize = 0.4f;

        // 보정으로 얻은 대응점 전부. 같은 순번끼리 짝이다.
        // pairsPerPoint 개씩 묶여 "보정 점 하나" 를 이룬다 — 수동은 마커 중심 1개, 자동은 투사 마커 꼭짓점 4개.
        public Vector2[] pairCamera = new Vector2[0];       // 카메라 픽셀
        public Vector2[] pairProjector = new Vector2[0];    // 프로젝터 정규화 좌표
        public int pairsPerPoint = 1;

        // 대응점을 어떤 모양의 변환으로 맞출지(2026-10-06).
        //   "square" — 이동 · 회전 · 균일 크기만. 정사각형은 어디서나 같은 크기의 정사각형으로 남는다.
        //              점마다 짚은 오차는 평균으로 흡수된다. 장비가 비스듬하면 가장자리에 그만큼 오차가 남는다.
        //   "plane"  — 평면 변환(호모그래피). 선은 곧게 남지만 비스듬한 설치의 사다리꼴 기울기까지 따른다.
        //   "warp"   — plane + 점마다 남는 렌즈 휨 보간. 보정 점 위에서는 정확히 맞는다(기본).
        //              대신 짚을 때의 오차도 그대로 따라가므로 마커를 사각형에 정확히 맞춰 짚어야 한다.
        //
        // 기본은 "warp" 다(2026-10-06, 현장 판단 — "3×3 으로 보정했으면 그 9자리에서는 정확해야 한다").
        // 콘텐츠 모양은 이 값과 무관하게 늘 반듯한 고정 크기 직사각형으로 그려진다(→ flatMarkerSize).
        public string model = ModelWarp;

        public const string ModelSquare = "square";
        public const string ModelPlane = "plane";
        public const string ModelWarp = "warp";

        // 보정 위에 사람이 덧대는 세부 조정(2026-10-06). 보정이 구한 자리 전체를 통째로 옮기고 돌리고 키운다.
        // 모양은 건드리지 않으므로 정사각형은 정사각형으로 남는다. 보정을 다시 해도 이 값은 남는다(R 로 0).
        //   fineOffset   — 화면 높이를 1 로 보는 이동량(x 오른쪽, y 아래쪽)
        //   fineRotation — 도, 시계 방향이 +
        //   fineScale    — 배율. 기준점은 보정 자리 사각형의 가운데
        //   fineTilt     — 원근 기울기(x: + 면 오른쪽이 넓어지고 왼쪽이 좁아진다, y: + 면 위쪽이 넓어진다).
        //                  책상을 비스듬히 보는 설치에서 "한쪽 끝은 바깥으로, 반대쪽은 덜" 밀리는 어긋남을 잡는다.
        //                  이것만은 정사각을 유지하지 않는다 — 원근 자체가 정사각을 사다리꼴로 만드는 일이다.
        public Vector2 fineOffset = Vector2.zero;
        public float fineRotation = 0f;
        public float fineScale = 1f;
        public Vector2 fineTilt = Vector2.zero;

        public const float MaxFineTilt = 0.5f;

        public bool HasFineAdjust => fineOffset != Vector2.zero || fineRotation != 0f || fineScale != 1f || fineTilt != Vector2.zero;

        public void ResetFineAdjust()
        {
            fineOffset = Vector2.zero;
            fineRotation = 0f;
            fineScale = 1f;
            fineTilt = Vector2.zero;
        }

        // 보정할 때의 프로젝터 화면비(가로/세로). 가로세로 거리를 화면 픽셀 비율대로 재는 데 쓴다.
        public float projectorAspect = 16f / 9f;

        // 캘리브레이션을 마쳤는지. false 면 항등 변환으로 동작한다(정합은 안 맞지만 화면은 뜬다).
        public bool valid = false;

        /// <summary>대응점 묶음이 온전히 들어 있는지(새 방식).</summary>
        public bool HasPairs =>
            pairCamera != null && pairProjector != null
            && pairCamera.Length == pairProjector.Length
            && pairsPerPoint >= 1 && pairCamera.Length % pairsPerPoint == 0
            && pairCamera.Length / pairsPerPoint >= 4;

        /// <summary>예전 4점 보정 값이 온전히 들어 있는지.</summary>
        public bool HasLegacyPoints =>
            projectorPoints != null && projectorPoints.Length == 4
            && cameraPoints != null && cameraPoints.Length == 4;

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

        /// <summary>대응점이 채워져 실제로 변환을 세울 수 있는 상태인지.</summary>
        public bool IsUsable => valid && (HasPairs || HasLegacyPoints);

        public void Reset()
        {
            projectorPoints = DefaultProjectorPoints();
            ClearCameraPoints();
            ResetFineAdjust();
        }

        /// <summary>
        /// 카메라 쪽 값만 지운다. 현장에서 옮겨 둔 조준점 자리(projectorPoints)는 남겨
        /// 다시 보정할 때 같은 자리에서 잡게 한다.
        /// </summary>
        public void ClearCameraPoints()
        {
            cameraPoints = new Vector2[0];
            pairCamera = new Vector2[0];
            pairProjector = new Vector2[0];
            pairsPerPoint = 1;
            valid = false;
        }

        /// <summary>손으로 고친 json 이 범위를 벗어났으면 되돌린다.</summary>
        public void Validate()
        {
            if (projectorPoints == null || projectorPoints.Length != 4) projectorPoints = DefaultProjectorPoints();
            cameraPoints ??= new Vector2[0];
            pairCamera ??= new Vector2[0];
            pairProjector ??= new Vector2[0];
            gridSize = Mathf.Clamp(gridSize, MinGridSize, MaxGridSize);
            autoMarkerSize = Mathf.Clamp(autoMarkerSize, MinAutoMarkerSize, MaxAutoMarkerSize);
            if (projectorAspect <= 0.1f || projectorAspect > 10f) projectorAspect = 16f / 9f;
            if (model != ModelSquare && model != ModelPlane && model != ModelWarp) model = ModelWarp;
            fineScale = Mathf.Clamp(fineScale, 0.5f, 2f);
            fineRotation = Mathf.Clamp(fineRotation, -45f, 45f);
            fineTilt = new Vector2(Mathf.Clamp(fineTilt.x, -MaxFineTilt, MaxFineTilt), Mathf.Clamp(fineTilt.y, -MaxFineTilt, MaxFineTilt));
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

        // 이 마커 폴더 안의 파일별 배치. 한 마커가 영상과 이미지를 함께 띄우므로
        // (→ SPEC §4) 장마다 어디에 놓을지가 따로 필요하다.
        //
        // 위 마커 값은 "이 마커의 콘텐츠 전체"를 움직이고, 여기 값은 한 장만 움직인다.
        // 콘텐츠가 한 장뿐인 마커는 이 목록을 신경 쓸 일이 없다(기본값이 마커 자리 그대로다).
        public List<ContentPlacement> items = new List<ContentPlacement>();

        public void ResetPlacement()
        {
            scale = 1f;
            offsetX = 0f;
            offsetY = 0f;
            rotationOffset = 0f;
        }

        public ContentPlacement FindItem(string file)
        {
            if (items == null || string.IsNullOrEmpty(file)) return null;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && items[i].file == file) return items[i];
            }
            return null;
        }

        /// <summary>
        /// 폴더에서 처음 본 파일의 항목을 만들어 둔다. 마커 항목과 마찬가지로 json 선편집을 요구하지 않는다.
        /// </summary>
        /// <param name="index">폴더 안 순번. 여러 장이 겹쳐 보이지 않게 기본 자리를 나누는 데 쓴다.</param>
        public ContentPlacement GetOrCreateItem(string file, int index)
        {
            var found = FindItem(file);
            if (found != null) return found;

            items ??= new List<ContentPlacement>();

            var created = new ContentPlacement { file = file };
            created.ResetPlacement(index);
            items.Add(created);
            return created;
        }
    }

    /// <summary>
    /// 마커 폴더 안 파일 한 장의 배치. 키는 파일 이름이다.
    ///
    /// 파일 이름을 바꾸면 항목을 못 찾아 기본값으로 돌아간다. 폴더에서 사라진 파일의 항목은
    /// 지우지 않고 남겨 둔다 — 되돌려 놓으면 맞춰 둔 값이 그대로 되살아난다.
    /// </summary>
    [Serializable]
    public class ContentPlacement
    {
        // 확장자를 포함한 파일 이름. 경로가 아니다(층 폴더를 옮겨도 값이 유지된다).
        public string file;

        public bool enabled = true;

        // 마커 값 위에 얹힌다. 크기는 곱하고, 위치와 각도는 더한다.
        public float scale = 1f;
        public float offsetX = 0f;
        public float offsetY = 0f;
        public float rotationOffset = 0f;

        /// <param name="index">폴더 안 순번</param>
        public void ResetPlacement(int index)
        {
            scale = 1f;
            offsetX = DefaultOffsetX(index);
            offsetY = 0f;
            rotationOffset = 0f;
        }

        /// <summary>
        /// 첫 장은 마커 자리 그대로 두고, 둘째 장부터 오른쪽으로 한 칸씩 밀어 둔다.
        /// 전부 같은 자리에 겹쳐 뜨면 현장에서 "왜 한 장만 보이지" 로 시간을 버린다.
        /// 콘텐츠가 하나뿐인 마커는 이 값이 0 이라 기존 동작과 같다.
        /// </summary>
        public static float DefaultOffsetX(int index) => index * 1.1f;
    }

    /// <summary>
    /// 실제로 한 장을 그릴 때 쓰는 배치값. 세트 공통 → 마커 → 파일 순으로 쌓아 만든다.
    ///
    /// 그리는 쪽이 "이 값이 마커 것인지 파일 것인지" 를 알 필요가 없게 하려고 합쳐 넘긴다.
    /// </summary>
    public struct ArUcoPlacement
    {
        public float scale;
        public float offsetX;
        public float offsetY;
        public float rotationOffset;

        public static ArUcoPlacement Identity => new ArUcoPlacement { scale = 1f };

        public static ArUcoPlacement Of(MarkerConfig marker)
        {
            return new ArUcoPlacement
            {
                scale = marker.scale,
                offsetX = marker.offsetX,
                offsetY = marker.offsetY,
                rotationOffset = marker.rotationOffset,
            };
        }

        /// <summary>마커 배치 위에 파일 한 장의 배치를 얹는다.</summary>
        public static ArUcoPlacement Of(MarkerConfig marker, ContentPlacement item)
        {
            var placement = Of(marker);
            if (item == null) return placement;

            placement.scale *= item.scale;
            placement.offsetX += item.offsetX;
            placement.offsetY += item.offsetY;
            placement.rotationOffset += item.rotationOffset;
            return placement;
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

        // 카메라가 보는 영상을 이 세트의 화면에 반투명으로 깔지.
        //
        // 편집모드를 나가도 유지된다 — 인쇄한 마커가 실제로 잡히는지, 초점과 조명이 충분한지는
        // 전시 상태 그대로 두고 보는 편이 빠르기 때문이다. 세트마다 카메라가 다르므로 값도 세트에 둔다.
        //
        // 전시 중에는 반드시 꺼 둔다. 프로젝터가 실물 책자 위에 카메라 영상을 겹쳐 쏜다.
        public bool showCameraPreview = false;

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
        // OpenCV predefined dictionary 번호. 2 = DICT_4X4_250 (ID 0~249).
        // 인쇄한 마커와 반드시 같아야 한다.
        //
        // 층당 콘텐츠를 110개까지 잡기로 해서(→ SPEC §4) 100개짜리로는 모자라 250개짜리를 쓴다.
        // 4X4 계열은 같은 바이트 표를 앞에서부터 잘라 쓴다 — DICT_4X4_250 의 0~99 번은
        // DICT_4X4_100 의 것과 비트까지 동일하다. 그래서 100 → 250 으로 넓혀도 이미 인쇄한
        // 마커를 다시 뽑을 필요가 없다(오류정정 비트도 1 로 같다).
        // 다만 마커 간 최소 해밍거리는 3 으로 100개짜리와 같다 — 더 넓혀도 오검출 여유가 더 줄지는 않는다.
        // 현장에서 엉뚱한 ID 가 튀면 칸 수를 늘린 5X5 계열로 올린다 — 이때는 전량 재인쇄해야 한다.
        public int dictionaryId = 2;

        // 0 = 즉시 반응(떨림 많음), 1 에 가까울수록 부드럽지만 늦게 따라온다.
        public float smoothing = 0.4f;

        // 정지 고정 문턱(카메라 픽셀). 마커 네 꼭짓점이 모두 이 안에서만 흔들리면 자리를 잠근다.
        // smoothing 은 떨림을 늦출 뿐 없애지 못한다 — 가만히 놓인 책자가 흔들려 보이면 이 값을 올린다. 0 = 끔.
        public float stillThresholdPixels = 2f;

        // 마커를 놓쳤을 때 콘텐츠를 몇 초 더 붙잡아 둘지. 손이 스칠 때의 깜빡임을 막는다.
        public float holdSeconds = 0.3f;

        // 원근 격자 분할. 1 이면 대각선을 경계로 그림이 꺾여 보인다. 6 이상 권장.
        public int warpSubdivisions = 10;

        // 콘텐츠는 원근 없이 **위치 · 회전만** 마커와 보정을 따르고, 크기와 모양은 고정한다(2026-10-06, 현장 판단).
        // 예전의 perspectiveMapping(원근 켜기) 스위치는 없앴다 — 사다리꼴 · 늘어남이 생기는 경로가 남지 않게.
        // 영상은 프로젝터 화면 위의 반듯한 직사각형(원본 비율)으로, 크기는 아래 flatMarkerSize 기준으로 늘 같다.
        //
        // "마커 한 변" 으로 칠 고정 크기(화면 짧은 변 = 1). 배치값(크기 · 위치)은 이 길이를 1 로 본다.
        // 0 이하이면 처음 읽을 때 manualTargetSize(보정 때 실물 마커에 맞춘 크기)로 채운다.
        // 영상 크기는 이 값 x globalScale x 마커 · 파일 scale 이다. 현장에서는 이 값 대신 globalScale(+ / -)로 맞춘다.
        public float flatMarkerSize = 0f;

        // 세트 하나가 동시에 표시할 최대 마커 수. 0 = 제한 없음.
        public int maxSimultaneous = 0;

        // 후보만 잡히고 인식이 안 될 때 전체 딕셔너리를 훑는 주기(프레임). 0 = 사용 안 함.
        //
        // 인쇄한 마커의 딕셔너리를 모를 때 답을 알려주는 진단 기능이다. 한 번 도는 데
        // 720p 기준 약 140ms 가 들어(21개 딕셔너리 전수 검출) 켜 두면 카메라 프레임이 눈에 띄게 준다.
        // dictionaryId 가 확정된 뒤에는 0 으로 둔다.
        public int autoScanIntervalFrames = 0;

        // 세트 하나가 동시에 돌릴 영상 디코더 수. 층당 PC 1대 구성이라 상한을 둔다.
        public int maxConcurrentVideos = 8;

        // ---- 관람 기준 (→ SPEC §5 재생 정책, ARCHITECTURE §5 관람 기록) ----
        // 영상 재생 유지와 관람 카운트가 같은 값을 쓴다. "한 번 이어서 재생된 영상" 이 곧 "관람 1회" 다.

        // 마커가 처음 잡힌 뒤 이 시간 동안 유지되어야 관람으로 센다.
        // 그 전에 사라지면 책장을 넘기다 스친 것이거나 오인식으로 보고 세지 않는다.
        public float viewMinDwellSeconds = 1f;

        // 관람 기록 유예. 마커를 놓친 뒤 이 시간 안에 다시 잡히면 같은 관람 1회로 이어서 센다.
        // 넘기면 관람이 끝난 것이다 — 그때 한 건을 기록한다. 순위에 반영되는 시점도 그만큼 뒤다.
        //
        // 20초 — 책장을 넘기거나 옆 사람과 얘기하다 돌아오는 시간을 넉넉히 덮는다. 그보다 길면
        // 다음 관람객의 관람이 앞사람 것에 합쳐져 세어지지 않는다.
        public float viewResumeGraceSeconds = 20f;

        // 영상 이어보기 유예. 마커를 놓친 뒤 이 시간 안에 다시 잡히면 영상은 멈춘 자리에서 이어서 돌고,
        // 등장 연출(빛 고리)도 다시 하지 않는다. 넘기면 영상은 처음으로 되감고 다음엔 연출부터 다시 한다.
        // holdSeconds(콘텐츠를 계속 그려 두는 시간)보다 길어야 한다. 그 사이에는 콘텐츠가 숨고 영상만 멈춰 있다.
        //
        // 관람 기록 유예와 따로 둔다(2026-10-01) — "같은 관람으로 셀지" 와 "영상을 이어서 틀지" 는 현장에서 다르게 맞출 수 있다.
        // 파일에 없으면(예전 설정) -1 로 읽혀 관람 기록 유예 값을 그대로 쓴다.
        public float videoResumeGraceSeconds = -1f;

        // 영상이 끝나면 돌아갈 시각(초) — 도입부 + 반복 구간으로 만든 영상의 반복 구간 시작(→ SPEC §5).
        // 모든 영상을 같은 틀로 만들므로 여기 한 값으로 맞춘다. 파일 이름의 "_loop<초>" 가 있으면 그 영상만 그 값을 쓴다.
        // 0 = 처음으로 돌아가는 평범한 루프. 영상 길이보다 긴 값이면 그 영상은 처음으로 돌아간다.
        public float videoLoopStartSeconds = 2.4167f;   // 2초 10프레임 @24fps

        // 관람을 파일로 남길지. persistentDataPath/stats/ 에 날짜별 CSV 로 쌓인다.
        public bool recordViews = true;

        // 콘텐츠(video/)가 없는 마커를 올렸을 때 그 자리에 "N번 마커 · 콘텐츠가 없습니다" 안내를 띄울지.
        // 설치 · 점검 때 "인식은 되는데 콘텐츠가 없다" 를 바로 구분하는 모드다. 전시 중에는 꺼 둔다 —
        // 책자 위에 빈 상자와 안내 문구가 비치면 관람객에게는 고장처럼 보인다. 꺼져 있으면 아무것도 그리지 않는다.
        public bool showEmptyGuide = false;

        // ---- 등장 연출 (→ ArUcoAppear.cs, ARCHITECTURE §5 등장 연출) ----
        // 마커를 놓고 멈추면 빛 고리가 퍼진 뒤 콘텐츠가 나타난다. 놓칠 때는 연출 없이 holdSeconds 뒤 꺼진다.
        public AppearConfig appear = new AppearConfig();

        // ---- 바닥 대기 안내 (→ ArUcoIdleGuide.cs) ----
        // 세트 카메라에 마커가 하나도 없으면 delaySeconds 뒤 테이블 가장자리에 안내 문구가 움직이며 뜬다.
        public IdleConfig idle = new IdleConfig();

        // 메모리에 올려 둘 이미지 수. 세트끼리 함께 쓰는 캐시라 층 전체 기준이다.
        //
        // 이미지는 영상과 달리 디코딩 부하가 없어 상한이 화면 성능이 아니라 메모리에 걸린다.
        // 1920x1080 한 장이 약 8MB 이므로 24장이면 200MB 남짓이다.
        // 넘으면 화면에 없는 것 중 가장 오래된 것부터 놓아준다.
        public int maxCachedImages = 24;

        // 캘리브레이션 전용으로 예약한 마커 ID. 콘텐츠 마커와 겹치면 안 된다.
        // 프로젝터가 이 ID 들을 투사하고 카메라가 그것을 읽어 대응점을 얻는다.
        //
        // 반드시 dictionaryId 가 가진 범위 안이어야 한다. 콘텐츠는 층당 110개 자리(0~110)를 잡고
        // 그 끝 네 개를 예약한다 — 콘텐츠에 0~106 이 남는다(→ SPEC §4). 딕셔너리(DICT_4X4_250)에는
        // 더 위 번호도 있지만, 콘텐츠 번호와 바로 이어 두어야 인쇄물 번호표에서 한눈에 들어온다.
        public int[] calibrationMarkerIds = { 107, 108, 109, 110 };

        // 수동 보정에서 "마커를 여기에 놓으세요" 자리를 표시할 사각형의 크기.
        // 화면 짧은 변을 1 로 보는 비율이다.
        //
        // 실물 마커가 몇 cm 인지, 프로젝터가 얼마나 떨어져 있는지에 따라 화면상 크기가 달라지므로
        // 현장에서 + / - 로 맞춘다. 사각형과 마커가 같은 크기여야 마커 중심이 조준점에 오고,
        // 그래야 대응점이 정확해진다.
        public float manualTargetSize = 0.12f;

        public const float MinManualTargetSize = 0.02f;
        public const float MaxManualTargetSize = 0.5f;

        // 책상 위 정사각형이 프로젝터 화면에서 가로 / 세로 몇 대 몇인지. 프로젝터가 비스듬히 비추면 화면에서 정사각형인 것이
        // 책상 위에서는 직사각형으로 늘어난다. 책상 위에서 정사각형으로 보이도록 맞춘다
        // (수동 보정 중 , / . — 놓을 자리 사각형, 마커 배치 중 [ / ] — 영상).
        // 보정 사각형과 영상이 같은 값을 쓴다: 영상도 이 비율로 그려야 돌렸을 때 책상 위에서 찌그러지지 않는다.
        public float manualTargetAspect = 1f;

        public const float MinManualTargetAspect = 0.4f;
        public const float MaxManualTargetAspect = 2.5f;

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
            videoLoopStartSeconds = Mathf.Max(0f, videoLoopStartSeconds);
            stillThresholdPixels = Mathf.Clamp(stillThresholdPixels, 0f, 20f);
            holdSeconds = Mathf.Max(0f, holdSeconds);
            warpSubdivisions = Mathf.Clamp(warpSubdivisions, 1, 32);
            maxSimultaneous = Mathf.Max(0, maxSimultaneous);
            maxConcurrentVideos = Mathf.Max(1, maxConcurrentVideos);
            maxCachedImages = Mathf.Max(1, maxCachedImages);
            viewMinDwellSeconds = Mathf.Max(0f, viewMinDwellSeconds);
            // hold 안에 돌아오는 것은 놓친 적도 없는 것이다. grace 가 그보다 짧으면 의미가 없다.
            viewResumeGraceSeconds = Mathf.Max(holdSeconds, viewResumeGraceSeconds);
            if (videoResumeGraceSeconds < 0f) videoResumeGraceSeconds = viewResumeGraceSeconds;
            videoResumeGraceSeconds = Mathf.Max(holdSeconds, videoResumeGraceSeconds);
            manualTargetSize = Mathf.Clamp(manualTargetSize, MinManualTargetSize, MaxManualTargetSize);
            if (flatMarkerSize <= 0f) flatMarkerSize = manualTargetSize;
            flatMarkerSize = Mathf.Clamp(flatMarkerSize, 0.01f, 1f);
            manualTargetAspect = Mathf.Clamp(manualTargetAspect, MinManualTargetAspect, MaxManualTargetAspect);
            appear ??= new AppearConfig();
            appear.Validate();
            idle ??= new IdleConfig();
            idle.Validate();

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
                set.calibration.Validate();
                set.markers ??= new List<MarkerConfig>();
                set.globalScale = Mathf.Max(0.05f, set.globalScale);

                // 사람이 손으로 고친 json 이 그대로 들어온다. 그리는 쪽에서 null 을 만나지 않게 여기서 막는다.
                foreach (var marker in set.markers)
                {
                    if (marker == null) continue;

                    marker.scale = Mathf.Max(0.05f, marker.scale);
                    marker.items ??= new List<ContentPlacement>();

                    foreach (var item in marker.items)
                    {
                        if (item == null) continue;
                        item.scale = Mathf.Max(0.05f, item.scale);
                    }
                }

                // 캘리브레이션이 반쯤 채워진 상태로 남으면 엉뚱한 자리에 투사된다. 아예 무효로 돌린다.
                if (set.calibration.valid && !set.calibration.IsUsable)
                {
                    problems.Add($"세트 '{set.name}' 의 캘리브레이션 대응점이 온전하지 않습니다. 다시 보정해야 합니다.");
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
