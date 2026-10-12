using System;
using System.Collections.Generic;
using System.IO;
using StoryRest.Stats;
using UnityEngine;

namespace StoryRest.Keyword
{
    /// <summary>질문 위에 붙는 분류. 문구 그림은 keywordwall.json 의 questions.titles 가 정한다.</summary>
    public enum QuestionKind
    {
        Random,     // 이 질문도 함께 만나보세요 · 쉬는 동안 궁금증 하나 · 잠깐, 그게 정말이야? · 어디 한번 알아볼까요?
        Least,      // 아직 만나보지 못한 질문 · 이 질문의 첫 탐험가를 찾습니다
        Week,       // 이번 주 호기심 1위
        Month,      // 이번 달 호기심 1위
        Live,       // 지금 누군가가 궁금해한 질문
    }

    /// <summary>화면 하나의 질문 칸. 내용이 바뀔 때마다 version 이 오른다.</summary>
    public class QuestionSlot
    {
        public QuestionKind kind;
        public WallQuestion question;   // null 이면 빈 칸
        public string titlePath;        // 분류 문구 그림. 없으면 null
        public float shownAt;
        public float expiresAt;
        public int version;
    }

    /// <summary>월 화면 하나의 상태 — 질문 칸 둘과 함께 떠다닐 해시태그.</summary>
    public class QuestionScreen
    {
        public readonly WallScreen config;
        public readonly QuestionSlot[] slots = { new QuestionSlot(), new QuestionSlot() };
        public readonly List<string> tags = new List<string>();
        public int tagVersion;

        public QuestionScreen(WallScreen config)
        {
            this.config = config;
        }
    }

    /// <summary>
    /// 벽에 무엇을 띄울지 정한다(→ WALL_CONTENT §5). 화면을 모르는 모델이고, 앱에 하나다.
    /// 월 화면(KeywordWall)은 Screens[i] 의 version 이 바뀌었을 때만 다시 그린다.
    ///
    /// - 칸마다 2~3분 수명이 있고, 지나면 분류 종류(랜덤 · 최소 선택 · 그 화면의 1위)를 같은 확률로 골라 새 질문을 넣는다.
    ///   두 화면 어디에든 이미 떠 있는 질문은 후보가 아니다. 1위가 이미 떠 있으면 그 종류는 이번에 쓸 수 없다.
    /// - 처음에는 두 번째 칸의 수명을 절반으로 시작한다. 이후로는 칸마다 따로 뽑으니 시차가 유지된다.
    /// - "지금 누군가" 는 마커가 관람으로 세어지는 순간(ViewLog.Started) 그 세트의 화면에 30초 뜬다.
    ///   층 전체에 하나만이고, 떠 있는 동안 들어온 것은 버린다(미루지 않는다 — 지나간 "지금" 이다).
    /// - 순위는 관람 기록 30일치를 전시물 단위로 센다. 핵심 · 자세히 마커는 같은 질문이라 합친다.
    /// </summary>
    public class WallQuestionDirector
    {
        const int WeekDays = 7;
        const int MonthDays = 30;
        const float EmptyRetrySeconds = 10f;

        readonly QuestionWallConfig _config;
        readonly int _floor;

        readonly List<WallQuestion> _questions;
        readonly Dictionary<int, WallQuestion> _byNumber = new Dictionary<int, WallQuestion>();
        readonly List<QuestionScreen> _screens = new List<QuestionScreen>();
        readonly Dictionary<QuestionKind, List<string>> _titles = new Dictionary<QuestionKind, List<string>>();

        readonly Dictionary<int, int> _week = new Dictionary<int, int>();
        readonly Dictionary<int, int> _month = new Dictionary<int, int>();
        readonly List<ViewRecord> _records = new List<ViewRecord>();

        bool _dirty = true;
        float _lastCountedAt = float.NegativeInfinity;

        // 메인 스레드 이벤트라 잠글 것은 없다. 한 틱 사이에 여럿 오면 최신 하나만 남는다.
        bool _hasPending;
        ViewRecord _pending;

        public IReadOnlyList<QuestionScreen> Screens => _screens;
        public int QuestionCount => _questions.Count;

