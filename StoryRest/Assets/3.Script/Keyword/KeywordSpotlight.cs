using System;
using System.Collections.Generic;
using StoryRest.ArUco;
using StoryRest.Stats;
using UnityEngine;

namespace StoryRest.Keyword
{
    /// <summary>월 왼쪽 위에 한 번에 하나씩 뜨는 카드. 제목과 그림. 그림이 없는 마커는 카드가 되지 않는다.</summary>
    public class SpotlightCard
    {
        public string label;        // "오늘의 추천" 같은 카드 이름
        public string topic;        // 주제 이름(마커 폴더 이름에서 번호를 뗀 것). 폴더에 제목이 없으면 null
        public int views;           // 인기 카드의 관람 횟수. 추천은 0
        public int markerId;
        public string imagePath;    // 그 마커의 sprite/ 첫 그림
    }

    /// <summary>
    /// 월에 띄울 "오늘의 추천 · 오늘 / 이번 주 / 이달의 인기 주제" 카드를 만든다(→ SPEC §3.1).
    ///
    /// 재료는 관람 기록(ViewLog)이다. 월 PC 는 ArUco PC 에서 받아 적은 미러를 읽으므로 어느 역할이든 같은 코드다.
    /// 세는 기준은 **관람 횟수**(건수)다. 시간 합계로 세면 한 사람이 오래 본 주제가 여럿이 잠깐씩 본 주제를 이긴다.
    /// 카드는 그림이 있어야 성립하므로, 순위에서는 **그림이 있는 마커 중** 가장 많이 본 것을 고른다.
    ///
    /// 파일을 매번 읽는다 — 최근 30일치라 해도 몇 MB 를 넘지 않고, 다시 읽는 주기가 분 단위라 부담이 없다.
    /// 그 대신 자정을 넘기거나 미러가 통째로 바뀌어도 따로 처리할 것이 없다.
    /// </summary>
    public class KeywordSpotlight
    {
        const int WeekDays = 7;
        const int MonthDays = 30;

        readonly SpotlightConfig _config;
        readonly string _contentRoot;

        // 마커 ID → 폴더 이름. 주제 이름의 출처다. 스프라이트 유무와 무관하게 번호 폴더 전부.
        Dictionary<int, string> _folderNames = new Dictionary<int, string>();

        // 마커 ID → 그 폴더의 첫 그림. 카드 그림으로 쓴다.
        Dictionary<int, string> _images = new Dictionary<int, string>();

        readonly List<SpotlightCard> _cards = new List<SpotlightCard>();
        readonly List<ViewRecord> _records = new List<ViewRecord>();

        bool _dirty = true;
        float _lastRebuildTime = float.NegativeInfinity;

        /// <summary>지금 보여 줄 카드들. 재료가 없는 카드(기록 없는 날 등)는 빠져 있다.</summary>
        public IReadOnlyList<SpotlightCard> Cards => _cards;

        /// <summary>카드 목록이 바뀔 때마다 1씩 오른다. 화면 쪽이 "내가 든 카드가 아직 유효한가" 를 이것으로 안다.</summary>
        public int Version { get; private set; }

        public KeywordSpotlight(SpotlightConfig config, string contentRoot)
        {
            _config = config;
            _contentRoot = contentRoot;

            ScanFolders();

            // 한 건이 적힐 때마다 파일을 다시 읽으면 관람이 몰릴 때 낭비다. 표시만 해 두고 다음 틱에 한 번 읽는다.
            ViewLog.Recorded += OnRecorded;
            ViewLog.DayRewritten += OnDayRewritten;
        }

        public void Dispose()
        {
            ViewLog.Recorded -= OnRecorded;
            ViewLog.DayRewritten -= OnDayRewritten;
        }

        void OnRecorded(ViewRecord record) => _dirty = true;
        void OnDayRewritten(DateTime day) => _dirty = true;

        /// <summary>매 프레임 부른다. 다시 셀 때가 됐으면 센다. 카드가 바뀌었으면 true.</summary>
        public bool Tick()
        {
            float now = Time.unscaledTime;
            if (!_dirty && now - _lastRebuildTime < Mathf.Max(5f, _config.refreshSeconds)) return false;

            _dirty = false;
            _lastRebuildTime = now;
            return Rebuild();
        }

        void ScanFolders()
        {
            _folderNames = ArUcoContentIndex.ScanFolderNames(_contentRoot);

            _images.Clear();
            foreach (var folder in ArUcoContentIndex.ScanSprites(_contentRoot))
            {
                if (!_images.ContainsKey(folder.markerId)) _images[folder.markerId] = folder.files[0];
            }
        }

