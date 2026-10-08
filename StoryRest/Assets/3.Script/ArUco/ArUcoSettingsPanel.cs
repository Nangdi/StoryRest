using System;
using System.Globalization;
using System.Collections.Generic;
using UnityEngine;
using StoryRest.Keyword;
using UnityEngine.UI;

namespace StoryRest.ArUco
{
    /// <summary>
    /// ESC 설정창(SettingsPanelUI)에 현장에서 조절할 값들의 줄을 덧붙인다.
    ///
    /// 줄의 **모양**은 프리팹이 정하고(`Assets/9.Prefab/SettingsRows.prefab` → SettingsRowTemplates),
    /// 줄의 **개수와 내용**은 여기서 정한다 — 역할에 따라 ArUco 줄이 통째로 빠지므로 씬에 다 그려 둘 수 없다.
    /// 값 범위와 설명이 코드 한 곳에 남는 것도 그대로다(→ ARCHITECTURE §3).
    ///
    /// 여기 두는 값의 기준: 설치자가 현장에서 "느낌으로" 맞추는 것. 카메라 번호나 디스플레이 배정처럼
    /// 한 번 정하면 끝나는 값은 aruco.json 을 직접 고치는 편이 낫다(잘못 건드리면 화면이 사라진다).
    ///
    /// 값을 바꾸면 살아 있는 세트에 바로 적용하고(→ ArUcoSet.ApplyConfig), 잠시 뒤 aruco.json 에 저장한다.
    /// 층 · 역할(Setting.json)만 예외다 — 시작할 때만 읽으므로 저장만 하고 재시작을 안내한다.
    /// 이 둘은 월 PC(wall)에도 있어야 하므로 세트가 없는 역할에서도 판을 붙인다.
    /// </summary>
    public class ArUcoSettingsPanel : MonoBehaviour
    {
        const float SaveDelay = 1f;

        [Tooltip("Assets/9.Prefab/SettingsRows.prefab — 줄의 모양. 글꼴·색·폭을 여기서 잡는다.")]
        [SerializeField] SettingsRowTemplates rowTemplates;

        StoryRestApp _app;
        SettingsPanelUI _ui;

        bool _syncing;
        bool _dirty;          // aruco.json 에 쓸 것이 있다
        bool _keywordDirty;   // keywordwall.json 에 쓸 것이 있다
        float _dirtySince;

        Text _restartNote;
        readonly List<Action> _syncers = new List<Action>();

        // 드롭다운 순서 = AppRole 순서. 설치자가 고르는 글은 Setting.json 의 값과 같은 단어로 시작한다.
        static readonly string[] RoleOptions =
        {
            "all — 카메라 + 키워드 월 (1층)",
            "aruco — 카메라·영상만 (2·3층)",
            "wall — 키워드 월만 (2·3층)",
        };
        static readonly string[] FloorOptions = { "1층", "2층", "3층" };

        public void Attach(SettingsPanelUI ui)
        {
            _app = GetComponent<StoryRestApp>();
            _ui = ui;

            var root = ui != null ? ui.PanelRoot : null;
            if (_app == null || root == null) return;

            if (rowTemplates == null || !rowTemplates.IsComplete)
            {
                Debug.LogError("[ArUco] 설정창 줄 원본이 비어 있습니다. 씬의 StoryRestApp > ArUcoSettingsPanel 에 " +
                               "Assets/9.Prefab/SettingsRows.prefab 을 넣고 안의 원본들을 이어 주세요.");
                return;
            }

            BuildRows(ui.RowsRoot);

            _ui.VisibilityChanged += OnVisibilityChanged;
            SyncFromConfig();
        }

        void OnDestroy()
        {
            if (_ui != null) _ui.VisibilityChanged -= OnVisibilityChanged;
        }

        void Update()
        {
            if ((_dirty || _keywordDirty) && Time.unscaledTime - _dirtySince >= SaveDelay) Save();
        }