        public WallQuestionDirector(QuestionWallConfig config, int floor, string contentRoot, IReadOnlyList<WallScreen> walls)
        {
            _config = config;
            _floor = floor;

            _questions = WallQuestionCatalog.Scan(contentRoot);
            foreach (var q in _questions) _byNumber[q.number] = q;

            foreach (var wall in walls) _screens.Add(new QuestionScreen(wall));

            ResolveTitles();

            int tagCount = 0;
            foreach (var q in _questions) tagCount += q.tagPaths.Count;
            Debug.Log($"[Wall] {floor}층 질문 {_questions.Count}개 · 해시태그 {tagCount}장을 읽었습니다.");
            if (_questions.Count == 0)
                Debug.LogWarning("[Wall] 띄울 질문이 없어 벽이 비어 있습니다. floor_<N>/<마커>/question/question.png 를 확인하세요.");

            ViewLog.Recorded += OnRecorded;
            ViewLog.DayRewritten += OnDayRewritten;
            ViewLog.Started += OnStarted;
        }

        public void Dispose()
        {
            ViewLog.Recorded -= OnRecorded;
            ViewLog.DayRewritten -= OnDayRewritten;
            ViewLog.Started -= OnStarted;
        }

        void OnRecorded(ViewRecord record) => _dirty = true;
        void OnDayRewritten(DateTime day) => _dirty = true;

        void OnStarted(ViewRecord record)
        {
            _pending = record;
            _hasPending = true;
        }

        void ResolveTitles()
        {
            string folder = Path.Combine(Application.streamingAssetsPath, _config.titleFolder);

            foreach (QuestionKind kind in Enum.GetValues(typeof(QuestionKind)))
            {
                var paths = new List<string>();
                foreach (string file in _config.TitleFiles(kind.ToString()))
                {
                    if (string.IsNullOrWhiteSpace(file)) continue;

                    string path = Path.Combine(folder, file.Trim());
                    if (File.Exists(path)) paths.Add(path);
                    else Debug.LogWarning($"[Wall] 분류 문구 그림이 없습니다({kind}): {path}");
                }
                _titles[kind] = paths;
            }
        }

        /// <summary>매 프레임 부른다(Time.unscaledTime).</summary>
        public void Tick(float now)
        {
            if (_questions.Count == 0 || _screens.Count == 0) return;

            if (_dirty || now - _lastCountedAt >= _config.refreshSeconds)
            {
                _dirty = false;
                _lastCountedAt = now;
                Count();
            }

            if (_hasPending)
            {
                _hasPending = false;
                TryShowLive(_pending, now);
            }

            for (int s = 0; s < _screens.Count; s++)
            {
                var screen = _screens[s];
                for (int i = 0; i < screen.slots.Length; i++)
                {
                    // 처음엔 expiresAt 이 0 이라 첫 틱에 채워진다.
                    if (now >= screen.slots[i].expiresAt) Fill(s, i, now);
                }
            }
        }

        // ── 관람 수 ────────────────────────────────────────────────────────────

        /// <summary>최근 7일 · 30일 관람 수를 전시물 단위로 센다. 30일치를 한 번만 읽으며 두 표를 같이 채운다.</summary>
        void Count()
        {
            _week.Clear();
            _month.Clear();
            DateTime today = DateTime.Now.Date;

            for (int day = 0; day < MonthDays; day++)
            {
                _records.Clear();
                ViewLog.ReadDay(today.AddDays(-day), _records);

                for (int k = 0; k < _records.Count; k++)
                {
                    var record = _records[k];
                    if (record.floor != _floor) continue;

                    int number = WallQuestionCatalog.ExhibitOf(record.markerId);
                    Bump(_month, number);
                    if (day < WeekDays) Bump(_week, number);
                }
            }
        }

