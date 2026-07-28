using System;
using System.Collections.Generic;
using UnityEngine;

// RS232 연결 상태 + 송수신 데이터를 게임씬에 즉시 표시하는 디버그 로그 패널.
// 별도의 Canvas/Text 셋업 없이 OnGUI로 그려서 빠르게 확인할 수 있도록 한다.
public class SerialPortLogUI : MonoBehaviour
{
    [Header("표시 설정")]
    [SerializeField] private bool visible = true;
    [SerializeField] private KeyCode toggleKey = KeyCode.F1;
    [SerializeField] private int maxLogLines = 200;
    [SerializeField] private int panelWidth = 520;
    [SerializeField] private int panelMargin = 10;
    [SerializeField] private int fontSize = 13;

    private readonly Queue<string> logLines = new Queue<string>();
    private Vector2 scroll;

    private SerialPortManager manager;
    private bool subscribed;

    private GUIStyle boxStyle;
    private GUIStyle headerStyle;
    private GUIStyle okStyle;
    private GUIStyle ngStyle;
    private GUIStyle offStyle;
    private GUIStyle logStyle;
    private bool stylesReady;

    private void Start()
    {
        TrySubscribe();
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey)) visible = !visible;

        // SerialPortManager.Instance가 늦게 만들어지는 경우 대비
        if (!subscribed) TrySubscribe();
    }

    private void TrySubscribe()
    {
        if (subscribed) return;
        manager = SerialPortManager.Instance;
        if (manager == null) return;

        manager.OnDataReceived += HandleReceived;
        manager.OnDataSent += HandleSent;
        manager.OnConnectionChanged += HandleConnectionChanged;
        subscribed = true;
    }

    private void OnDestroy()
    {
        if (!subscribed || manager == null) return;
        manager.OnDataReceived -= HandleReceived;
        manager.OnDataSent -= HandleSent;
        manager.OnConnectionChanged -= HandleConnectionChanged;
        subscribed = false;
    }

    private void HandleReceived(int id, string data) => AppendLog($"[RX][Ctrl {id}] {data}");
    private void HandleSent(int id, string data) => AppendLog($"[TX][Ctrl {id}] {data}");
    private void HandleConnectionChanged(int id, bool isOpen) =>
        AppendLog($"[CON][Ctrl {id}] {(isOpen ? "OPEN" : "CLOSE")}");

    private void AppendLog(string line)
    {
        string formatted = $"{DateTime.Now:HH:mm:ss} {line}";
        logLines.Enqueue(formatted);
        while (logLines.Count > maxLogLines) logLines.Dequeue();

        // 스크롤을 항상 최신 로그(하단)로 고정
        scroll.y = float.MaxValue;
    }

    private void EnsureStyles()
    {
        if (stylesReady) return;

        boxStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft };

        headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = fontSize + 1 };

        okStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize };
        okStyle.normal.textColor = new Color(0.4f, 1f, 0.4f);

        ngStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize };
        ngStyle.normal.textColor = new Color(1f, 0.4f, 0.4f);

        offStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize };
        offStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);

        logStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize, wordWrap = false, richText = false };

        stylesReady = true;
    }

    private void OnGUI()
    {
        if (!visible) return;
        EnsureStyles();

        float h = Screen.height - panelMargin * 2;
        GUILayout.BeginArea(new Rect(panelMargin, panelMargin, panelWidth, h), boxStyle);

        GUILayout.Label($"RS232 상태  (토글: {toggleKey})", headerStyle);
        GUILayout.Space(4);

        DrawConnectionStatus();

        GUILayout.Space(8);
        GUILayout.Label("송수신 로그 (TX/RX/CON)", headerStyle);

        scroll = GUILayout.BeginScrollView(scroll);
        foreach (var line in logLines)
        {
            GUILayout.Label(line, logStyle);
        }
        GUILayout.EndScrollView();

        if (GUILayout.Button("로그 지우기"))
        {
            logLines.Clear();
        }

        GUILayout.EndArea();
    }

    private void DrawConnectionStatus()
    {
        var portJson = JsonManager.instance != null ? JsonManager.instance.portJson : null;
        if (portJson == null || portJson.ports == null || portJson.ports.Count == 0)
        {
            GUILayout.Label("포트 설정 없음 (port.json)", ngStyle);
            return;
        }

        foreach (var cfg in portJson.ports)
        {
            string head = $"Ctrl {cfg.controllerId}  {cfg.com}  {cfg.baudLate}bps  ";

            if (!cfg.enabled)
            {
                GUILayout.Label(head + "[DISABLED]", offStyle);
                continue;
            }

            bool open = manager != null && manager.IsControllerOpen(cfg.controllerId);
            GUILayout.Label(head + (open ? "[OPEN]" : "[CLOSE]"), open ? okStyle : ngStyle);
        }
    }
}