        void OnVisibilityChanged(bool visible)
        {
            // 열 때: 편집모드(P 키 등)가 바꿔 둔 값을 따라잡는다. 닫을 때: 기다리지 않고 바로 저장한다.
            if (visible) SyncFromConfig();
            else if (_dirty || _keywordDirty) Save();
        }

        // ── 값 ↔ UI ──────────────────────────────────────────────────────────

        void SyncFromConfig()
        {
            _syncing = true;
            for (int i = 0; i < _syncers.Count; i++) _syncers[i]();
            _syncing = false;
        }

        void Changed()
        {
            if (_syncing) return;

            for (int i = 0; i < _app.Sets.Count; i++) _app.Sets[i].ApplyConfig();

            _dirty = true;
            _dirtySince = Time.unscaledTime;
        }

        // 월 값이 바뀌었다. 살아 있는 월에 알리고, 짝 값(min/max)을 맞춘 뒤라 다른 줄의 표시도 다시 읽는다.
        void KeywordChanged()
        {
            if (_syncing) return;

            for (int i = 0; i < _app.Walls.Count; i++) _app.Walls[i].ApplyConfig();

            _keywordDirty = true;
            _dirtySince = Time.unscaledTime;
            SyncFromConfig();
        }

        void Save()
        {
            if (_dirty) _app.SaveConfig();
            if (_keywordDirty) _app.SaveKeywordConfig();
            _dirty = false;
            _keywordDirty = false;
        }

        // ── 줄 구성 ───────────────────────────────────────────────────────────

        void BuildRows(Transform parent)
        {
            if (AppSettings.HasArUco(_app.RunningRole)) BuildArUcoRows(parent);
            if (AppSettings.HasWall(_app.RunningRole)) BuildKeywordRows(parent);
            BuildAssignmentRows(parent);
            BuildSettingRows(parent);
        }

        // ── 화면 · 카메라 배정 ────────────────────────────────────────────────

        const int MinChoiceCount = 4;   // 장비가 아직 안 꽂혀 있어도 미리 고를 수 있게 최소 이만큼은 보여 준다

        static readonly string[] LiveSetOptions = { "없음", "테이블 A", "테이블 B" };
        static readonly string[] RankOptions = { "없음", "이번 달 호기심 1위", "이번 주 호기심 1위" };

        /// <summary>
        /// 어느 테이블(세트)이 어느 카메라 · 프로젝터를 쓰고, 어느 벽(월)이 어느 프로젝터 · 테이블을 받는지 고른다.
        /// 이 층 · 역할에서 실제로 쓰는 것만 보인다. 카메라 · 디스플레이는 시작할 때 열리므로 재시작해야 적용된다.
        /// 디스플레이는 Unity 의 Game 뷰처럼 1 부터 센다(디스플레이 1 = 내부 번호 0).
        /// </summary>
        void BuildAssignmentRows(Transform parent)
        {
            var displays = DisplayOptions();
            bool any = false;

            if (AppSettings.HasArUco(_app.RunningRole))
            {
                var cameras = CameraOptions();
                foreach (var set in _app.Config.SetsForFloor(_app.RunningFloor))
                {
                    if (!any) Header(parent, "화면 · 카메라 배정 (바꾸면 재시작해야 적용 · 디스플레이는 1 부터)");
                    any = true;

                    var target = set;
                    DropdownRow(parent, $"테이블 {target.name} 카메라  sets.{target.name}.camera.deviceId", cameras,
                        "이 테이블을 비추는 카메라. 번호는 Windows 가 매긴 장치 순서다.",
                        () => target.camera.deviceId,
                        v => { target.camera.deviceId = v; Changed(); });

                    DropdownRow(parent, $"테이블 {target.name} 프로젝터  sets.{target.name}.displayIndex", displays,
                        "이 테이블 위로 콘텐츠를 쏘는 프로젝터.",
                        () => target.displayIndex,
                        v => { target.displayIndex = v; Changed(); });
                }
            }

            if (AppSettings.HasWall(_app.RunningRole) && _app.KeywordConfig != null)
            {
                var walls = _app.KeywordConfig.WallsFor(_app.RunningFloor, _app.RunningRole);
                for (int i = 0; i < walls.Count; i++)
                {
                    if (!any) Header(parent, "화면 · 카메라 배정 (바꾸면 재시작해야 적용 · 디스플레이는 1 부터)");
                    any = true;

                    var wall = walls[i];
                    string label = $"월 {i + 1}";

                    DropdownRow(parent, $"{label} 프로젝터  walls.displayIndex", displays,
                        "이 벽을 비추는 프로젝터. 테이블 프로젝터와 겹치면 시작할 때 경고한다.",
                        () => wall.displayIndex,
                        v => { wall.displayIndex = v; KeywordChanged(); });

                    DropdownRow(parent, $"{label} 이 받는 테이블  walls.liveSets", LiveSetOptions,
                        "이 테이블에서 찍은 질문이 \"지금 누군가가 궁금해한 질문\" 으로 이 벽에 뜬다. 관람객이 마주 보는 벽으로.",
                        () => wall.ShowsLiveFrom("A") ? 1 : wall.ShowsLiveFrom("B") ? 2 : 0,
                        v => { wall.liveSets = v == 1 ? new[] { "A" } : v == 2 ? new[] { "B" } : new string[0]; KeywordChanged(); });

                    DropdownRow(parent, $"{label} 순위 분류  walls.rank", RankOptions,
                        "이 벽에만 뜨는 순위 분류.",
                        () => RankIndex(wall.rank),
                        v => { wall.rank = v == 1 ? "month" : v == 2 ? "week" : ""; KeywordChanged(); });
                }
            }

            if (any) Note(parent, "바꾼 값은 바로 파일에 저장되고, 프로그램을 다시 시작하면 적용됩니다.");
        }