        bool Rebuild()
        {
            var fresh = new List<SpotlightCard>(4);
            DateTime today = DateTime.Now.Date;

            var recommend = Recommend(today);
            if (recommend != null) fresh.Add(recommend);

            // 30일치를 한 번만 읽으면서 오늘 · 7일 · 30일 표를 같이 채운다. 오늘 파일을 세 번 읽을 이유가 없다.
            var todayCounts = new Dictionary<int, int>();
            var weekCounts = new Dictionary<int, int>();
            var monthCounts = new Dictionary<int, int>();

            for (int i = 0; i < MonthDays; i++)
            {
                _records.Clear();
                ViewLog.ReadDay(today.AddDays(-i), _records);

                for (int k = 0; k < _records.Count; k++)
                {
                    int id = _records[k].markerId;
                    Bump(monthCounts, id);
                    if (i < WeekDays) Bump(weekCounts, id);
                    if (i == 0) Bump(todayCounts, id);
                }
            }

            AddTop(fresh, _config.todayLabel, todayCounts);
            AddTop(fresh, _config.weekLabel, weekCounts);
            AddTop(fresh, _config.monthLabel, monthCounts);

            if (SameAs(fresh)) return false;

            _cards.Clear();
            _cards.AddRange(fresh);
            Version++;
            return true;
        }

        /// <summary>
        /// 오늘의 추천. 날짜를 씨앗으로 골라 하루 동안은 같은 주제가 뜬다 — 관람객이 오전에 본 추천이 오후에 바뀌어 있으면
        /// "추천" 이 아니라 무작위처럼 보인다. 그림이 있는 폴더 중에서 고른다 — 그림이 하나도 없으면 추천 카드도 없다.
        /// </summary>
        SpotlightCard Recommend(DateTime today)
        {
            var pool = new List<int>(_images.Keys);
            if (pool.Count == 0) return null;

            pool.Sort();
            var random = new System.Random(today.Year * 10000 + today.Month * 100 + today.Day);
            int markerId = pool[random.Next(pool.Count)];

            return MakeCard(_config.recommendLabel, markerId, 0);
        }

        /// <summary>그림이 있는 마커 중 가장 많이 본 것. 그림 없는 마커가 1위여도 건너뛴다 — 카드에 실을 것이 없다.</summary>
        void AddTop(List<SpotlightCard> into, string label, Dictionary<int, int> counts)
        {
            int bestId = -1;
            int best = 0;

            foreach (var pair in counts)
            {
                if (!_images.ContainsKey(pair.Key)) continue;

                // 동점이면 번호가 작은 쪽. 매번 다른 주제가 뜨는 것보다 낫다.
                if (pair.Value > best || (pair.Value == best && pair.Key < bestId))
                {
                    best = pair.Value;
                    bestId = pair.Key;
                }
            }

            if (bestId < 0) return;

            into.Add(MakeCard(label, bestId, best));
        }

        SpotlightCard MakeCard(string label, int markerId, int views)
        {
            return new SpotlightCard
            {
                label = label,
                topic = TopicName(markerId),
                views = views,
                markerId = markerId,
                imagePath = _images[markerId],
            };
        }

        static void Bump(Dictionary<int, int> counts, int id)
        {
            counts.TryGetValue(id, out int n);
            counts[id] = n + 1;
        }

        /// <summary>
        /// 폴더 이름에서 주제 이름을 뽑는다. "17_별의일생" → "별의일생", "017 별의 일생" → "별의 일생".
        /// 번호만 있는 폴더("17")는 null — "17번 주제" 같은 글은 관람객에게 아무 뜻이 없어 아예 안 쓴다.
        /// </summary>
        string TopicName(int markerId)
        {
            if (!_folderNames.TryGetValue(markerId, out string folder)) return null;

            int i = 0;
            while (i < folder.Length && char.IsDigit(folder[i])) i++;
            while (i < folder.Length && (folder[i] == '_' || folder[i] == '-' || folder[i] == '.' || char.IsWhiteSpace(folder[i]))) i++;

            string name = folder.Substring(i).Trim();
            return name.Length > 0 ? name : null;
        }

        bool SameAs(List<SpotlightCard> other)
        {
            if (other.Count != _cards.Count) return false;

            for (int i = 0; i < other.Count; i++)
            {
                var a = _cards[i];
                var b = other[i];
                if (a.label != b.label || a.markerId != b.markerId || a.views != b.views || a.topic != b.topic) return false;
            }

            return true;
        }
    }
}
