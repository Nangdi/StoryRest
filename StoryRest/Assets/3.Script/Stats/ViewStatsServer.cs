using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace StoryRest.Stats
{
    /// <summary>
    /// 관람 기록을 월 PC 로 보내는 서버. role=aruco 일 때 StoryRestApp 이 붙인다.
    ///
    /// 한 줄짜리 텍스트 프로토콜이다. CSV 에 적는 줄을 그대로 실어 보내므로 형식이 둘이 아니다.
    ///
    ///   클라이언트 → 서버   sync 2026-09-11              그 날 파일을 통째로 달라
    ///   서버 → 클라이언트   view 2026-09-11T14:03:27,2,A,17,42.5   한 건 (sync 응답이든 방금 적힌 것이든 같다)
    ///                      synced 2026-09-11 37          sync 응답 끝 · 보낸 줄 수
    ///
    /// 서버가 원본이고 월 PC 는 미러다. 월 PC 가 재시작하거나 선이 빠졌다 돌아오면 sync 로 다시 맞추면 되므로
    /// 서버는 누가 무엇을 받았는지 기억하지 않는다. 연결이 없으면 그냥 파일에만 적힌다 — 잃는 것이 없다.
    ///
    /// 클라이언트 수를 제한하지 않는다. 월 PC 는 한 대지만, 점검용 노트북을 잠깐 붙여 볼 수도 있다.
    /// </summary>
    public class ViewStatsServer : MonoBehaviour
    {
        class Client
        {
            public TcpClient tcp;
            public StreamWriter writer;
            public readonly object writeLock = new object();
            public string name;
        }

        int _port;
        TcpListener _listener;
        CancellationTokenSource _cts;

        readonly List<Client> _clients = new List<Client>();
        readonly object _clientsLock = new object();

        public int Port => _port;

        /// <summary>지금 붙어 있는 클라이언트 수. HUD · 설정창 표시용.</summary>
        public int ClientCount
        {
            get { lock (_clientsLock) return _clients.Count; }
        }

        public void Setup(int port)
        {
            _port = port;
        }

        void Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
            }
            catch (Exception e)
            {
                Debug.LogError($"[Stats] 관람 기록 서버를 {_port} 포트에 열지 못했습니다. 월 PC 가 기록을 받지 못합니다.\n{e.Message}");
                _listener = null;
                return;
            }

            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_cts.Token);

            ViewLog.Recorded += OnRecorded;
            Debug.Log($"[Stats] 관람 기록 서버 대기 중 — 포트 {_port}");
        }

        void OnDestroy()
        {
            ViewLog.Recorded -= OnRecorded;

            try { _cts?.Cancel(); } catch { /* 이미 정리됨 */ }
            _cts?.Dispose();
            _cts = null;

            try { _listener?.Stop(); } catch { /* ignore */ }
            _listener = null;

            lock (_clientsLock)
            {
                foreach (var c in _clients) Close(c);
                _clients.Clear();
            }
        }

        // 메인 스레드 — ViewLog.Append 가 카운터(메인 스레드)에서 불리므로 여기도 메인 스레드다.
        void OnRecorded(ViewRecord record)
        {
            Broadcast("view " + ViewLog.Format(record));
        }

        async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException)
                {
                    break; // Stop() 으로 닫힘
                }
                catch (Exception e)
                {
                    if (token.IsCancellationRequested) break;
                    Debug.LogWarning($"[Stats] 클라이언트 수락 실패: {e.Message}");
                    continue;
                }

                var client = new Client
                {
                    tcp = tcp,
                    name = tcp.Client.RemoteEndPoint?.ToString() ?? "?",
                };

                try
                {
                    tcp.NoDelay = true;
                    client.writer = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { AutoFlush = false };
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Stats] 클라이언트 {client.name} 스트림을 열지 못했습니다: {e.Message}");
                    Close(client);
                    continue;
                }

                lock (_clientsLock) _clients.Add(client);
                Debug.Log($"[Stats] 월 PC 연결됨 — {client.name} (연결 {ClientCount}대)");

                _ = ServeAsync(client, token);
            }
        }

        // 클라이언트 하나의 수신 루프. sync 요청만 받는다.
        async Task ServeAsync(Client client, CancellationToken token)
        {
            try
            {
                using (var reader = new StreamReader(client.tcp.GetStream(), Encoding.UTF8))
                {
                    while (!token.IsCancellationRequested)
                    {
                        string line = await reader.ReadLineAsync();
                        if (line == null) break; // 상대가 끊음

                        Handle(client, line.Trim());
                    }
                }
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                    Debug.LogWarning($"[Stats] 클라이언트 {client.name} 수신 오류: {e.Message}");
            }
            finally
            {
                lock (_clientsLock) _clients.Remove(client);
                Close(client);
                Debug.Log($"[Stats] 월 PC 연결 끊김 — {client.name} (연결 {ClientCount}대)");
            }
        }

        // 백그라운드 스레드. 파일 읽기는 정적 함수라 메인 스레드가 필요 없다.
        void Handle(Client client, string line)
        {
            if (!line.StartsWith("sync ")) return;

            if (!DateTime.TryParseExact(line.Substring(5).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out var day))
            {
                Debug.LogWarning($"[Stats] 알 수 없는 sync 요청: {line}");
                return;
            }

            var lines = new List<string>();
            ViewLog.ReadDayLines(day, lines);

            // 한 날의 응답은 한 번에 보낸다 — 중간에 방금 적힌 view 가 끼어들지 않게 잠근 채로 쓴다.
            lock (client.writeLock)
            {
                if (client.writer == null) return;

                try
                {
                    for (int i = 0; i < lines.Count; i++) client.writer.WriteLine("view " + lines[i]);
                    client.writer.WriteLine($"synced {day:yyyy-MM-dd} {lines.Count}");
                    client.writer.Flush();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Stats] {client.name} 에 sync 응답을 보내지 못했습니다: {e.Message}");
                }
            }
        }

        void Broadcast(string line)
        {
            Client[] snapshot;
            lock (_clientsLock) snapshot = _clients.ToArray();

            for (int i = 0; i < snapshot.Length; i++)
            {
                var client = snapshot[i];

                lock (client.writeLock)
                {
                    if (client.writer == null) continue;

                    try
                    {
                        client.writer.WriteLine(line);
                        client.writer.Flush();
                    }
                    catch (Exception e)
                    {
                        // 수신 루프가 끊김을 알아채고 정리한다. 여기서는 로그만 남긴다.
                        Debug.LogWarning($"[Stats] {client.name} 에 보내지 못했습니다: {e.Message}");
                    }
                }
            }
        }

        static void Close(Client client)
        {
            lock (client.writeLock)
            {
                try { client.writer?.Dispose(); } catch { /* ignore */ }
                client.writer = null;
            }

            try { client.tcp?.Close(); } catch { /* ignore */ }
            client.tcp = null;
        }
    }
}