        static void Bump(Dictionary<int, int> counts, int key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        /// <summary>가장 많이 본 전시물. 기록이 없으면 null. 동점이면 번호가 작은 쪽 — 매번 다른 것이 뜨는 것보다 낫다.</summary>
        WallQuestion Top(Dictionary<int, int> counts)
        {
            WallQuestion best = null;
            int bestCount = 0;

            foreach (var pair in counts)
            {
                if (!_byNumber.TryGetValue(pair.Key, out var q)) continue;
                if (pair.Value > bestCount || (pair.Value == bestCount && best != null && q.number < best.number))
                {
                    bestCount = pair.Value;
                    best = q;
                }
            }

            return best;
        }

        // ── 칸 채우기 ──────────────────────────────────────────────────────────

        void Fill(int screenIndex, int slotIndex, float now)
        {
            var screen = _screens[screenIndex];
            var other = screen.slots[1 - slotIndex];

            var kinds = new List<QuestionKind> { QuestionKind.Random, QuestionKind.Least };

            QuestionKind? rank = RankOf(screen.config);
            if (rank.HasValue)
            {
                var top = Top(rank.Value == QuestionKind.Week ? _week : _month);
                if (top != null && !IsShown(top, screenIndex, slotIndex)) kinds.Add(rank.Value);
            }

            // 한 화면의 두 칸이 같은 조건 분류를 갖지 않게. 랜덤은 문구가 넷이라 겹쳐도 문구로 갈린다.
            if (other.question != null && other.kind != QuestionKind.Random) kinds.Remove(other.kind);

            Shuffle(kinds);

            foreach (var kind in kinds)
            {
                var question = Pick(kind, screenIndex, slotIndex);
                if (question == null) continue;

                float duration = UnityEngine.Random.Range(_config.minRotateSeconds, _config.maxRotateSeconds);

                // 첫 채움에서만 두 번째 칸을 절반으로 — 두 질문이 같은 순간에 바뀌지 않게 시차를 만든다.
                if (screen.slots[slotIndex].version == 0 && slotIndex == 1) duration *= 0.5f;

                Assign(screenIndex, slotIndex, kind, question, now, duration);
                return;
            }

            // 띄울 것이 없다(질문이 두세 개뿐인 층 등). 비워 두고 잠시 뒤 다시 본다.
            var slot = screen.slots[slotIndex];
            if (slot.question != null)
            {
                slot.question = null;
                slot.titlePath = null;
                slot.version++;
                RebuildTags(screen);
            }
            slot.expiresAt = now + EmptyRetrySeconds;
        }

        WallQuestion Pick(QuestionKind kind, int screenIndex, int slotIndex)
        {
            switch (kind)
            {
                case QuestionKind.Week:
                case QuestionKind.Month:
                {
                    var top = Top(kind == QuestionKind.Week ? _week : _month);
                    return top != null && !IsShown(top, screenIndex, slotIndex) ? top : null;
                }

                case QuestionKind.Least:
                {
                    // 최근 7일 선택이 가장 적은 것들 중 하나. 기록이 없으면 전부 0회라 전체 중 무작위가 된다.
                    int least = int.MaxValue;
                    var pool = new List<WallQuestion>();

                    foreach (var q in _questions)
                    {
                        if (IsShown(q, screenIndex, slotIndex)) continue;

                        _week.TryGetValue(q.number, out int n);
                        if (n < least)
                        {
                            least = n;
                            pool.Clear();
                        }
                        if (n == least) pool.Add(q);
                    }

                    return pool.Count > 0 ? pool[UnityEngine.Random.Range(0, pool.Count)] : null;
                }

                default:
                {
                    var pool = new List<WallQuestion>();
                    foreach (var q in _questions)
                        if (!IsShown(q, screenIndex, slotIndex)) pool.Add(q);

                    return pool.Count > 0 ? pool[UnityEngine.Random.Range(0, pool.Count)] : null;
                }
            }
        }

        void Assign(int screenIndex, int slotIndex, QuestionKind kind, WallQuestion question, float now, float duration)
        {
            var screen = _screens[screenIndex];
            var slot = screen.slots[slotIndex];
            var other = screen.slots[1 - slotIndex];

            slot.kind = kind;
            slot.question = question;
            slot.titlePath = PickTitle(kind, other.question != null ? other.titlePath : null);
            slot.shownAt = now;
            slot.expiresAt = now + duration;
            slot.version++;

            RebuildTags(screen);

            Debug.Log($"[Wall] 화면 {screenIndex + 1} · 칸 {slotIndex + 1} ← {kind} · 전시물 {question.number} ({duration:0}초)");
        }

        /// <summary>그 분류의 문구 그림 하나. 같은 화면의 다른 칸과 같은 그림은 피한다.</summary>
        string PickTitle(QuestionKind kind, string avoid)
        {
            if (!_titles.TryGetValue(kind, out var paths) || paths.Count == 0) return null;

            var pool = paths.FindAll(p => p != avoid);
            if (pool.Count == 0) pool = paths;
            return pool[UnityEngine.Random.Range(0, pool.Count)];
        }

        /// <summary>두 질문의 해시태그를 합쳐(같은 글자는 하나) 전부 띄운다. maxTags 보다 많으면 무작위로 자른다.</summary>
        void RebuildTags(QuestionScreen screen)
        {
            var keys = new List<string>();
            var paths = new List<string>();

            foreach (var slot in screen.slots)
            {
                if (slot.question == null) continue;
                for (int i = 0; i < slot.question.tagPaths.Count; i++)
                {
                    if (keys.Contains(slot.question.tagKeys[i])) continue;
                    keys.Add(slot.question.tagKeys[i]);
                    paths.Add(slot.question.tagPaths[i]);
                }
            }

            Shuffle(paths);
            int count = Mathf.Min(paths.Count, _config.maxTags);

            screen.tags.Clear();
            for (int i = 0; i < count; i++) screen.tags.Add(paths[i]);
            screen.tagVersion++;
        }

        // ── 지금 누군가가 궁금해한 질문 ─────────────────────────────────────────

        void TryShowLive(ViewRecord record, float now)
        {
            // 층 전체에 하나만. 떠 있는 동안 들어온 것은 버린다.
            foreach (var screen in _screens)
                foreach (var slot in screen.slots)
                    if (slot.question != null && slot.kind == QuestionKind.Live) return;

            if (record.floor != _floor) return;
            if (!_byNumber.TryGetValue(WallQuestionCatalog.ExhibitOf(record.markerId), out var question)) return;

            int screenIndex = _screens.FindIndex(s => s.config.ShowsLiveFrom(record.set));
            if (screenIndex < 0) return;

            var target = _screens[screenIndex];

            // 같은 질문이 이 화면에 있으면 그 칸을, 없으면 더 오래된 칸(빈 칸이 있으면 빈 칸)을 바꾼다.
            int slotIndex = Array.FindIndex(target.slots, s => s.question == question);
            if (slotIndex < 0)
            {
                var a = target.slots[0];
                var b = target.slots[1];
                slotIndex = a.question == null ? 0
                          : b.question == null ? 1
                          : a.shownAt <= b.shownAt ? 0 : 1;
            }

            // 다른 화면에 같은 질문이 떠 있으면 그 칸은 곧바로 다른 질문으로 바뀌게 한다.
            for (int s = 0; s < _screens.Count; s++)
            {
                if (s == screenIndex) continue;
                foreach (var slot in _screens[s].slots)
                    if (slot.question == question) slot.expiresAt = now;
            }

            Assign(screenIndex, slotIndex, QuestionKind.Live, question, now, _config.liveSeconds);
        }

        // ── 보조 ──────────────────────────────────────────────────────────────

        static QuestionKind? RankOf(WallScreen wall)
        {
            if (wall == null || string.IsNullOrWhiteSpace(wall.rank)) return null;
            if (string.Equals(wall.rank.Trim(), "week", StringComparison.OrdinalIgnoreCase)) return QuestionKind.Week;
            if (string.Equals(wall.rank.Trim(), "month", StringComparison.OrdinalIgnoreCase)) return QuestionKind.Month;
            return null;
        }

        /// <summary>그 질문이 어느 화면에든 떠 있는가. 지금 바꾸려는 칸은 빼고 본다.</summary>
        bool IsShown(WallQuestion question, int exceptScreen, int exceptSlot)
        {
            for (int s = 0; s < _screens.Count; s++)
            {
                var slots = _screens[s].slots;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (s == exceptScreen && i == exceptSlot) continue;
                    if (slots[i].question == question) return true;
                }
            }
            return false;
        }

        static void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
