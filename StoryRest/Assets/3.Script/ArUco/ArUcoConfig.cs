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

        // 마커를 놓쳤을 때 콘텐츠를 몇 초 더 붙잡아 둘지. 손이 스칠 때의 깜빡임을 막는다.
        public float holdSeconds = 0.3f;

        // 원근 격자 분할. 1 이면 대각선을 경계로 그림이 꺾여 보인다. 6 이상 권장.
        public int warpSubdivisions = 10;

        // false 면 원근 없이 화면상 회전·크기만 맞춘다. 마커가 작아 심하게 떨릴 때의 대안.
        public bool perspectiveMapping = true;

        // 세트 하나가 동시에 표시할 최대 마커 수. 0 = 제한 없음.
        public int maxSimultaneous = 0;

        // 후보만 잡히고 인식이 안 될 때 전체 딕셔너리를 훑는 주기(프레임). 0 = 사용 안 함.
        //
        // 인쇄한 마커의 딕셔너리를 모를 때 답을 알려주는 진단 기능이다. 한 번 도는 데
        // 720p 기준 약 140ms 가 들어(21개 딕셔너리 전수 검출) 켜 두면 카메라 프레임이 눈에 띄게 준다.
        // dictionaryId 가 확정된 뒤에는 0 으로 둔다.
        public int autoScanIntervalFrames = 0;

        // 세트 하나가 동시에 돌릴 영상 디코더 수. 층당 PC 1대 구성이라 상한을 둔다.
        public int maxConcurrentVideos = 4;

        // ---- 관람 기준 (→ SPEC §5 재생 정책, ARCHITECTURE §5 관람 기록) ----
        // 영상 재생 유지와 관람 카운트가 같은 값을 쓴다. "한 번 이어서 재생된 영상" 이 곧 "관람 1회" 다.

        // 마커가 처음 잡힌 뒤 이 시간 동안 유지되어야 관람으로 센다.
        // 그 전에 사라지면 책장을 넘기다 스친 것이거나 오인식으로 보고 세지 않는다.
        public float viewMinDwellSeconds = 1f;

        // 마커를 놓친 뒤 이 시간 안에 다시 잡히면 같은 관람이다 — 영상은 멈춘 자리에서 이어서 돈다.
        // 넘기면 관람이 끝난 것이다 — 기록을 남기고 영상은 처음으로 되감는다.
        // holdSeconds(콘텐츠를 계속 그려 두는 시간)보다 길어야 한다. 그 사이에는 콘텐츠가 숨고 영상만 멈춰 있다.
        //
        // 20초 — 책장을 넘기거나 옆 사람과 얘기하다 돌아오는 시간을 넉넉히 덮는다. 그보다 길면
        // 다음 관람객이 앞사람이 보던 중간부터 보게 되고 그 사람은 세어지지 않는다.
        public float viewResumeGraceSeconds = 20f;

        // 관람을 파일로 남길지. persistentDataPath/stats/ 에 날짜별 CSV 로 쌓인다.
        public bool recordViews = true;

        // ---- 등장 연출 (→ ArUcoAppear.cs, ARCHITECTURE §5 등장 연출) ----
        // 마커를 놓고 멈추면 빛 고리가 퍼진 뒤 콘텐츠가 나타난다. 놓칠 때는 연출 없이 holdSeconds 뒤 꺼진다.
        public AppearConfig appear = new AppearConfig();

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
            maxCachedImages = Mathf.Max(1, maxCachedImages);
            viewMinDwellSeconds = Mathf.Max(0f, viewMinDwellSeconds);
            // hold 안에 돌아오는 것은 놓친 적도 없는 것이다. grace 가 그보다 짧으면 의미가 없다.
            viewResumeGraceSeconds = Mathf.Max(holdSeconds, viewResumeGraceSeconds);
            manualTargetSize = Mathf.Clamp(manualTargetSize, MinManualTargetSize, MaxManualTargetSize);
            appear ??= new AppearConfig();
            appear.Validate();

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
