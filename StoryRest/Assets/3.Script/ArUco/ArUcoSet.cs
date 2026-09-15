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
        ArUcoImageCache _images;

        ArUcoMarkerTracker _tracker;
        ArUcoProjectionView _view;
        ArUcoContentLibrary _library;
        ArUcoViewCounter _views;      // recordViews 가 꺼져 있으면 null
        int _floor;

        bool _projectionReady;
        bool _previewProjectionReady;

        readonly List<int> _visibleIds = new List<int>();

        // debugPreviewContent 격자용. 매 프레임 새로 담되 할당은 하지 않는다.
        readonly List<(int markerId, ContentEntry entry)> _previewEntries
            = new List<(int markerId, ContentEntry entry)>();

        public SetConfig Config => _set;
        public ArUcoMarkerTracker Tracker => _tracker;
        public ArUcoProjectionView View => _view;

        /// <summary>이번 실행에서 센 관람 수. 기록을 끄면 -1.</summary>
        public int ViewsCountedThisRun => _views?.CountedThisRun ?? -1;

        /// <summary>관람 카운터. 기록을 끄면 null. 디버그 패널이 읽는다.</summary>
        public ArUcoViewCounter Views => _views;

        /// <summary>
        /// 켜면 콘텐츠를 그리지 않는다. 편집모드가 정한다 —
        /// 캘리브레이션 중에는 조준점이 가려지지 않게 기본으로 켜지고, 필요하면 사람이 뒤집는다.
        /// 마커 추적은 그대로 돌아가므로 어느 마커가 잡히는지는 계속 볼 수 있다.
        /// </summary>
        public bool SuppressContent { get; set; }

        /// <summary>
        /// 마커 인식 없이 콘텐츠를 격자로 늘어놓는 디버그 표시. 설정(debugPreviewContent)으로 시작하되
        /// 실행 중 F6 으로 뒤집을 수 있다. 파일에는 남기지 않는다 — 전시 중 켜 둔 채 저장되면 안 된다.
        /// </summary>
        public bool DebugPreview { get; set; }

        /// <summary>
        /// 카메라 영상을 화면에 깔지. 편집모드 밖에서도 동작하며 값은 설정에 남는다
        /// (→ SetConfig.showCameraPreview). 켜져 있는 동안만 프레임을 텍스처로 올린다.
        /// </summary>
        public bool CameraPreviewEnabled
        {
            get => _set != null && _set.showCameraPreview;
            set
            {
                if (_set == null) return;

                _set.showCameraPreview = value;
                if (_tracker != null) _tracker.ProducePreviewTexture = value;
            }
        }

        /// <summary>
        /// 켜면 관람을 세지 않는다. 편집모드가 켠다 — 설치자가 배치를 맞추느라 올려 둔 마커는 관람이 아니다.
        /// 켜는 순간 진행 중이던 관람은 끊긴 것으로 보고 마저 기록한다.
        /// </summary>
        public bool SuppressViewCounting
        {
            get => _suppressViewCounting;
            set
            {
                if (_suppressViewCounting == value) return;
                _suppressViewCounting = value;
                if (value) _views?.Flush();
            }
        }
        bool _suppressViewCounting;

        /// <summary>편집 중인 마커에 판을 깔아 어느 것을 조정 중인지 보이게 한다. -1 이면 표시 안 함.</summary>
        public int HighlightMarkerId { get; set; } = -1;

        /// <summary>
        /// 편집 중인 콘텐츠의 폴더 안 순번. -1 이면 마커 전체(= 그 마커의 콘텐츠가 통째로 움직인다).
        /// 한 마커가 영상과 이미지를 함께 띄우므로 어느 장을 만지는 중인지 보여야 한다.
        /// </summary>
        public int HighlightItemIndex { get; set; } = -1;

        /// <summary>이번 프레임에 보이는 마커들. 화면상 큰 것부터 정렬되어 있다(편집모드에서 고를 때 쓴다).</summary>
        public IReadOnlyList<int> VisibleMarkerIds => _visibleIds;

        /// <param name="displayIndex">
        /// 실제로 출력할 디스플레이. 보통 set.displayIndex 지만, 에디터 분할 프리뷰에서는 0 이 넘어온다.
        /// </param>
        public void Initialize(ArUcoConfig config, SetConfig set, ArUcoContentIndex content,
                               ArUcoImageCache images, int floor, int setIndex, Rect viewport, int displayIndex)
        {
            _config = config;
            _set = set;
            _content = content;
            _images = images;
            _floor = floor;
            DebugPreview = config.debugPreviewContent;

            if (config.recordViews)
                _views = new ArUcoViewCounter(floor, set.name, config.viewMinDwellSeconds, config.viewResumeGraceSeconds);

            _view = gameObject.AddComponent<ArUcoProjectionView>();
            _view.Setup(set.name, displayIndex, viewport, setIndex);
            _view.ShowStatus("카메라를 여는 중입니다...");

            // 영상 슬롯은 세트마다 따로 갖는다. 세트가 둘이면 디코더도 그만큼 늘어나므로
            // maxConcurrentVideos 는 "세트 하나당" 상한이라는 점에 유의한다.
            _library = new ArUcoContentLibrary(
                transform, config.maxConcurrentVideos, config.viewResumeGraceSeconds);

            // 검출기는 세트마다 별도 인스턴스다. 같은 GameObject 에 둘을 붙일 수 없으므로
            // (DisallowMultipleComponent) 세트가 각자의 GameObject 를 갖는 구조여야 한다.
            _tracker = gameObject.AddComponent<ArUcoMarkerTracker>();
            ApplyConfigToTracker();

            // 전시 중에는 프로젝터에 카메라 영상을 쏘지 않는다. 설정으로 켠 세트만 프레임을 올린다.
            _tracker.ProducePreviewTexture = _set.showCameraPreview;
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
            _tracker.RequestedFourcc = cam.fourcc;
            _tracker.FlipHorizontal = cam.flipHorizontal;
            _tracker.FlipVertical = cam.flipVertical;

            _tracker.DictionaryId = _config.dictionaryId;
            _tracker.Smoothing = _config.smoothing;
            _tracker.HoldSeconds = _config.holdSeconds;
            _tracker.AutoScanIntervalFrames = _config.autoScanIntervalFrames;
        }

        /// <summary>
        /// 설정 패널에서 값을 바꾼 뒤 호출한다. 재시작 없이 바뀌는 값을 살아 있는 객체에 다시 밀어 넣는다.
        /// 카메라 해상도·디바이스 같은 값은 Open 때 한 번만 읽으므로 여기서는 바뀌지 않는다.
        /// </summary>
        public void ApplyConfig()
        {
            if (_tracker == null || _library == null || _config == null) return;

            ApplyConfigToTracker();
            _library.ResumeGraceSeconds = _config.viewResumeGraceSeconds;

            if (_config.recordViews && _views == null)
            {
                _views = new ArUcoViewCounter(_floor, _set.name, _config.viewMinDwellSeconds, _config.viewResumeGraceSeconds);
            }
            else if (!_config.recordViews && _views != null)
            {
                _views.Flush();
                _views = null;
            }

            if (_views != null)
            {
                _views.MinDwellSeconds = _config.viewMinDwellSeconds;
                _views.ResumeGraceSeconds = _config.viewResumeGraceSeconds;
            }
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

            if (DebugPreview)
            {
                DrawContentPreview();
                return;
            }

            // 격자를 끄면 항등으로 바꿔 둔 변환을 카메라 기준으로 되돌린다.
            if (_previewProjectionReady)
            {
                _previewProjectionReady = false;
                _projectionReady = false;
                _view.ShowStatus(null);
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

            // 관람은 전시 상태에서 콘텐츠를 실제로 그린 프레임에서만 센다. 편집모드와 디버그 격자에서는 세지 않는다.
            if (!_suppressViewCounting) _views?.Update(_visibleIds, Time.unscaledTime);
        }

        void OnDestroy()
        {
            // 종료 시점에 보고 있던 관람을 마저 적는다.
            _views?.Flush();
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

                var entries = _content != null ? _content.GetEntries(marker.id) : null;

                if (entries == null || entries.Count == 0)
                {
                    // 자리만 잡아 두면 현장에서 "인식은 되는데 콘텐츠가 없다"를 바로 구분할 수 있다.
                    var empty = ArUcoPlacement.Of(markerConfig);

                    style.texture = null;
                    style.flipV = false;
                    style.aspect = 1f;
                    style.tint = PlaceholderColor(marker.id, false);

                    if (_view.Draw(plane, empty, style)) drawn++;
                    _view.DrawLabel(plane, empty, style, $"{marker.id}번 마커\n콘텐츠가 없습니다");

                    if (marker.id == HighlightMarkerId) _view.DrawHighlight(plane, empty, style);
                    continue;
                }

                bool any = false;

                // 폴더 안의 파일을 전부 그린다. 이름순이라 나중 것이 위에 올라간다(→ SPEC §4).
                for (int e = 0; e < entries.Count; e++)
                {
                    var entry = entries[e];

                    // 파일을 처음 본 순간 배치 항목이 생긴다. json 을 미리 손볼 필요가 없다.
                    var item = markerConfig.GetOrCreateItem(entry.fileName, e);
                    if (!item.enabled) continue;

                    var placement = ArUcoPlacement.Of(markerConfig, item);

                    if (TryGetTexture(entry, out Texture texture, out bool flipV, out float aspect))
                    {
                        style.texture = texture;
                        style.flipV = flipV;
                        style.aspect = aspect;
                        style.tint = Color.white;
                    }
                    else
                    {
                        // 영상 슬롯을 기다리거나 이미지를 읽는 중이다. 곧 뜨므로 문구는 띄우지 않고
                        // 자리만 색으로 잡아 둔다.
                        style.texture = null;
                        style.flipV = false;
                        style.aspect = 1f;
                        style.tint = PlaceholderColor(marker.id, true);
                    }

                    if (_view.Draw(plane, placement, style)) any = true;

                    if (marker.id == HighlightMarkerId && e == HighlightItemIndex)
                        _view.DrawHighlight(plane, placement, style);
                }

                if (any) drawn++;

                // 마커 전체를 고른 상태(-1)면 콘텐츠가 아니라 마커 자리를 표시한다.
                if (marker.id == HighlightMarkerId && HighlightItemIndex < 0)
                {
                    style.texture = null;
                    style.aspect = 1f;
                    _view.DrawHighlight(plane, ArUcoPlacement.Of(markerConfig), style);
                }
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
        /// 콘텐츠 한 장의 이번 프레임 텍스처. 영상과 이미지가 갈라지는 유일한 자리다.
        /// 아직 준비 전(영상 디코딩 / 이미지 로딩)이면 false 를 돌린다.
        /// </summary>
        bool TryGetTexture(ContentEntry entry, out Texture texture, out bool flipV, out float aspect)
        {
            if (entry.IsVideo) return _library.TryGetFrame(entry.path, out texture, out flipV, out aspect);

            // 이미지는 AVPro 를 거치지 않으므로 상하 반전이 없다.
            flipV = false;

            if (_images != null) return _images.TryGet(entry.path, out texture, out aspect);

            texture = null;
            aspect = 1f;
            return false;
        }

        /// <summary>
        /// 마커 인식 없이 등록된 콘텐츠를 화면에 격자로 늘어놓는다(DebugPreview).
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

            // 마커가 아니라 파일 단위로 늘어놓는다. 한 마커가 여러 장을 가질 수 있으므로
            // 마커 단위로 그리면 정작 확인하려던 파일이 화면에 나오지 않는다.
            _previewEntries.Clear();

            var ids = _content != null ? _content.MarkerIds : new List<int>();
            foreach (int id in ids)
            {
                foreach (var entry in _content.GetEntries(id)) _previewEntries.Add((id, entry));
            }

            _view.BeginFrame();

            if (_previewEntries.Count == 0)
            {
                _view.ShowStatus($"[디버그] 콘텐츠가 없습니다.\n{_content?.Root}");
                _view.EndFrame();
                _library.EndFrame();
                return;
            }

            _view.ShowStatus($"[디버그] 콘텐츠 미리보기 — 마커 {ids.Count}개 · 파일 {_previewEntries.Count}개 " +
                             $"— 전시 전에 F6 으로 끄세요 (aruco.json 의 debugPreviewContent 도 확인)");

            int columns = Mathf.CeilToInt(Mathf.Sqrt(_previewEntries.Count));
            int rows = Mathf.CeilToInt(_previewEntries.Count / (float)columns);

            // 격자는 화면 좌표를 직접 쓰므로 세트 공통 배치와 파일별 배치를 적용하지 않는다.
            // 여기서 확인하려는 것은 배치가 아니라 "파일이 제대로 들어갔는지" 다.
            var placement = ArUcoPlacement.Identity;
            var style = ArUcoDrawStyle.Default(1f, Vector2.zero, _config.warpSubdivisions, false);

            for (int i = 0; i < _previewEntries.Count; i++)
            {
                var (id, entry) = _previewEntries[i];

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

                if (TryGetTexture(entry, out Texture texture, out bool flipV, out float aspect))
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
