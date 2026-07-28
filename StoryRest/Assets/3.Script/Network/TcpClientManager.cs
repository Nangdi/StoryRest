using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

// 메인 서버에 접속하는 TCP 클라이언트 매니저.
// JsonManager.tcpJson 설정을 읽어 TcpClientChannel을 생성하고,
// 백그라운드에서 받은 메시지를 메인 스레드에서 이벤트로 다시 쏴준다.
public class TcpClientManager : MonoBehaviour
{
    private const int MaxRecentMessages = 20;

    public static TcpClientManager Instance { get; private set; }

    [Header("TCP 설정 오버라이드")]
    [Tooltip("켜면 인스펙터의 host/port 값을 사용한다. 끄면 JsonManager.tcpJson을 사용.")]
    [SerializeField] private bool overrideJsonConfig = false;
    [SerializeField] private string overrideHost = "127.0.0.1";
    [SerializeField] private int overridePort = 5000;
    [SerializeField] private float overrideReconnectIntervalSeconds = 3f;
    [SerializeField] private bool overrideAppendOutgoingLineEnding = true;

    [Header("실행 옵션")]
    [SerializeField] private bool autoStart = true;

    [Header("키보드 테스트")]
    [SerializeField] private bool enableKeyboardTest = true;
    [SerializeField] private KeyCode sendTestMessageKey = KeyCode.T;
    [SerializeField] private string testMessage = "test";

    // 백그라운드 스레드에서 받은 메시지를 메인 스레드에서 처리하기 위한 큐.
    private readonly ConcurrentQueue<string> receivedQueue = new ConcurrentQueue<string>();
    // 백그라운드에서 일어난 연결 상태 변화를 메인 스레드에서 처리하기 위한 큐.
    private readonly ConcurrentQueue<bool> connectionChangedQueue = new ConcurrentQueue<bool>();
    // UI 등에서 표시하기 위한 최근 송수신 로그.
    private readonly Queue<string> recentMessages = new Queue<string>();
    private readonly object recentLock = new object();

    private TcpClientChannel channel;
    private bool isConnected;

    // 메인 서버에서 한 줄 메시지가 도착했을 때 발생.
    public event Action<string> MessageReceived;
    // 연결 상태가 바뀔 때 발생(true=연결, false=끊김).
    public event Action<bool> ConnectionChanged;

    public bool IsConnected => isConnected;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void Start()
    {
        if (autoStart)
            StartChannel();
    }

    private void Update()
    {
        // 백그라운드에서 들어온 수신 메시지를 메인 스레드에서 흘려준다.
        while (receivedQueue.TryDequeue(out string line))
        {
            AddRecentMessage($"[수신] {line}");
            try
            {
                MessageReceived?.Invoke(line);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TCP] MessageReceived 처리 중 예외: {ex}");
            }
        }

        // 백그라운드에서 발생한 연결 상태 변화를 메인 스레드에서 알린다.
        while (connectionChangedQueue.TryDequeue(out bool connected))
        {
            isConnected = connected;
            AddRecentMessage(connected ? "[연결] 서버에 연결됨" : "[연결] 끊어짐");
            try
            {
                ConnectionChanged?.Invoke(connected);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TCP] ConnectionChanged 처리 중 예외: {ex}");
            }
        }

        HandleKeyboardTest();
    }

    private void OnDestroy()
    {
        StopChannel();

        if (Instance == this)
            Instance = null;
    }

    private void OnApplicationQuit()
    {
        StopChannel();
    }

    public void StartChannel()
    {
        if (channel != null)
            return;

        string host;
        int port;
        float reconnect;
        bool appendNewline;

        if (overrideJsonConfig || JsonManager.instance == null || JsonManager.instance.tcpJson == null)
        {
            host = overrideHost;
            port = overridePort;
            reconnect = overrideReconnectIntervalSeconds;
            appendNewline = overrideAppendOutgoingLineEnding;
        }
        else
        {
            TcpJson cfg = JsonManager.instance.tcpJson;
            host = cfg.host;
            port = cfg.port;
            reconnect = cfg.reconnectIntervalSeconds;
            appendNewline = cfg.appendOutgoingLineEnding;
        }

        Debug.Log($"[TCP] 채널 시작 host={host} port={port} reconnect={reconnect}s");

        channel = new TcpClientChannel(host, port, reconnect, appendNewline,
            EnqueueReceived, EnqueueConnectionChanged);
        channel.Start();
    }

    public void StopChannel()
    {
        if (channel == null)
            return;

        channel.Stop();
        channel = null;
    }

    // 메인 서버로 한 줄 메시지를 송신한다. 연결되어 있지 않으면 false 반환.
    public bool Send(string message)
    {
        if (channel == null)
        {
            AddRecentMessage("[경고] 채널이 시작되지 않음");
            return false;
        }

        if (!channel.IsConnected)
        {
            AddRecentMessage($"[경고] 미연결 상태에서 송신 시도: {message}");
            return false;
        }

        bool ok = channel.Send(message);
        AddRecentMessage(ok ? $"[송신] {message}" : $"[실패] {message}");
        return ok;
    }

    public IReadOnlyList<string> GetRecentMessagesSnapshot()
    {
        lock (recentLock)
            return new List<string>(recentMessages);
    }

    private void EnqueueReceived(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        receivedQueue.Enqueue(line);
    }

    private void EnqueueConnectionChanged(bool connected)
    {
        connectionChangedQueue.Enqueue(connected);
    }

    private void AddRecentMessage(string message)
    {
        lock (recentLock)
        {
            recentMessages.Enqueue(message);
            while (recentMessages.Count > MaxRecentMessages)
                recentMessages.Dequeue();
        }
    }

    private void HandleKeyboardTest()
    {
        if (!enableKeyboardTest)
            return;

        if (Input.GetKeyDown(sendTestMessageKey))
            Send(testMessage);
    }
}
