using UnityEngine;
using UnityEngine.UI;

// ESC 키로 열고 닫는 런타임 설정창.
// gameSettingData.json 값을 실시간으로 토글하고 즉시 적용 + 저장한다.
// UI는 런타임에 생성하지 않고 씬에 오브젝트로 만들어 인스펙터에서 연결한다.
public class SettingsPanelUI : MonoBehaviour
{
    [Header("패널")]
    [Tooltip("ESC로 켜고 끌 설정창 루트 오브젝트. 이 스크립트는 항상 켜져 있는 오브젝트(캔버스 등)에 둔다.")]
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private KeyCode toggleKey = KeyCode.Escape;
    [Tooltip("시작할 때 설정창을 숨긴 상태로 둘지 여부.")]
    [SerializeField] private bool hideOnStart = true;

    [Header("설정 토글")]
    [SerializeField] private Toggle useUnityOnTopToggle;
    [SerializeField] private Toggle showMouseCursorToggle;

    [Header("적용 대상 (비워두면 자동 탐색)")]
    [SerializeField] private UnityAlwaysOnTop alwaysOnTop;

    // 데이터 -> UI 반영 중에는 onValueChanged 콜백이 저장을 유발하지 않도록 막는다.
    private bool _syncing;

    private void Start()
    {
        if (alwaysOnTop == null)
            alwaysOnTop = FindObjectOfType<UnityAlwaysOnTop>();

        SyncFromData();

        if (useUnityOnTopToggle != null)
            useUnityOnTopToggle.onValueChanged.AddListener(OnUseUnityOnTopChanged);
        if (showMouseCursorToggle != null)
            showMouseCursorToggle.onValueChanged.AddListener(OnShowMouseCursorChanged);

        if (hideOnStart)
            SetPanelVisible(false);
    }

    // 설정창 표시/숨김 + 마우스 커서 연동.
    // 창이 보일 때는 조작할 수 있도록 커서를 강제로 표시하고,
    // 창이 닫히면 커서를 설정값(showMouseCursor)대로 되돌린다(숨김이면 같이 숨김).
    public void SetPanelVisible(bool show)
    {
        if (panelRoot != null)
            panelRoot.SetActive(show);

        if (show)
        {
            SyncFromData();          // 열 때 최신 json 값 반영
            Cursor.visible = true;   // 조작을 위해 커서 강제 표시
        }
        else
        {
            RestoreCursor();         // 설정값대로 복원
        }
    }

    // 현재 showMouseCursor 설정값대로 커서 표시/숨김을 되돌린다.
    private void RestoreCursor()
    {
        bool show = JsonManager.instance != null && JsonManager.instance.gameSettingData.showMouseCursor;
        if (alwaysOnTop != null) alwaysOnTop.ApplyMouseCursor(show);
        else Cursor.visible = show;
    }

    private void OnDestroy()
    {
        if (useUnityOnTopToggle != null)
            useUnityOnTopToggle.onValueChanged.RemoveListener(OnUseUnityOnTopChanged);
        if (showMouseCursorToggle != null)
            showMouseCursorToggle.onValueChanged.RemoveListener(OnShowMouseCursorChanged);
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey) && panelRoot != null)
            SetPanelVisible(!panelRoot.activeSelf);
    }

    // 현재 gameSettingData -> 토글 UI 반영
    public void SyncFromData()
    {
        var data = JsonManager.instance != null ? JsonManager.instance.gameSettingData : null;
        if (data == null) return;

        _syncing = true;
        if (useUnityOnTopToggle != null) useUnityOnTopToggle.isOn = data.useUnityOnTop;
        if (showMouseCursorToggle != null) showMouseCursorToggle.isOn = data.showMouseCursor;
        _syncing = false;
    }

    private void OnUseUnityOnTopChanged(bool value)
    {
        if (_syncing) return;

        var jm = JsonManager.instance;
        if (jm == null) return;

        jm.gameSettingData.useUnityOnTop = value;
        jm.SaveGameSettingData();               // json 파일에 실시간 저장
        if (alwaysOnTop != null) alwaysOnTop.ApplyAlwaysOnTop(value);
    }

    private void OnShowMouseCursorChanged(bool value)
    {
        if (_syncing) return;

        var jm = JsonManager.instance;
        if (jm == null) return;

        jm.gameSettingData.showMouseCursor = value;
        jm.SaveGameSettingData();               // json 파일에 실시간 저장
        // 창이 열려 있는 동안엔 조작을 위해 커서를 계속 표시한다.
        // 실제 커서 반영은 창을 닫을 때 RestoreCursor()가 설정값대로 처리한다.
    }
}
