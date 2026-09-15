using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace StoryRest.Stats
{
    /// <summary>
    /// ArUco PC 의 관람 기록을 받아 이 PC 의 stats/ 폴더에 미러로 적는다. role=wall 일 때 StoryRestApp 이 붙인다.
    ///
    /// 받은 것을 자기 파일에 적어 두는 이유 — 연출 쪽이 역할을 몰라도 되게 하기 위해서다.
    /// all 이든 wall 이든 ViewLog.ReadDay / Recorded / DayRewritten 만 보면 같은 것이 보인다.
    /// 파일로 남기므로 ArUco PC 가 잠깐 꺼져 있어도 지난 순위는 계속 낼 수 있다.
    ///
    /// 연결될 때마다 최근 SyncDays 일치를 통째로 다시 받는다(→ ViewStatsServer 프로토콜).
    /// 끊긴 사이에 쌓인 것은 이걸로 따라잡고, 그 뒤로는 한 건씩 실시간으로 받아 append 한다.
    /// 재접속 · 채널 처리는 기존 TcpClientChannel 을 그대로 쓴다.
    /// </summary>
    public class ViewStatsClient : MonoBehaviour
    {
        /// <summary>연결될 때 다시 맞추는 날 수. "한 달 순위" 를 낼 수 있는 만큼.</summary>
        public const int SyncDays = 31;

        const float ReconnectSeconds = 5f;

        // sync 응답을 모으는 중인 날. synced 가 오면 파일로 쓴다.
        class DaySync
        {
            public DateTime day;
            public readonly List<string> lines = new List<string>();
            public readonly HashSet<string> seen = new HashSet<string>();
        }

        string _host;
        int _port;
        TcpClientChannel _channel;

        // 채널 콜백은 백그라운드 스레드에서 온다. 파일 쓰기와 이벤트는 메인 스레드에서 해야 하므로 큐로 넘긴다.
        readonly ConcurrentQueue<string> _received = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<bool> _connection = new ConcurrentQueue<bool>();

        readonly Dictionary<string, DaySync> _syncing = new Dictionary<string, DaySync>();

        /// <summary>
        /// 주고받은 것 한 줄씩 — "→ 내용" 보냄, "← 내용" 받음, 연결/끊김. 메인 스레드에서 난다.
        /// sync 로 받는 수백 줄은 synced 가 올 때 한 줄로 접는다. ESC 의 TCP 패널이 구독한다.
        /// </summary>
        public event Action<string> Traffic;

        public bool IsConnected { get; private set; }
        public string Host => _host;
        public int Port => _port;

        /// <summary>마지막으로 한 건을 받은 시각(Time.unscaledTime). 0 이면 아직 없음.</summary>
        public float LastReceivedAt { get; private set; }

        /// <summary>마지막으로 받은 줄. "지금 뭐가 들어왔나" 를 눈으로 맞춰 보는 용도.</summary>
        public string LastReceivedLine { get; private set; } = "";

        /// <summary>이번 실행에서 실시간으로 받아 적은 건수(sync 로 받은 것은 세지 않는다).</summary>
        public int ReceivedThisRun { get; private set; }

        /// <summary>아직 synced 가 오지 않은 날 수. 0 이면 재동기화가 끝난 것.</summary>
        public int SyncPendingDays => _syncing.Count;

        /// <summary>마지막 재동기화로 받아 맞춘 건수(31일 합계)와 그 시각.</summary>
        public int LastSyncLines { get; private set; }
        public float LastSyncedAt { get; private set; }

        /// <summary>이번 실행에서 연결이 된 횟수. 1보다 크면 그 사이에 끊긴 적이 있다.</summary>
        public int ConnectCount { get; private set; }

        public void Setup(string host, int port)
        {
            _host = host;
            _port = port;
        }

        void Start()
        {
            _channel = new TcpClientChannel(_host, _port, ReconnectSeconds, appendOutgoingLineEnding: true,
                onMessage: line => _received.Enqueue(line),
                onConnectionChanged: connected => _connection.Enqueue(connected));
            _channel.Start();

            Debug.Log($"[Stats] 관람 기록 클라이언트 — ArUco PC {_host}:{_port} 에 접속 시도");
        }

        void OnDestroy()
        {
            _channel?.Stop();
            _channel = null;
        }

        void Update()
        {
            // 연결 상태를 먼저 처리한다. 연결되자마자 sync 를 걸어 두어야
            // 같은 프레임에 들어온 view 줄이 진행 중인 sync 에 합쳐진다.
            while (_connection.TryDequeue(out bool connected))
            {
                IsConnected = connected;

                if (connected)
                {
                    ConnectCount++;
                    Traffic?.Invoke($"<color=#7fe07f>연결됨 {_host}:{_port}</color>");
                    RequestSync();
                }
                else
                {
                    _syncing.Clear();
                    Debug.LogWarning("[Stats] ArUco PC 와 끊겼습니다. 다시 붙으면 최근 기록을 다시 받습니다.");
                    Traffic?.Invoke($"<color=#ffd633>끊김 {_host}:{_port} — {ReconnectSeconds:0}초마다 재시도</color>");
                }
            }

            while (_received.TryDequeue(out string line)) Handle(line.Trim());
        }

        void RequestSync()
        {
            _syncing.Clear();
            LastSyncLines = 0;

            var today = DateTime.Now.Date;
            for (int i = SyncDays - 1; i >= 0; i--)
            {
                var day = today.AddDays(-i);
                string key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _syncing[key] = new DaySync { day = day };
                _channel.Send("sync " + key);
            }

            Debug.Log($"[Stats] ArUco PC 연결됨 — 최근 {SyncDays}일 기록을 다시 받습니다.");
            Traffic?.Invoke($"→ sync ×{SyncDays} (최근 {SyncDays}일 요청)");
        }

        void Handle(string line)
        {
            if (line.StartsWith("view "))
            {
                HandleView(line.Substring(5));
            }
            else if (line.StartsWith("synced "))
            {
                HandleSynced(line.Substring(7));
            }
        }

        void HandleView(string csv)
        {
            if (csv.Length < 10) return;

            // 그 날의 sync 가 진행 중이면 거기에 모은다. 방금 적힌 건이 sync 응답보다 먼저 올 수 있어
            // 같은 줄이 두 번 오기도 하므로 줄 단위로 거른다.
            string key = csv.Substring(0, 10);
            if (_syncing.TryGetValue(key, out var sync))
            {
                if (sync.seen.Add(csv)) sync.lines.Add(csv);
                return;
            }

            if (!ViewLog.TryParse(csv, out var record))
            {
                Debug.LogWarning($"[Stats] 읽을 수 없는 관람 기록을 받았습니다: {csv}");
                return;
            }

            LastReceivedAt = Time.unscaledTime;
            LastReceivedLine = csv;
            ReceivedThisRun++;
            Traffic?.Invoke($"← view {csv}");
            ViewLog.Append(record); // 미러에 적고 Recorded 를 낸다 — ArUco PC 와 같은 경로
        }

        void HandleSynced(string rest)
        {
            string key = rest.Length >= 10 ? rest.Substring(0, 10) : rest;
            if (!_syncing.TryGetValue(key, out var sync)) return;
            _syncing.Remove(key);

            LastSyncLines += sync.lines.Count;
            if (_syncing.Count == 0) LastSyncedAt = Time.unscaledTime;

            // 빈 날까지 31줄을 찍으면 로그가 그것으로 차 버린다. 받은 것이 있는 날과 마지막만 남긴다.
            if (sync.lines.Count > 0)
                Traffic?.Invoke($"← view ×{sync.lines.Count} + synced {key}");
            if (_syncing.Count == 0)
                Traffic?.Invoke($"재동기화 끝 — {SyncDays}일에서 {LastSyncLines}건");

            // 서버에 없는 날은 파일을 만들지 않는다 — 31개의 빈 파일이 생기는 것을 막는다.
            if (sync.lines.Count == 0 && !File.Exists(ViewLog.FileFor(sync.day))) return;

            if (ViewLog.WriteDay(sync.day, sync.lines) && sync.lines.Count > 0)
                Debug.Log($"[Stats] {key} 기록 {sync.lines.Count}건을 받아 맞췄습니다.");
        }
    }
}
