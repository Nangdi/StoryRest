using System;
using System.Collections;
using System.Collections.Generic;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.VideoioModule;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 웹캠 영상에서 ArUco 마커를 검출하고 "네 꼭짓점 / 중심 / 기울기 / 크기" 를 뽑아낸다.
/// 카메라 캡처는 OpenCV VideoCapture (DirectShow 우선) — UVC 미지원 비전카메라 호환.
///
/// 네 꼭짓점을 그대로 넘기는 것이 핵심이다. 이 네 점이면 마커가 놓인 평면이 결정되므로
/// (ArUcoHomography 참고) 카메라 캘리브레이션 없이도 페이지가 눕는 각도까지 재현할 수 있다.
/// solvePnP 로 3D 자세를 풀지 않는 이유도 같다 — 평면 위 매핑에는 내부 파라미터가 필요 없다.
/// </summary>
[DisallowMultipleComponent]
public class ArUcoMarkerTracker : MonoBehaviour
{
    public enum ArUcoDictionary
    {
        DICT_4X4_50 = Objdetect.DICT_4X4_50,
        DICT_4X4_100 = Objdetect.DICT_4X4_100,
        DICT_4X4_250 = Objdetect.DICT_4X4_250,
        DICT_4X4_1000 = Objdetect.DICT_4X4_1000,
        DICT_5X5_50 = Objdetect.DICT_5X5_50,
        DICT_5X5_100 = Objdetect.DICT_5X5_100,
        DICT_5X5_250 = Objdetect.DICT_5X5_250,
        DICT_5X5_1000 = Objdetect.DICT_5X5_1000,
        DICT_6X6_50 = Objdetect.DICT_6X6_50,
        DICT_6X6_100 = Objdetect.DICT_6X6_100,
        DICT_6X6_250 = Objdetect.DICT_6X6_250,
        DICT_6X6_1000 = Objdetect.DICT_6X6_1000,
        DICT_7X7_50 = Objdetect.DICT_7X7_50,
        DICT_7X7_100 = Objdetect.DICT_7X7_100,
        DICT_7X7_250 = Objdetect.DICT_7X7_250,
        DICT_7X7_1000 = Objdetect.DICT_7X7_1000,
        DICT_ARUCO_ORIGINAL = Objdetect.DICT_ARUCO_ORIGINAL,
    }

    public enum CaptureBackend
    {
        DSHOW = Videoio.CAP_DSHOW,
        ANY = Videoio.CAP_ANY,
        MSMF = Videoio.CAP_MSMF,
    }

    /// <summary>마커 하나의 추적 결과. 좌표는 영상 픽셀 기준(좌상단 원점, y 아래로 증가).</summary>
    public struct TrackedMarker
    {
        public int id;

        // 영상에 찍힌 네 꼭짓점 (좌상 → 우상 → 우하 → 좌하).
        // 원근 매핑은 이 네 점만으로 마커 평면을 복원한다.
        public Vector2 corner0;
        public Vector2 corner1;
        public Vector2 corner2;
        public Vector2 corner3;

        // 아래 셋은 네 꼭짓점에서 유도한 값. 원근을 끈 모드와 정렬·표시용으로 쓴다.
        public Vector2 center;
        public float angleDeg;   // 화면 기준 반시계 방향이 + (UI 의 Z 회전과 같은 방향)
        public float sizePx;     // 마커 한 변의 평균 길이
        public bool visible;     // false 면 잠깐 놓쳐서 holdSeconds 동안 붙잡아 두는 중
    }

    class MarkerState
    {
        public Vector2 c0, c1, c2, c3;
        public Vector2 center;
        public float angleDeg;
        public float sizePx;
        public float lastSeenTime;
        public bool hasValue;

