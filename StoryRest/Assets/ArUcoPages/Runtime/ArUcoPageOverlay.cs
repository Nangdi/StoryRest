using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 카메라 영상을 화면에 깔고, 인식된 ArUco 마커가 놓인 평면 위에 페이지별 이미지를 얹는다.
///
/// 마커 네 꼭짓점으로 호모그래피를 세워 이미지를 그 평면에 눕히므로,
/// 책을 기울이거나 넘기면 이미지도 페이지 면을 따라 원근이 생긴다.
/// (원근을 끄고 회전만 쓰려면 aruco.json 의 perspectiveMapping 을 false 로 둔다.)
///
/// 씬 준비물은 빈 GameObject 하나에 이 스크립트를 붙이는 것이 전부다.
/// 캔버스와 이미지 오브젝트, 검출기, 편집모드는 전부 런타임에 붙으므로 인스펙터 연결이 없다.
/// </summary>
[DisallowMultipleComponent]
public class ArUcoPageOverlay : MonoBehaviour
{
    [Header("표시")]
    [Tooltip("영상 비율과 화면 비율이 다를 때 남는 여백의 색.")]
    [SerializeField] Color letterboxColor = Color.black;
    [Tooltip("다른 UI 캔버스보다 뒤에 깔고 싶으면 값을 낮춘다.")]
    [SerializeField] int canvasSortingOrder = -100;
    [Tooltip("카메라 영상을 켜고 끄는 키. 영상을 꺼도 마커 이미지는 그대로 뜬다.")]
    [SerializeField] KeyCode cameraToggleKey = KeyCode.C;

    [Header("편집모드")]
    [Tooltip("끄면 전시 중 실수로 값이 바뀌는 것을 막을 수 있다.")]
    [SerializeField] bool enableAdjustMode = true;

    ArUcoJson _config;
    ArUcoMarkerTracker _tracker;
    ArUcoImageLibrary _images;

    Canvas _canvas;
    RawImage _frameImage;
    RectTransform _frameRect;
    ArUcoWarpedImage _highlight;
    Text _statusText;

    readonly List<ArUcoWarpedImage> _pool = new List<ArUcoWarpedImage>();
    readonly List<int> _visibleIds = new List<int>();

    // 매 프레임 재사용하는 격자 버퍼. 마커 수만큼 할당이 생기지 않게 한 벌만 돌려 쓴다.
    readonly List<Vector2> _nodes = new List<Vector2>();
    int _gridDivisions;

    string _configPath;
    int _lastDictionaryId;
    bool _cameraVisible = true;

    public ArUcoJson Config => _config;
    public ArUcoMarkerTracker Tracker => _tracker;

    /// <summary>카메라 영상 표시 여부. 꺼도 마커 위 이미지는 계속 보인다.</summary>
    public bool CameraVisible
    {
        get => _cameraVisible;
        set => _cameraVisible = value;
    }

    /// <summary>편집모드에서 조정할 마커를 고르기 위해 참조한다. 화면상 큰 것부터 정렬되어 있다.</summary>
    public IReadOnlyList<int> VisibleMarkerIds => _visibleIds;

    /// <summary>편집 중인 마커에 반투명 판을 깔아 어느 것을 조정 중인지 보이게 한다. -1 이면 표시 안 함.</summary>
    public int HighlightMarkerId { get; set; } = -1;

    void Awake()
    {
        LoadConfig();
        BuildUI();

        _cameraVisible = _config.showCameraOnStart;

        _images = new ArUcoImageLibrary();
        _images.SetFolder(_config.imageFolder, _config.imagePrefix);
        RegisterMarkersFromFolder();

        _tracker = gameObject.AddComponent<ArUcoMarkerTracker>();
        ApplyConfigToTracker();
        // 이 오버레이는 카메라 영상을 화면에 깔고 그 위에 이미지를 얹는 구조라 프리뷰가 필수다.
        _tracker.ProducePreviewTexture = true;
        _tracker.Open();
        _lastDictionaryId = _config.dictionaryId;

        if (enableAdjustMode && GetComponent<ArUcoAdjustMode>() == null)
            gameObject.AddComponent<ArUcoAdjustMode>();
    }

