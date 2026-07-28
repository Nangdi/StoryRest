using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// 메인 서버에 접속하는 단일 TCP 클라이언트 채널.
// 연결/재연결, 줄 단위 수신, 송신을 담당한다.
public class TcpClientChannel
{
    private const int IdlePollDelayMs = 10;

    public string Host { get; }
    public int Port { get; }
    public bool AppendOutgoingLineEnding { get; }
    public float ReconnectIntervalSeconds { get; }
    public bool IsConnected => tcpClient != null && tcpClient.Connected;

    // 수신한 한 줄을 메인 스레드로 전달하기 위한 콜백. 호출 스레드는 백그라운드.
    private readonly Action<string> onMessage;
    // 연결 상태가 바뀔 때 호출되는 콜백(true=연결됨, false=끊김). 호출 스레드는 백그라운드.
    private readonly Action<bool> onConnectionChanged;

    private TcpClient tcpClient;
    private NetworkStream networkStream;
    private StreamReader reader;
    private StreamWriter writer;
    private CancellationTokenSource cancellationTokenSource;
    private readonly object writerLock = new object();
    private bool isRunning;

    public TcpClientChannel(string host, int port, float reconnectIntervalSeconds, bool appendOutgoingLineEnding,
        Action<string> onMessage, Action<bool> onConnectionChanged)
    {
        Host = host;
        Port = port;
        ReconnectIntervalSeconds = reconnectIntervalSeconds;
        AppendOutgoingLineEnding = appendOutgoingLineEnding;
        this.onMessage = onMessage;
        this.onConnectionChanged = onConnectionChanged;
    }

    // 백그라운드에서 접속을 시도하고, 연결되면 수신 루프를 돌린다.
    public void Start()
    {
        if (isRunning)
            return;

        isRunning = true;
        cancellationTokenSource = new CancellationTokenSource();
        _ = RunAsync(cancellationTokenSource.Token);
    }

    // 송신은 메인 스레드에서 호출되는 동기 API. 짧은 메시지 위주이므로 lock으로 직렬화한다.
    public bool Send(string message)
    {
        if (string.IsNullOrEmpty(message))
            return false;

        lock (writerLock)
        {
            if (writer == null || !IsConnected)
                return false;

            try
            {
                if (AppendOutgoingLineEnding)
                    writer.WriteLine(message);
                else
                    writer.Write(message);

                writer.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TCP] 송신 실패: {ex.Message}");
                return false;
            }
        }
    }

    public void Stop()
    {
        isRunning = false;

        if (cancellationTokenSource != null)
        {
            try { cancellationTokenSource.Cancel(); }
            catch { /* 이미 dispose 됐을 수 있음 */ }
            cancellationTokenSource.Dispose();
            cancellationTokenSource = null;
        }

        CloseStream();
    }

    private async Task RunAsync(CancellationToken token)
    {
        bool wasConnected = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                tcpClient = new TcpClient();
                Debug.Log($"[TCP] 접속 시도 {Host}:{Port}");
                await tcpClient.ConnectAsync(Host, Port);

                networkStream = tcpClient.GetStream();
                reader = new StreamReader(networkStream, Encoding.UTF8);

                lock (writerLock)
                {
                    writer = new StreamWriter(networkStream, new UTF8Encoding(false));
                    writer.AutoFlush = false;
                }

                wasConnected = true;
                Debug.Log($"[TCP] 연결됨 {Host}:{Port}");
                onConnectionChanged?.Invoke(true);

                await ReceiveLoopAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    Debug.LogWarning($"[TCP] 연결/수신 오류: {ex.Message}");
            }
            finally
            {
                CloseStream();

                if (wasConnected)
                {
                    wasConnected = false;
                    onConnectionChanged?.Invoke(false);
                }
            }

            if (token.IsCancellationRequested)
                break;

            if (ReconnectIntervalSeconds <= 0f)
                break;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ReconnectIntervalSeconds), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && IsConnected)
        {
            string line = await ReadLineAsync(reader, token);

            if (line == null)
            {
                // 상대가 정상 종료했거나 취소됨
                break;
            }

            onMessage?.Invoke(line);
        }
    }

    // ReadLineAsync가 취소 토큰을 직접 받지 못하므로 Task.WhenAny로 감싼다.
    private async Task<string> ReadLineAsync(StreamReader streamReader, CancellationToken token)
    {
        Task<string> readTask = streamReader.ReadLineAsync();
        Task cancelTask = Task.Delay(Timeout.Infinite, token);
        Task completed = await Task.WhenAny(readTask, cancelTask);

        if (completed == cancelTask)
            return null;

        return await readTask;
    }

    private void CloseStream()
    {
        lock (writerLock)
        {
            try { writer?.Dispose(); } catch { /* ignore */ }
            writer = null;
        }

        try { reader?.Dispose(); } catch { /* ignore */ }
        reader = null;

        try { networkStream?.Dispose(); } catch { /* ignore */ }
        networkStream = null;

        try { tcpClient?.Close(); } catch { /* ignore */ }
        tcpClient = null;
    }
}