        // 중심·기울기·크기는 따로 보간하지 않고 보간된 꼭짓점에서 다시 뽑는다.
        // 그래야 원근 모드와 회전 모드가 같은 자세를 가리키고, 각도 wrap 처리도 필요 없다.
        public void Recompute()
        {
            center = (c0 + c1 + c2 + c3) * 0.25f;
            sizePx = (Vector2.Distance(c0, c1) + Vector2.Distance(c1, c2)
                    + Vector2.Distance(c2, c3) + Vector2.Distance(c3, c0)) * 0.25f;

            // 마커 윗변(c0→c1)의 방향이 곧 화면상 기울기.
            // 영상 좌표는 y 가 아래로 커지므로, UI 회전(반시계 +)으로 쓰려면 y 부호를 뒤집는다.
            Vector2 top = c1 - c0;
            angleDeg = Mathf.Atan2(-top.y, top.x) * Mathf.Rad2Deg;
        }
    }

    [Header("카메라")]
    [Tooltip("OpenCV VideoCapture 디바이스 인덱스.")]
    public int DeviceId = 0;
    [Tooltip("0 = CAP_DSHOW(권장) / 1 = CAP_ANY / 2 = CAP_MSMF")]
    public int Backend = 0;
    [Tooltip("0 이면 카메라 기본값을 쓴다.")]
    public int RequestedWidth = 0;
    public int RequestedHeight = 0;
    public int RequestedFps = 0;
    [Tooltip("거울 효과용이 아니다. 카메라가 애초에 거울상을 내보낼 때만 쓴다.")]
    public bool FlipHorizontal = false;
    public bool FlipVertical = false;

    [Header("검출")]
    [Tooltip("OpenCV predefined dictionary 번호. 바꾼 뒤에는 BuildDetector 를 다시 호출해야 한다.")]
    public int DictionaryId = 0;
    [Tooltip("0 = 즉시 반응(떨림), 1 에 가까울수록 부드럽지만 늦다.")]
    public float Smoothing = 0.4f;
    [Tooltip("마커를 놓친 뒤 몇 초 더 붙잡아 둘지.")]
    public float HoldSeconds = 0.3f;
    [Tooltip("후보만 잡히고 인식이 안 될 때 전체 딕셔너리를 훑는 주기(프레임). 0 = 사용 안 함.")]
    public int AutoScanIntervalFrames = 60;

    [Header("프리뷰")]
    [Tooltip("카메라 영상을 텍스처로 올린다. 프로젝터 투사에는 쓰지 않으므로 평소엔 꺼 둔다.\n" +
             "켜면 매 프레임 색공간 변환과 GPU 업로드가 추가된다 — 편집모드에서만 켤 것.")]
    public bool ProducePreviewTexture = false;

    [Header("Debug")]
    [Tooltip("프리뷰 영상에 검출된 마커 테두리를 그린다. ProducePreviewTexture 가 켜져 있어야 보인다.")]
    public bool DrawDetectedMarkers = false;
    [Tooltip("마커로 인식되지 못한 후보를 빨간 테두리로 그린다.")]
    public bool ShowRejectedCandidates = false;
    [Tooltip("검출된 ID 조합이 바뀔 때 Console 로그")]
    public bool LogIdChanges = false;

    [Header("Events")]
    public UnityEvent<int[]> OnIdsDetected;

    public bool IsInitialized { get; private set; }
    public string LastError { get; private set; }
    public Texture2D FrameTexture { get; private set; }
    public int FrameWidth => FrameTexture != null ? FrameTexture.width : 0;
    public int FrameHeight => FrameTexture != null ? FrameTexture.height : 0;
    public int RejectedCount { get; private set; }
    public int[] LastDetectedIds { get; private set; } = new int[0];

    /// <summary>이번 프레임에 표시할 마커들. 화면상 크기가 큰 순서로 정렬되어 있다.</summary>
    public IReadOnlyList<TrackedMarker> Markers => _tracked;

    VideoCapture _capture;
    Mat _bgrMat;
    Mat _rgbMat;    // 검출용 3채널
    Mat _rgbaMat;   // 프리뷰용 4채널
    Mat _ids;
    List<Mat> _corners;
    List<Mat> _rejected;
    Dictionary _arucoDict;
    ArucoDetector _detector;
    Coroutine _initCoroutine;