    void LoadConfig()
    {
        // 프로젝트에 자체 설정 관리자가 있으면 ArUcoConfigStore 에 끼워 공유시킬 수 있다.
        // 아무것도 안 끼우면 StreamingAssets/aruco.json 을 직접 읽는다.
        _configPath = ArUcoConfigStore.DefaultPath;
        _config = ArUcoConfigStore.Load(_configPath);
    }

    public void SaveConfig()
    {
        ArUcoConfigStore.Save(_config, _configPath);
    }

    /// <summary>현장에서 png 를 넣거나 갈아끼웠을 때 재실행 없이 다시 읽는다.</summary>
    public void ReloadImages()
    {
        _images.SetFolder(_config.imageFolder, _config.imagePrefix);
        _images.Clear();
        RegisterMarkersFromFolder();
        Debug.Log($"[ArUco] 이미지를 다시 읽었습니다: {_images.FolderPath}");
    }

    // 폴더에 image_3.png 를 넣어두면 3번 페이지가 자동으로 생긴다.
    // json 을 미리 손보지 않아도 되고, 편집모드에서 Tab 으로 바로 고를 수 있다.
    void RegisterMarkersFromFolder()
    {
        var ids = _images.ScanMarkerIds();
        for (int i = 0; i < ids.Count; i++) _config.GetOrCreate(ids[i]);
    }

    /// <summary>딕셔너리를 바꿨다면 검출기를 다시 만든다.</summary>
    public void ApplyDictionaryChange()
    {
        if (_config.dictionaryId == _lastDictionaryId) return;
        _lastDictionaryId = _config.dictionaryId;
        _tracker.DictionaryId = _config.dictionaryId;
        _tracker.BuildDetector();
    }

    // 검출기는 이제 설정 클래스를 모른다(세트마다 따로 돌려야 하므로).
    // 여기서 값을 옮겨 담아 준다.
    void ApplyConfigToTracker()
    {
        var cam = _config.camera ?? new ArUcoCameraConfig();

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
        _tracker.AutoScanIntervalFrames = _config.autoScanIntervalFrames;
    }

    void BuildUI()
    {
        var canvasGo = new GameObject("ArUcoCanvas", typeof(Canvas), typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);

        _canvas = canvasGo.GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = canvasSortingOrder;

        // 스케일러를 상수 배율로 두어 RectTransform 1 단위 = 화면 1픽셀이 되게 한다.
        // 마커의 영상 픽셀 좌표를 그대로 옮길 수 있어 계산이 단순해진다.
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        var letterbox = NewRawImage("Letterbox", canvasGo.transform);
        letterbox.color = letterboxColor;
        Stretch(letterbox.rectTransform);

        _frameImage = NewRawImage("CameraFrame", canvasGo.transform);
        _frameRect = _frameImage.rectTransform;

        // 이미지들보다 먼저 만들어 항상 뒤에 깔리게 한다(자식 순서 = 그리는 순서).
        _highlight = NewWarpedImage("Highlight", _frameRect);
        _highlight.color = new Color(1f, 0.85f, 0.2f, 0.35f);
        _highlight.enabled = false;

        _statusText = NewText("Status", canvasGo.transform);
        Stretch(_statusText.rectTransform);
        _statusText.alignment = TextAnchor.MiddleCenter;
        _statusText.fontSize = 26;
    }

    void Update()
    {
        if (_tracker == null) return;

        if (Input.GetKeyDown(cameraToggleKey))
            _cameraVisible = !_cameraVisible;

        if (!_tracker.IsInitialized || _tracker.FrameTexture == null)
        {
            _statusText.enabled = true;
            _statusText.text = string.IsNullOrEmpty(_tracker.LastError)
                ? "카메라를 여는 중입니다..."
                : $"{_tracker.LastError}\n\naruco.json 의 camera.deviceId / camera.backend 를 확인하세요.\n(backend: 0=DSHOW, 1=ANY, 2=MSMF)";
            _frameImage.enabled = false;
            HideFrom(0);
            _visibleIds.Clear();
            return;
        }

        _statusText.enabled = false;
        _frameImage.texture = _tracker.FrameTexture;

        // 영상만 끄고 프레임 RectTransform 은 그대로 둔다.
        // 마커 이미지가 이 RectTransform 의 자식이라 좌표 계산은 영향을 받지 않는다.
        _frameImage.enabled = _cameraVisible;

        FitFrameToScreen();
        PlaceOverlays();
    }

