using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.ArUco
{
    /// <summary>
    /// ESC 설정창(SettingsPanelUI)에 현장에서 조절할 값들의 줄을 덧붙인다.
    ///
    /// 씬의 설정창은 인스펙터로 토글 두 개를 이어 둔 작은 판이다. 줄을 씬에 손으로 만들어 잇는 대신
    /// 실행할 때 코드로 만들어 붙인다 — 항목이 늘어도 씬을 건드리지 않고, 값 범위와 설명이 코드 한 곳에 남는다.
    ///
    /// 여기 두는 값의 기준: 설치자가 현장에서 "느낌으로" 맞추는 것. 카메라 번호나 디스플레이 배정처럼
    /// 한 번 정하면 끝나는 값은 aruco.json 을 직접 고치는 편이 낫다(잘못 건드리면 화면이 사라진다).
    ///
    /// 값을 바꾸면 살아 있는 세트에 바로 적용하고(→ ArUcoSet.ApplyConfig), 잠시 뒤 aruco.json 에 저장한다.
    /// 층 번호만 예외다 — 시작할 때만 읽으므로 저장만 하고 재시작을 안내한다.
    /// </summary>
    public class ArUcoSettingsPanel : MonoBehaviour
    {
        const float SaveDelay = 1f;
        const float PanelWidth = 680f;

        StoryRestApp _app;
        SettingsPanelUI _ui;

        Font _font;
        int _fontSize = 18;
        Color _textColor = Color.white;
        Toggle _toggleTemplate;

        bool _syncing;
        bool _dirty;
        float _dirtySince;

        Text _floorNote;
        readonly List<Action> _syncers = new List<Action>();

        public void Attach(SettingsPanelUI ui)
        {
            _app = GetComponent<StoryRestApp>();
            _ui = ui;

            var root = ui.PanelRoot;
            if (_app == null || root == null) return;

            BorrowStyle(root);
            ResizePanel(root);
            BuildRows(root.transform);

            _ui.VisibilityChanged += OnVisibilityChanged;
            SyncFromConfig();
        }

        void OnDestroy()
        {
            if (_ui != null) _ui.VisibilityChanged -= OnVisibilityChanged;
        }

        void Update()
        {
            if (_dirty && Time.unscaledTime - _dirtySince >= SaveDelay) Save();
        }

        void OnVisibilityChanged(bool visible)
        {
            // 열 때: 편집모드(P 키 등)가 바꿔 둔 값을 따라잡는다. 닫을 때: 기다리지 않고 바로 저장한다.
            if (visible) SyncFromConfig();
            else if (_dirty) Save();
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

        void Save()
        {
            _dirty = false;
            _app.SaveConfig();
        }

        // ── 줄 구성 ───────────────────────────────────────────────────────────

        void BuildRows(Transform parent)
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

            Header(parent, "층 · 역할 (Setting.json)");

            var settings = _app.Settings;
            IntRow(parent, "이 PC 의 층  floor", 1, 3,
                () => settings.floor,
                v =>
                {
                    settings.floor = v;
                    settings.Save();
                    UpdateFloorNote();
                },
                null, applyToSets: false);

            _floorNote = Note(parent, "");
            UpdateFloorNote();

            // 역할은 PC 를 바꿔 꽂는 일이라 여기서 바꾸지 않는다 — 파일을 고치고 재시작한다.
            Note(parent, RoleNote());

            Note(parent, $"설정 파일: {ArUcoConfigIO.Path}");
        }

        string RoleNote()
        {
            var settings = _app.Settings;
            string role = AppSettings.RoleName(_app.RunningRole);

            switch (_app.RunningRole)
            {
                case AppRole.ArUco:
                    return $"역할 {role} — 관람 기록을 포트 {settings.statsPort} 로 월 PC 에 보냄. 바꾸려면 Setting.json 의 role 을 고치고 재시작";
                case AppRole.Wall:
                    return $"역할 {role} — ArUco PC {settings.statsHost}:{settings.statsPort} 에서 관람 기록을 받음";
                default:
                    return $"역할 {role} — 이 PC 가 카메라와 키워드 월을 다 맡음. 바꾸려면 Setting.json 의 role 을 고치고 재시작";
            }
        }

        void UpdateFloorNote()
        {
            if (_floorNote == null) return;

            int running = _app.Settings.floor;
            _floorNote.text = running == _app.RunningFloor
                ? $"지금 {running}층 구성으로 실행 중"
                : $"<color=#ffd633>{running}층으로 저장됨 — 재시작해야 적용됩니다 (지금은 {_app.RunningFloor}층)</color>";
        }

        // ── 위젯 ──────────────────────────────────────────────────────────────

        void FloatRow(Transform parent, string label, float min, float max, float step, string format,
                      Func<float> get, Action<float> set, string help)
        {
            var row = Row(parent, label, help);
            var slider = Slider(row, false, min, max);
            var value = ValueText(row);

            void Sync()
            {
                float v = get();
                slider.SetValueWithoutNotify(v);
                value.text = v.ToString(format);
            }

            slider.onValueChanged.AddListener(raw =>
            {
                if (_syncing) return;
                float v = Mathf.Round(raw / step) * step;   // 눈금에 맞춰 잡음을 없앤다
                set(v);
                Sync();
                Changed();
            });

            _syncers.Add(Sync);
        }

        void IntRow(Transform parent, string label, int min, int max,
                    Func<int> get, Action<int> set, string help, bool applyToSets = true)
        {
            var row = Row(parent, label, help);
            var slider = Slider(row, true, min, max);
            var value = ValueText(row);

            void Sync()
            {
                int v = get();
                slider.SetValueWithoutNotify(v);
                value.text = v.ToString();
            }

            slider.onValueChanged.AddListener(raw =>
            {
                if (_syncing) return;
                set(Mathf.RoundToInt(raw));
                Sync();
                if (applyToSets) Changed();
            });

            _syncers.Add(Sync);
        }

        void ToggleRow(Transform parent, string label, Func<bool> get, Action<bool> set)
        {
            var toggle = Instantiate(_toggleTemplate, parent);
            toggle.name = "Toggle_" + label;
            toggle.onValueChanged.RemoveAllListeners();

            var text = toggle.GetComponentInChildren<Text>();
            if (text != null) text.text = label;

            void Sync() => toggle.SetIsOnWithoutNotify(get());

            toggle.onValueChanged.AddListener(v =>
            {
                if (_syncing) return;
                set(v);
                Changed();
            });

            _syncers.Add(Sync);
        }

        RectTransform Row(Transform parent, string label, string help)
        {
            var go = new GameObject("Row_" + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var element = go.AddComponent<LayoutElement>();
            element.preferredHeight = string.IsNullOrEmpty(help) ? 28f : 44f;

            // 라벨 + 도움말은 세로로 쌓아 한 줄 안에 둔다.
            var textGo = new GameObject("Label", typeof(RectTransform));
            textGo.transform.SetParent(go.transform, false);

            var text = textGo.AddComponent<Text>();
            ApplyTextStyle(text);
            text.alignment = TextAnchor.MiddleLeft;
            text.text = string.IsNullOrEmpty(help)
                ? label
                : $"{label}\n<size={_fontSize - 5}><color=#aaaaaa>{help}</color></size>";

            var textElement = textGo.AddComponent<LayoutElement>();
            textElement.flexibleWidth = 1f;

            return go.GetComponent<RectTransform>();
        }

        Text ValueText(RectTransform row)
        {
            var go = new GameObject("Value", typeof(RectTransform));
            go.transform.SetParent(row, false);

            var text = go.AddComponent<Text>();
            ApplyTextStyle(text);
            text.alignment = TextAnchor.MiddleRight;

            var element = go.AddComponent<LayoutElement>();
            element.preferredWidth = 56f;

            return text;
        }

        Slider Slider(RectTransform row, bool wholeNumbers, float min, float max)
        {
            var go = new GameObject("Slider", typeof(RectTransform));
            go.transform.SetParent(row, false);

            var element = go.AddComponent<LayoutElement>();
            element.preferredWidth = 220f;
            element.preferredHeight = 20f;

            var background = Image(go.transform, "Background", new Color(1f, 1f, 1f, 0.15f));
            Stretch(background.rectTransform, new Vector2(0f, 6f), new Vector2(0f, -6f));

            var fillArea = new GameObject("Fill Area", typeof(RectTransform)).GetComponent<RectTransform>();
            fillArea.SetParent(go.transform, false);
            Stretch(fillArea, new Vector2(6f, 6f), new Vector2(-6f, -6f));

            var fill = Image(fillArea, "Fill", new Color(0.35f, 0.7f, 1f, 0.9f));
            Stretch(fill.rectTransform, Vector2.zero, Vector2.zero);

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform)).GetComponent<RectTransform>();
            handleArea.SetParent(go.transform, false);
            Stretch(handleArea, new Vector2(8f, 0f), new Vector2(-8f, 0f));

            var handle = Image(handleArea, "Handle", Color.white);
            handle.rectTransform.sizeDelta = new Vector2(16f, 0f);

            var slider = go.AddComponent<Slider>();
            slider.targetGraphic = handle;
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = wholeNumbers;

            return slider;
        }

        void Header(Transform parent, string text)
        {
            var go = new GameObject("Header", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<Text>();
            ApplyTextStyle(label);
            label.fontStyle = FontStyle.Bold;
            label.alignment = TextAnchor.LowerLeft;
            label.text = text;

            var element = go.AddComponent<LayoutElement>();
            element.preferredHeight = 34f;
        }

        Text Note(Transform parent, string text)
        {
            var go = new GameObject("Note", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<Text>();
            ApplyTextStyle(label);
            label.fontSize = _fontSize - 4;
            label.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            label.alignment = TextAnchor.MiddleLeft;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.text = text;

            var element = go.AddComponent<LayoutElement>();
            element.preferredHeight = 22f;

            return label;
        }

        Image Image(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = color;
            return image;
        }

        static void Stretch(RectTransform rect, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        void ApplyTextStyle(Text text)
        {
            text.font = _font;
            text.fontSize = _fontSize;
            text.color = _textColor;
            text.supportRichText = true;
            text.raycastTarget = false;
        }

        // 씬 판의 글꼴·크기·토글 모양을 그대로 빌려 쓴다. 새 줄만 다른 모양이면 덧붙인 티가 난다.
        void BorrowStyle(GameObject root)
        {
            _toggleTemplate = root.GetComponentInChildren<Toggle>(true);

            var sample = _toggleTemplate != null ? _toggleTemplate.GetComponentInChildren<Text>(true) : null;
            if (sample == null) sample = root.GetComponentInChildren<Text>(true);

            if (sample != null)
            {
                _font = sample.font;
                _fontSize = sample.fontSize;
                _textColor = sample.color;
            }

            if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        // 판은 토글 두 개 크기로 고정되어 있다. 줄이 늘어난 만큼 높이가 따라오게 하고 폭을 넓힌다.
        static void ResizePanel(GameObject root)
        {
            var rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(PanelWidth, rect.sizeDelta.y);

            if (root.GetComponent<ContentSizeFitter>() == null)
            {
                var fitter = root.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            var layout = root.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandHeight = false;
            }
        }
    }
}