    readonly Dictionary<int, MarkerState> _states = new Dictionary<int, MarkerState>();
    readonly List<TrackedMarker> _tracked = new List<TrackedMarker>();
    readonly HashSet<int> _seenThisFrame = new HashSet<int>();
    readonly List<int> _expired = new List<int>();
    readonly float[] _pointBuffer = new float[2];

    int[] _idBuffer = new int[16];
    int _scanCounter;
    string _lastIdsSignature = "";

    static readonly Comparison<TrackedMarker> BySizeDescending =
        (a, b) => b.sizePx.CompareTo(a.sizePx);

    /// <summary>
    /// 설정 필드를 채운 뒤 호출한다. 카메라를 열고 검출기를 만든다.
    /// 세트마다 별도의 GameObject 에 붙여 여러 대를 동시에 돌릴 수 있다.
    /// </summary>
    public void Open()
    {
        if (_initCoroutine != null) StopCoroutine(_initCoroutine);
        _initCoroutine = StartCoroutine(InitializeCamera());
    }

    IEnumerator InitializeCamera()
    {
        int backend = BackendToVideoio(Backend);

        _capture = new VideoCapture();

        // Play 진입 직후엔 카메라 OS 핸들이 아직 release 안 됐을 수 있어 retry 한다.
        bool opened = false;
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            opened = _capture.open(DeviceId, backend);
            if (opened && _capture.isOpened()) break;
            opened = false;
            _capture.release();
            Debug.LogWarning($"[ArUco] deviceId={DeviceId} backend={backend} open 시도 {attempt}/{maxAttempts} 실패 — 0.5초 후 재시도");
            yield return new WaitForSeconds(0.5f);
        }

        if (!opened || !_capture.isOpened())
        {
            Debug.LogWarning($"[ArUco] deviceId={DeviceId} 지정한 backend 실패 — CAP_ANY 폴백");
            _capture.release();
            opened = _capture.open(DeviceId, Videoio.CAP_ANY);
        }

        if (!opened || !_capture.isOpened())
        {
            LastError = $"카메라를 열지 못했습니다 (deviceId={DeviceId})";
            Debug.LogError($"[ArUco] {LastError}");
            yield break;
        }

        if (RequestedWidth > 0) _capture.set(Videoio.CAP_PROP_FRAME_WIDTH, RequestedWidth);
        if (RequestedHeight > 0) _capture.set(Videoio.CAP_PROP_FRAME_HEIGHT, RequestedHeight);
        if (RequestedFps > 0) _capture.set(Videoio.CAP_PROP_FPS, RequestedFps);

        // 첫 프레임 도착까지 대기 (최대 약 1초)
        _bgrMat = new Mat();
        for (int tries = 0; tries < 60; tries++)
        {
            if (_capture.grab() && _capture.retrieve(_bgrMat) && _bgrMat.width() > 0) break;
            yield return null;
        }

        if (_bgrMat == null || _bgrMat.width() == 0 || _bgrMat.height() == 0)
        {
            LastError = "카메라 첫 프레임 수신 실패";
            Debug.LogError($"[ArUco] {LastError}");
            _capture.release();
            yield break;
        }

        int w = _bgrMat.width();
        int h = _bgrMat.height();

        _rgbMat = new Mat(h, w, CvType.CV_8UC3);
        _rgbaMat = new Mat(h, w, CvType.CV_8UC4);
        FrameTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
        FrameTexture.wrapMode = TextureWrapMode.Clamp;

        _ids = new Mat();
        _corners = new List<Mat>();
        _rejected = new List<Mat>();

        BuildDetector();