    // 영상 비율을 유지한 채 화면에 최대한 크게 맞춘다(레터박스).
    void FitFrameToScreen()
    {
        float camW = _tracker.FrameWidth;
        float camH = _tracker.FrameHeight;
        if (camW <= 0f || camH <= 0f) return;

        float screenW = Screen.width;
        float screenH = Screen.height;

        float fitW, fitH;
        if (camW / camH > screenW / screenH)
        {
            fitW = screenW;
            fitH = screenW * camH / camW;
        }
        else
        {
            fitH = screenH;
            fitW = screenH * camW / camH;
        }

        _frameRect.sizeDelta = new Vector2(fitW, fitH);
    }

    void PlaceOverlays()
    {
        float camW = _tracker.FrameWidth;
        float camH = _tracker.FrameHeight;
        Vector2 frameSize = _frameRect.sizeDelta;

        var markers = _tracker.Markers;
        int limit = _config.maxSimultaneous > 0
            ? Mathf.Min(_config.maxSimultaneous, markers.Count)
            : markers.Count;

        _visibleIds.Clear();
        int used = 0;
        bool highlightPlaced = false;

        for (int i = 0; i < markers.Count && used < limit; i++)
        {
            var marker = markers[i];
            var config = _config.GetOrCreate(marker.id);

            _visibleIds.Add(marker.id);
            if (!config.enabled) continue;

            Texture2D texture = _images.Get(config);
            if (texture == null) continue;

            // 마커가 놓인 평면. 여기에 이미지를 눕히면 페이지 기울기를 그대로 따라간다.
            var plane = BuildMarkerPlane(marker);
            if (!plane.IsValid) continue;

            // 마커 한 변을 1 로 보는 상대 크기라 카메라 거리가 바뀌어도 값이 유지된다.
            float halfWidth = 0.5f * _config.globalScale * config.scale;
            float halfHeight = halfWidth * texture.height / Mathf.Max(1, texture.width);

            if (!BuildGrid(plane, config, halfWidth, halfHeight, 0f, camW, camH, frameSize))
                continue;

            var view = GetView(used);
            view.SetTexture(texture);
            view.SetNodes(_gridDivisions, _gridDivisions, _nodes);
            view.enabled = true;

            // 편집 중인 페이지는 같은 평면에 조금 더 큰 판을 깔아 표시한다.
            if (marker.id == HighlightMarkerId
                && BuildGrid(plane, config, halfWidth, halfHeight, 0.06f, camW, camH, frameSize))
            {
                _highlight.SetNodes(_gridDivisions, _gridDivisions, _nodes);
                highlightPlaced = true;
            }

            used++;
        }

        HideFrom(used);
        _highlight.enabled = highlightPlaced;
    }

    /// <summary>
    /// 이미지를 얹을 평면을 세운다. 단위 정사각형이 마커 자리에 오도록 맞춘 호모그래피다.
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

    /// <summary>
    /// 이미지 사각형을 격자로 나눠 각 꼭짓점을 마커 평면 위로 옮긴 뒤,
    /// 영상 픽셀 좌표를 프레임 RectTransform 의 로컬 좌표로 바꿔 _nodes 에 담는다.
    /// 마커 평면이 카메라와 거의 평행해 좌표가 발산하면 false 를 돌린다.
    /// </summary>
    bool BuildGrid(ArUcoHomography plane, ArUcoMarkerConfig config,
                   float halfWidth, float halfHeight, float margin,
                   float camW, float camH, Vector2 frameSize)
    {
        halfWidth += margin;
        halfHeight += margin;

        // 원근이 없으면 사각형 한 장으로도 정확하다. 쪼갤 이유가 없다.
        _gridDivisions = _config.perspectiveMapping
            ? Mathf.Clamp(_config.warpSubdivisions, 1, 32)
            : 1;

        float rad = config.rotationOffset * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);

