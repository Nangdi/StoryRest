using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 현장에서 이미지 위치·크기·회전을 키보드로 맞추는 편집모드.
///
/// F1 로 들어가고 나온다. 슬라이더를 마우스로 끄는 대신
/// 방향키로 위치, +/- 로 크기, [/] 로 회전을 조정한다.
/// 값이 바뀌면 aruco.json 에 자동 저장되므로 따로 저장 절차가 필요 없다.
/// </summary>
[RequireComponent(typeof(ArUcoPageOverlay))]
public class ArUcoAdjustMode : MonoBehaviour
{
    [Header("키")]
    [SerializeField] KeyCode toggleKey = KeyCode.F1;
    [SerializeField] KeyCode nextMarkerKey = KeyCode.Tab;
    [SerializeField] KeyCode resetMarkerKey = KeyCode.Backspace;
    [SerializeField] KeyCode reloadImagesKey = KeyCode.F5;
    [SerializeField] KeyCode outlineToggleKey = KeyCode.D;
    [Tooltip("원근 매핑을 껐다 켠다. 마커가 작아 이미지가 심하게 떨릴 때 비교용.")]
    [SerializeField] KeyCode perspectiveToggleKey = KeyCode.P;
    // GameManager 가 S 를 게임 시작에 쓰고 있어 겹치지 않도록 Enter 로 둔다.
    // 값은 자동 저장되므로 즉시 저장이 필요할 때만 쓰면 된다.
    [SerializeField] KeyCode saveKey = KeyCode.Return;

    [Header("반복 입력")]
    [Tooltip("키를 누르고 있을 때 연속 조정이 시작되기까지의 시간(초).")]
    [SerializeField] float repeatDelay = 0.35f;
    [Tooltip("연속 조정 속도(초당 횟수).")]
    [SerializeField] float repeatRate = 14f;
    [Tooltip("Shift 를 같이 눌렀을 때 조정폭 배수.")]
    [SerializeField] float fineMultiplier = 0.2f;

    [Tooltip("값이 바뀐 뒤 이 시간만큼 조용하면 json 에 저장한다.")]
    [SerializeField] float autoSaveDelay = 1.5f;

    ArUcoPageOverlay _overlay;
    ArUcoJson _config;

    Text _hud;
    RawImage _hudBackground;

    readonly Dictionary<KeyCode, float> _holdTimes = new Dictionary<KeyCode, float>();
    readonly StringBuilder _builder = new StringBuilder(512);

    int _selectedId = -1;
    bool _active;
    bool _cursorWasVisible;
    bool _dirty;
    float _dirtySince;

    public bool IsActive => _active;

    void Awake()
    {
        _overlay = GetComponent<ArUcoPageOverlay>();
    }

    void Start()
    {
        _config = _overlay.Config;
        BuildHud();

        // 시작 시엔 SetActive(false) 를 쓰지 않는다.
        // 커서 복원까지 하게 되면 UnityAlwaysOnTop / SettingsPanelUI 가 정한 커서 상태를 덮어쓴다.
        _active = false;
        _hud.enabled = false;
        _hudBackground.enabled = false;
    }

    void BuildHud()
    {
        _hudBackground = ArUcoPageOverlay.NewRawImage("AdjustHudBackground", _overlay.CanvasRoot);
        _hudBackground.color = new Color(0f, 0f, 0f, 0.75f);
        PlaceTopLeft(_hudBackground.rectTransform, new Vector2(560f, 440f));

        _hud = ArUcoPageOverlay.NewText("AdjustHud", _overlay.CanvasRoot);
        _hud.alignment = TextAnchor.UpperLeft;
        _hud.fontSize = 20;
        _hud.lineSpacing = 1.25f;
        PlaceTopLeft(_hud.rectTransform, new Vector2(536f, 416f));
        _hud.rectTransform.anchoredPosition = new Vector2(32f, -32f);
    }

