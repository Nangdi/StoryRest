using System;
using System.Collections.Generic;
using StoryRest.Stats;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 마커가 "관람됐다" 고 셀지 정한다. 세트마다 하나씩 갖는다.
    ///
    /// 관람 1회 = 영상이 끊기지 않고 이어진 한 구간. 영상 재생 유지(ArUcoContentLibrary)와 같은 시간 기준을 쓰므로
    /// "한 번 세어진 관람" 과 "한 번 이어서 재생된 영상" 이 늘 같은 것을 가리킨다(→ SPEC §5).
    ///
    /// - 마커가 처음 잡힌 뒤 minDwell 동안 유지되어야 관람으로 센다.
    ///   1초도 안 돼 사라지는 것은 책장을 넘기다 스친 것이거나 오인식이다.
    /// - 세는 중에 마커를 놓쳐도 resumeGrace 안에 돌아오면 같은 관람이다.
    ///   손이 마커를 가리거나 조명이 튀어 잠깐 못 잡는 것은 흔하다 — 이것을 따로 세면 한 사람이 여러 번으로 잡힌다.
    /// - resumeGrace 를 넘기면 관람이 끝난 것이다. 그때 지속시간과 함께 한 건을 기록한다.
    ///   다음에 잡히면 새 관람이다.
    /// - 영상이 끝나 루프로 다시 도는 것은 여기 아무 영향이 없다. 세는 것은 마커가 놓여 있던 구간이지
    ///   영상이 몇 번 돌았는지가 아니다 — 한 사람이 오래 보면 몇 번을 돌아도 1회다.
    /// </summary>
    public class ArUcoViewCounter
    {
        class Session
        {
            public DateTime startedAt;   // 기록에 남길 시각
            public float firstSeen;      // 아래 둘은 Time.unscaledTime
            public float lastSeen;
            public bool counted;         // minDwell 을 넘겨 관람으로 확정됐는지
        }

        readonly int _floor;
        readonly string _setName;
        float _minDwell;
        float _resumeGrace;

        /// <summary>설정 패널에서 바꾸면 다음 판정부터 바로 적용된다.</summary>
        public float MinDwellSeconds
        {
            get => _minDwell;
            set => _minDwell = Mathf.Max(0f, value);
        }

        public float ResumeGraceSeconds
        {
            get => _resumeGrace;
            set => _resumeGrace = Mathf.Max(0f, value);
        }

        readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
        readonly List<int> _ended = new List<int>();

        /// <summary>이번 실행에서 센 관람 수. 편집모드 HUD 에서 "세고 있는지" 확인하는 용도다.</summary>
        public int CountedThisRun { get; private set; }

        /// <summary>디버그 패널용 — 지금 잡혀 있는 마커 하나의 상태.</summary>
        public struct LiveView
        {
            public int markerId;
            public float elapsed;     // 처음 잡힌 뒤 지난 시간
            public float sinceSeen;   // 마지막으로 보인 뒤 지난 시간. 0 이면 이번 프레임에도 보였다
            public bool counted;
        }

        /// <summary>진행 중인 관람을 목록에 담는다(디버그 패널용). 마커 ID 순.</summary>
        public void GetLive(List<LiveView> into, float now)
        {
            foreach (var pair in _sessions)
            {
                var s = pair.Value;
                into.Add(new LiveView
                {
                    markerId = pair.Key,
                    elapsed = now - s.firstSeen,
                    sinceSeen = now - s.lastSeen,
                    counted = s.counted,
                });
            }

            into.Sort((a, b) => a.markerId.CompareTo(b.markerId));
        }

        public ArUcoViewCounter(int floor, string setName, float minDwellSeconds, float resumeGraceSeconds)
        {
            _floor = floor;
            _setName = setName;
            MinDwellSeconds = minDwellSeconds;
            ResumeGraceSeconds = resumeGraceSeconds;
        }

        /// <summary>매 프레임, 이번 프레임에 잡힌 콘텐츠 마커 ID 들로 호출한다.</summary>
        public void Update(IReadOnlyList<int> visibleIds, float now)
        {
            for (int i = 0; i < visibleIds.Count; i++)
            {
                int id = visibleIds[i];

                if (!_sessions.TryGetValue(id, out var session))
                {
                    session = new Session { startedAt = DateTime.Now, firstSeen = now };
                    _sessions[id] = session;
                }

                session.lastSeen = now;

                if (!session.counted && now - session.firstSeen >= _minDwell)
                {
                    session.counted = true;
                    CountedThisRun++;
                }
            }

            // 놓친 지 resumeGrace 가 지난 것은 끝난 관람이다. 보이는 것은 방금 lastSeen 을 갱신했으므로 걸리지 않는다.
            _ended.Clear();

            foreach (var pair in _sessions)
            {
                if (now - pair.Value.lastSeen > _resumeGrace) _ended.Add(pair.Key);
            }

            for (int i = 0; i < _ended.Count; i++)
            {
                int id = _ended[i];
                End(id, _sessions[id]);
                _sessions.Remove(id);
            }
        }

        /// <summary>
        /// 종료할 때 호출한다. 진행 중이던 관람을 지금까지의 시간으로 기록한다.
        /// 전원이 끊기면 여기까지 오지 못하므로, 그 순간 보고 있던 것만 잃는다.
        /// </summary>
        public void Flush()
        {
            foreach (var pair in _sessions) End(pair.Key, pair.Value);
            _sessions.Clear();
        }

        void End(int markerId, Session session)
        {
            if (!session.counted) return;

            float seconds = session.lastSeen - session.firstSeen;
            var record = new ViewRecord(session.startedAt, _floor, _setName, markerId, seconds);

            if (ViewLog.Append(record))
                Debug.Log($"[Stats] 관람 기록 — 세트 {_setName} · 마커 {markerId} · {seconds:0.0}초");
        }
    }
}
