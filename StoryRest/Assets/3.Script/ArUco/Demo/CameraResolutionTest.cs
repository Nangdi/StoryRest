using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RenderHeads.Media.AVProVideo;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.ArUco.Demo
{
    /// <summary>
    /// 카메라 · 영상 점검(CameraTest 씬). 카메라가 실제로 어떤 해상도 · 포맷 · fps 로 열리는지 보고,
    /// 카메라 영상이나 테스트 영상 파일을 화면에 띄워 크기를 바꿔 가며 들여다본다. 마커 인식과는 상관없이 뜬다.
    ///
    /// 전시와 같은 부품(<see cref="ArUcoMarkerTracker"/>)으로 카메라를 연다. 여기서 열리는 값이 곧 전시에서 열리는 값이다.
    /// 시작값은 aruco.json 의 첫 세트 camera 를 읽어 온다(읽기만 하고 저장하지 않는다).
    /// 테스트 영상은 StreamingAssets/test_video/ 에 넣는다(→ 그 폴더 README). 전시와 같이 AVPro 로 재생한다.
    ///
    /// 소스
    ///   M         카메라 ↔ 영상 전환(영상이 있으면 영상으로 시작)
    ///   N · P     다음 · 이전 영상. 한 번 연 영상은 닫지 않고 멈춰 두므로 같은 장면 · 같은 크기 · 같은 자리로 바로 넘어간다(A/B 전환).
    ///   C         나란히 비교. 폴더의 영상(최대 <see cref="MaxCompare"/>개)을 칸으로 나눠 동시에 띄운다.
    ///             칸마다 같은 화면 크기 · 같은 영역을 보여 주므로 휠 · 드래그가 모든 칸에 똑같이 걸린다.
    ///             N · P 는 칸 순서를 한 칸씩 민다.
    ///   K         일시정지 · 재생(보이는 영상 모두, 같은 시각에 맞춰 멈춤)
    ///   , .       한 프레임 앞 · 뒤(멈춘 상태)      S  보이는 영상들의 재생 시각을 첫 영상에 맞춤
    ///   F5        폴더 다시 읽기
    ///   F8        글씨 점검 화면(<see cref="ScreenTestPattern"/>)을 영상 아래에 깐다. Unity 가 그린 글씨와 영상 속 글씨를
    ///             한 화면에서 나란히 비교한다. 켜져 있는 동안 Tab · + - · ↑↓ 는 그쪽(모드 · 글자 크기)이 받고,
    ///             영상 크기는 휠, 자리는 드래그로 맞춘다.
    /// 카메라 (바꾸면 다시 연다)
    ///   1 ~ 6     요청 해상도 프리셋     0  카메라 기본값(요청 안 함)
    ///   F         포맷 순환(MJPG · YUY2 · NV12 · 기본)
    ///   D         디바이스 번호 다음     B  백엔드 순환(DSHOW · ANY · MSMF)
    ///   R         같은 값으로 다시 열기
    /// 화면
    ///   휠 · + -  표시 크기              Space  화면에 맞춤     Z  1:1(카메라 픽셀 = 화면 픽셀)
    ///   드래그    영상 옮기기            H · V  좌우 · 상하 뒤집기
    ///   G         격자 켜고 끄기         Tab    안내 숨기기
    /// </summary>
    public class CameraResolutionTest : MonoBehaviour
    {
        static readonly Vector2Int[] Presets =
        {
            new Vector2Int(640, 480),
            new Vector2Int(1280, 720),
            new Vector2Int(1920, 1080),
            new Vector2Int(2560, 1440),
            new Vector2Int(3840, 2160),
            new Vector2Int(1024, 768),
        };
        static readonly string[] Fourccs = { "MJPG", "YUY2", "NV12", "" };
        static readonly string[] BackendNames = { "DSHOW", "ANY", "MSMF" };
        static readonly string[] VideoExtensions = { ".mp4", ".mov", ".m4v", ".wmv", ".avi", ".mkv", ".webm" };
        const string VideoFolder = "test_video";
        const int MaxCompare = 6;
        const float CellGap = 6f;

        [Header("화면")]
        [SerializeField] RawImage _video;
        [SerializeField] RawImage _grid;
        [SerializeField] TMP_Text _info;

        [Header("카메라 (시작할 때 aruco.json 첫 세트 값으로 덮어씀)")]
        [SerializeField] bool _loadFromConfig = true;
        [SerializeField] int _deviceId;
        [SerializeField] int _backend;
        [SerializeField] int _width = 1280;
        [SerializeField] int _height = 720;
        [SerializeField] int _fps = 30;
        [SerializeField] string _fourcc = "MJPG";
        [SerializeField] int _dictionaryId = 2;

        [Header("표시")]
        [SerializeField] float _minScale = 0.1f;
        [SerializeField] float _maxScale = 8f;

        // 나란히 비교의 한 칸. 칸 안에 영상이 보이는 부분만 RawImage 로 잘라 그린다(마스크 없이 uvRect 로 자름).
        class CompareCell
        {
            public RectTransform root;
            public RawImage image;
            public TMP_Text label;
        }

        ArUcoMarkerTracker _tracker;
        readonly List<string> _videos = new List<string>();
        readonly List<MediaPlayer> _players = new List<MediaPlayer>(); // _videos 와 같은 순서. 처음 보여 줄 때 연다.
        readonly HashSet<int> _pendingSync = new HashSet<int>();      // 열리면 재생 시각을 맞출 영상
        int _videoIndex = -1;
        int _syncHint = -1; // 맞출 기준이 화면에 없을 때 쓰는 영상(방금 전까지 보던 것)
        bool _showVideo;
        bool _compare;
        bool _paused;
        bool _fit = true;
        float _scale = 1f;
        Vector2 _pan;
        bool _dragging;
        Vector2 _dragStart, _panStart;
        bool _flipH, _flipV;
        bool _showInfo = true;
        bool _patternWasOn;
        bool _reopening;
        float _openedAt;
        RectTransform _cellRoot;
        readonly List<CompareCell> _cells = new List<CompareCell>();
        Vector2 _cellSize;
        int _cellCols = 1;
        readonly StringBuilder _sb = new StringBuilder(1024);

        int CompareCount => Mathf.Min(MaxCompare, _videos.Count);
        int VisibleCount => !_showVideo || _videoIndex < 0 ? 0 : _compare ? CompareCount : 1;
        int VisibleIndex(int k) => (_videoIndex + k) % _videos.Count;

        void Start()
        {
            if (_loadFromConfig) LoadFromConfig();
            if (_grid != null)
            {
                _grid.texture = BuildGridTexture();
                _grid.gameObject.SetActive(false);
            }
            // 영상이 있으면 영상만 띄운다. 카메라는 M 으로 넘어갈 때 처음 연다.
            ScanVideos();
            _showVideo = _videos.Count > 0;
            if (_showVideo) ShowVideo(0);
            else StartCoroutine(Reopen());
        }

        void ScanVideos()
        {
            CloseAllPlayers();
            _videos.Clear();
            string dir = Path.Combine(Application.streamingAssetsPath, VideoFolder);
            if (!Directory.Exists(dir))
            {
                Debug.LogWarning($"[CameraTest] 테스트 영상 폴더가 없습니다: {dir}");
                return;
            }

            foreach (var file in Directory.GetFiles(dir))
            {
                if (System.Array.IndexOf(VideoExtensions, Path.GetExtension(file).ToLowerInvariant()) >= 0)
                    _videos.Add(file);
            }
            _videos.Sort(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _videos.Count; i++) _players.Add(null);
            Debug.Log($"[CameraTest] 테스트 영상 {_videos.Count}개  {dir}");
        }

        void CloseAllPlayers()
        {
            foreach (var p in _players)
            {
                if (p == null) continue;
                p.CloseMedia();
                Destroy(p.gameObject);
            }
            _players.Clear();
            _pendingSync.Clear();
            _syncHint = -1;
        }

        // index 의 플레이어. 없으면 만들어 연다.
        MediaPlayer PlayerAt(int index)
        {
            if (_players[index] != null) return _players[index];

            // 비활성 상태에서 붙여야 Awake 전에 설정이 들어간다(→ ArUcoContentLibrary.CreateSlot).
            var go = new GameObject($"MediaPlayer {index} {Path.GetFileName(_videos[index])}");
            go.transform.SetParent(transform, false);
            go.SetActive(false);
            var player = go.AddComponent<MediaPlayer>();
            player.AutoOpen = false;
            player.AutoStart = false;
            player.Loop = true;
            player.AudioMuted = true;
            player.AudioVolume = 0f;
            // 전시(ArUcoContentLibrary.CreateSlot)와 같은 필터. 이게 다르면 줄였을 때 깨지는 정도가 현장과 달라진다.
            player.PlatformOptionsWindows.useTextureMips = true;
            player.TextureFilterMode = FilterMode.Trilinear;
            player.TextureAnisoLevel = 8;
            player.PlatformOptionsWindows.useHapNotchLC = true;
            player.PlatformOptionsWindows.hintAlphaChannel = true;
            go.SetActive(true);

            string path = _videos[index];
            if (!player.OpenMedia(MediaPathType.AbsolutePathOrURL, path, autoPlay: false))
                Debug.LogWarning($"[CameraTest] 영상을 열지 못했습니다: {path}");
            _players[index] = player;
            return player;
        }

        static bool IsReady(MediaPlayer p) => p != null && p.Control != null && p.Control.CanPlay();

        // index 를 첫 칸(단일 보기면 유일한 칸)으로 보여 준다. 안 보이게 된 영상은 닫지 않고 멈춰 둔다.
        void ShowVideo(int index)
        {
            if (_videos.Count == 0) { _videoIndex = -1; return; }

            if (_videoIndex >= 0) _syncHint = _videoIndex;
            _videoIndex = (index % _videos.Count + _videos.Count) % _videos.Count;
            RefreshPlayback();
        }

        // 보이는 영상은 열고(재생 시각을 맞출 대상으로 올림) 안 보이는 영상은 멈춘다.
        void RefreshPlayback()
        {
            var visible = new HashSet<int>();
            for (int k = 0; k < VisibleCount; k++) visible.Add(VisibleIndex(k));

            for (int i = 0; i < _players.Count; i++)
            {
                if (visible.Contains(i)) continue;
                _pendingSync.Remove(i);
                if (IsReady(_players[i])) _players[i].Control.Pause();
            }

            foreach (int i in visible)
            {
                bool wasHidden = !IsReady(_players[i]) || !_players[i].Control.IsPlaying() || _paused;
                PlayerAt(i);
                if (wasHidden) _pendingSync.Add(i);
            }
        }

        // 막 열렸거나 다시 보이게 된 영상을 기준 영상의 재생 시각에 맞춘 뒤 재생(일시정지 중이면 멈춘 채로)한다.
        void ProcessPendingSync()
        {
            if (_pendingSync.Count == 0) return;

            MediaPlayer reference = null;
            for (int k = 0; k < VisibleCount; k++)
            {
                int i = VisibleIndex(k);
                if (!_pendingSync.Contains(i) && IsReady(_players[i])) { reference = _players[i]; break; }
            }
            if (reference == null && _syncHint >= 0 && _syncHint < _players.Count && IsReady(_players[_syncHint]))
                reference = _players[_syncHint];

            var done = new List<int>();
            foreach (int i in _pendingSync)
            {
                var p = _players[i];
                if (!IsReady(p)) continue;
                if (reference != null && reference != p) p.Control.Seek(reference.Control.GetCurrentTime());
                if (_paused) p.Control.Pause();
                else p.Control.Play();
                done.Add(i);
            }
            foreach (int i in done) _pendingSync.Remove(i);
        }

        // 보이는 영상 모두에 같은 일을 한다.
        void ForEachVisible(System.Action<MediaPlayer> action)
        {
            for (int k = 0; k < VisibleCount; k++)
            {
                var p = _players[VisibleIndex(k)];
                if (IsReady(p)) action(p);
            }
        }

        void SetPaused(bool paused)
        {
            _paused = paused;
            if (paused) ForEachVisible(p => p.Control.Pause());
            // 멈출 때도 다시 틀 때도 첫 칸 기준으로 시각을 맞춘다. 멈춘 화면끼리 같은 프레임이어야 비교가 된다.
            ResyncVisible();
        }

        void ResyncVisible()
        {
            _syncHint = -1;
            for (int k = 1; k < VisibleCount; k++) _pendingSync.Add(VisibleIndex(k));
            // 첫 칸이 아직 열리는 중이면 그대로 맞출 대상에 남겨 둔다(열리면 재생 상태가 들어간다).
            var first = VisibleCount > 0 ? _players[VisibleIndex(0)] : null;
            if (IsReady(first))
            {
                _pendingSync.Remove(VisibleIndex(0));
                if (_paused) first.Control.Pause();
                else first.Control.Play();
            }
        }

        void LoadFromConfig()
        {
            var config = ArUcoConfigIO.Load();
            if (config == null || config.sets == null || config.sets.Count == 0 || config.sets[0].camera == null) return;

            var cam = config.sets[0].camera;
            _deviceId = cam.deviceId;
            _backend = cam.backend;
            _width = cam.width;
            _height = cam.height;
            _fps = cam.fps;
            _fourcc = cam.fourcc ?? "";
            _flipH = cam.flipHorizontal;
            _flipV = cam.flipVertical;
            _dictionaryId = config.dictionaryId;
        }

        // 트래커는 열 때 한 번만 설정을 읽는다. 값을 바꾸면 통째로 버리고 새로 만든다.
        // 이전 트래커가 카메라를 놓은 뒤(OnDestroy 에서 워커가 끝날 때까지 기다림) 다음 프레임에 연다.
        IEnumerator Reopen()
        {
            if (_reopening) yield break;
            _reopening = true;

            if (_tracker != null)
            {
                Destroy(_tracker.gameObject);
                _tracker = null;
                yield return null;
            }

            var go = new GameObject($"Tracker (device {_deviceId})");
            go.transform.SetParent(transform, false);
            _tracker = go.AddComponent<ArUcoMarkerTracker>();
            _tracker.DeviceId = _deviceId;
            _tracker.Backend = _backend;
            _tracker.RequestedWidth = _width;
            _tracker.RequestedHeight = _height;
            _tracker.RequestedFps = _fps;
            _tracker.RequestedFourcc = _fourcc;
            _tracker.FlipHorizontal = _flipH;
            _tracker.FlipVertical = _flipV;
            _tracker.DictionaryId = _dictionaryId;
            _tracker.ProducePreviewTexture = true;
            _tracker.DrawDetectedMarkers = true;
            _tracker.Open();

            _openedAt = Time.unscaledTime;
            _reopening = false;
        }

        void Update()
        {
            // 글씨 점검 화면(F8)은 영상 아래에 깔린다. 막 켰을 때 영상이 화면을 다 덮고 있으면 반으로 줄여 글씨가 보이게 한다.
            bool pattern = ScreenTestPattern.IsOn;
            if (pattern && !_patternWasOn && _fit && !_compare)
            {
                _scale = FitScale() * 0.5f;
                _fit = false;
                _pan = Vector2.zero;
            }
            _patternWasOn = pattern;

            HandleSourceKeys();
            HandleCameraKeys();
            HandleViewInput();
            ProcessPendingSync();
            Layout();
            UpdateInfo();
        }

        void HandleSourceKeys()
        {
            if (Input.GetKeyDown(KeyCode.M))
            {
                _showVideo = !_showVideo;
                if (_showVideo) ShowVideo(Mathf.Max(0, _videoIndex));
                else RefreshPlayback();
                if (!_showVideo && _tracker == null) StartCoroutine(Reopen());
            }
            if (Input.GetKeyDown(KeyCode.N)) { _showVideo = true; ShowVideo(_videoIndex + 1); }
            if (Input.GetKeyDown(KeyCode.P)) { _showVideo = true; ShowVideo(_videoIndex - 1); }
            if (Input.GetKeyDown(KeyCode.C) && _videos.Count > 0)
            {
                _compare = !_compare;
                _showVideo = true;
                // 칸 크기가 바뀌므로 화면 맞춤으로 돌아간다.
                _fit = true;
                _pan = Vector2.zero;
                ShowVideo(Mathf.Max(0, _videoIndex));
            }
            if (Input.GetKeyDown(KeyCode.F5))
            {
                ScanVideos();
                if (_showVideo) ShowVideo(Mathf.Clamp(_videoIndex, 0, Mathf.Max(0, _videos.Count - 1)));
            }

            if (!_showVideo) return;
            if (Input.GetKeyDown(KeyCode.K)) SetPaused(!_paused);
            if (Input.GetKeyDown(KeyCode.S)) ResyncVisible();
            if (_paused && Input.GetKeyDown(KeyCode.Comma)) ForEachVisible(p => p.Control.SeekToFrameRelative(-1));
            if (_paused && Input.GetKeyDown(KeyCode.Period)) ForEachVisible(p => p.Control.SeekToFrameRelative(1));
        }

        void HandleCameraKeys()
        {
            // 카메라 전용 키. 영상을 보는 중이거나 글씨 점검 화면(F 가 겹침)이 떠 있으면 받지 않는다.
            if (_showVideo || ScreenTestPattern.IsOn) return;

            bool reopen = false;

            for (int i = 0; i < Presets.Length; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i))
                {
                    _width = Presets[i].x;
                    _height = Presets[i].y;
                    reopen = true;
                }
            }
            if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0))
            {
                _width = 0;
                _height = 0;
                reopen = true;
            }
            if (Input.GetKeyDown(KeyCode.F))
            {
                int i = System.Array.IndexOf(Fourccs, _fourcc ?? "");
                _fourcc = Fourccs[(i + 1) % Fourccs.Length];
                reopen = true;
            }
            if (Input.GetKeyDown(KeyCode.D))
            {
                int count = Mathf.Max(1, WebCamTexture.devices.Length);
                _deviceId = (_deviceId + 1) % count;
                reopen = true;
            }
            if (Input.GetKeyDown(KeyCode.B))
            {
                _backend = (_backend + 1) % BackendNames.Length;
                reopen = true;
            }
            if (Input.GetKeyDown(KeyCode.R)) reopen = true;

            if (reopen) StartCoroutine(Reopen());

            // 뒤집기는 워커가 매 프레임 읽으므로 다시 열 필요가 없다.
            if (Input.GetKeyDown(KeyCode.H)) { _flipH = !_flipH; if (_tracker != null) _tracker.FlipHorizontal = _flipH; }
            if (Input.GetKeyDown(KeyCode.V)) { _flipV = !_flipV; if (_tracker != null) _tracker.FlipVertical = _flipV; }
        }

        bool IsComparing => _showVideo && _compare && _videoIndex >= 0;

        void HandleViewInput()
        {
            // 휠 값의 크기는 마우스 · 드라이버마다 0.1 ~ 120 으로 제각각이라 방향만 쓴다. 한 칸 = 한 단계.
            float wheel = Input.mouseScrollDelta.y;
            int zoom = wheel > 0.001f ? 1 : wheel < -0.001f ? -1 : 0;
            bool fromWheel = zoom != 0;

            // 글씨 점검 화면이 떠 있으면 + - · Tab 은 그쪽(글자 크기 · 모드)이 쓴다. 영상 크기는 휠로만.
            bool pattern = ScreenTestPattern.IsOn;
            if (!pattern && (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))) zoom++;
            if (!pattern && (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))) zoom--;

            if (zoom != 0)
            {
                if (_fit) { _scale = FitScale(); _fit = false; }
                float step = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 1.02f : 1.15f;
                float before = _scale;
                _scale = Mathf.Clamp(_scale * Mathf.Pow(step, zoom), _minScale, _maxScale);

                // 휠은 커서 자리를 기준으로 키운다(커서 밑의 점이 그 자리에 남는다). 키보드는 영상 중심 기준.
                // 비교 중에는 커서가 올라간 칸의 중심이 기준이다(모든 칸이 같은 _pan 을 쓴다).
                if (fromWheel && before > 0f)
                {
                    Vector2 cursor = (Vector2)Input.mousePosition - new Vector2(Screen.width, Screen.height) * 0.5f;
                    if (IsComparing) cursor -= CellCenterNear(cursor);
                    _pan = cursor - (cursor - _pan) * (_scale / before);
                }
            }

            if (Input.GetKeyDown(KeyCode.Space)) { _fit = true; _pan = Vector2.zero; }
            if (Input.GetKeyDown(KeyCode.Z)) { _fit = false; _scale = 1f; }
            if (Input.GetKeyDown(KeyCode.G) && _grid != null) _grid.gameObject.SetActive(!_grid.gameObject.activeSelf);
            if (!pattern && Input.GetKeyDown(KeyCode.Tab)) _showInfo = !_showInfo;

            if (Input.GetMouseButtonDown(0))
            {
                _dragging = true;
                _dragStart = Input.mousePosition;
                _panStart = _pan;
            }
            if (Input.GetMouseButtonUp(0)) _dragging = false;
            if (_dragging) _pan = _panStart + ((Vector2)Input.mousePosition - _dragStart);
        }

        // 영상 index 의 텍스처. 디코딩 준비 전이면 null.
        Texture VideoTexture(int index, out bool flipV)
        {
            flipV = false;
            if (index < 0 || index >= _players.Count) return null;
            var producer = _players[index] != null ? _players[index].TextureProducer : null;
            var tex = producer != null ? producer.GetTexture() : null;
            if (tex == null || tex.width <= 0) return null;
            flipV = producer.RequiresVerticalFlip();
            return tex;
        }

        // 지금 보여 줄 텍스처(비교 중이면 첫 칸 = 크기 기준). 영상은 디코딩 준비 전이면 null.
        Texture CurrentTexture(out bool flipV)
        {
            flipV = false;
            if (_showVideo) return VideoTexture(_videoIndex, out flipV);
            return _tracker != null && _tracker.IsInitialized ? _tracker.FrameTexture : null;
        }

        float FitScale()
        {
            var tex = CurrentTexture(out _);
            if (tex == null) return 1f;
            if (IsComparing) return Mathf.Min(_cellSize.x / tex.width, _cellSize.y / tex.height);
            return Mathf.Min((float)Screen.width / tex.width, (float)Screen.height / tex.height);
        }

        // 캔버스는 ConstantPixelSize · 배율 1 이라 RectTransform 크기가 곧 화면 픽셀이다.
        void Layout()
        {
            if (_video == null) return;

            if (IsComparing)
            {
                _video.enabled = false;
                if (_grid != null) _grid.enabled = false;
                LayoutCompare();
                return;
            }
            SetCellsActive(0);

            var tex = CurrentTexture(out bool flipV);
            _video.texture = tex;
            _video.enabled = tex != null;
            if (_grid != null) _grid.enabled = tex != null;
            if (tex == null) return;

            _video.uvRect = flipV ? new Rect(0f, 1f, 1f, -1f) : new Rect(0f, 0f, 1f, 1f);

            float scale = _fit ? FitScale() : _scale;
            var rt = _video.rectTransform;
            rt.sizeDelta = new Vector2(tex.width * scale, tex.height * scale);
            rt.anchoredPosition = _pan;

            if (_grid != null && _grid.gameObject.activeSelf)
            {
                // 격자 한 칸 = 카메라 100px. 영상과 같이 커지고 줄어든다.
                var g = _grid.rectTransform;
                g.sizeDelta = rt.sizeDelta;
                g.anchoredPosition = rt.anchoredPosition;
                _grid.uvRect = new Rect(0f, 0f, tex.width / 100f, tex.height / 100f);
            }
        }

        // 칸 배치: 첫 영상 비율로 칸 안에 맞췄을 때 가장 크게 보이는 열 수를 고른다.
        void ComputeCellGrid(int count, float aspect)
        {
            float best = -1f;
            for (int cols = 1; cols <= count; cols++)
            {
                int rows = (count + cols - 1) / cols;
                float w = (Screen.width - CellGap * (cols + 1)) / cols;
                float h = (Screen.height - CellGap * (rows + 1)) / rows;
                float shown = Mathf.Min(w, h * aspect);
                if (shown > best)
                {
                    best = shown;
                    _cellCols = cols;
                    _cellSize = new Vector2(w, h);
                }
            }
        }

        Vector2 CellCenter(int k)
        {
            int rows = (CompareCount + _cellCols - 1) / _cellCols;
            int col = k % _cellCols, row = k / _cellCols;
            float x = -Screen.width * 0.5f + CellGap + (_cellSize.x + CellGap) * col + _cellSize.x * 0.5f;
            float y = Screen.height * 0.5f - CellGap - (_cellSize.y + CellGap) * row - _cellSize.y * 0.5f;
            // 마지막 줄이 덜 찼으면 가운데로 모은다.
            int inRow = row == rows - 1 ? CompareCount - row * _cellCols : _cellCols;
            x += (_cellCols - inRow) * (_cellSize.x + CellGap) * 0.5f;
            return new Vector2(x, y);
        }

        Vector2 CellCenterNear(Vector2 point)
        {
            Vector2 best = Vector2.zero;
            float bestDist = float.MaxValue;
            for (int k = 0; k < CompareCount; k++)
            {
                var c = CellCenter(k);
                float d = (c - point).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }

        // 모든 칸에 같은 화면 크기(첫 영상 픽셀 × 배율) · 같은 _pan 으로 그린다. 해상도가 다른 영상도 같은 크기로 늘려 그려서
        // 전시(정해진 크기의 직사각형에 그림)와 같은 조건으로 화질을 비교한다.
        void LayoutCompare()
        {
            int count = CompareCount;
            var refTex = VideoTexture(_videoIndex, out _);
            ComputeCellGrid(count, refTex != null ? (float)refTex.width / refTex.height : 16f / 9f);
            SetCellsActive(count);

            float scale = _fit ? FitScale() : _scale;
            float half = 0.5f;
            for (int k = 0; k < count; k++)
            {
                int index = VisibleIndex(k);
                var cell = _cells[k];
                cell.root.anchoredPosition = CellCenter(k);
                cell.root.sizeDelta = _cellSize;

                var tex = VideoTexture(index, out bool flipV);
                cell.image.texture = tex;
                cell.label.text = CellLabel(k, index, tex, refTex, scale);
                if (tex == null) { cell.image.enabled = false; continue; }

                float dw = (refTex != null ? refTex.width : tex.width) * scale;
                float dh = dw * tex.height / tex.width;
                float left = _pan.x - dw * half, bottom = _pan.y - dh * half;
                float xMin = Mathf.Max(-_cellSize.x * half, left), xMax = Mathf.Min(_cellSize.x * half, left + dw);
                float yMin = Mathf.Max(-_cellSize.y * half, bottom), yMax = Mathf.Min(_cellSize.y * half, bottom + dh);
                if (xMax <= xMin || yMax <= yMin) { cell.image.enabled = false; continue; }

                cell.image.enabled = true;
                var rt = cell.image.rectTransform;
                rt.anchoredPosition = new Vector2((xMin + xMax) * half, (yMin + yMax) * half);
                rt.sizeDelta = new Vector2(xMax - xMin, yMax - yMin);

                float u0 = (xMin - left) / dw, u1 = (xMax - left) / dw;
                float v0 = (yMin - bottom) / dh, v1 = (yMax - bottom) / dh;
                cell.image.uvRect = flipV ? new Rect(u0, 1f - v0, u1 - u0, -(v1 - v0)) : new Rect(u0, v0, u1 - u0, v1 - v0);
            }
        }

        string CellLabel(int k, int index, Texture tex, Texture refTex, float scale)
        {
            _sb.Clear();
            _sb.Append("<mark=#000000B0>").Append(k + 1).Append(". ").Append(Path.GetFileName(_videos[index]));
            var p = _players[index];
            if (tex == null || p == null || p.Info == null)
            {
                _sb.Append("  <color=#FFD54F>여는 중...</color></mark>");
                return _sb.ToString();
            }
            // 표시 % = 화면 픽셀 / 그 영상 픽셀. 같은 화면 크기라도 해상도가 낮은 영상은 이 값이 크다(= 늘려 그림).
            float shown = (refTex != null ? refTex.width : tex.width) * scale / tex.width;
            _sb.Append("\n").Append(p.Info.GetVideoWidth()).Append('x').Append(p.Info.GetVideoHeight())
               .Append(" · ").Append(p.Info.GetVideoFrameRate().ToString("0.##")).Append("fps · 재생 ")
               .Append(p.Info.GetVideoDisplayRate().ToString("0.0")).Append("fps · 표시 ")
               .Append((shown * 100f).ToString("0")).Append("% · ")
               .Append(p.Control != null ? p.Control.GetCurrentTime().ToString("0.00") : "-").Append("초</mark>");
            return _sb.ToString();
        }

        // 칸은 실행 중에 만든다(점검 씬 전용). 필요한 수만 켠다.
        void SetCellsActive(int count)
        {
            if (count > 0 && _cellRoot == null)
            {
                var go = new GameObject("CompareCells", typeof(RectTransform));
                _cellRoot = (RectTransform)go.transform;
                _cellRoot.SetParent(_video.transform.parent, false);
                _cellRoot.SetSiblingIndex(_video.transform.GetSiblingIndex() + 1);
                _cellRoot.anchorMin = _cellRoot.anchorMax = new Vector2(0.5f, 0.5f);
                _cellRoot.sizeDelta = Vector2.zero;
                _cellRoot.anchoredPosition = Vector2.zero;
            }

            while (_cells.Count < count) _cells.Add(CreateCell(_cells.Count));
            for (int k = 0; k < _cells.Count; k++) _cells[k].root.gameObject.SetActive(k < count);
        }

        CompareCell CreateCell(int k)
        {
            var root = new GameObject($"Cell {k + 1}", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)root.transform;
            rt.SetParent(_cellRoot, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            var bg = root.GetComponent<Image>();
            bg.color = new Color(0.15f, 0.15f, 0.15f, 0.35f);
            bg.raycastTarget = false;

            var imageGo = new GameObject("Video", typeof(RectTransform), typeof(RawImage));
            imageGo.transform.SetParent(rt, false);
            var image = imageGo.GetComponent<RawImage>();
            image.raycastTarget = false;
            image.rectTransform.anchorMin = image.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);

            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            labelGo.transform.SetParent(rt, false);
            var label = labelGo.GetComponent<TextMeshProUGUI>();
            if (_info != null) label.font = _info.font;
            label.fontSize = 18f;
            label.color = Color.white;
            label.raycastTarget = false;
            label.alignment = TextAlignmentOptions.TopLeft;
            var lrt = label.rectTransform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(8f, 8f);
            lrt.offsetMax = new Vector2(-8f, -8f);

            return new CompareCell { root = rt, image = image, label = label };
        }

        void UpdateInfo()
        {
            if (_info == null) return;
            _info.gameObject.SetActive(_showInfo);
            if (!_showInfo) return;

            _sb.Clear();
            _sb.Append("<b>카메라 · 영상 점검</b>   지금: ")
               .AppendLine(!_showVideo ? "<color=#7CFF8A>카메라</color>"
                   : IsComparing ? $"<color=#FFD54F>영상 나란히 비교 ({CompareCount}개)</color>"
                   : "<color=#FFD54F>영상 파일</color>");

            if (_showVideo)
            {
                AppendVideoInfo();
                _sb.AppendLine();
                _sb.AppendLine("<size=80%><color=#BBBBBB>N/P 다음·이전 영상" + (IsComparing ? "(칸 순서)" : "(같은 시각으로 전환)")
                    + "  C 나란히 비교 " + (IsComparing ? "끄기" : "켜기") + "  F5 영상 폴더 다시 읽기  M 카메라로  F8 글씨 빼곡(비교용)");
                _sb.AppendLine("K 일시정지" + (_paused ? "<color=#FF6E6E>(멈춤)</color>" : "") + "  , . 한 프레임(멈춘 상태)  S 재생 시각 맞추기");
                _sb.Append(ScreenTestPattern.IsOn
                    ? "글씨 빼곡 켜짐 — 휠 영상 크기 · 드래그 영상 이동 · ↑↓ 글자 크기 · Tab 글씨 모드 · F8 끄기</color></size>"
                    : "휠 · +/- 크기(Shift 미세)  Space 화면 맞춤  Z 1:1  드래그 이동  " + (IsComparing ? "" : "G 격자  ") + "Tab 안내 숨김</color></size>");
                _info.text = _sb.ToString();
                return;
            }

            _sb.Append("요청   device ").Append(_deviceId)
               .Append(" · ").Append(BackendNames[Mathf.Clamp(_backend, 0, BackendNames.Length - 1)])
               .Append(" · ").Append(_width > 0 ? $"{_width}x{_height}" : "기본 해상도")
               .Append(" · ").Append(_fps).Append("fps · ")
               .AppendLine(string.IsNullOrEmpty(_fourcc) ? "기본 포맷" : _fourcc);

            if (_tracker == null || !_tracker.IsInitialized)
            {
                string err = _tracker != null ? _tracker.LastError : null;
                _sb.AppendLine(string.IsNullOrEmpty(err)
                    ? $"<color=#FFD54F>여는 중... ({Time.unscaledTime - _openedAt:0.0}초)</color>"
                    : $"<color=#FF6E6E>{err}</color>");
            }
            else
            {
                bool mismatch = _width > 0 && (_tracker.FrameWidth != _width || _tracker.FrameHeight != _height);
                _sb.Append("실제   ").Append(mismatch ? "<color=#FF6E6E>" : "<color=#7CFF8A>")
                   .Append(_tracker.CaptureFormat).Append("</color>")
                   .Append(mismatch ? "  ← 요청과 다름" : "").AppendLine();
                _sb.Append("측정   ").Append(_tracker.CaptureFps.ToString("0.0")).Append("fps · 검출 ")
                   .Append(_tracker.DetectMs.ToString("0.0")).Append("ms · 마커 ")
                   .Append(_tracker.Markers.Count).Append("개");
                if (_tracker.Markers.Count > 0)
                {
                    _sb.Append(" [");
                    for (int i = 0; i < _tracker.Markers.Count; i++)
                    {
                        if (i > 0) _sb.Append(", ");
                        _sb.Append(_tracker.Markers[i].id).Append(' ')
                           .Append(_tracker.Markers[i].sizePx.ToString("0")).Append("px");
                    }
                    _sb.Append(']');
                }
                _sb.AppendLine();
            }

            if (_tracker != null && _tracker.IsInitialized)
            {
                float scale = _fit ? FitScale() : _scale;
                _sb.Append("표시   ").Append((scale * 100f).ToString("0")).Append("%")
                   .Append(_fit ? " (화면 맞춤)" : "")
                   .Append(" · ").Append(Mathf.RoundToInt(_tracker.FrameWidth * scale)).Append('x')
                   .Append(Mathf.RoundToInt(_tracker.FrameHeight * scale))
                   .Append(" / 화면 ").Append(Screen.width).Append('x').Append(Screen.height)
                   .Append(_flipH ? " · 좌우반전" : "").Append(_flipV ? " · 상하반전" : "").AppendLine();
            }

            var devices = WebCamTexture.devices;
            _sb.Append("장치   ");
            if (devices.Length == 0) _sb.Append("(없음)");
            for (int i = 0; i < devices.Length; i++)
            {
                if (i > 0) _sb.Append(" · ");
                _sb.Append(i).Append(": ").Append(devices[i].name);
            }
            _sb.AppendLine();

            _sb.AppendLine();
            _sb.AppendLine("<size=80%><color=#BBBBBB>M 카메라↔영상  N/P 다음·이전 영상  C 영상 나란히 비교  F5 영상 폴더 다시 읽기");
            _sb.AppendLine("1~6 해상도(640x480 · 1280x720 · 1920x1080 · 2560x1440 · 3840x2160 · 1024x768)  0 기본값");
            _sb.AppendLine("F 포맷  D 디바이스  B 백엔드  R 다시 열기  H/V 뒤집기");
            _sb.Append("휠 · +/- 크기(Shift 미세)  Space 화면 맞춤  Z 1:1  드래그 이동  G 격자  Tab 안내 숨김</color></size>");

            _info.text = _sb.ToString();
        }

        void AppendVideoInfo()
        {
            if (_videoIndex < 0)
            {
                _sb.Append("<color=#FF6E6E>영상 없음 — StreamingAssets/").Append(VideoFolder)
                   .AppendLine("/ 에 넣고 F5</color>");
                return;
            }

            // 비교 중에는 칸마다 이름표가 붙으므로 크기 기준(첫 칸)만 적는다.
            if (IsComparing)
            {
                var refTex = VideoTexture(_videoIndex, out _);
                float s = _fit ? FitScale() : _scale;
                _sb.Append("크기 기준 1번 ").Append(Path.GetFileName(_videos[_videoIndex]));
                if (refTex != null)
                    _sb.Append(" · 칸마다 ").Append(Mathf.RoundToInt(refTex.width * s)).Append('x')
                       .Append(Mathf.RoundToInt(refTex.height * s)).Append(_fit ? " (칸에 맞춤)" : "");
                _sb.AppendLine();
                if (_videos.Count > MaxCompare)
                    _sb.Append("<color=#FFD54F>폴더에 ").Append(_videos.Count).Append("개 — 앞에서 ").Append(MaxCompare)
                       .AppendLine("개만 비교. N/P 로 민다</color>");
                return;
            }

            _sb.Append("파일   ").Append(_videoIndex + 1).Append('/').Append(_videos.Count).Append("  ")
               .AppendLine(Path.GetFileName(_videos[_videoIndex]));

            var player = _players[_videoIndex];
            var info = player != null ? player.Info : null;
            var tex = CurrentTexture(out _);
            if (info == null || tex == null)
            {
                _sb.AppendLine("<color=#FFD54F>여는 중...</color>");
                return;
            }

            var control = player.Control;
            float scale = _fit ? FitScale() : _scale;
            _sb.Append("영상   ").Append(info.GetVideoWidth()).Append('x').Append(info.GetVideoHeight())
               .Append(" · ").Append(info.GetVideoFrameRate().ToString("0.##")).Append("fps · 재생 ")
               .Append(info.GetVideoDisplayRate().ToString("0.0")).Append("fps · ")
               .Append(control != null ? control.GetCurrentTime().ToString("0.0") : "-").Append(" / ")
               .Append(info.GetDuration().ToString("0.0")).AppendLine("초");
            _sb.Append("표시   ").Append((scale * 100f).ToString("0")).Append("%")
               .Append(_fit ? " (화면 맞춤)" : "")
               .Append(" · ").Append(Mathf.RoundToInt(tex.width * scale)).Append('x')
               .Append(Mathf.RoundToInt(tex.height * scale))
               .Append(" / 화면 ").Append(Screen.width).Append('x').Append(Screen.height).AppendLine();
        }

        static Texture2D BuildGridTexture()
        {
            // 한 칸짜리 테두리 텍스처를 Repeat 로 깔아 격자를 만든다.
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Point,
                name = "CameraTestGrid",
            };
            var clear = new Color32(0, 0, 0, 0);
            var line = new Color32(255, 255, 255, 110);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = (x == 0 || y == 0) ? line : clear;
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        void OnDestroy()
        {
            if (_grid != null && _grid.texture != null) Destroy(_grid.texture);
            foreach (var p in _players)
                if (p != null) p.CloseMedia();
        }
    }
}
