using System;
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
            BuildSettingRows(parent);
        }

        // 키워드 월(→ keywordwall.json). 살아 있는 월이 같은 설정 객체를 보고 있어 바꾸는 즉시 따라온다 —
        // 시간·속도는 매 프레임, 크기·수명·투명도는 다음에 태어나는 낱말부터. 개수만 시작할 때 정해져 재시작이 필요하다.
        void BuildKeywordRows(Transform parent)
        {
            var wall = _app.KeywordConfig;
            var spot = wall != null ? wall.spotlight : null;
            if (spot == null) return;

            Header(parent, "키워드 월 — 추천 카드 (바꾸면 바로 적용 · keywordwall.json 에 저장)");

            ToggleRow(parent, "추천 카드 상시 노출  spotlight.alwaysOn",
                () => spot.alwaysOn, v => spot.alwaysOn = v, keyword: true);

            FloatRow(parent, "카드 뜨는 간격  spotlight.intervalSeconds", 15f, 300f, 5f, "0",
                () => spot.intervalSeconds, v => spot.intervalSeconds = v,
                "한 장이 뜨고 다음 장이 뜰 때까지(초). 상시 노출이면 쓰지 않는다.", keyword: true);

            FloatRow(parent, "카드 떠 있는 시간  spotlight.cardSeconds", 2f, 60f, 1f, "0",
                () => spot.cardSeconds,
                v =>
                {
                    spot.cardSeconds = v;
                    spot.fadeSeconds = Mathf.Min(spot.fadeSeconds, v * 0.5f);
                },
                "완전히 보이는 시간(초). 페이드는 이 값의 절반을 넘지 못한다.", keyword: true);

            FloatRow(parent, "카드 페이드  spotlight.fadeSeconds", 0f, 3f, 0.1f, "0.0",
                () => spot.fadeSeconds, v => spot.fadeSeconds = Mathf.Min(v, spot.cardSeconds * 0.5f),
                "나타나고 사라지는 데 걸리는 시간(초).", keyword: true);

            ToggleRow(parent, "제목 아래 주제 이름  spotlight.showTopicName",
                () => spot.showTopicName, v => spot.showTopicName = v, keyword: true);

            FloatRow(parent, "순위 다시 세는 주기  spotlight.refreshSeconds", 5f, 300f, 5f, "0",
                () => spot.refreshSeconds, v => spot.refreshSeconds = v,
                "관람 기록을 다시 읽어 카드 목록을 갱신하는 주기(초). 기록이 한 건 적힐 때도 다시 센다.", keyword: true);

            Header(parent, "키워드 월 — 떠다니는 낱말·그림");

            IntRow(parent, "한 화면 개수  maxOnScreen", 1, 40,
                () => wall.maxOnScreen, v => wall.maxOnScreen = v,
                "동시에 떠 있는 개수. 시작할 때 만들어지므로 재시작해야 적용.", applyToSets: false, keyword: true);

            FloatRow(parent, "속도 최소  minSpeed", 0.005f, 0.15f, 0.005f, "0.000",
                () => wall.minSpeed, v => wall.minSpeed = Mathf.Min(v, wall.maxSpeed),
                "화면 짧은 변을 1 로 본 초당 이동량. 다음에 태어나는 것부터.", keyword: true);
            FloatRow(parent, "속도 최대  maxSpeed", 0.005f, 0.15f, 0.005f, "0.000",
                () => wall.maxSpeed, v => wall.maxSpeed = Mathf.Max(v, wall.minSpeed), null, keyword: true);

            FloatRow(parent, "투명도 최소  minAlpha", 0f, 1f, 0.05f, "0.00",
                () => wall.minAlpha, v => wall.minAlpha = Mathf.Min(v, wall.maxAlpha),
                "옅은 것과 진한 것이 섞여야 배경처럼 깔린다.", keyword: true);
            FloatRow(parent, "투명도 최대  maxAlpha", 0f, 1f, 0.05f, "0.00",
                () => wall.maxAlpha, v => wall.maxAlpha = Mathf.Max(v, wall.minAlpha), null, keyword: true);

            FloatRow(parent, "머무는 시간 최소  minLifetime", 3f, 120f, 1f, "0",
                () => wall.minLifetime, v => wall.minLifetime = Mathf.Min(v, wall.maxLifetime),
                "하나가 떠 있는 시간(초). 지나면 사라지고 다른 것이 나온다.", keyword: true);
            FloatRow(parent, "머무는 시간 최대  maxLifetime", 3f, 120f, 1f, "0",
                () => wall.maxLifetime, v => wall.maxLifetime = Mathf.Max(v, wall.minLifetime), null, keyword: true);

            FloatRow(parent, "나타나고 사라지는 시간  fadeSeconds", 0f, 5f, 0.1f, "0.0",
                () => wall.fadeSeconds, v => wall.fadeSeconds = v,
                "카드가 뜨기 이만큼 전에 그 자리의 낱말이 먼저 빠진다.", keyword: true);

            FloatRow(parent, "낱말 글자 최소  minFontSize", 16f, 200f, 2f, "0",
                () => wall.minFontSize, v => wall.minFontSize = Mathf.Min(v, wall.maxFontSize),
                "낱말 모드(keywords.txt)일 때. 1920x1200 기준 픽셀.", keyword: true);
            FloatRow(parent, "낱말 글자 최대  maxFontSize", 16f, 200f, 2f, "0",
                () => wall.maxFontSize, v => wall.maxFontSize = Mathf.Max(v, wall.minFontSize), null, keyword: true);

            FloatRow(parent, "그림 크기 최소  minSpriteSize", 100f, 1200f, 10f, "0",
                () => wall.minSpriteSize, v => wall.minSpriteSize = Mathf.Min(v, wall.maxSpriteSize),
                "스프라이트 모드일 때 긴 변(1920x1200 기준 픽셀).", keyword: true);
            FloatRow(parent, "그림 크기 최대  maxSpriteSize", 100f, 1200f, 10f, "0",
                () => wall.maxSpriteSize, v => wall.maxSpriteSize = Mathf.Max(v, wall.minSpriteSize), null, keyword: true);

            FloatRow(parent, "서로 밀어내는 세기  separation", 0f, 2f, 0.1f, "0.0",
                () => wall.separation, v => wall.separation = v,
                "겹쳤을 때 벌어지는 속도. 0 = 안 밀어냄. 너무 크면 튕기듯 움직인다.", keyword: true);

            FloatRow(parent, "가장자리 여백  edgeMargin", 0f, 0.2f, 0.01f, "0.00",
                () => wall.edgeMargin, v => wall.edgeMargin = v,
                "화면 비율. 렌즈 왜곡이 크거나 모서리가 벽에 걸리면 올린다.", keyword: true);

            ToggleRow(parent, "같은 것 두 개 안 띄움  avoidDuplicates",
                () => wall.avoidDuplicates, v => wall.avoidDuplicates = v, keyword: true);

            Note(parent, $"설정 파일: {KeywordWallConfig.Path}   (자리·크기·글꼴은 씬의 KeywordWall_A/B 에서)");
        }

        void BuildArUcoRows(Transform parent)
        {
            var config = _app.Config;

            Header(parent, "ArUco — 현장 조절값 (바꾸면 바로 적용 · aruco.json 에 저장)");

            FloatRow(parent, "콘텐츠 유지  holdSeconds", 0f, 2f, 0.05f, "0.00",
                () => config.holdSeconds, v => config.holdSeconds = v,
                "마커를 놓친 뒤 콘텐츠를 몇 초 더 그려 둘지. 손이 스칠 때의 깜빡임을 막는다.");

            FloatRow(parent, "관람 최소 유지  viewMinDwellSeconds", 0f, 5f, 0.1f, "0.0",
                () => config.viewMinDwellSeconds, v => config.viewMinDwellSeconds = v,
                "이 시간 이상 잡혀야 관람 1회로 센다.");

            FloatRow(parent, "놓침 복귀 유예  viewResumeGraceSeconds", 1f, 60f, 1f, "0",
                () => config.viewResumeGraceSeconds, v => config.viewResumeGraceSeconds = Mathf.Max(config.holdSeconds, v),
                "놓친 뒤 이 시간 안에 돌아오면 같은 관람 · 영상은 멈춘 자리에서 이어서.");

            FloatRow(parent, "움직임 부드럽게  smoothing", 0f, 0.95f, 0.05f, "0.00",
                () => config.smoothing, v => config.smoothing = v,
                "0 = 즉시 반응(떨림), 클수록 부드럽지만 늦게 따라온다.");

            IntRow(parent, "동시 표시 마커 수  maxSimultaneous", 0, 8,
                () => config.maxSimultaneous, v => config.maxSimultaneous = v,
                "0 = 제한 없음. 세트 하나가 한 번에 띄울 마커 수.");

            ToggleRow(parent, "원근 매핑  perspectiveMapping",
                () => config.perspectiveMapping, v => config.perspectiveMapping = v);

            ToggleRow(parent, "관람 기록  recordViews",
                () => config.recordViews, v => config.recordViews = v);

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
            var value = row.value;

            slider.wholeNumbers = false;
            slider.minValue = min;
            slider.maxValue = max;

            void Sync()
            {
                float v = get();
                slider.SetValueWithoutNotify(v);
                if (value != null) value.text = v.ToString(format);
            }

            slider.onValueChanged.AddListener(raw =>
            {
                if (_syncing) return;
                float v = Mathf.Round(raw / step) * step;   // 눈금에 맞춰 잡음을 없앤다
                set(v);
                Sync();
                if (keyword) KeywordChanged(); else Changed();
            });

            _syncers.Add(Sync);
        }

        void IntRow(Transform parent, string label, int min, int max,
                    Func<int> get, Action<int> set, string help, bool applyToSets = true, bool keyword = false)
        {
            var row = Row(parent, rowTemplates.sliderRow, label, help);
            var slider = row.slider;
            var value = row.value;

            slider.wholeNumbers = true;
            slider.minValue = min;
            slider.maxValue = max;

            void Sync()
            {
                int v = get();
                slider.SetValueWithoutNotify(v);
                if (value != null) value.text = v.ToString();
            }

            slider.onValueChanged.AddListener(raw =>
            {
                if (_syncing) return;
                set(Mathf.RoundToInt(raw));
                Sync();
                if (keyword) KeywordChanged(); else if (applyToSets) Changed();
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
