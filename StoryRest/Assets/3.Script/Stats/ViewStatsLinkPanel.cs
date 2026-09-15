using System;
using System.Collections.Generic;
using System.Text;
using StoryRest.ArUco;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.Stats
{
    /// <summary>
    /// 관람 기록 링크 상태 패널. F7 로 켜고 끈다.
    ///
    /// "월 PC 가 붙어 있나", "방금 기록이 건너갔나" 를 로그 파일을 열지 않고 현장에서 바로 본다.
    /// 세트 HUD(ArUcoProjectionView)가 아니라 자기 캔버스를 디스플레이 0 에 띄운다 —
    /// 월 PC(wall)에는 세트가 없어 HUD 가 없고, 링크는 세트가 아니라 PC 단위의 일이기 때문이다.
    ///
    /// 표시 전용이다. 값을 바꾸는 조작이 없으므로 편집모드 밖에서 받는다(→ SPEC §6, F4 와 같은 예외).
    /// </summary>
    public class ViewStatsLinkPanel : MonoBehaviour
    {
        [SerializeField] KeyCode toggleKey = KeyCode.F7;

        StoryRestApp _app;

        bool _visible;
        RectTransform _panel;
        TMP_Text _text;

        readonly List<string> _names = new List<string>();
        readonly StringBuilder _builder = new StringBuilder(1024);

        public bool IsVisible => _visible;
        public KeyCode ToggleKey => toggleKey;

        void Awake()
        {
            _app = GetComponent<StoryRestApp>();
        }

        void OnDestroy()
        {
            if (_panel != null) Destroy(_panel.gameObject);
        }

        void LateUpdate()
        {
            if (Input.GetKeyDown(toggleKey)) SetVisible(!_visible);
            if (!_visible) return;

            _builder.Clear();
            Append(_builder);
            _text.text = _builder.ToString();
        }

        void SetVisible(bool visible)
        {
            _visible = visible;

            if (visible && _panel == null) Build();
            if (_panel != null) _panel.gameObject.SetActive(visible);
        }

        // ── 내용 ─────────────────────────────────────────────────────────────

        void Append(StringBuilder b)
        {
            b.AppendLine($"<b>관람 기록 링크</b>   <color=#888888>({toggleKey} 로 닫기)</color>");

            if (_app == null)
            {
                b.AppendLine("<color=#ff8888>StoryRestApp 이 없습니다</color>");
                return;
            }

            var settings = _app.Settings;
            b.AppendLine($"<color=#888888>{_app.RunningFloor}층 · role {AppSettings.RoleName(_app.RunningRole)}</color>");
            b.AppendLine();

            switch (_app.RunningRole)
            {
                case AppRole.ArUco: AppendServer(b, _app.StatsServer, settings); break;
                case AppRole.Wall:  AppendClient(b, _app.StatsClient, settings); break;
                default:
                    b.AppendLine("<color=#888888>통신 없음 — all 은 한 프로세스라 기록 파일을 바로 읽습니다.</color>");
                    b.AppendLine("<color=#888888>2·3층에서 이 화면이 보이면 Setting.json 의 role 이 잘못된 것입니다.</color>");
                    break;
            }

            b.AppendLine();
            b.Append($"<color=#666666><size=16>{ViewLog.FileFor(DateTime.Now)}</size></color>");
        }

        void AppendServer(StringBuilder b, ViewStatsServer server, AppSettings settings)
        {
            b.AppendLine("<b>서버</b>   <color=#888888>기록 원본 · 월 PC 가 여기로 붙는다</color>");

            if (server == null)
            {
                b.AppendLine("<color=#ff8888>서버 컴포넌트가 없습니다</color>");
                return;
            }

            b.AppendLine(server.IsListening
                ? $"<color=#7fe07f>포트 {server.Port} 대기 중</color>"
                : $"<color=#ff8888>포트 {server.Port} 를 열지 못했습니다 — 다른 프로그램이 쓰고 있거나 권한 문제. 시작 로그 확인</color>");

            _names.Clear();
            server.GetClientNames(_names);

            if (_names.Count == 0)
            {
                b.AppendLine("<color=#ffd633>연결된 월 PC 없음</color>   " +
                             "<color=#888888>월 PC 의 statsHost 가 이 PC 의 IP 인지 · 방화벽에서 포트를 열었는지 확인</color>");
            }
            else
            {
                b.AppendLine($"<color=#7fe07f>연결된 월 PC {_names.Count}대</color>");
                for (int i = 0; i < _names.Count; i++) b.AppendLine($"  {_names[i]}");
            }

            b.AppendLine();
            b.AppendLine($"이번 실행에 내보낸 기록 {server.SentThisRun}건   " +
                         $"<color=#888888>(연결 없이 파일에만 적힌 것은 세지 않음)</color>");
            AppendLastLine(b, "마지막 전송", server.LastSentAt, server.LastSentLine);
        }

        void AppendClient(StringBuilder b, ViewStatsClient client, AppSettings settings)
        {
            b.AppendLine("<b>클라이언트</b>   <color=#888888>ArUco PC 의 기록을 받아 이 PC 에 미러로 적는다</color>");

            if (client == null)
            {
                b.AppendLine("<color=#ff8888>클라이언트 컴포넌트가 없습니다</color>");
                return;
            }

            if (client.IsConnected)
            {
                b.AppendLine($"<color=#7fe07f>ArUco PC {client.Host}:{client.Port} 연결됨</color>" +
                             (client.ConnectCount > 1 ? $"   <color=#ffd633>(이번 실행에 {client.ConnectCount - 1}번 끊겼다 다시 붙음)</color>" : ""));
            }
            else
            {
                b.AppendLine($"<color=#ff8888>ArUco PC {client.Host}:{client.Port} 에 붙지 못함 — 5초마다 다시 시도</color>");
                b.AppendLine("<color=#888888>statsHost 가 ArUco PC 의 IP 인지 · ArUco PC 가 켜져 있고 role 이 aruco 인지 · 방화벽 확인</color>");
            }

            b.AppendLine();

            if (client.SyncPendingDays > 0)
                b.AppendLine($"<color=#ffd633>재동기화 중 — {ViewStatsClient.SyncDays}일 중 {client.SyncPendingDays}일 남음</color>");
            else if (client.LastSyncedAt > 0f)
                b.AppendLine($"재동기화 완료 — 최근 {ViewStatsClient.SyncDays}일에서 {client.LastSyncLines}건 받아 맞춤   " +
                             $"<color=#888888>({Ago(client.LastSyncedAt)})</color>");
            else
                b.AppendLine("<color=#888888>아직 재동기화한 적 없음</color>");

            b.AppendLine($"이번 실행에 실시간으로 받은 기록 {client.ReceivedThisRun}건");
            AppendLastLine(b, "마지막 수신", client.LastReceivedAt, client.LastReceivedLine);
        }

        static void AppendLastLine(StringBuilder b, string label, float at, string line)
        {
            if (at <= 0f)
            {
                b.AppendLine($"<color=#888888>{label}: 아직 없음</color>");
                return;
            }

            // 방금 건너간 것은 초록으로 잠깐 띄워 "지금 갔다" 가 눈에 들어오게 한다.
            string color = Time.unscaledTime - at < 3f ? "#7fe07f" : "#888888";
            b.AppendLine($"<color={color}>{label}: {Ago(at)}</color>   <color=#888888><size=16>{line}</size></color>");
        }

        static string Ago(float at)
        {
            float seconds = Time.unscaledTime - at;
            if (seconds < 60f) return $"{seconds:0}초 전";
            if (seconds < 3600f) return $"{seconds / 60f:0}분 전";
            return $"{seconds / 3600f:0.#}시간 전";
        }

        // ── 표시 ─────────────────────────────────────────────────────────────

        // 디스플레이 0 의 오버레이. 세트 HUD(좌상단)와 겹치지 않게 우상단에 둔다.
        void Build()
        {
            var canvasGo = new GameObject("StatsLinkCanvas", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = 0;
            canvas.sortingOrder = 200;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            var panelGo = new GameObject("Panel", typeof(RectTransform));
            panelGo.transform.SetParent(canvasGo.transform, false);

            _panel = panelGo.GetComponent<RectTransform>();
            _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(1f, 1f);
            _panel.sizeDelta = new Vector2(760f, 0f);
            _panel.anchoredPosition = new Vector2(-24f, -24f);

            var background = panelGo.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.78f);
            background.raycastTarget = false;

            var layout = panelGo.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(28, 28, 24, 24);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = panelGo.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(_panel, false);

            _text = textGo.AddComponent<TextMeshProUGUI>();
            _text.color = Color.white;
            _text.raycastTarget = false;
            _text.richText = true;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.fontSize = 22f;
            _text.lineSpacing = 8f;
        }
    }
}
