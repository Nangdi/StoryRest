using System;
using System.Collections.Generic;
using System.Text;
using StoryRest.Stats;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 관람 기록 디버그 패널. F4 로 켜고 끈다.
    ///
    /// "지금 세고 있나", "오늘 몇 번 세어졌나" 를 CSV 를 열지 않고 화면에서 바로 본다.
    /// 세트마다 그 세트의 화면에 뜬다 — 세트끼리 기록이 따로이기 때문이다(→ SPEC §2).
    ///
    /// 표시 전용이다. 값을 바꾸는 조작이 없으므로 편집모드 밖에서 받는다(→ SPEC §6, C 키와 같은 예외).
    /// 편집모드가 켜져 있는 동안은 HUD 자리를 편집모드에 내주고 숨는다.
    /// </summary>
    public class ArUcoStatsPanel : MonoBehaviour
    {
        [SerializeField] KeyCode toggleKey = KeyCode.F4;

        [Tooltip("오늘 파일을 다시 읽는 주기(초). 관람이 끝날 때마다 파일에 한 줄이 붙으므로 주기적으로 따라잡는다.")]
        [SerializeField] float refreshSeconds = 2f;

        [Tooltip("오늘 순위에 보여줄 마커 수.")]
        [SerializeField] int topCount = 6;

        StoryRestApp _app;
        ArUcoEditMode _editMode;
        ArUcoHelpPanel _help;

        bool _visible;
        float _nextRefresh;

        // 오늘 파일에서 읽은 것. 세트 이름 → (마커 → 횟수), (마커 → 누적 초)
        readonly List<ViewRecord> _today = new List<ViewRecord>();
        readonly Dictionary<string, Dictionary<int, int>> _countBySet = new Dictionary<string, Dictionary<int, int>>();
        readonly Dictionary<string, Dictionary<int, float>> _secondsBySet = new Dictionary<string, Dictionary<int, float>>();
        readonly Dictionary<string, int> _totalBySet = new Dictionary<string, int>();
        DateTime _loadedDay;

        readonly List<ArUcoViewCounter.LiveView> _live = new List<ArUcoViewCounter.LiveView>();
        readonly List<KeyValuePair<int, int>> _ranked = new List<KeyValuePair<int, int>>();
        readonly StringBuilder _builder = new StringBuilder(1024);

        public bool IsVisible => _visible;
        public KeyCode ToggleKey => toggleKey;

        void Awake()
        {
            _app = GetComponent<StoryRestApp>();
            _editMode = GetComponent<ArUcoEditMode>();
            _help = GetComponent<ArUcoHelpPanel>();
        }

        // 편집모드(LateUpdate)가 HUD 를 그린 뒤에 돌아야 서로 덮어쓰지 않는다.
        // 같은 LateUpdate 라도 나중에 붙은 컴포넌트가 나중에 돌므로 StoryRestApp 이 편집모드 뒤에 붙인다.
        void LateUpdate()
        {
            if (_app == null || _app.Sets.Count == 0) return;

            if (Input.GetKeyDown(toggleKey)) SetVisible(!_visible);
            if (!_visible) return;

            // 편집모드나 단축키 안내가 HUD 를 쓰는 동안은 비켜 준다. 닫히면 다음 프레임에 다시 그린다.
            if (_editMode != null && _editMode.IsActive) return;
            if (_help != null && _help.IsVisible) return;

            float now = Time.unscaledTime;
            if (now >= _nextRefresh)
            {
                ReloadToday();
                _nextRefresh = now + Mathf.Max(0.5f, refreshSeconds);
            }

            for (int i = 0; i < _app.Sets.Count; i++) DrawFor(_app.Sets[i], now);
        }

        void SetVisible(bool visible)
        {
            _visible = visible;
            _nextRefresh = 0f;

            if (visible) return;

            // 편집모드나 단축키 안내가 켜져 있으면 HUD 는 그쪽 것이다. 건드리지 않는다.
            if (_editMode != null && _editMode.IsActive) return;
            if (_help != null && _help.IsVisible) return;
            for (int i = 0; i < _app.Sets.Count; i++) _app.Sets[i].View.ShowHud(null);
        }

        void ReloadToday()
        {
            _today.Clear();
            _countBySet.Clear();
            _secondsBySet.Clear();
            _totalBySet.Clear();

            _loadedDay = DateTime.Now.Date;
            ViewLog.ReadDay(_loadedDay, _today);

            for (int i = 0; i < _today.Count; i++)
            {
                var r = _today[i];

                if (!_countBySet.TryGetValue(r.set, out var counts))
                {
                    counts = new Dictionary<int, int>();
                    _countBySet[r.set] = counts;
                    _secondsBySet[r.set] = new Dictionary<int, float>();
                }

                counts.TryGetValue(r.markerId, out int c);
                counts[r.markerId] = c + 1;

                var seconds = _secondsBySet[r.set];
                seconds.TryGetValue(r.markerId, out float s);
                seconds[r.markerId] = s + r.seconds;

                _totalBySet.TryGetValue(r.set, out int total);
                _totalBySet[r.set] = total + 1;
            }
        }

        void DrawFor(ArUcoSet set, float now)
        {
            var view = set.View;
            if (view == null) return;

            var counter = set.Views;
            string setName = set.Config.name;

            _builder.Clear();
            _builder.AppendLine($"<b>관람 기록</b> — 세트 {setName}   <color=#888888>({toggleKey} 로 닫기)</color>");

            if (counter == null)
            {
                _builder.AppendLine("<color=#ffd633>기록이 꺼져 있습니다 (aruco.json 의 recordViews)</color>");
                view.ShowHud(_builder.ToString());
                return;
            }

            var config = _app.Config;
            _builder.AppendLine($"<color=#888888>{config.viewMinDwellSeconds:0.#}초 이상 유지 = 관람 1회 · " +
                                $"{config.viewResumeGraceSeconds:0.#}초 안에 돌아오면 같은 관람</color>");
            _builder.AppendLine();

            // ---- 지금 ----
            _live.Clear();
            counter.GetLive(_live, now);

            int liveCounted = 0;
            for (int i = 0; i < _live.Count; i++) if (_live[i].counted) liveCounted++;

            _builder.AppendLine("<b>지금</b>");
            if (_live.Count == 0)
            {
                _builder.AppendLine("  <color=#888888>잡힌 마커 없음</color>");
            }
            else
            {
                for (int i = 0; i < _live.Count; i++)
                {
                    var v = _live[i];

                    // 세어진 것은 초록, 아직 유지 시간을 채우는 중이면 노랑, 놓쳐서 유예 중이면 회색.
                    string color = !v.counted ? "#ffd633" : v.sinceSeen > 0.05f ? "#888888" : "#7fe07f";
                    string state = !v.counted
                        ? $"대기 {v.elapsed:0.0}s / {config.viewMinDwellSeconds:0.#}s"
                        : v.sinceSeen > 0.05f
                            ? $"놓침 {v.sinceSeen:0.0}s / {config.viewResumeGraceSeconds:0.#}s"
                            : $"{v.elapsed:0.0}s ✓";

                    _builder.AppendLine($"  <color={color}>{v.markerId,3}번  {state}</color>");
                }
            }
            _builder.AppendLine();

            // ---- 오늘 ----
            _totalBySet.TryGetValue(setName, out int fileTotal);
            int today = fileTotal + liveCounted;

            _builder.AppendLine($"<b>오늘 {today}회</b>   <color=#888888>파일 {fileTotal} + 진행 중 {liveCounted} · " +
                                $"이번 실행 {counter.CountedThisRun}</color>");

            if (_countBySet.TryGetValue(setName, out var counts) && counts.Count > 0)
            {
                _ranked.Clear();
                foreach (var pair in counts) _ranked.Add(pair);
                _ranked.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key));

                var seconds = _secondsBySet[setName];
                int shown = Mathf.Min(topCount, _ranked.Count);

                for (int i = 0; i < shown; i++)
                {
                    int id = _ranked[i].Key;
                    int n = _ranked[i].Value;
                    seconds.TryGetValue(id, out float total);
                    _builder.AppendLine($"  {i + 1,2}. {id,3}번  {n,3}회   <color=#888888>평균 {total / n:0.0}s</color>");
                }

                if (_ranked.Count > shown)
                    _builder.AppendLine($"  <color=#888888>… 외 {_ranked.Count - shown}개</color>");
            }
            else
            {
                _builder.AppendLine("  <color=#888888>아직 파일에 기록된 관람이 없습니다</color>");
            }

            _builder.AppendLine($"<color=#666666><size=16>{ViewLog.FileFor(_loadedDay)}</size></color>");

            view.ShowHud(_builder.ToString());
        }
    }
}
