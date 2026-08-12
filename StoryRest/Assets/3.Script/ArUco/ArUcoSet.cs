using System.Collections.Generic;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 카메라 1대 + 프로젝터 1대로 이루어진 인터랙션 세트 하나.
    ///
    /// 세트끼리는 완전히 독립이다. 검출기·출력·보정값을 각자 갖고, 마커 상태를 공유하지 않는다.
    /// 같은 마커 ID 가 두 세트에 동시에 놓이면 각 세트가 각자 재생한다.
    /// </summary>
    [DisallowMultipleComponent]
    public class ArUcoSet : MonoBehaviour
    {
        ArUcoConfig _config;
        SetConfig _set;
        ArUcoContentIndex _content;

        ArUcoMarkerTracker _tracker;
        ArUcoProjectionView _view;
        ArUcoContentLibrary _library;

        bool _projectionReady;
        bool _previewProjectionReady;

        readonly List<int> _visibleIds = new List<int>();

        public SetConfig Config => _set;
        public ArUcoMarkerTracker Tracker => _tracker;
        public ArUcoProjectionView View => _view;

        /// <summary>
        /// 켜면 콘텐츠를 그리지 않는다. 캘리브레이션 중에 영상이 화면을 가리지 않게 하려는 것이다.
        /// </summary>
        public bool SuppressContent { get; set; }

        /// <summary>편집모드에서만 카메라 영상을 만든다. 평상시에는 투사하지 않으므로 꺼 둔다.</summary>
        public bool CameraPreviewEnabled
        {
            get => _tracker != null && _tracker.ProducePreviewTexture;
            set { if (_tracker != null) _tracker.ProducePreviewTexture = value; }
        }

        /// <summary>편집 중인 마커에 판을 깔아 어느 것을 조정 중인지 보이게 한다. -1 이면 표시 안 함.</summary>
        public int HighlightMarkerId { get; set; } = -1;

        /// <summary>이번 프레임에 보이는 마커들. 화면상 큰 것부터 정렬되어 있다(편집모드에서 고를 때 쓴다).</summary>
        public IReadOnlyList<int> VisibleMarkerIds => _visibleIds;

        /// <param name="displayIndex">
        /// 실제로 출력할 디스플레이. 보통 set.displayIndex 지만, 에디터 분할 프리뷰에서는 0 이 넘어온다.
        /// </param>
        public void Initialize(ArUcoConfig config, SetConfig set, ArUcoContentIndex content,
                               int setIndex, Rect viewport, int displayIndex)
        {
            _config = config;
            _set = set;
            _content = content;

            _view = gameObject.AddComponent<ArUcoProjectionView>();
            _view.Setup(set.name, displayIndex, viewport, setIndex);
            _view.ShowStatus("카메라를 여는 중입니다...");

            // 영상 슬롯은 세트마다 따로 갖는다. 세트가 둘이면 디코더도 그만큼 늘어나므로
            // maxConcurrentVideos 는 "세트 하나당" 상한이라는 점에 유의한다.
            _library = new ArUcoContentLibrary(
                content, transform, config.maxConcurrentVideos,
                Mathf.Max(1.5f, config.holdSeconds * 3f));

            // 검출기는 세트마다 별도 인스턴스다. 같은 GameObject 에 둘을 붙일 수 없으므로
            // (DisallowMultipleComponent) 세트가 각자의 GameObject 를 갖는 구조여야 한다.
            _tracker = gameObject.AddComponent<ArUcoMarkerTracker>();
            ApplyConfigToTracker();

            // 프로젝터에는 카메라 영상을 쏘지 않는다. 프리뷰는 편집모드에서만 켠다.
            _tracker.ProducePreviewTexture = false;
            _tracker.Open();
        }

        void ApplyConfigToTracker()
        {
            var cam = _set.camera;

            _tracker.DeviceId = cam.deviceId;
            _tracker.Backend = cam.backend;
            _tracker.RequestedWidth = cam.width;
            _tracker.RequestedHeight = cam.height;
            _tracker.RequestedFps = cam.fps;
            _tracker.FlipHorizontal = cam.flipHorizontal;
            _tracker.FlipVertical = cam.flipVertical;

            _tracker.DictionaryId = _config.dictionaryId;
            _tracker.Smoothing = _config.smoothing;
            _tracker.HoldSeconds = _config.holdSeconds;
        }

        /// <summary>편집모드에서 보정값을 고친 뒤 호출한다.</summary>
        public void RebuildProjection()
        {
            if (!_tracker.IsInitialized) return;

            _view.SetProjection(ArUcoProjection.Build(
                _set.calibration, _tracker.FrameWidth, _tracker.FrameHeight, _set.name));

            _projectionReady = true;
        }

        void Update()
        {
            // 에디터에서 Play 중 스크립트를 고치면 도메인이 다시 로드된다.
            // 이때 Unity 오브젝트 참조(_tracker/_view)는 살아남지만 일반 클래스 필드는 null 이 된다.
            // Initialize 를 다시 거치기 전까지는 아무것도 하지 않는다.
            if (_tracker == null || _view == null || _library == null || _config == null || _set == null) return;

            if (_config.debugPreviewContent)
            {
                DrawContentPreview();
                return;
            }

            if (!_tracker.IsInitialized)
            {
                _view.ShowStatus(string.IsNullOrEmpty(_tracker.LastError)
                    ? $"세트 {_set.name}: 카메라를 여는 중입니다..."
                    : $"세트 {_set.name}: {_tracker.LastError}\n\n" +
                      $"aruco.json 의 sets[].camera.deviceId / backend 를 확인하세요.\n" +
                      $"(backend: 0=DSHOW, 1=ANY, 2=MSMF)");

                _view.BeginFrame();
                _view.EndFrame();
                _library.EndFrame();
                _visibleIds.Clear();
                return;
            }

            // 카메라 해상도는 첫 프레임이 도착한 뒤에야 알 수 있다. 그때 한 번 변환을 세운다.
            if (!_projectionReady) RebuildProjection();

            _view.ShowStatus(null);
            _view.ShowCameraPreview(CameraPreviewEnabled ? _tracker.FrameTexture : null);

            // 캘리브레이션 중에는 콘텐츠가 화면을 가리면 안 된다. 마커 추적은 계속 돈다.
            if (SuppressContent)
            {
                _visibleIds.Clear();
                for (int i = 0; i < _tracker.Markers.Count; i++)
                {
                    if (!_config.IsCalibrationMarker(_tracker.Markers[i].id))
                        _visibleIds.Add(_tracker.Markers[i].id);
                }

                _view.BeginFrame();
                _view.EndFrame();
                _library.EndFrame();
                return;
            }

            DrawMarkers();
        }

        void DrawMarkers()
        {
            var markers = _tracker.Markers;

            int limit = _config.maxSimultaneous > 0
                ? Mathf.Min(_config.maxSimultaneous, markers.Count)
                : markers.Count;

            _visibleIds.Clear();
            _view.BeginFrame();

            int drawn = 0;

            for (int i = 0; i < markers.Count && drawn < limit; i++)
            {
                var marker = markers[i];

                // 캘리브레이션용으로 예약한 ID 는 콘텐츠를 갖지 않는다.
                if (_config.IsCalibrationMarker(marker.id)) continue;

                _visibleIds.Add(marker.id);

                var markerConfig = _set.GetOrCreate(marker.id);
                if (!markerConfig.enabled) continue;

                var plane = BuildMarkerPlane(marker);
                if (!plane.IsValid) continue;

                var style = ArUcoDrawStyle.Default(
                    _set.globalScale, new Vector2(_set.globalOffsetX, _set.globalOffsetY),
                    _config.warpSubdivisions, _config.perspectiveMapping);

                if (_library.TryGetFrame(marker.id, out Texture texture, out bool flipV, out float aspect))
                {
                    style.texture = texture;
                    style.flipV = flipV;
                    style.aspect = aspect;
                    style.tint = Color.white;
                }
                else
                {
                    // 콘텐츠 폴더가 없거나, 슬롯이 모자라거나, 아직 첫 프레임이 안 나온 상태다.
                    // 자리만 잡아 두면 현장에서 "인식은 되는데 영상이 없다"를 바로 구분할 수 있다.
                    style.tint = PlaceholderColor(marker.id, _content != null && _content.Has(marker.id));
                }

                if (_view.Draw(plane, markerConfig, style)) drawn++;

                if (marker.id == HighlightMarkerId)
                    _view.DrawHighlight(plane, markerConfig, style);
            }

            _view.EndFrame();
            _library.EndFrame();
        }

        /// <summary>현장에서 콘텐츠 파일을 갈아끼운 뒤 호출한다.</summary>
        public void ReloadContent()
        {
            _library.ReleaseAll();
        }

        /// <summary>
        /// 마커 인식 없이 등록된 콘텐츠를 화면에 격자로 늘어놓는다(debugPreviewContent).
        /// 카메라가 없는 자리에서 영상 파일과 재생 경로만 점검할 때 쓴다.
        /// </summary>
        void DrawContentPreview()
        {
            // 마커 평면 대신 화면 좌표를 그대로 쓴다. 변환을 항등으로 두어
            // 아래에서 만드는 정규화 좌표가 그대로 화면 위치가 되게 한다.
            if (!_previewProjectionReady)
            {
                _view.SetProjection(ArUcoProjection.Build(null, 1, 1, _set.name));
                _previewProjectionReady = true;
            }

            var ids = _content != null ? _content.MarkerIds : new List<int>();

            _view.BeginFrame();

            if (ids.Count == 0)
            {
                _view.ShowStatus($"[디버그] 콘텐츠가 없습니다.\n{_content?.Root}");
                _view.EndFrame();
                _library.EndFrame();
                return;
            }

            _view.ShowStatus($"[디버그] 콘텐츠 미리보기 {ids.Count}개 — 전시 전에 debugPreviewContent 를 끄세요");

            int columns = Mathf.CeilToInt(Mathf.Sqrt(ids.Count));
            int rows = Mathf.CeilToInt(ids.Count / (float)columns);

            // 격자는 화면 좌표를 직접 쓰므로 세트 공통 배치를 적용하지 않는다.
            // 여기서 확인하려는 것은 배치가 아니라 "영상 파일이 제대로 들어갔는지" 다.
            var placement = new MarkerConfig { id = -1 };
            var style = ArUcoDrawStyle.Default(1f, Vector2.zero, _config.warpSubdivisions, false);

            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];

                float cellW = 1f / columns;
                float cellH = 1f / rows;
                float x = (i % columns) * cellW;
                float y = (i / columns) * cellH;

                // 셀 안쪽으로 조금 들여 이웃과 붙지 않게 한다.
                float pad = Mathf.Min(cellW, cellH) * 0.08f;
                var p0 = new Vector2(x + pad, y + pad);
                var p1 = new Vector2(x + cellW - pad, y + pad);
                var p2 = new Vector2(x + cellW - pad, y + cellH - pad);
                var p3 = new Vector2(x + pad, y + cellH - pad);

                var plane = ArUcoHomography.FromUnitSquare(p0, p1, p2, p3);
                if (!plane.IsValid) continue;

                if (_library.TryGetFrame(id, out Texture texture, out bool flipV, out float aspect))
                {
                    style.texture = texture;
                    style.flipV = flipV;
                    style.aspect = aspect;
                    style.tint = Color.white;
                }
                else
                {
                    style.texture = null;
                    style.flipV = false;
                    style.aspect = 1f;
                    style.tint = PlaceholderColor(id, true);
                }

                _view.Draw(plane, placement, style);
            }

            _view.EndFrame();
            _library.EndFrame();
        }

        /// <summary>
        /// 콘텐츠를 얹을 평면을 세운다. 단위 정사각형이 마커 자리에 오도록 맞춘 호모그래피다.
        /// </summary>
        ArUcoHomography BuildMarkerPlane(ArUcoMarkerTracker.TrackedMarker marker)
        {
            if (_config.perspectiveMapping)
            {
                // 실제로 찍힌 네 꼭짓점을 그대로 쓴다 → 원근이 살아난다.
                return ArUcoHomography.FromUnitSquare(
                    marker.corner0, marker.corner1, marker.corner2, marker.corner3);
            }

            // 회전만 쓰는 모드: 마커를 "화면과 나란한 정사각형" 으로 가정해 원근을 지운다.
            float half = marker.sizePx * 0.5f;
            float rad = marker.angleDeg * Mathf.Deg2Rad;
            Vector2 right = new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad)) * half;
            Vector2 down = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad)) * half;
            Vector2 c = marker.center;

            return ArUcoHomography.FromUnitSquare(
                c - right - down, c + right - down, c + right + down, c - right + down);
        }

        // 마커마다 다른 색을 주되 이웃한 ID 끼리 비슷해지지 않도록 황금비로 색상환을 돈다.
        // 콘텐츠가 준비된 마커는 진하게, 폴더가 없는 마커는 흐리게 그려 현장에서 바로 구분되게 한다.
        static Color PlaceholderColor(int markerId, bool hasContent)
        {
            float hue = Mathf.Repeat(markerId * 0.6180339887f, 1f);
            Color color = Color.HSVToRGB(hue, hasContent ? 0.7f : 0.15f, 1f);
            color.a = hasContent ? 0.85f : 0.45f;
            return color;
        }
    }
}