    static void PlaceTopLeft(RectTransform rect, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = size;
        rect.anchoredPosition = new Vector2(20f, -20f);
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey)) SetActive(!_active);

        if (!_active)
        {
            FlushIfDirty(force: false);
            return;
        }

        HandleSelection();
        HandleAdjustKeys();
        HandleCommands();
        UpdateHud();
        FlushIfDirty(force: false);
    }

    void SetActive(bool active)
    {
        _active = active;
        _hud.enabled = active;
        _hudBackground.enabled = active;
        _overlay.HighlightMarkerId = active ? _selectedId : -1;

        if (active)
        {
            // 커서가 숨겨져 있어도 편집 중에는 창을 옮기거나 할 수 있게 보여준다.
            // 나갈 때 원래대로 돌려놓아야 키오스크 설정을 덮어쓰지 않는다.
            _cursorWasVisible = Cursor.visible;
            Cursor.visible = true;
            if (_selectedId < 0) SelectNext(1);
        }
        else
        {
            FlushIfDirty(force: true);
            Cursor.visible = _cursorWasVisible;
        }
    }

    void HandleSelection()
    {
        if (!Input.GetKeyDown(nextMarkerKey)) return;
        SelectNext(IsShiftHeld() ? -1 : 1);
    }

    void SelectNext(int direction)
    {
        var visible = _overlay.VisibleMarkerIds;
        if (visible.Count == 0) return;

        int index = IndexOf(visible, _selectedId);

        index = index < 0
            ? (direction > 0 ? 0 : visible.Count - 1)
            : ((index + direction) % visible.Count + visible.Count) % visible.Count;

        _selectedId = visible[index];
        _overlay.HighlightMarkerId = _selectedId;
    }

    void HandleAdjustKeys()
    {
        if (_selectedId < 0) return;

        var marker = _config.GetOrCreate(_selectedId);
        float fine = IsShiftHeld() ? fineMultiplier : 1f;

        float dx = (KeyStep(KeyCode.RightArrow) - KeyStep(KeyCode.LeftArrow)) * _config.adjustPositionStep * fine;
        float dy = (KeyStep(KeyCode.UpArrow) - KeyStep(KeyCode.DownArrow)) * _config.adjustPositionStep * fine;

        float grow = KeyStep(KeyCode.Equals) + KeyStep(KeyCode.Plus) + KeyStep(KeyCode.KeypadPlus);
        float shrink = KeyStep(KeyCode.Minus) + KeyStep(KeyCode.KeypadMinus);
        float ds = (grow - shrink) * _config.adjustScaleStep * fine;

        float dr = (KeyStep(KeyCode.RightBracket) - KeyStep(KeyCode.LeftBracket)) * _config.adjustRotationStep * fine;

        float dGlobal = (KeyStep(KeyCode.PageUp) - KeyStep(KeyCode.PageDown)) * _config.adjustScaleStep * fine;

        if (dx == 0f && dy == 0f && ds == 0f && dr == 0f && dGlobal == 0f) return;

        marker.offsetX += dx;
        marker.offsetY += dy;
        marker.scale = Mathf.Max(0.05f, marker.scale + ds);
        marker.rotationOffset = Mathf.Repeat(marker.rotationOffset + dr + 180f, 360f) - 180f;
        _config.globalScale = Mathf.Max(0.05f, _config.globalScale + dGlobal);

        MarkDirty();
    }

    void HandleCommands()
    {
        if (Input.GetKeyDown(resetMarkerKey) && _selectedId >= 0)
        {
            var marker = _config.GetOrCreate(_selectedId);
            marker.scale = 1f;
            marker.offsetX = 0f;
            marker.offsetY = 0f;
            marker.rotationOffset = 0f;
            MarkDirty();
        }

        if (Input.GetKeyDown(reloadImagesKey))
            _overlay.ReloadImages();

        if (Input.GetKeyDown(outlineToggleKey))
            _overlay.Tracker.DrawDetectedMarkers = !_overlay.Tracker.DrawDetectedMarkers;

        if (Input.GetKeyDown(perspectiveToggleKey))
        {
            _config.perspectiveMapping = !_config.perspectiveMapping;
            MarkDirty();
        }

        if (Input.GetKeyDown(saveKey))
            FlushIfDirty(force: true);
    }

    void MarkDirty()
    {
        _dirty = true;
        _dirtySince = Time.unscaledTime;
    }

    void FlushIfDirty(bool force)
    {
        if (!_dirty) return;
        if (!force && Time.unscaledTime - _dirtySince < autoSaveDelay) return;

        _overlay.SaveConfig();
        _dirty = false;
    }

    // 누르고 있으면 연속으로 조정되도록, 이번 프레임에 몇 번 눌린 것으로 칠지 계산한다.
    float KeyStep(KeyCode key)
    {
        if (Input.GetKeyDown(key))
        {
            _holdTimes[key] = 0f;
            return 1f;
        }

        if (!Input.GetKey(key))
        {
            _holdTimes.Remove(key);
            return 0f;
        }

        float previous = _holdTimes.TryGetValue(key, out var value) ? value : 0f;
        float current = previous + Time.unscaledDeltaTime;
        _holdTimes[key] = current;

        if (current < repeatDelay) return 0f;

        // 반복 구간에서 지난 프레임과 이번 프레임 사이에 넘어간 반복 횟수만큼 적용한다.
        float before = Mathf.Floor(Mathf.Max(0f, previous - repeatDelay) * repeatRate);
        float after = Mathf.Floor((current - repeatDelay) * repeatRate);
        return after - before;
    }

    static bool IsShiftHeld()
    {
        return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    }

    static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == value) return i;
        }
        return -1;
    }

    void UpdateHud()
    {
        var visible = _overlay.VisibleMarkerIds;

        _builder.Clear();
        _builder.AppendLine("<color=#ffd633><b>편집모드</b></color>   <color=#aaaaaa>F1 로 나가기</color>");
        _builder.AppendLine();

        if (_selectedId < 0)
        {
            _builder.AppendLine("카메라에 마커를 비춰주세요.");
        }
        else
        {
            var marker = _config.GetOrCreate(_selectedId);
            bool onScreen = IndexOf(visible, _selectedId) >= 0;

            _builder.AppendLine($"선택   <b>{_selectedId}번 마커</b>{(onScreen ? "" : "  <color=#ff8888>(화면에 없음)</color>")}   <color=#aaaaaa>Tab 다음</color>");
            _builder.AppendLine($"크기   <b>{marker.scale:0.00}</b>   <color=#aaaaaa>+ / -</color>");
            _builder.AppendLine($"좌우   <b>{marker.offsetX:+0.00;-0.00; 0.00}</b>   <color=#aaaaaa>← / →</color>");
            _builder.AppendLine($"상하   <b>{marker.offsetY:+0.00;-0.00; 0.00}</b>   <color=#aaaaaa>↑ / ↓</color>");
            _builder.AppendLine($"회전   <b>{marker.rotationOffset:+0.0;-0.0; 0.0}°</b>   <color=#aaaaaa>[ / ]</color>");
        }

        _builder.AppendLine($"전체배율   <b>{_config.globalScale:0.00}</b>   <color=#aaaaaa>PageUp / PageDown</color>");
        _builder.AppendLine($"원근       <b>{(_config.perspectiveMapping ? "켜짐 (페이지 면에 눕힘)" : "꺼짐 (항상 정면)")}</b>   <color=#aaaaaa>{perspectiveToggleKey}</color>");
        _builder.AppendLine();
        _builder.AppendLine($"<color=#aaaaaa>Shift 같이 누르면 미세조정   ·   {resetMarkerKey} 이 마커 초기화</color>");
        _builder.AppendLine($"<color=#aaaaaa>{_overlay.CameraToggleKey} 카메라 영상 {(_overlay.CameraVisible ? "끄기" : "켜기")}   ·   {reloadImagesKey} 이미지 다시 읽기</color>");
        _builder.AppendLine($"<color=#aaaaaa>{outlineToggleKey} 마커 테두리   ·   {saveKey} 저장</color>");
        _builder.AppendLine();

        _builder.Append(visible.Count > 0
            ? $"<color=#88ff88>보이는 마커: {string.Join(", ", visible)}</color>"
            : $"<color=#ff8888>마커가 보이지 않습니다 (후보 {_overlay.Tracker.RejectedCount}개)</color>");

        if (_dirty) _builder.Append("   <color=#ffd633>* 저장 대기</color>");

        _hud.text = _builder.ToString();
    }
}
