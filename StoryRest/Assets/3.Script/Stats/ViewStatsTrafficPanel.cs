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
    ///
    /// **이 스크립트는 판 프리팹의 뿌리에 붙어 있다**(→ ARCHITECTURE §3).
    /// `Assets/9.Prefab/StatsTrafficPanel.prefab` 이 자리·크기·글꼴·색을 정하고, 여기서는 글만 채운다.
    /// </summary>
    public class ViewStatsTrafficPanel : MonoBehaviour
    {
        const int MaxLines = 150;

        [Header("판 조각 (프리팹에서 연결)")]
        [SerializeField] Text _title;
        [SerializeField] Text _status;
        [SerializeField] Text _logHeader;
        [SerializeField] Text _log;
        [SerializeField] ScrollRect _scroll;

        StoryRestApp _app;
        SettingsPanelUI _ui;

        GameObject _root;

        readonly Queue<string> _lines = new Queue<string>();
        readonly StringBuilder _builder = new StringBuilder(4096);
        readonly List<string> _names = new List<string>();

        bool _logDirty;

        /// <summary>설정창에 물린다. 판은 이미 프리팹으로 만들어져 있고, 여기서는 재료만 잇는다.</summary>
        public void Attach(StoryRestApp app, SettingsPanelUI ui)
        {
            _app = app;
            _ui = ui;

            var settingsRoot = ui != null ? ui.PanelRoot : null;
            if (_app == null || settingsRoot == null) return;

            if (_title == null || _status == null || _log == null)
            {
                Debug.LogError("[Stats] TCP 패널 프리팹의 연결이 비었습니다. " +
                               "Assets/9.Prefab/StatsTrafficPanel.prefab 에서 Title·Status·Log 를 이어 주세요.");
                return;
            }

            _root = gameObject;

            _title.text = $"TCP — 관람 기록 링크   <size=14><color=#aaaaaa>role {AppSettings.RoleName(_app.RunningRole)} · " +
                          $"Setting.json 의 statsHost / statsPort</color></size>";

            if (_logHeader != null)
                _logHeader.text = $"송수신 로그 (최근 {MaxLines}줄)   → 보냄 · ← 받음 · sync 응답은 한 줄로 접음";

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
            if (_scroll != null && _scroll.content != null && _scroll.viewport != null
                && _scroll.content.rect.height > _scroll.viewport.rect.height)
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

    }
}
