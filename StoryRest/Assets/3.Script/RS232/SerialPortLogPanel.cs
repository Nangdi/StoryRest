using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 게임씬에 미리 만들어진 Canvas/UI 위에서 동작하는 로그 패널.
// Inspector에서 텍스트/스크롤뷰/버튼을 직접 연결한다.
public class SerialPortLogPanel : MonoBehaviour
{
    [Header("연결 상태 텍스트 (인덱스 0~4 = Ctrl 1~5)")]
    [SerializeField] private TMP_Text[] connectionStatusTexts;

    [Header("로그 표시")]
    [SerializeField] private TMP_Text logText;
    [SerializeField] private ScrollRect logScrollRect;

    [Header("버튼")]
    [SerializeField] private Button clearButton;

    [Header("토글 (ESC 키로 표시/숨김)")]
    [SerializeField] private GameObject togglePanel;
    [SerializeField] private KeyCode toggleKey = KeyCode.Escape;
    [Tooltip("시작할 때 로그 창을 숨긴 상태로 둘지 여부.")]
    [SerializeField] private bool hideOnStart = true;

    [Header("동작 설정")]
    [SerializeField] private int maxLogLines = 200;

    [Header("상태 색상")]
    [SerializeField] private Color openColor = new Color(0.4f, 1f, 0.4f);
    [SerializeField] private Color closeColor = new Color(1f, 0.4f, 0.4f);
    [SerializeField] private Color disabledColor = new Color(0.7f, 0.7f, 0.7f);

    private readonly Queue<string> logLines = new Queue<string>();
    private readonly StringBuilder builder = new StringBuilder();

    private SerialPortManager manager;
    private bool subscribed;

    private void Start()
    {
        if (clearButton != null) clearButton.onClick.AddListener(ClearLog);
        TrySubscribe();
        RefreshAllStatus();

        if (hideOnStart && togglePanel != null)
            togglePanel.SetActive(false);
    }

    private void Update()
    {
        // SerialPortManager.Instance가 늦게 만들어지는 경우 대비
        if (!subscribed) TrySubscribe();

        if (Input.GetKeyDown(toggleKey) && togglePanel != null)
        {
            togglePanel.SetActive(!togglePanel.activeSelf);
        }
    }

    private void OnDestroy()
    {
        if (clearButton != null) clearButton.onClick.RemoveListener(ClearLog);
        if (!subscribed || manager == null) return;
        manager.OnDataReceived -= HandleReceived;
        manager.OnDataSent -= HandleSent;
        manager.OnConnectionChanged -= HandleConnectionChanged;
        subscribed = false;
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
        RefreshAllStatus();
    }

    private void HandleReceived(int id, string data) => AppendLog($"[Receive][Ctrl {id}] {data}");
    private void HandleSent(int id, string data) => AppendLog($"[Send][Ctrl {id}] {data}");

    private void HandleConnectionChanged(int id, bool isOpen)
    {
        UpdateStatus(id, isOpen, IsEnabled(id));
        AppendLog($"[Connect][Ctrl {id}] {(isOpen ? "OPEN" : "CLOSE")}");
    }

    private bool IsEnabled(int controllerId)
    {
        var portJson = JsonManager.instance != null ? JsonManager.instance.portJson : null;
        if (portJson == null || portJson.ports == null) return true;
        foreach (var cfg in portJson.ports)
        {
            if (cfg.controllerId == controllerId) return cfg.enabled;
        }
        return true;
    }

    private void RefreshAllStatus()
    {
        var portJson = JsonManager.instance != null ? JsonManager.instance.portJson : null;
        if (portJson == null || portJson.ports == null) return;

        foreach (var cfg in portJson.ports)
        {
            bool open = manager != null && manager.IsControllerOpen(cfg.controllerId);
            UpdateStatus(cfg.controllerId, open, cfg.enabled);
        }
    }

    private void UpdateStatus(int controllerId, bool isOpen, bool enabled)
    {
        if (connectionStatusTexts == null) return;
        int idx = controllerId - 1;
        if (idx < 0 || idx >= connectionStatusTexts.Length) return;
        var t = connectionStatusTexts[idx];
        if (t == null) return;

        string com = "";
        int baud = 0;
        var portJson = JsonManager.instance != null ? JsonManager.instance.portJson : null;
        if (portJson != null && portJson.ports != null)
        {
            foreach (var cfg in portJson.ports)
            {
                if (cfg.controllerId == controllerId)
                {
                    com = cfg.com;
                    baud = cfg.baudLate;
                    break;
                }
            }
        }

        string state;
        Color color;
        if (!enabled) { state = "DISABLED"; color = disabledColor; }
        else if (isOpen) { state = "OPEN"; color = openColor; }
        else { state = "CLOSE"; color = closeColor; }

        t.text = $"Ctrl {controllerId}  {com}  {baud}bps  [{state}]";
        t.color = color;
    }

    private void AppendLog(string line)
    {
        string formatted = $"{DateTime.Now:HH:mm:ss} {line}";
        logLines.Enqueue(formatted);
        while (logLines.Count > maxLogLines) logLines.Dequeue();

        builder.Clear();
        foreach (var l in logLines) builder.AppendLine(l);
        if (logText != null) logText.text = builder.ToString();

        // ContentSizeFitter 재계산 후, Content가 Viewport를 넘었을 때만 끝으로 스크롤.
        // 항상 0으로 강제하면 로그가 적어 Content가 Viewport보다 짧을 때 위로 튕겨 올라가는 현상이 생긴다.
        if (logScrollRect != null && logScrollRect.content != null && logScrollRect.viewport != null)
        {
            Canvas.ForceUpdateCanvases();
            if (logScrollRect.content.rect.height > logScrollRect.viewport.rect.height)
            {
                logScrollRect.verticalNormalizedPosition = 0f;
            }
        }
    }

    public void ClearLog()
    {
        logLines.Clear();
        if (logText != null) logText.text = string.Empty;
    }
}
