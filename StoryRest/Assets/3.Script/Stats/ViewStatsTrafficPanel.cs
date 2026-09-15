using System;
using System.Collections.Generic;
using System.Text;
using StoryRest.ArUco;
using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.Stats
{
    /// <summary>
    /// ESC 설정창 옆에 서는 TCP 패널 — 관람 기록 링크의 연결 상태와 주고받은 줄.
    ///
    /// 설정창(SettingsPanelUI)과 같이 열리고 닫힌다. 설정창은 화면 가운데 680 폭이라 이 판은 오른쪽 끝에 붙여
    /// 서로 겹치지 않는다. 설정창과 같은 캔버스 · 같은 글꼴을 쓰므로 한 창처럼 보인다.
    ///
    /// 로그는 서버 · 클라이언트의 Traffic 이벤트를 그대로 쌓는다. 시작할 때부터 붙어 있으므로 창을 열기 전 것도 남는다.
    /// F7(ViewStatsLinkPanel)이 "지금 상태" 를 프로젝터 화면에 크게 띄우는 것이라면, 여기는 마우스로 조작하는 자리에서
    /// "무엇이 오갔나" 를 시간순으로 훑는 용도다.
    /// </summary>
    public class ViewStatsTrafficPanel : MonoBehaviour
    {
        // 설정창(가운데 680)의 오른쪽 끝 1300 과 이 판의 왼쪽 끝(1920-24-560=1336) 사이를 띄운다.
        const float PanelWidth = 560f;
        const float LogHeight = 560f;
        const int MaxLines = 150;

        StoryRestApp _app;
        SettingsPanelUI _ui;

        Font _font;
        int _fontSize = 18;
        Color _textColor = Color.white;

        GameObject _root;
        Text _status;
        Text _log;
        ScrollRect _scroll;

        readonly Queue<string> _lines = new Queue<string>();
        readonly StringBuilder _builder = new StringBuilder(4096);
        readonly List<string> _names = new List<string>();

        bool _logDirty;

        public void Attach(SettingsPanelUI ui)
        {
            _app = GetComponent<StoryRestApp>();
            _ui = ui;

            var settingsRoot = ui.PanelRoot;
            if (_app == null || settingsRoot == null) return;

            BorrowStyle(settingsRoot);
            Build(settingsRoot.transform.parent);

            Subscribe();

            _ui.VisibilityChanged += OnVisibilityChanged;
            _root.SetActive(settingsRoot.activeSelf);
        }

        void OnDestroy()
        {
            if (_ui != null) _ui.VisibilityChanged -= OnVisibilityChanged;
            if (_app != null && _app.StatsServer != null) _app.StatsServer.Traffic -= Append;
            if (_app != null && _app.StatsClient != null) _app.StatsClient.Traffic -= Append;
        }

        void Subscribe()
        {
            if (_app.StatsServer != null) _app.StatsServer.Traffic += Append;
            if (_app.StatsClient != null) _app.StatsClient.Traffic += Append;
        }

        void OnVisibilityChanged(bool visible)
        {
            if (_root != null) _root.SetActive(visible);
        }

        void Update()
        {
            if (_root == null || !_root.activeInHierarchy) return;

            _status.text = StatusText();

            if (!_logDirty) return;
            _logDirty = false;

            _builder.Clear();
            foreach (string line in _lines) _builder.AppendLine(line);
            _log.text = _builder.ToString();

            // 줄이 늘어난 뒤 맨 아래로. Content 가 Viewport 보다 짧을 때 0 으로 두면 위로 튕기므로 넘칠 때만.
            Canvas.ForceUpdateCanvases();
            if (_scroll.content.rect.height > _scroll.viewport.rect.height)
                _scroll.verticalNormalizedPosition = 0f;
        }

        void Append(string line)
        {
            _lines.Enqueue($"<color=#888888>{DateTime.Now:HH:mm:ss}</color> {line}");
            while (_lines.Count > MaxLines) _lines.Dequeue();
            _logDirty = true;
        }

        // ── 상태 줄 ───────────────────────────────────────────────────────────

        string StatusText()
        {
            switch (_app.RunningRole)
            {
                case AppRole.ArUco:
                {
                    var server = _app.StatsServer;
                    if (server == null) return "<color=#ff8888>서버 컴포넌트가 없습니다</color>";
                    if (!server.IsListening)
                        return $"<color=#ff8888>서버 — 포트 {server.Port} 를 열지 못함</color>  <color=#aaaaaa>(다른 프로그램이 쓰는 중이거나 권한 문제)</color>";

                    _names.Clear();
                    server.GetClientNames(_names);

                    string clients = _names.Count == 0
                        ? "<color=#ffd633>연결된 월 PC 없음</color>"
                        : $"<color=#7fe07f>월 PC {_names.Count}대</color>  <color=#aaaaaa>{string.Join(", ", _names)}</color>";

                    return $"<color=#7fe07f>서버 — 포트 {server.Port} 대기 중</color>   {clients}\n" +
                           $"<color=#aaaaaa>이번 실행 전송 {server.SentThisRun}건 · 마지막 {Ago(server.LastSentAt)}</color>";
                }

                case AppRole.Wall:
                {
                    var client = _app.StatsClient;
                    if (client == null) return "<color=#ff8888>클라이언트 컴포넌트가 없습니다</color>";

                    string link = client.IsConnected
                        ? $"<color=#7fe07f>클라이언트 — ArUco PC {client.Host}:{client.Port} 연결됨</color>"
                        : $"<color=#ff8888>클라이언트 — ArUco PC {client.Host}:{client.Port} 에 붙지 못함</color>  <color=#aaaaaa>(5초마다 재시도)</color>";

                    string sync = client.SyncPendingDays > 0
                        ? $"<color=#ffd633>재동기화 중 {client.SyncPendingDays}일 남음</color>"
                        : client.LastSyncedAt > 0f ? $"재동기화 완료 {client.LastSyncLines}건" : "아직 재동기화 전";

                    return $"{link}\n" +
                           $"<color=#aaaaaa>{sync} · 이번 실행 수신 {client.ReceivedThisRun}건 · 마지막 {Ago(client.LastReceivedAt)}</color>";
                }

                default:
                    return "<color=#aaaaaa>통신 없음 — all 은 한 프로세스라 기록 파일을 바로 읽습니다.\n" +
                           "2·3층이라면 아래 층·역할에서 aruco / wall 로 바꾸고 재시작하세요.</color>";
            }
        }

        static string Ago(float at)
        {
            if (at <= 0f) return "없음";
            float seconds = Time.unscaledTime - at;
            if (seconds < 60f) return $"{seconds:0}초 전";
            if (seconds < 3600f) return $"{seconds / 60f:0}분 전";
            return $"{seconds / 3600f:0.#}시간 전";
        }

        // ── 만들기 ────────────────────────────────────────────────────────────

        void Build(Transform canvas)
        {
            _root = new GameObject("TcpPanel", typeof(RectTransform));
            _root.transform.SetParent(canvas, false);

            var rect = _root.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = new Vector2(-24f, 0f);
            rect.sizeDelta = new Vector2(PanelWidth, 0f);

            var background = _root.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.85f);
            background.raycastTarget = false;

            var layout = _root.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 16, 16);
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = _root.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var title = Label(_root.transform, "Title", _fontSize, FontStyle.Bold);
            title.text = $"TCP — 관람 기록 링크   <size={_fontSize - 4}><color=#aaaaaa>role {AppSettings.RoleName(_app.RunningRole)} · " +
                         $"Setting.json 의 statsHost / statsPort</color></size>";
            title.GetComponent<LayoutElement>().preferredHeight = 30f;

            _status = Label(_root.transform, "Status", _fontSize - 2, FontStyle.Normal);
            _status.GetComponent<LayoutElement>().preferredHeight = 48f;

            var header = Label(_root.transform, "LogHeader", _fontSize - 4, FontStyle.Normal);
            header.color = new Color(0.66f, 0.66f, 0.66f, 1f);
            header.text = $"송수신 로그 (최근 {MaxLines}줄)   → 보냄 · ← 받음 · sync 응답은 한 줄로 접음";
            header.GetComponent<LayoutElement>().preferredHeight = 22f;

            BuildLog(_root.transform);
        }

        void BuildLog(Transform parent)
        {
            var scrollGo = new GameObject("Log", typeof(RectTransform));
            scrollGo.transform.SetParent(parent, false);

            var element = scrollGo.AddComponent<LayoutElement>();
            element.preferredHeight = LogHeight;

            var frame = scrollGo.AddComponent<Image>();
            frame.color = new Color(1f, 1f, 1f, 0.05f);
            frame.raycastTarget = true;   // 휠 스크롤이 여기서 잡힌다

            var viewport = new GameObject("Viewport", typeof(RectTransform)).GetComponent<RectTransform>();
            viewport.SetParent(scrollGo.transform, false);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(8f, 8f);
            viewport.offsetMax = new Vector2(-8f, -8f);
            viewport.pivot = new Vector2(0f, 1f);
            viewport.gameObject.AddComponent<Image>().color = Color.white;
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;

            var content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.sizeDelta = Vector2.zero;

            var contentFitter = content.gameObject.AddComponent<ContentSizeFitter>();
            contentFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _log = content.gameObject.AddComponent<Text>();
            ApplyTextStyle(_log);
            _log.fontSize = _fontSize - 4;
            _log.alignment = TextAnchor.UpperLeft;
            _log.horizontalOverflow = HorizontalWrapMode.Wrap;
            _log.verticalOverflow = VerticalWrapMode.Overflow;

            _scroll = scrollGo.AddComponent<ScrollRect>();
            _scroll.content = content;
            _scroll.viewport = viewport;
            _scroll.horizontal = false;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 30f;
        }

        Text Label(Transform parent, string name, int size, FontStyle style)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var text = go.AddComponent<Text>();
            ApplyTextStyle(text);
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;

            go.AddComponent<LayoutElement>();
            return text;
        }

        void ApplyTextStyle(Text text)
        {
            text.font = _font;
            text.fontSize = _fontSize;
            text.color = _textColor;
            text.supportRichText = true;
            text.raycastTarget = false;
        }

        // 설정창의 글꼴·색을 그대로 빌린다(→ ArUcoSettingsPanel.BorrowStyle 과 같은 이유).
        void BorrowStyle(GameObject settingsRoot)
        {
            // 제목이 아니라 토글 줄의 글을 기준으로 잡는다 — 설정창 본문과 같은 크기가 되게.
            var toggle = settingsRoot.GetComponentInChildren<Toggle>(true);
            var sample = toggle != null ? toggle.GetComponentInChildren<Text>(true) : null;
            if (sample == null) sample = settingsRoot.GetComponentInChildren<Text>(true);

            if (sample != null)
            {
                _font = sample.font;
                _fontSize = sample.fontSize;
                _textColor = sample.color;
            }

            if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
    }
}