        LastError = null;
        IsInitialized = true;
        _initCoroutine = null;
        Debug.Log($"[ArUco] Ready (deviceId={DeviceId}, backend={backend}, dict={(ArUcoDictionary)DictionaryId}, {w}x{h})");
    }

    static int BackendToVideoio(int value)
    {
        switch (value)
        {
            case 1: return Videoio.CAP_ANY;
            case 2: return Videoio.CAP_MSMF;
            default: return Videoio.CAP_DSHOW;
        }
    }

    /// <summary>딕셔너리를 바꿨을 때 검출기를 다시 만든다.</summary>
    public void BuildDetector()
    {
        _detector?.Dispose();
        _arucoDict?.Dispose();

        _arucoDict = Objdetect.getPredefinedDictionary(DictionaryId);

        // ArUcoDialInput / ArUcoIdDetector 에서 현장 검증된 파라미터를 그대로 쓴다.
        var detectorParams = new DetectorParameters();
        detectorParams.set_minDistanceToBorder(3);
        detectorParams.set_useAruco3Detection(true);
        detectorParams.set_cornerRefinementMethod(Objdetect.CORNER_REFINE_SUBPIX);
        detectorParams.set_minSideLengthCanonicalImg(16);
        detectorParams.set_errorCorrectionRate(0.7);

        var refineParams = new RefineParameters(10f, 3f, true);
        _detector = new ArucoDetector(_arucoDict, detectorParams, refineParams);
    }

    void Update()
    {
        if (!IsInitialized || _capture == null || !_capture.isOpened() || _detector == null) return;

        if (!_capture.grab()) return;
        if (!_capture.retrieve(_bgrMat)) return;
        if (_bgrMat.empty()) return;

        Imgproc.cvtColor(_bgrMat, _rgbMat, Imgproc.COLOR_BGR2RGB);

        // 프리뷰용 4채널 변환과 GPU 업로드는 화면에 영상을 띄울 때만 한다.
        // 프로젝터에는 카메라 영상을 쏘지 않으므로 평소에는 이 비용이 통째로 빠진다.
        if (ProducePreviewTexture)
            Imgproc.cvtColor(_bgrMat, _rgbaMat, Imgproc.COLOR_BGR2RGBA);

        ApplyFlip();

        DisposeList(_corners); _corners.Clear();
        DisposeList(_rejected); _rejected.Clear();
        _detector.detectMarkers(_rgbMat, _corners, _ids, _rejected);

        RejectedCount = _rejected.Count;
        int markerCount = (int)_ids.total();

        ReadDetections(markerCount);
        NotifyIdChanges(markerCount);
        UpdateTrackedList();

        if (AutoScanIntervalFrames > 0 && markerCount == 0 && _rejected.Count > 0)
        {
            _scanCounter++;
            if (_scanCounter >= AutoScanIntervalFrames)
            {
                _scanCounter = 0;
                ScanAllDictionaries();
            }
        }

        if (!ProducePreviewTexture) return;

        if (DrawDetectedMarkers && markerCount > 0)
            Objdetect.drawDetectedMarkers(_rgbaMat, _corners, _ids, new Scalar(0, 255, 0, 255));

        if (ShowRejectedCandidates && _rejected.Count > 0)
        {
            using (var empty = new Mat())
                Objdetect.drawDetectedMarkers(_rgbaMat, _rejected, empty, new Scalar(255, 0, 0, 255));
        }

        OpenCVMatUtils.MatToTexture2D(_rgbaMat, FrameTexture);
    }

    void ApplyFlip()
    {
        if (!FlipHorizontal && !FlipVertical) return;

        // 검출용(_rgbMat)은 항상, 프리뷰용(_rgbaMat)은 만들었을 때만 뒤집는다.
        int code = FlipHorizontal && FlipVertical ? -1 : (FlipHorizontal ? 1 : 0);

        Core.flip(_rgbMat, _rgbMat, code);
        if (ProducePreviewTexture) Core.flip(_rgbaMat, _rgbaMat, code);
    }

    void ReadDetections(int markerCount)
    {
        _seenThisFrame.Clear();
        if (markerCount == 0) return;

        if (_idBuffer.Length < markerCount) _idBuffer = new int[markerCount];
        _ids.get(0, 0, _idBuffer);

        float now = Time.time;
        float step = SmoothingStep();

        for (int i = 0; i < markerCount && i < _corners.Count; i++)
        {
            // 1x4 CV_32FC2 → 마커 기준 좌상 → 우상 → 우하 → 좌하 순서
            using (Mat reshaped = _corners[i].reshape(2, 4))
            {
                Vector2 c0 = ReadPoint(reshaped, 0);
                Vector2 c1 = ReadPoint(reshaped, 1);
                Vector2 c2 = ReadPoint(reshaped, 2);
                Vector2 c3 = ReadPoint(reshaped, 3);

                int id = _idBuffer[i];
                if (!_states.TryGetValue(id, out var state))
                {
                    state = new MarkerState();
                    _states[id] = state;
                }

                if (!state.hasValue)
                {
                    // 처음 잡힌 마커는 보간 없이 바로 자리를 잡아야 이미지가 화면 구석에서 날아오지 않는다.
                    state.c0 = c0; state.c1 = c1; state.c2 = c2; state.c3 = c3;
                    state.hasValue = true;
                }
                else
                {
                    // 꼭짓점을 각각 보간한다. 원근 매핑은 마커보다 훨씬 큰 이미지까지 이 네 점으로
                    // 외삽하므로, 몇 픽셀의 코너 떨림도 이미지 가장자리에서는 크게 흔들린다.
                    state.c0 = Vector2.Lerp(state.c0, c0, step);
                    state.c1 = Vector2.Lerp(state.c1, c1, step);
                    state.c2 = Vector2.Lerp(state.c2, c2, step);
                    state.c3 = Vector2.Lerp(state.c3, c3, step);
                }

                state.Recompute();
                state.lastSeenTime = now;
                _seenThisFrame.Add(id);
            }
        }
    }

    Vector2 ReadPoint(Mat reshaped, int row)
    {
        reshaped.get(row, 0, _pointBuffer);
        return new Vector2(_pointBuffer[0], _pointBuffer[1]);
    }

    // smoothing 값을 프레임레이트와 무관한 보간 계수로 바꾼다.
    // 0 이면 즉시 반응, 1 에 가까울수록 천천히 따라온다.
    float SmoothingStep()
    {
        float s = Mathf.Clamp(Smoothing, 0f, 0.99f);
        if (s <= 0f) return 1f;
        return 1f - Mathf.Pow(s, Mathf.Max(Time.deltaTime, 0.0001f) * 60f);
    }

    void UpdateTrackedList()
    {
        float now = Time.time;
        float hold = Mathf.Max(0f, HoldSeconds);

        _tracked.Clear();
        _expired.Clear();

        foreach (var pair in _states)
        {
            var state = pair.Value;
            bool seen = _seenThisFrame.Contains(pair.Key);

            if (!seen && now - state.lastSeenTime > hold)
            {
                _expired.Add(pair.Key);
                continue;
            }

            _tracked.Add(new TrackedMarker
            {
                id = pair.Key,
                corner0 = state.c0,
                corner1 = state.c1,
                corner2 = state.c2,
                corner3 = state.c3,
                center = state.center,
                angleDeg = state.angleDeg,
                sizePx = state.sizePx,
                visible = seen,
            });
        }

        for (int i = 0; i < _expired.Count; i++) _states.Remove(_expired[i]);

        // maxSimultaneous 로 개수를 자를 때 "가장 크게 잡힌 것" 이 남도록 정렬해 둔다.
        _tracked.Sort(BySizeDescending);
    }

    void NotifyIdChanges(int markerCount)
    {
        if (markerCount > 0)
        {
            var ids = new int[markerCount];
            Array.Copy(_idBuffer, ids, markerCount);
            LastDetectedIds = ids;

            string signature = string.Join(",", ids);
            if (signature != _lastIdsSignature)
            {
                _lastIdsSignature = signature;
                if (LogIdChanges) Debug.Log($"[ArUco] Detected IDs: [{signature}]");
                OnIdsDetected?.Invoke(ids);
            }
        }
        else if (_lastIdsSignature != "")
        {
            _lastIdsSignature = "";
            LastDetectedIds = new int[0];
            if (LogIdChanges) Debug.Log("[ArUco] No markers");
        }
    }

    // 인쇄한 마커의 딕셔너리를 모를 때, 후보만 잡히고 인식이 안 되면 전체를 훑어 알려준다.
    void ScanAllDictionaries()
    {
        int[] allDicts = {
            Objdetect.DICT_4X4_50, Objdetect.DICT_4X4_100, Objdetect.DICT_4X4_250, Objdetect.DICT_4X4_1000,
            Objdetect.DICT_5X5_50, Objdetect.DICT_5X5_100, Objdetect.DICT_5X5_250, Objdetect.DICT_5X5_1000,
            Objdetect.DICT_6X6_50, Objdetect.DICT_6X6_100, Objdetect.DICT_6X6_250, Objdetect.DICT_6X6_1000,
            Objdetect.DICT_7X7_50, Objdetect.DICT_7X7_100, Objdetect.DICT_7X7_250, Objdetect.DICT_7X7_1000,
            Objdetect.DICT_ARUCO_ORIGINAL,
            Objdetect.DICT_APRILTAG_16h5, Objdetect.DICT_APRILTAG_25h9,
            Objdetect.DICT_APRILTAG_36h10, Objdetect.DICT_APRILTAG_36h11,
        };
        string[] names = {
            "DICT_4X4_50","DICT_4X4_100","DICT_4X4_250","DICT_4X4_1000",
            "DICT_5X5_50","DICT_5X5_100","DICT_5X5_250","DICT_5X5_1000",
            "DICT_6X6_50","DICT_6X6_100","DICT_6X6_250","DICT_6X6_1000",
            "DICT_7X7_50","DICT_7X7_100","DICT_7X7_250","DICT_7X7_1000",
            "DICT_ARUCO_ORIGINAL",
            "DICT_APRILTAG_16h5","DICT_APRILTAG_25h9",
            "DICT_APRILTAG_36h10","DICT_APRILTAG_36h11",
        };

        for (int k = 0; k < allDicts.Length; k++)
        {
            using (var dict = Objdetect.getPredefinedDictionary(allDicts[k]))
            {
                var dp = new DetectorParameters();
                dp.set_errorCorrectionRate(0.8);
                dp.set_useAruco3Detection(true);

                using (var det = new ArucoDetector(dict, dp))
                using (var ids = new Mat())
                {
                    var corners = new List<Mat>();
                    var rejected = new List<Mat>();
                    det.detectMarkers(_rgbMat, corners, ids, rejected);

                    if (corners.Count > 0)
                    {
                        int n = (int)ids.total();
                        int[] buf = new int[n];
                        if (n > 0) ids.get(0, 0, buf);
                        Debug.LogWarning($"[ArUco] AUTO-SCAN MATCH: {names[k]} ids=[{string.Join(",", buf)}] — " +
                                         $"aruco.json 의 dictionaryId 를 {allDicts[k]} ({names[k]}) 로 바꿔주세요.");
                    }

                    DisposeList(corners);
                    DisposeList(rejected);
                }
            }
        }
    }

    static void DisposeList(List<Mat> list)
    {
        if (list == null) return;
        foreach (var m in list) m.Dispose();
    }

    void OnDestroy()
    {
        IsInitialized = false;
        if (_initCoroutine != null) { StopCoroutine(_initCoroutine); _initCoroutine = null; }

        _capture?.release();
        _capture?.Dispose(); _capture = null;
        _bgrMat?.Dispose(); _bgrMat = null;
        _rgbMat?.Dispose(); _rgbMat = null;
        _rgbaMat?.Dispose(); _rgbaMat = null;
        _ids?.Dispose(); _ids = null;
        DisposeList(_corners); _corners = null;
        DisposeList(_rejected); _rejected = null;
        _arucoDict?.Dispose(); _arucoDict = null;
        _detector?.Dispose(); _detector = null;
        if (FrameTexture != null) { Destroy(FrameTexture); FrameTexture = null; }
    }
}
