using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.VideoioModule;
using UnityEngine;
using UnityEngine.Events;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// 웹캠 영상에서 ArUco 마커를 검출하고 "네 꼭짓점 / 중심 / 기울기 / 크기" 를 뽑아낸다.
/// 카메라 캡처는 OpenCV VideoCapture (DirectShow 우선) — UVC 미지원 비전카메라 호환.
///
/// 네 꼭짓점을 그대로 넘기는 것이 핵심이다. 이 네 점이면 마커가 놓인 평면이 결정되므로
/// (ArUcoHomography 참고) 카메라 캘리브레이션 없이도 페이지가 눕는 각도까지 재현할 수 있다.
/// solvePnP 로 3D 자세를 풀지 않는 이유도 같다 — 평면 위 매핑에는 내부 파라미터가 필요 없다.
///
/// **캡처와 검출은 워커 스레드에서 돈다**(→ ARCHITECTURE §6).
/// grab() 은 다음 카메라 프레임이 도착할 때까지 블로킹이라, 메인 스레드에서 돌리면
/// 렌더 프레임레이트가 카메라 프레임레이트에 묶인다. 카메라가 2대면 두 번 막혀 반토막 난다.
///
///   [워커]  grab → retrieve → cvtColor → detectMarkers → 잠금 버퍼에 결과 기록
///   [메인]  버퍼를 읽어 스무딩 → 추적 목록 갱신 (편집모드일 때만 Texture2D 업로드)
///
/// OpenCV Mat 연산은 Unity API 가 아니라 워커에서 안전하다. Texture2D 업로드만 메인에서 한다.
/// 스무딩은 Time.deltaTime 기반이라 메인에 남는다 — 렌더 주기와 맞아야 부드럽다.
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

    /// <summary>워커가 메인에 넘기는 검출 결과 한 건. Unity API 를 건드리지 않는 순수 값이다.</summary>
    struct RawMarker
    {
        public int id;
        public Vector2 c0, c1, c2, c3;
    }

    class MarkerState
    {
        // 목표(마지막으로 검출된 자리)와 현재(화면에 그려지는 자리)를 나눠 둔다.
        // 카메라는 30fps, 화면은 60fps 이상이라 새 검출이 없는 프레임에도 스무딩은 계속 나아가야 한다.
        public Vector2 t0, t1, t2, t3;
        public Vector2 c0, c1, c2, c3;

        public Vector2 center;
        public float angleDeg;
        public float sizePx;
        public float lastSeenTime;
        public bool hasValue;
        public bool inLatestFrame;

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

    // ── 설정 (Open 전에 채운다) ────────────────────────────────────────────────
    [Header("카메라")]
    [Tooltip("OpenCV VideoCapture 디바이스 인덱스.")]
    public int DeviceId = 0;
    [Tooltip("0 = CAP_DSHOW(권장) / 1 = CAP_ANY / 2 = CAP_MSMF")]
    public int Backend = 0;
    [Tooltip("0 이면 카메라 기본값을 쓴다.")]
    public int RequestedWidth = 0;
    public int RequestedHeight = 0;
    public int RequestedFps = 0;

    [Tooltip("픽셀 포맷 4글자(MJPG 권장). 빈 값이면 드라이버 기본값.\n" +
             "지정하지 않으면 무압축 YUY2 로 열려 720p 가 10fps 로 떨어지는 카메라가 많다.")]
    public string RequestedFourcc = "MJPG";

    // 거울 효과용이 아니다. 카메라가 애초에 거울상을 내보낼 때만 쓴다.
    // 아래 스위치들은 워커 스레드가 매 프레임 읽으므로 volatile 백킹 필드를 둔 프로퍼티다.
    public bool FlipHorizontal { get => _flipHorizontal; set => _flipHorizontal = value; }
    public bool FlipVertical { get => _flipVertical; set => _flipVertical = value; }

    // ── 검출 ──
    // OpenCV predefined dictionary 번호. 바꾼 뒤에는 BuildDetector 를 다시 호출해야 한다.
    public int DictionaryId { get => _dictionaryId; set => _dictionaryId = value; }
    [Tooltip("0 = 즉시 반응(떨림), 1 에 가까울수록 부드럽지만 늦다.")]
    public float Smoothing = 0.4f;
    [Tooltip("마커를 놓친 뒤 몇 초 더 붙잡아 둘지.")]
    public float HoldSeconds = 0.3f;

    // 후보만 잡히고 인식이 안 될 때 전체 딕셔너리를 훑는 주기(프레임). 0 = 사용 안 함.\n
    // 인쇄한 마커의 딕셔너리를 모를 때만 켜는 진단 기능이다. 한 번에 100ms 넘게 걸린다.
    public int AutoScanIntervalFrames { get => _autoScanInterval; set => _autoScanInterval = value; }

    // ── 프리뷰 ──
    // 카메라 영상을 텍스처로 올린다. 프로젝터 투사에는 쓰지 않으므로 평소엔 꺼 둔다.\n
    // 켜면 매 프레임 색공간 변환과 GPU 업로드가 추가된다 — 편집모드에서만 켤 것.
    public bool ProducePreviewTexture { get => _producePreview; set => _producePreview = value; }

    // ── Debug ──
    // 프리뷰 영상에 검출된 마커 테두리를 그린다. ProducePreviewTexture 가 켜져 있어야 보인다.
    public bool DrawDetectedMarkers { get => _drawDetected; set => _drawDetected = value; }
    // 마커로 인식되지 못한 후보를 빨간 테두리로 그린다.
    public bool ShowRejectedCandidates { get => _showRejected; set => _showRejected = value; }
    [Tooltip("검출된 ID 조합이 바뀔 때 Console 로그")]
    public bool LogIdChanges = false;

    [Header("Events")]
    public UnityEvent<int[]> OnIdsDetected;

    volatile bool _flipHorizontal;
    volatile bool _flipVertical;
    volatile int _dictionaryId = 0;
    volatile int _autoScanInterval = 0;
    volatile bool _producePreview;
    volatile bool _drawDetected;
    volatile bool _showRejected;

    // ── 상태 ──────────────────────────────────────────────────────────────────
    public bool IsInitialized => _initialized;
    public string LastError => _lastError;

    /// <summary>편집모드 프리뷰용 텍스처. 프리뷰를 켠 뒤 첫 프레임이 올라올 때 만들어진다.</summary>
    public Texture2D FrameTexture { get; private set; }

    /// <summary>카메라가 실제로 내보내는 해상도. 첫 프레임이 도착해야 정해진다.</summary>
    public int FrameWidth => _frameWidth;
    public int FrameHeight => _frameHeight;

    public int RejectedCount => _rejectedCount;
    public int[] LastDetectedIds { get; private set; } = new int[0];

    /// <summary>이번 프레임에 표시할 마커들. 화면상 크기가 큰 순서로 정렬되어 있다.</summary>
    public IReadOnlyList<TrackedMarker> Markers => _tracked;

    // ── 성능 계측 (편집모드 HUD 에 띄운다) ─────────────────────────────────────
    /// <summary>워커가 다음 카메라 프레임을 기다린 시간. 예전에는 이만큼 메인 스레드가 막혔다.</summary>
    public float CaptureWaitMs => _captureWaitMs;

    /// <summary>워커의 detectMarkers 소요 시간.</summary>
    public float DetectMs => _detectMs;

    /// <summary>카메라에서 실제로 들어오는 초당 프레임 수. 렌더 프레임레이트와는 무관하다.</summary>
    public float CaptureFps => _captureFps;

    /// <summary>카메라가 실제로 열린 포맷. 로그와 HUD 에 그대로 띄운다.</summary>
    public string CaptureFormat => _captureFormat;

    volatile bool _initialized;
    volatile int _frameWidth;
    volatile int _frameHeight;
    volatile int _rejectedCount;
    volatile float _captureWaitMs;
    volatile float _detectMs;
    volatile float _captureFps;
    volatile string _lastError;
    volatile string _captureFormat = "";

    // ── 워커 ──────────────────────────────────────────────────────────────────
    Thread _worker;
    volatile bool _running;
    volatile bool _rebuildDetector;

    readonly object _resultLock = new object();
    RawMarker[] _sharedMarkers = new RawMarker[32];
    int _sharedCount;
    int _sharedSequence;

    // 프리뷰 프레임. 워커가 채우고 메인이 텍스처로 올린다. 같은 잠금으로 보호한다.
    Mat _sharedPreview;
    bool _sharedPreviewDirty;

    readonly ConcurrentQueue<(LogType type, string message)> _logs
        = new ConcurrentQueue<(LogType, string)>();

    // 워커 전용. 메인 스레드는 건드리지 않는다.
    VideoCapture _capture;
    Mat _bgrMat;
    Mat _grayMat;    // 검출 입력. detectMarkers 는 어차피 내부에서 그레이로 바꾼다.
    Mat _rgbaMat;    // 프리뷰용 4채널
    Mat _ids;
    List<Mat> _corners;
    List<Mat> _rejected;
    Dictionary _arucoDict;
    ArucoDetector _detector;
    int _scanCounter;

    // 메인 전용
    readonly Dictionary<int, MarkerState> _states = new Dictionary<int, MarkerState>();
    readonly List<TrackedMarker> _tracked = new List<TrackedMarker>();
    readonly List<int> _expired = new List<int>();
    RawMarker[] _frameMarkers = new RawMarker[32];
    int _frameCount;
    int _lastSequence;
    string _lastIdsSignature = "";

    static readonly Comparison<TrackedMarker> BySizeDescending =
        (a, b) => b.sizePx.CompareTo(a.sizePx);

    /// <summary>
    /// 설정 필드를 채운 뒤 호출한다. 워커 스레드를 띄워 카메라를 열고 검출을 시작한다.
    /// 세트마다 별도의 GameObject 에 붙여 여러 대를 동시에 돌릴 수 있다 — 스레드도 그만큼 늘어난다.
    /// </summary>
    public void Open()
    {
        if (_running) return;

        _running = true;
        _worker = new Thread(CaptureLoop)
        {
            Name = $"ArUcoCapture{DeviceId}",
            IsBackground = true,
        };
        _worker.Start();
    }

    /// <summary>딕셔너리를 바꿨을 때 검출기를 다시 만든다. 실제 재생성은 워커가 한다.</summary>
    public void BuildDetector()
    {
        _rebuildDetector = true;
    }

    // ── 워커 스레드 ───────────────────────────────────────────────────────────
    void CaptureLoop()
    {
        try
        {
            if (!OpenCamera()) return;

            BuildDetectorInternal();
            _initialized = true;

            var frameClock = new Stopwatch();
            var sectionClock = new Stopwatch();
            frameClock.Start();

            while (_running)
            {
                if (_rebuildDetector)
                {
                    _rebuildDetector = false;
                    BuildDetectorInternal();
                }

                sectionClock.Restart();
                if (!_capture.grab())
                {
                    // 카메라가 잠깐 끊겼다. 바쁜 대기로 CPU 를 태우지 않는다.
                    Thread.Sleep(5);
                    continue;
                }
                float waitMs = (float)sectionClock.Elapsed.TotalMilliseconds;

                if (!_capture.retrieve(_bgrMat) || _bgrMat.empty()) continue;

                bool preview = _producePreview;

                // 검출기는 그레이스케일만 쓴다. 컬러를 넘기면 OpenCV 가 안에서 같은 변환을 한다.
                Imgproc.cvtColor(_bgrMat, _grayMat, Imgproc.COLOR_BGR2GRAY);
                if (preview) Imgproc.cvtColor(_bgrMat, _rgbaMat, Imgproc.COLOR_BGR2RGBA);

                ApplyFlip(preview);

                sectionClock.Restart();
                DisposeList(_corners); _corners.Clear();
                DisposeList(_rejected); _rejected.Clear();
                _detector.detectMarkers(_grayMat, _corners, _ids, _rejected);
                float detectMs = (float)sectionClock.Elapsed.TotalMilliseconds;

                int markerCount = (int)_ids.total();
                _rejectedCount = _rejected.Count;

                if (preview) DrawDebugOverlays(markerCount);

                Publish(markerCount, preview);

                _captureWaitMs = Mathf.Lerp(_captureWaitMs, waitMs, 0.1f);
                _detectMs = Mathf.Lerp(_detectMs, detectMs, 0.1f);

                float elapsed = (float)frameClock.Elapsed.TotalSeconds;
                frameClock.Restart();
                if (elapsed > 0f) _captureFps = Mathf.Lerp(_captureFps, 1f / elapsed, 0.1f);

                // 인쇄한 마커의 딕셔너리를 모를 때 쓰는 진단 기능. 한 번에 100ms 넘게 걸리지만
                // 워커에서 도는 덕분에 화면은 멈추지 않는다(그래도 평소에는 꺼 둔다).
                if (_autoScanInterval > 0 && markerCount == 0 && _rejected.Count > 0)
                {
                    _scanCounter++;
                    if (_scanCounter >= _autoScanInterval)
                    {
                        _scanCounter = 0;
                        ScanAllDictionaries();
                    }
                }
            }
        }
        catch (Exception e)
        {
            _lastError = $"캡처 스레드가 멈췄습니다: {e.Message}";
            Log(LogType.Error, $"[ArUco] deviceId={DeviceId} 캡처 스레드 예외\n{e}");
        }
        finally
        {
            _initialized = false;
            ReleaseNative();
        }
    }

    bool OpenCamera()
    {
        int backend = BackendToVideoio(Backend);

        _capture = new VideoCapture();

        // Play 진입 직후엔 카메라 OS 핸들이 아직 release 안 됐을 수 있어 retry 한다.
        bool opened = false;
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts && _running; attempt++)
        {
            opened = _capture.open(DeviceId, backend);
            if (opened && _capture.isOpened()) break;
            opened = false;
            _capture.release();
            Log(LogType.Warning, $"[ArUco] deviceId={DeviceId} backend={backend} open 시도 {attempt}/{maxAttempts} 실패 — 0.5초 후 재시도");
            Thread.Sleep(500);
        }

        if (!_running) return false;

        if (!opened || !_capture.isOpened())
        {
            Log(LogType.Warning, $"[ArUco] deviceId={DeviceId} 지정한 backend 실패 — CAP_ANY 폴백");
            _capture.release();
            opened = _capture.open(DeviceId, Videoio.CAP_ANY);
        }

        if (!opened || !_capture.isOpened())
        {
            _lastError = $"카메라를 열지 못했습니다 (deviceId={DeviceId})";
            Log(LogType.Error, $"[ArUco] {_lastError}");
            return false;
        }

        // 포맷을 해상도보다 먼저 지정한다. MJPG 로 열어야 720p 30fps 가 USB 대역폭 안에 들어간다.
        // 지정하지 않으면 드라이버가 무압축 YUY2 를 골라 10fps 로 떨어지는 카메라가 흔하다.
        if (!string.IsNullOrEmpty(RequestedFourcc) && RequestedFourcc.Length == 4)
        {
            _capture.set(Videoio.CAP_PROP_FOURCC, VideoWriter.fourcc(
                RequestedFourcc[0], RequestedFourcc[1], RequestedFourcc[2], RequestedFourcc[3]));
        }

        if (RequestedWidth > 0) _capture.set(Videoio.CAP_PROP_FRAME_WIDTH, RequestedWidth);
        if (RequestedHeight > 0) _capture.set(Videoio.CAP_PROP_FRAME_HEIGHT, RequestedHeight);
        if (RequestedFps > 0) _capture.set(Videoio.CAP_PROP_FPS, RequestedFps);

        // 드라이버 버퍼를 최소로. 큐가 쌓이면 화면이 실제보다 늦은 장면을 따라간다.
        _capture.set(Videoio.CAP_PROP_BUFFERSIZE, 1);

        // 첫 프레임 도착까지 대기 (최대 약 2초)
        _bgrMat = new Mat();
        for (int tries = 0; tries < 120 && _running; tries++)
        {
            if (_capture.grab() && _capture.retrieve(_bgrMat) && _bgrMat.width() > 0) break;
            Thread.Sleep(16);
        }

        if (!_running) return false;

        if (_bgrMat == null || _bgrMat.width() == 0 || _bgrMat.height() == 0)
        {
            _lastError = "카메라 첫 프레임 수신 실패";
            Log(LogType.Error, $"[ArUco] {_lastError}");
            return false;
        }

        int w = _bgrMat.width();
        int h = _bgrMat.height();

        _grayMat = new Mat(h, w, CvType.CV_8UC1);
        _rgbaMat = new Mat(h, w, CvType.CV_8UC4);
        _sharedPreview = new Mat(h, w, CvType.CV_8UC4);

        _ids = new Mat();
        _corners = new List<Mat>();
        _rejected = new List<Mat>();

        _frameWidth = w;
        _frameHeight = h;
        _captureFormat = $"{w}x{h} {FourccToString(_capture.get(Videoio.CAP_PROP_FOURCC))} " +
                         $"{_capture.get(Videoio.CAP_PROP_FPS):0}fps";
        _lastError = null;

        Log(LogType.Log, $"[ArUco] Ready (deviceId={DeviceId}, backend={backend}, " +
                         $"dict={(ArUcoDictionary)_dictionaryId}, {_captureFormat})");

        // 요청한 것과 다르게 열렸으면 짚어 준다. 전시 현장에서 원인을 찾기 가장 어려운 축이다.
        if (RequestedWidth > 0 && (w != RequestedWidth || h != RequestedHeight))
        {
            Log(LogType.Warning, $"[ArUco] deviceId={DeviceId} 요청 {RequestedWidth}x{RequestedHeight} → " +
                                 $"실제 {w}x{h}. 카메라가 지원하는 해상도로 aruco.json 을 맞추세요.");
        }

        double actualFps = _capture.get(Videoio.CAP_PROP_FPS);
        if (actualFps > 0 && actualFps < 20)
        {
            Log(LogType.Warning, $"[ArUco] deviceId={DeviceId} 가 {actualFps:0}fps 로 열렸습니다. " +
                                 $"마커 반응이 느려집니다 — sets[].camera.fourcc 를 MJPG 로 두거나 " +
                                 $"해상도를 낮추세요.");
        }

        return true;
    }

    void BuildDetectorInternal()
    {
        _detector?.Dispose();
        _arucoDict?.Dispose();

        _arucoDict = Objdetect.getPredefinedDictionary(_dictionaryId);

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

    void ApplyFlip(bool preview)
    {
        if (!_flipHorizontal && !_flipVertical) return;

        // 검출용(_grayMat)은 항상, 프리뷰용(_rgbaMat)은 만들었을 때만 뒤집는다.
        int code = _flipHorizontal && _flipVertical ? -1 : (_flipHorizontal ? 1 : 0);

        Core.flip(_grayMat, _grayMat, code);
        if (preview) Core.flip(_rgbaMat, _rgbaMat, code);
    }

    void DrawDebugOverlays(int markerCount)
    {
        if (_drawDetected && markerCount > 0)
            Objdetect.drawDetectedMarkers(_rgbaMat, _corners, _ids, new Scalar(0, 255, 0, 255));

        if (_showRejected && _rejected.Count > 0)
        {
            using (var empty = new Mat())
                Objdetect.drawDetectedMarkers(_rgbaMat, _rejected, empty, new Scalar(255, 0, 0, 255));
        }
    }

    /// <summary>검출 결과를 메인 스레드가 읽을 버퍼로 옮긴다. Mat 은 넘기지 않는다.</summary>
    void Publish(int markerCount, bool preview)
    {
        int count = Mathf.Min(markerCount, _corners.Count);

        // 잠금 밖에서 미리 파싱해 둔다. 메인 스레드를 세워 두는 시간을 최대한 짧게 가져간다.
        if (_parseBuffer.Length < count) _parseBuffer = new RawMarker[count];
        if (_idBuffer.Length < markerCount) _idBuffer = new int[markerCount];
        if (markerCount > 0) _ids.get(0, 0, _idBuffer);

        for (int i = 0; i < count; i++)
        {
            // 1x4 CV_32FC2 → 마커 기준 좌상 → 우상 → 우하 → 좌하 순서
            using (Mat reshaped = _corners[i].reshape(2, 4))
            {
                _parseBuffer[i] = new RawMarker
                {
                    id = _idBuffer[i],
                    c0 = ReadPoint(reshaped, 0),
                    c1 = ReadPoint(reshaped, 1),
                    c2 = ReadPoint(reshaped, 2),
                    c3 = ReadPoint(reshaped, 3),
                };
            }
        }

        lock (_resultLock)
        {
            if (_sharedMarkers.Length < count) _sharedMarkers = new RawMarker[count];
            Array.Copy(_parseBuffer, _sharedMarkers, count);
            _sharedCount = count;
            _sharedSequence++;

            if (preview)
            {
                _rgbaMat.copyTo(_sharedPreview);
                _sharedPreviewDirty = true;
            }
        }
    }

    RawMarker[] _parseBuffer = new RawMarker[32];
    int[] _idBuffer = new int[32];
    readonly float[] _pointBuffer = new float[2];

    Vector2 ReadPoint(Mat reshaped, int row)
    {
        reshaped.get(row, 0, _pointBuffer);
        return new Vector2(_pointBuffer[0], _pointBuffer[1]);
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

        for (int k = 0; k < allDicts.Length && _running; k++)
        {
            using (var dict = Objdetect.getPredefinedDictionary(allDicts[k]))
            using (var dp = new DetectorParameters())
            {
                dp.set_errorCorrectionRate(0.8);
                dp.set_useAruco3Detection(true);

                using (var det = new ArucoDetector(dict, dp))
                using (var ids = new Mat())
                {
                    var corners = new List<Mat>();
                    var rejected = new List<Mat>();
                    det.detectMarkers(_grayMat, corners, ids, rejected);

                    if (corners.Count > 0)
                    {
                        int n = (int)ids.total();
                        int[] buf = new int[n];
                        if (n > 0) ids.get(0, 0, buf);
                        Log(LogType.Warning, $"[ArUco] AUTO-SCAN MATCH: {names[k]} ids=[{string.Join(",", buf)}] — " +
                                             $"aruco.json 의 dictionaryId 를 {allDicts[k]} ({names[k]}) 로 바꿔주세요.");
                    }

                    DisposeList(corners);
                    DisposeList(rejected);
                }
            }
        }
    }

    void ReleaseNative()
    {
        _capture?.release();
        _capture?.Dispose(); _capture = null;
        _bgrMat?.Dispose(); _bgrMat = null;
        _grayMat?.Dispose(); _grayMat = null;
        _rgbaMat?.Dispose(); _rgbaMat = null;
        _ids?.Dispose(); _ids = null;
        DisposeList(_corners); _corners = null;
        DisposeList(_rejected); _rejected = null;
        _arucoDict?.Dispose(); _arucoDict = null;
        _detector?.Dispose(); _detector = null;

        lock (_resultLock)
        {
            _sharedPreview?.Dispose(); _sharedPreview = null;
            _sharedPreviewDirty = false;
        }
    }

    void Log(LogType type, string message) => _logs.Enqueue((type, message));

    static string FourccToString(double value)
    {
        int v = (int)value;
        if (v == 0) return "?";

        var chars = new char[4];
        for (int i = 0; i < 4; i++) chars[i] = (char)((v >> (8 * i)) & 0xFF);
        return new string(chars);
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

    // ── 메인 스레드 ───────────────────────────────────────────────────────────
    void Update()
    {
        FlushLogs();

        if (!_initialized) return;

        if (_producePreview) UploadPreview();

        // 새 검출이 없어도 스무딩은 계속 나아간다. 카메라 30fps, 화면 60fps 이상이라
        // 프레임마다 목표점으로 조금씩 다가가야 움직임이 끊기지 않는다.
        bool fresh = PullSnapshot();
        if (fresh) ApplyDetections();

        AdvanceSmoothing();
        UpdateTrackedList();

        if (fresh) NotifyIdChanges();
    }

    void FlushLogs()
    {
        while (_logs.TryDequeue(out var entry))
        {
            switch (entry.type)
            {
                case LogType.Error: Debug.LogError(entry.message); break;
                case LogType.Warning: Debug.LogWarning(entry.message); break;
                default: Debug.Log(entry.message); break;
            }
        }
    }

    void UploadPreview()
    {
        lock (_resultLock)
        {
            if (!_sharedPreviewDirty || _sharedPreview == null) return;
            _sharedPreviewDirty = false;

            if (FrameTexture == null || FrameTexture.width != _sharedPreview.width()
                                     || FrameTexture.height != _sharedPreview.height())
            {
                if (FrameTexture != null) Destroy(FrameTexture);

                FrameTexture = new Texture2D(_sharedPreview.width(), _sharedPreview.height(),
                                             TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            OpenCVMatUtils.MatToTexture2D(_sharedPreview, FrameTexture);
        }
    }

    bool PullSnapshot()
    {
        lock (_resultLock)
        {
            if (_sharedSequence == _lastSequence) return false;

            _lastSequence = _sharedSequence;
            _frameCount = _sharedCount;

            if (_frameMarkers.Length < _frameCount) _frameMarkers = new RawMarker[_frameCount];
            Array.Copy(_sharedMarkers, _frameMarkers, _frameCount);
        }
        return true;
    }

    void ApplyDetections()
    {
        float now = Time.time;

        foreach (var pair in _states) pair.Value.inLatestFrame = false;

        for (int i = 0; i < _frameCount; i++)
        {
            var raw = _frameMarkers[i];

            if (!_states.TryGetValue(raw.id, out var state))
            {
                state = new MarkerState();
                _states[raw.id] = state;
            }

            state.t0 = raw.c0; state.t1 = raw.c1; state.t2 = raw.c2; state.t3 = raw.c3;

            if (!state.hasValue)
            {
                // 처음 잡힌 마커는 보간 없이 바로 자리를 잡아야 이미지가 화면 구석에서 날아오지 않는다.
                state.c0 = raw.c0; state.c1 = raw.c1; state.c2 = raw.c2; state.c3 = raw.c3;
                state.hasValue = true;
            }

            state.lastSeenTime = now;
            state.inLatestFrame = true;
        }
    }

    /// <summary>
    /// 꼭짓점을 각각 목표로 끌어당긴다. 원근 매핑은 마커보다 훨씬 큰 이미지까지 이 네 점으로
    /// 외삽하므로, 몇 픽셀의 코너 떨림도 이미지 가장자리에서는 크게 흔들린다.
    /// </summary>
    void AdvanceSmoothing()
    {
        float step = SmoothingStep();

        foreach (var pair in _states)
        {
            var state = pair.Value;
            if (!state.hasValue) continue;

            if (step >= 1f)
            {
                state.c0 = state.t0; state.c1 = state.t1;
                state.c2 = state.t2; state.c3 = state.t3;
            }
            else
            {
                state.c0 = Vector2.Lerp(state.c0, state.t0, step);
                state.c1 = Vector2.Lerp(state.c1, state.t1, step);
                state.c2 = Vector2.Lerp(state.c2, state.t2, step);
                state.c3 = Vector2.Lerp(state.c3, state.t3, step);
            }

            state.Recompute();
        }
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

            if (!state.inLatestFrame && now - state.lastSeenTime > hold)
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
                visible = state.inLatestFrame,
            });
        }

        for (int i = 0; i < _expired.Count; i++) _states.Remove(_expired[i]);

        // maxSimultaneous 로 개수를 자를 때 "가장 크게 잡힌 것" 이 남도록 정렬해 둔다.
        _tracked.Sort(BySizeDescending);
    }

    void NotifyIdChanges()
    {
        if (_frameCount > 0)
        {
            var ids = new int[_frameCount];
            for (int i = 0; i < _frameCount; i++) ids[i] = _frameMarkers[i].id;
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

    static void DisposeList(List<Mat> list)
    {
        if (list == null) return;
        foreach (var m in list) m.Dispose();
    }

    void OnDestroy()
    {
        Shutdown();
        FlushLogs();

        if (FrameTexture != null) { Destroy(FrameTexture); FrameTexture = null; }
    }

    void OnApplicationQuit() => Shutdown();

    /// <summary>
    /// 워커를 세우고 카메라를 놓아준다.
    /// grab() 이 블로킹이라 끝나기를 기다려야 한다 — 기다리지 않고 release 하면 네이티브에서 죽는다.
    /// </summary>
    void Shutdown()
    {
        if (!_running) return;

        _running = false;
        _initialized = false;

        if (_worker != null && _worker.IsAlive)
        {
            if (!_worker.Join(3000))
                Debug.LogWarning($"[ArUco] deviceId={DeviceId} 캡처 스레드가 제때 끝나지 않았습니다.");
        }
        _worker = null;
    }
}