        _nodes.Clear();

        for (int y = 0; y <= _gridDivisions; y++)
        {
            float py = Mathf.Lerp(-halfHeight, halfHeight, (float)y / _gridDivisions);

            for (int x = 0; x <= _gridDivisions; x++)
            {
                float px = Mathf.Lerp(-halfWidth, halfWidth, (float)x / _gridDivisions);

                // 마커 평면 안에서 회전·이동시킨다.
                // 평면 위에서 처리하므로 책을 어떻게 돌리고 눕혀도 이미지는 페이지의 같은 자리에 머문다.
                float rx = px * cos - py * sin + config.offsetX;
                float ry = px * sin + py * cos + config.offsetY;

                // 마커 중심이 (0.5, 0.5), 한 변이 1. v 는 아래로 증가하므로 y 부호를 뒤집는다.
                if (!plane.TryMap(new Vector2(0.5f + rx, 0.5f - ry), out Vector2 pixel))
                    return false;

                // MatToTexture2D 가 세로를 뒤집어 그리므로 화면 y 도 부호를 반대로 잡는다.
                _nodes.Add(new Vector2(
                    (pixel.x / camW - 0.5f) * frameSize.x,
                    (0.5f - pixel.y / camH) * frameSize.y));
            }
        }

        return true;
    }

    ArUcoWarpedImage GetView(int index)
    {
        while (_pool.Count <= index)
        {
            var view = NewWarpedImage($"MarkerImage{_pool.Count}", _frameRect);
            _pool.Add(view);
        }
        return _pool[index];
    }

    void HideFrom(int index)
    {
        for (int i = index; i < _pool.Count; i++) _pool[i].enabled = false;
        if (index == 0) _highlight.enabled = false;
    }

    internal static ArUcoWarpedImage NewWarpedImage(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(ArUcoWarpedImage));
        go.transform.SetParent(parent, false);

        var image = go.GetComponent<ArUcoWarpedImage>();
        image.raycastTarget = false;

        // 격자 꼭짓점을 프레임 로컬 좌표로 직접 넣으므로 RectTransform 은 프레임 전체를 덮게 둔다.
        // (RectTransform 의 위치·크기·회전은 쓰지 않는다.)
        Stretch(image.rectTransform);
        return image;
    }

    internal static RawImage NewRawImage(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        go.transform.SetParent(parent, false);

        var image = go.GetComponent<RawImage>();
        image.raycastTarget = false;

        var rect = image.rectTransform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        return image;
    }

    internal static Text NewText(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(parent, false);

        var text = go.GetComponent<Text>();
        text.font = ArUcoUIFont.Get();
        text.color = Color.white;
        text.raycastTarget = false;
        text.supportRichText = true;
        return text;
    }

    internal static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    /// <summary>편집모드 HUD 도 같은 캔버스에 올린다.</summary>
    internal Transform CanvasRoot => _canvas != null ? _canvas.transform : transform;

    /// <summary>편집모드 안내에 표시할 카메라 토글 키.</summary>
    internal KeyCode CameraToggleKey => cameraToggleKey;
}

/// <summary>
/// 런타임에 만드는 UI 에서 쓸 폰트.
/// 안내 문구가 한글이라 TMP 폰트 애셋 연결 없이도 나오도록 OS 폰트를 먼저 시도한다.
/// </summary>
public static class ArUcoUIFont
{
    static Font _cached;

    public static Font Get()
    {
        if (_cached != null) return _cached;

        _cached = Font.CreateDynamicFontFromOSFont("Malgun Gothic", 20)
                  ?? Font.CreateDynamicFontFromOSFont("맑은 고딕", 20)
                  ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return _cached;
    }
}