        static int RankIndex(string rank)
        {
            if (string.Equals(rank?.Trim(), "month", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(rank?.Trim(), "week", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }

        /// <summary>"카메라 0 — 장치 이름". 꽂혀 있지 않은 번호도 최소 몇 개는 미리 보여 준다.</summary>
        static string[] CameraOptions()
        {
            var devices = WebCamTexture.devices;
            int count = Mathf.Max(devices.Length, MinChoiceCount);
            var options = new string[count];
            for (int i = 0; i < count; i++)
                options[i] = i < devices.Length ? $"카메라 {i} — {devices[i].name}" : $"카메라 {i} (연결 안 됨)";
            return options;
        }

        /// <summary>"디스플레이 1" … 지금 연결된 수보다 많이 보여 주되, 없는 것은 표시한다.</summary>
        static string[] DisplayOptions()
        {
            int connected = Display.displays.Length;
            int count = Mathf.Max(connected, MinChoiceCount);
            var options = new string[count];
            for (int i = 0; i < count; i++)
                options[i] = i < connected ? $"디스플레이 {i + 1}" : $"디스플레이 {i + 1} (연결 안 됨)";
            return options;
        }

        // 키워드 월(→ keywordwall.json). 살아 있는 월 · 디렉터가 같은 설정 객체를 보고 있어 바꾸는 즉시 따라온다 —
        // 주기 · 시간은 다음 교체부터, 크기 · 투명도 · 움직임은 다음에 나타나는 항목부터.
        void BuildKeywordRows(Transform parent)
        {
            var wall = _app.KeywordConfig;
            var q = wall != null ? wall.questions : null;
            if (q == null) return;

            Header(parent, "키워드 월 — 질문 교체 (바꾸면 바로 적용 · keywordwall.json 에 저장)");

            FloatRow(parent, "질문 머무는 시간 최소  questions.minRotateSeconds", 10f, 600f, 10f, "0",
                () => q.minRotateSeconds, v => q.minRotateSeconds = Mathf.Min(v, q.maxRotateSeconds),
                "한 칸의 질문이 바뀌기까지(초). 칸마다 이 범위에서 뽑는다. 다음 교체부터.", keyword: true);
            FloatRow(parent, "질문 머무는 시간 최대  questions.maxRotateSeconds", 10f, 600f, 10f, "0",
                () => q.maxRotateSeconds, v => q.maxRotateSeconds = Mathf.Max(v, q.minRotateSeconds), null, keyword: true);

            FloatRow(parent, "지금 누군가 머무는 시간  questions.liveSeconds", 5f, 120f, 5f, "0",
                () => q.liveSeconds, v => q.liveSeconds = v,
                "마커가 인식된 질문이 벽에 머무는 시간(초). 떠 있는 동안 들어온 인식은 이 분류를 바꾸지 않는다.", keyword: true);

            IntRow(parent, "해시태그 최대 개수  questions.maxTags", 0, 30,
                () => q.maxTags, v => q.maxTags = v,
                "두 질문의 해시태그를 전부 띄우되 이보다 많으면 무작위로 자른다. 다음 교체부터.", applyToSets: false, keyword: true);

            Header(parent, "키워드 월 — 색 · 크기 · 움직임");

            ToggleRow(parent, "그라데이션 무작위  gradientPick",
                () => wall.PickGradientAtRandom, v => wall.gradientPick = v ? "random" : "first", keyword: true);

            FloatRow(parent, "질문 크기  questions.questionScale", 0.3f, 2f, 0.05f, "0.00",
                () => q.questionScale, v => q.questionScale = v,
                "질문 그림 원본 크기에 곱하는 배율. 분류 문구도 같은 배율로 따라간다(PSD 비율 유지). 다음에 나타나는 질문부터.", keyword: true);

            FloatRow(parent, "해시태그 높이 최소  questions.minTagHeight", 16f, 200f, 2f, "0",
                () => q.minTagHeight, v => q.minTagHeight = Mathf.Min(v, q.maxTagHeight),
                "1920x1200 기준 픽셀. 질문보다 작게 둔다.", keyword: true);
            FloatRow(parent, "해시태그 높이 최대  questions.maxTagHeight", 16f, 200f, 2f, "0",
                () => q.maxTagHeight, v => q.maxTagHeight = Mathf.Max(v, q.minTagHeight), null, keyword: true);

            FloatRow(parent, "질문 속도 최대  questions.maxQuestionSpeed", 0f, 0.05f, 0.001f, "0.000",
                () => q.maxQuestionSpeed, v => q.maxQuestionSpeed = Mathf.Max(v, q.minQuestionSpeed),
                "질문이 흐르는 속도(화면 짧은 변 = 1, 초당). 읽혀야 하므로 느리게.", keyword: true);

            FloatRow(parent, "해시태그 속도 최소  minSpeed", 0.005f, 0.15f, 0.005f, "0.000",
                () => wall.minSpeed, v => wall.minSpeed = Mathf.Min(v, wall.maxSpeed),
                "화면 짧은 변을 1 로 본 초당 이동량. 다음에 나타나는 것부터.", keyword: true);
            FloatRow(parent, "해시태그 속도 최대  maxSpeed", 0.005f, 0.15f, 0.005f, "0.000",
                () => wall.maxSpeed, v => wall.maxSpeed = Mathf.Max(v, wall.minSpeed), null, keyword: true);

            FloatRow(parent, "해시태그 투명도 최소  minAlpha", 0f, 1f, 0.05f, "0.00",
                () => wall.minAlpha, v => wall.minAlpha = Mathf.Min(v, wall.maxAlpha),
                "질문 · 분류 문구는 늘 불투명하다.", keyword: true);
            FloatRow(parent, "해시태그 투명도 최대  maxAlpha", 0f, 1f, 0.05f, "0.00",
                () => wall.maxAlpha, v => wall.maxAlpha = Mathf.Max(v, wall.minAlpha), null, keyword: true);

            FloatRow(parent, "나타나고 사라지는 시간  fadeSeconds", 0f, 5f, 0.1f, "0.0",
                () => wall.fadeSeconds, v => wall.fadeSeconds = v,
                "질문 · 해시태그가 바뀔 때의 교차 페이드(초).", keyword: true);

            FloatRow(parent, "서로 밀어내는 세기  separation", 0f, 2f, 0.1f, "0.0",
                () => wall.separation, v => wall.separation = v,
                "겹쳤을 때 벌어지는 속도. 0 = 안 밀어냄. 큰 질문은 덜 밀리고 해시태그가 비킨다.", keyword: true);

            FloatRow(parent, "가장자리 여백  edgeMargin", 0f, 0.2f, 0.01f, "0.00",
                () => wall.edgeMargin, v => wall.edgeMargin = v,
                "화면 비율. 렌즈 왜곡이 크거나 모서리가 벽에 걸리면 올린다.", keyword: true);

            Note(parent, $"설정 파일: {KeywordWallConfig.Path}   (어느 벽이 어느 세트 · 순위인지는 walls[].liveSets · rank)");
        }

        void BuildArUcoRows(Transform parent)
        {
            var config = _app.Config;

            Header(parent, "ArUco — 현장 조절값 (바꾸면 바로 적용 · aruco.json 에 저장 · 오른쪽 숫자를 누르면 직접 입력)");

            FloatRow(parent, "콘텐츠 유지  holdSeconds", 0f, 2f, 0.05f, "0.00",
                () => config.holdSeconds, v => config.holdSeconds = v,
                "마커를 놓친 뒤 콘텐츠를 몇 초 더 그려 둘지. 손이 스칠 때의 깜빡임을 막는다.");

            FloatRow(parent, "관람 최소 유지  viewMinDwellSeconds", 0f, 5f, 0.1f, "0.0",
                () => config.viewMinDwellSeconds, v => config.viewMinDwellSeconds = v,
                "이 시간 이상 잡혀야 관람 1회로 센다.");

            FloatRow(parent, "영상 이어보기 유예  videoResumeGraceSeconds", 1f, 60f, 1f, "0",
                () => config.videoResumeGraceSeconds, v => config.videoResumeGraceSeconds = Mathf.Max(config.holdSeconds, v),
                "놓친 뒤 이 시간 안에 돌아오면 영상은 멈춘 자리에서 이어서 · 연출 없이 바로. 넘기면 다음엔 처음부터.");

            FloatRow(parent, "관람 기록 유예  viewResumeGraceSeconds", 1f, 60f, 1f, "0",
                () => config.viewResumeGraceSeconds, v => config.viewResumeGraceSeconds = Mathf.Max(config.holdSeconds, v),
                "놓친 뒤 이 시간 안에 돌아오면 같은 관람 1회로 센다. 넘기면 그때 한 건을 기록한다(순위 반영도 그만큼 뒤).");

            FloatRow(parent, "움직임 부드럽게  smoothing", 0f, 0.95f, 0.05f, "0.00",
                () => config.smoothing, v => config.smoothing = v,
                "0 = 즉시 반응(떨림), 클수록 부드럽지만 늦게 따라온다.");

            FloatRow(parent, "영상 반복 시작  videoLoopStartSeconds", 0f, 60f, 0.05f, "0.000",
                () => config.videoLoopStartSeconds, v => config.videoLoopStartSeconds = v,
                "영상이 끝나면 이 초로 돌아간다(도입부는 한 번만) · 0 = 처음으로. 파일 이름 _loop<초> 가 있으면 그게 우선.");

            FloatRow(parent, "정지 고정  stillThresholdPixels", 0f, 10f, 0.5f, "0.0",
                () => config.stillThresholdPixels, v => config.stillThresholdPixels = v,
                "가만히 있는 마커의 떨림을 없앤다. 이 픽셀 안의 흔들림은 무시 · 0 = 끔. 그래도 떨리면 올린다.");

            IntRow(parent, "동시 표시 마커 수  maxSimultaneous", 0, 8,
                () => config.maxSimultaneous, v => config.maxSimultaneous = v,
                "0 = 제한 없음. 세트 하나가 한 번에 띄울 마커 수.");


            ToggleRow(parent, "관람 기록  recordViews",
                () => config.recordViews, v => config.recordViews = v);

            ToggleRow(parent, "콘텐츠 없는 마커 안내  showEmptyGuide",
                () => config.showEmptyGuide, v => config.showEmptyGuide = v);

            BuildAppearRows(parent, config);

            Note(parent, $"설정 파일: {ArUcoConfigIO.Path}");
        }

        // 등장 연출(→ ArUcoAppear.cs). 드롭다운 순서 = AppearConfig.EffectNames 순서.
        static readonly string[] AppearEffectOptions =
        {
            "없음 — 잡히면 바로",
            "마커에서 솟아나옴",
            "페이드",
            "위에서부터 서서히",
            "회오리 모양으로",
            "물결치며 펴짐",
        };

        void BuildAppearRows(Transform parent, ArUcoConfig config)
        {
            var appear = config.appear;

            Header(parent, "등장 연출 (appear)");

            ToggleRow(parent, "등장 연출 켜기  appear.enabled",
                () => appear.enabled, v => appear.enabled = v);

            DropdownRow(parent, "연출  appear.effect", AppearEffectOptions,
                "마커를 놓고 멈추면 고리가 퍼진 뒤 이 방식으로 콘텐츠가 뜬다.",
                () => (int)appear.Effect,
                v =>
                {
                    appear.SetEffect((AppearEffect)v);
                    Changed();
                });

            ToggleRow(parent, "읽는 중 빛 고리  appear.ring",
                () => appear.ring, v => appear.ring = v);

            FloatRow(parent, "읽는 시간  appear.recognizeSeconds", 0f, 4f, 0.1f, "0.0",
                () => appear.recognizeSeconds, v => appear.recognizeSeconds = v,
                "놓은 뒤 콘텐츠가 뜨기까지. 고리 하나가 이 시간에 걸쳐 퍼진다.");

            FloatRow(parent, "등장 시간  appear.appearSeconds", 0.1f, 4f, 0.1f, "0.0",
                () => appear.appearSeconds, v => appear.appearSeconds = Mathf.Max(0.05f, v),
                "콘텐츠가 다 나타나기까지.");

            FloatRow(parent, "멈춤 판정  appear.settleSeconds", 0f, 1.5f, 0.05f, "0.00",
                () => appear.settleSeconds, v => appear.settleSeconds = v,
                "마커가 이 시간 동안 느리면 놓은 것으로 본다. 움직이는 동안은 읽지 않는다.");

            FloatRow(parent, "움직임 기준  appear.moveThreshold", 0.1f, 3f, 0.1f, "0.0",
                () => appear.moveThreshold, v => appear.moveThreshold = v,
                "마커 한 변 길이 / 초. 이보다 빠르면 움직이는 중.");
        }

        // 층과 역할은 시작할 때만 읽는다. 고르면 파일에는 바로 쓰되 다음 실행부터 적용된다.
        void BuildSettingRows(Transform parent)
        {
            var settings = _app.Settings;

            Header(parent, "이 PC 의 층 · 역할 (Setting.json — 바꾸면 재시작해야 적용)");

            DropdownRow(parent, "층  floor", FloorOptions,
                "StreamingAssets/floor_<N>/ 의 콘텐츠와 그 층 번호가 든 세트·키워드 월을 쓴다.",
                () => settings.floor - 1,
                v =>
                {
                    settings.floor = v + 1;
                    SaveSettings();
                });

            DropdownRow(parent, "역할  role", RoleOptions,
                "그래픽카드 출력이 3개뿐이라 2·3층은 ArUco PC 와 월 PC 로 나눈다.",
                () => (int)settings.Role,
                v =>
                {
                    settings.role = AppSettings.RoleName((AppRole)v);
                    SaveSettings();
                });

            _restartNote = Note(parent, "");
            UpdateRestartNote();

            Note(parent, LinkNote());
            Note(parent, $"설정 파일: {AppSettings.Path}");
        }

        void SaveSettings()
        {
            _app.Settings.Save();
            UpdateRestartNote();
        }

        string LinkNote()
        {
            var settings = _app.Settings;

            switch (_app.RunningRole)
            {
                case AppRole.ArUco:
                    return $"관람 기록을 포트 {settings.statsPort} 로 월 PC 에 보냄 (statsPort 는 Setting.json 에서)";
                case AppRole.Wall:
                    return $"ArUco PC {settings.statsHost}:{settings.statsPort} 에서 관람 기록을 받음 (statsHost 는 Setting.json 에서)";
                default:
                    return "이 PC 가 카메라와 키워드 월을 다 맡음 — PC 사이 통신 없음";
            }
        }

        void UpdateRestartNote()
        {
            if (_restartNote == null) return;

            var settings = _app.Settings;
            bool floorChanged = settings.floor != _app.RunningFloor;
            bool roleChanged = settings.Role != _app.RunningRole;

            string running = $"{_app.RunningFloor}층 · {AppSettings.RoleName(_app.RunningRole)}";

            _restartNote.text = !floorChanged && !roleChanged
                ? $"지금 {running} 구성으로 실행 중"
                : $"<color=#ffd633>{settings.floor}층 · {AppSettings.RoleName(settings.Role)} 으로 저장됨 — " +
                  $"재시작해야 적용됩니다 (지금은 {running})</color>";
        }

        // ── 위젯 — 원본을 복제해 값만 물린다 ──────────────────────────────────

        void FloatRow(Transform parent, string label, float min, float max, float step, string format,
                      Func<float> get, Action<float> set, string help, bool keyword = false)
        {
            var row = Row(parent, rowTemplates.sliderRow, label, help);
            var slider = row.slider;
            var input = ValueInput(row, InputField.ContentType.DecimalNumber);

            slider.wholeNumbers = false;
            slider.minValue = min;
            slider.maxValue = max;

            void Sync()
            {
                float v = get();
                slider.SetValueWithoutNotify(v);
                ShowValue(row, input, v.ToString(format, CultureInfo.InvariantCulture));
            }

            void Apply(float v)
            {
                set(Mathf.Clamp(v, min, max));
                Sync();
                if (keyword) KeywordChanged(); else Changed();
            }

            slider.onValueChanged.AddListener(raw =>
            {
                if (_syncing) return;
                Apply(Mathf.Round(raw / step) * step);   // 눈금에 맞춰 잡음을 없앤다
            });

            // 직접 친 값은 눈금에 맞추지 않는다 — 슬라이더로 못 맞추는 값을 넣으려고 치는 것이다.
            input?.onEndEdit.AddListener(text =>
            {
                if (TryParseNumber(text, out float v)) Apply(v); else Sync();
            });

            _syncers.Add(Sync);
        }

        void IntRow(Transform parent, string label, int min, int max,
                    Func<int> get, Action<int> set, string help, bool applyToSets = true, bool keyword = false)
        {
            var row = Row(parent, rowTemplates.sliderRow, label, help);
            var slider = row.slider;
            var input = ValueInput(row, InputField.ContentType.IntegerNumber);

            slider.wholeNumbers = true;
            slider.minValue = min;
            slider.maxValue = max;

            void Sync()
            {
                int v = get();
                slider.SetValueWithoutNotify(v);
                ShowValue(row, input, v.ToString(CultureInfo.InvariantCulture));
            }

            void Apply(int v)
            {
                set(Mathf.Clamp(v, min, max));
                Sync();
                if (keyword) KeywordChanged(); else if (applyToSets) Changed();
            }

            slider.onValueChanged.AddListener(raw =>
            {
                if (_syncing) return;
                Apply(Mathf.RoundToInt(raw));
            });

            input?.onEndEdit.AddListener(text =>
            {
                if (TryParseNumber(text, out float v)) Apply(Mathf.RoundToInt(v)); else Sync();
            });

            _syncers.Add(Sync);
        }

        void ToggleRow(Transform parent, string label, Func<bool> get, Action<bool> set, bool applyToSets = true,
                       bool keyword = false)
        {
            var row = Row(parent, rowTemplates.toggleRow, label, null);
            var toggle = row.toggle;

            toggle.onValueChanged.RemoveAllListeners();

            void Sync() => toggle.SetIsOnWithoutNotify(get());

            toggle.onValueChanged.AddListener(v =>
            {
                if (_syncing) return;
                set(v);
                if (keyword) KeywordChanged(); else if (applyToSets) Changed();
            });

            _syncers.Add(Sync);
        }

        void DropdownRow(Transform parent, string label, string[] options, string help,
                         Func<int> get, Action<int> set)
        {
            var row = Row(parent, rowTemplates.dropdownRow, label, help);
            var dropdown = row.dropdown;

            dropdown.onValueChanged.RemoveAllListeners();
            dropdown.ClearOptions();
            dropdown.AddOptions(new List<string>(options));

            void Sync() => dropdown.SetValueWithoutNotify(Mathf.Clamp(get(), 0, options.Length - 1));

            dropdown.onValueChanged.AddListener(v =>
            {
                if (_syncing) return;
                set(v);
                Sync();
            });

            _syncers.Add(Sync);
        }

        /// <summary>원본을 복제해 이름과 도움말을 넣는다. 도움말은 이름 아래 작은 글씨로 한 줄 더 붙는다.</summary>
        /// <summary>
        /// 슬라이더 옆 숫자를 직접 칠 수 있게 한다. 슬라이더 눈금으로는 5.75 같은 값을 정확히 못 맞춘다.
        /// 원본 줄에 입력칸(row.input)이 있으면 그걸 쓰고, 없으면 숫자 글자(row.value)에 입력칸을 붙인다 —
        /// 프리팹을 고치지 않아도 모든 슬라이더 줄이 입력을 받는다.
        /// </summary>
        static InputField ValueInput(SettingsRow row, InputField.ContentType contentType)
        {
            var input = row.input;

            if (input == null && row.value != null)
            {
                input = row.value.gameObject.AddComponent<InputField>();
                input.textComponent = row.value;
                input.targetGraphic = row.value;

                // 글자가 클릭을 받아야 입력칸이 잡힌다.
                row.value.raycastTarget = true;
                row.value.supportRichText = false;
            }

            if (input == null) return null;

            input.contentType = contentType;
            input.lineType = InputField.LineType.SingleLine;
            input.caretWidth = 2;
            input.selectionColor = new Color(1f, 0.84f, 0.2f, 0.45f);
            return input;
        }

        static void ShowValue(SettingsRow row, InputField input, string text)
        {
            if (input != null)
            {
                // 치는 중인 글자를 덮어쓰지 않는다. 다른 줄이 바뀌어 전체 Sync 가 돌 때도 마찬가지다.
                if (!input.isFocused) input.SetTextWithoutNotify(text);
            }
            else if (row.value != null)
            {
                row.value.text = text;
            }
        }

        // 쉼표를 소수점으로 치는 경우도 받아 준다(5,75).
        static bool TryParseNumber(string text, out float value)
        {
            text = (text ?? "").Trim().Replace(',', '.');
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        SettingsRow Row(Transform parent, SettingsRow template, string label, string help)
        {
            var row = Instantiate(template, parent);
            row.gameObject.SetActive(true);
            row.name = "Row_" + label;

            if (row.label != null)
            {
                row.label.text = string.IsNullOrEmpty(help)
                    ? label
                    : $"{label}\n<size=13><color=#aaaaaa>{help}</color></size>";
            }

            // 도움말이 없는 줄은 한 줄 높이면 된다. 있으면 두 줄이라 원본 높이를 그대로 쓴다.
            var element = row.GetComponent<LayoutElement>();
            if (element != null && string.IsNullOrEmpty(help)) element.preferredHeight = 28f;

            return row;
        }

        void Header(Transform parent, string text)
        {
            var label = Instantiate(rowTemplates.header, parent);
            label.gameObject.SetActive(true);
            label.name = "Header";
            label.text = text;
        }

        Text Note(Transform parent, string text)
        {
            var label = Instantiate(rowTemplates.note, parent);
            label.gameObject.SetActive(true);
            label.name = "Note";
            label.text = text;
            return label;
        }
    }
}
