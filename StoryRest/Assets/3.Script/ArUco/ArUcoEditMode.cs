using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 현장에서 값을 맞추는 편집모드. 실행 중인 전시 화면 위에서 조정하고 파일로 남긴다.
    ///
    /// 두 가지를 잡는다.
    ///  - **코너 보정**: 카메라 픽셀 → 프로젝터 좌표 변환(→ ARCHITECTURE §1). 설치 시 1회.
    ///  - **마커 배치**: 마커별 위치·크기·회전. 콘텐츠를 추가할 때마다.
    ///
    /// 세트가 여럿이면 하나를 골라 조정한다. HUD 는 그 세트의 화면에만 뜨고
    /// 나머지 프로젝터는 전시 상태를 유지한다.
    ///
    /// 전시장 PC 는 예고 없이 꺼지므로 값이 바뀌면 곧바로 저장한다(무입력 1.5초 + Enter 즉시).
    /// </summary>
    [DisallowMultipleComponent]
    public class ArUcoEditMode : MonoBehaviour
    {
        enum Stage
        {
            Placement,      // 마커별 배치 조정
            Calibration,    // 카메라↔프로젝터 코너 보정
        }

        enum CalibrationPhase
        {
            Idle,           // 안내만 표시
            Auto,           // 마커를 투사하고 카메라가 읽기를 기다린다
            Manual,         // 조준점을 하나씩 짚어 나간다
            Verify,         // 구한 변환으로 다시 쏴서 눈으로 확인
        }

        [Header("키")]
        // F1 은 단축키 안내(ArUcoHelpPanel), F4 는 관람 기록(ArUcoStatsPanel)이 쓴다.
        [SerializeField] KeyCode toggleKey = KeyCode.F2;
        [SerializeField] KeyCode stageKey = KeyCode.F3;
        // 카메라 없이 콘텐츠 파일만 점검하는 격자(→ ArUcoSet.DebugPreview). 편집모드와 무관하게 받는다.
        [SerializeField] KeyCode debugPreviewKey = KeyCode.F6;
        [SerializeField] KeyCode nextMarkerKey = KeyCode.Tab;
        // 한 마커가 영상과 이미지를 함께 띄우므로 그 안에서 장을 고르는 키가 따로 필요하다.
        [SerializeField] KeyCode prevItemKey = KeyCode.Comma;
        [SerializeField] KeyCode nextItemKey = KeyCode.Period;
        [SerializeField] KeyCode resetMarkerKey = KeyCode.Backspace;
        [SerializeField] KeyCode reloadContentKey = KeyCode.F5;
        [SerializeField] KeyCode outlineKey = KeyCode.D;
        [SerializeField] KeyCode cameraPreviewKey = KeyCode.C;
        [SerializeField] KeyCode contentKey = KeyCode.V;
        [SerializeField] KeyCode hudKey = KeyCode.H;
        [SerializeField] KeyCode actionKey = KeyCode.Space;
        // GameManager 가 S 를 쓰고 SettingsPanelUI 가 Esc 를 쓰므로 겹치지 않게 Enter 로 둔다.
        [SerializeField] KeyCode saveKey = KeyCode.Return;

        [Header("반복 입력")]
        [SerializeField] float repeatDelay = 0.35f;
        [SerializeField] float repeatRate = 14f;
        [Tooltip("Shift 를 같이 누르면 몇 배로 빠르게 움직일지.")]
        [SerializeField] float fastMultiplier = 10f;

        [Header("저장")]
        [SerializeField] float autoSaveDelay = 1.5f;

        [Header("캘리브레이션")]
        [Tooltip("자동 인식에서 몇 프레임을 모아 평균낼지. 마커가 미세하게 떨리므로 여러 장을 겹친다.")]
        [SerializeField] int autoSampleFrames = 30;
        [Tooltip("자동 보정에서 자리를 옮긴 뒤 카메라가 새 자리를 따라올 때까지 값을 모으지 않고 기다리는 시간(초).")]
        [SerializeField] float autoSettleSeconds = 0.6f;
        [Tooltip("보정 중 방향키 한 번에 옮기는 거리(화면 높이 비율, 1080p 에서 약 1px). Shift 면 fastMultiplier 배.")]
        [SerializeField] float calibrationPointStep = 0.001f;
        [Tooltip("보정 시작 화면이나 자동 보정 중에 누르면 곧바로 수동 보정(실물 마커로 짚기)으로 들어간다.")]
        [SerializeField] KeyCode manualKey = KeyCode.M;

        [Header("보정 세부 조정")]
        [Tooltip("보정을 마친 상태(보정 단계 대기 · 검증 화면)에서 방향키 · [ ] · + - 로 보정 결과 전체를 옮기고 돌리고 키운다. 이 키는 0 으로 되돌린다.")]
        [SerializeField] KeyCode fineResetKey = KeyCode.R;
        [Tooltip("+ / - 한 번에 바뀌는 배율. Shift 면 fastMultiplier 배.")]
        [SerializeField] float fineScaleStep = 0.0005f;
        [Tooltip("[ / ] 한 번에 도는 각도(도). Shift 면 fastMultiplier 배.")]
        [SerializeField] float fineRotationStep = 0.05f;
        [Tooltip("Ctrl + 방향키 한 번에 바뀌는 기울기. Shift 면 fastMultiplier 배.")]
        [SerializeField] float fineTiltStep = 0.002f;
        [Tooltip("보정 단계에서 맞춤 방식(정사각 → 평면 → 평면+휨)을 돌린다. 짚어 둔 점으로 곧바로 다시 풀므로 재보정이 필요 없다.")]
        [SerializeField] KeyCode modelKey = KeyCode.G;

        StoryRestApp _app;
        ArUcoHelpPanel _help;

        bool _active;
        bool _cursorWasVisible;

        // 콘텐츠를 그릴지. 단계를 바꿀 때마다 그 단계의 기본값으로 돌아가고, 그 위에서 사람이 뒤집는다
        // (배치는 보면서 맞추고, 보정은 조준점만 남기는 것이 기본).
        bool _showContent = true;

        int _setIndex;
        Stage _stage = Stage.Placement;
        int _selectedMarkerId = -1;

        // 선택한 마커 폴더 안에서 몇 번째 파일을 조정 중인지. -1 이면 마커 전체를 움직인다.
        // 콘텐츠가 한 장뿐인 마커에서는 굳이 들어갈 일이 없다(마커 전체 = 그 한 장).
        int _selectedItemIndex = -1;

        CalibrationPhase _phase = CalibrationPhase.Idle;
        // 보정 자리 사각형의 네 귀퉁이(작업용). 설정의 projectorPoints 를 복사해 두고 여기서 움직인다.
        readonly Vector2[] _quad = new Vector2[4];

        // 사각형에서 무엇을 옮기는지. -1 = 전체, 0~3 = 귀퉁이 하나.
        int _selection = -1;

        // 자동 보정 — 귀퉁이마다 투사 마커 꼭짓점 넷의 누적합과 표본 수, 이번 프레임에 보였는지.
        readonly Vector2[] _autoAccum = new Vector2[16];
        readonly int[] _autoCount = new int[4];
        readonly bool[] _autoSeen = new bool[4];
        float _autoSettleUntil;

        // 수동 보정 — 짚을 격자 점들과 지금까지 짚은 대응점. 다 짚어야 설정에 들어가므로
        // 중간에 나가도 기존 보정이 깨지지 않는다.
        readonly List<Vector2> _points = new List<Vector2>();
        int _manualIndex;
        readonly List<Vector2> _pairCamera = new List<Vector2>();
        readonly List<Vector2> _pairProjector = new List<Vector2>();
        readonly Vector2[] _guide = new Vector2[4];

        string _calibrationMessage = "";

        bool _dirty;
        float _dirtySince;

        // 안내판 자리. 0 = 단계 기본(배치는 좌상단, 보정은 가운데), 1~4 = 네 귀퉁이, 5 = 한 줄로 접기.
        // 안내판이 맞추려는 마커를 가릴 때 H 로 옮긴다. 세션 동안만 기억한다.
        int _hudSlot;
        const int HudSlotCount = 6;
        const int HudSlotCompact = 5;

        readonly Dictionary<KeyCode, float> _holdTimes = new Dictionary<KeyCode, float>();
        readonly StringBuilder _builder = new StringBuilder(1024);

        Texture2D _crosshair;

        // 마커 놓을 자리 표시용 사각형. 화면상 선 두께를 일정하게 유지하려고
        // 크기가 바뀌면 다시 만든다(→ TargetFrame).
        Texture2D _frame;
        int _frameThickness = -1;

        public bool IsActive => _active;

        public KeyCode ToggleKey => toggleKey;
        public KeyCode DebugPreviewKey => debugPreviewKey;

        void Awake()
        {
            _app = GetComponent<StoryRestApp>();
            _help = GetComponent<ArUcoHelpPanel>();
        }

        void OnDestroy()
        {
            if (_crosshair != null) Destroy(_crosshair);
            if (_frame != null) Destroy(_frame);
        }

        ArUcoSet CurrentSet
        {
            get
            {
                if (_app == null || _app.Sets.Count == 0) return null;
                _setIndex = Mathf.Clamp(_setIndex, 0, _app.Sets.Count - 1);
                return _app.Sets[_setIndex];
            }
        }

        // Set.Update 가 화면을 채운 뒤에 그 위로 편집 UI 를 얹는다.
        void LateUpdate()
        {
            // 설정창에 숫자를 치는 동안에는 단축키를 받지 않는다(Backspace 가 보정값을 지운다).
            if (UiTyping.IsTyping) return;

            if (Input.GetKeyDown(toggleKey)) SetActive(!_active);
            if (Input.GetKeyDown(debugPreviewKey)) ToggleDebugPreview();

            if (!_active)
            {
                HandleGlobalKeys();
                FlushIfDirty(false);
                return;
            }

            // 세트를 먼저 고른다. 바뀐 뒤에 집어야 이번 프레임의 키가 옮겨 간 세트에 걸린다
            // (이전 세트에 걸리면 그 세트에 편집 상태가 남는다).
            HandleSetSelection();

            var set = CurrentSet;
            if (set == null || set.View == null || _app.Config == null) return;

            HandleStageSwitch(set);
            HandleCommonKeys(set);

            if (_stage == Stage.Placement) UpdatePlacement(set);
            else UpdateCalibration(set);

            UpdateHud(set);
            FlushIfDirty(false);
        }

        void SetActive(bool active)
        {
            _active = active;

            if (active)
            {
                // 커서를 숨겨 둔 키오스크 상태라도 편집 중에는 창을 다룰 수 있어야 한다.
                _cursorWasVisible = Cursor.visible;
                Cursor.visible = true;

                // 들어올 때는 늘 마커 배치부터. 지난번 단계를 기억하면 보정을 마친 뒤 다시 들어왔을 때
                // 방향키가 영상이 아니라 보정 결과 전체(세부 조정)를 움직여 "영상 위치가 안 바뀐다" 로 보인다.
                _stage = Stage.Placement;
                _phase = CalibrationPhase.Idle;
                _showContent = true;
            }
            else
            {
                FlushIfDirty(true);
                Cursor.visible = _cursorWasVisible;
                _phase = CalibrationPhase.Idle;
            }

            // 편집을 벗어나면 전시 상태로 되돌린다. 어떤 편집 UI 도 남으면 안 된다.
            // 카메라 영상은 예외다 — 설정에 남는 값이라 사람이 끄기 전까지 유지한다.
            foreach (var s in _app.Sets)
            {
                s.SuppressContent = false;
                s.SuppressViewCounting = active;   // 설치자가 마커를 만지는 것은 관람이 아니다
                s.SuppressAppear = active;         // 배치를 맞추는 동안 고리·등장을 기다릴 수는 없다
                s.HighlightMarkerId = -1;
                s.HighlightItemIndex = -1;
                s.View.ShowHud(null);
                s.View.BeginOverlay();
                s.View.EndOverlay();
                s.Tracker.DrawDetectedMarkers = false;
            }
        }

        /// <summary>
        /// 편집모드 밖에서도 받는 키. 카메라 영상만 허용한다 —
        /// 설정에 남는 스위치라 편집모드까지 들어가지 않고도 켜고 끌 수 있어야 한다.
        ///
        /// 배치나 보정처럼 값을 바꾸는 조작은 여기서 받지 않는다(→ SPEC §6, 관람객이 건드릴 수 없어야 한다).
        /// </summary>
        void HandleGlobalKeys()
        {
            if (!Input.GetKeyDown(cameraPreviewKey)) return;
            if (_app == null || _app.Sets.Count == 0) return;

            // 밖에서는 세트를 고르는 개념이 없다. 한 번에 전부 같은 상태로 맞춘다 —
            // 하나라도 켜져 있으면 끄고, 전부 꺼져 있으면 켠다.
            bool anyOn = false;
            for (int i = 0; i < _app.Sets.Count; i++)
            {
                if (!_app.Sets[i].CameraPreviewEnabled) continue;

                anyOn = true;
                break;
            }

            for (int i = 0; i < _app.Sets.Count; i++) _app.Sets[i].CameraPreviewEnabled = !anyOn;

            MarkDirty();
        }

        /// <summary>
        /// 디버그 격자를 모든 세트에서 한꺼번에 뒤집는다. 표시 스위치라 편집모드 안팎 어디서든 받되,
        /// 파일에는 남기지 않는다(→ ArUcoSet.DebugPreview).
        /// </summary>
        void ToggleDebugPreview()
        {
            if (_app == null || _app.Sets.Count == 0) return;

            bool anyOn = false;
            for (int i = 0; i < _app.Sets.Count; i++)
            {
                if (!_app.Sets[i].DebugPreview) continue;

                anyOn = true;
                break;
            }

            for (int i = 0; i < _app.Sets.Count; i++) _app.Sets[i].DebugPreview = !anyOn;
        }

        void HandleSetSelection()
        {
            for (int i = 0; i < _app.Sets.Count && i < 9; i++)
            {
                if (!Input.GetKeyDown(KeyCode.Alpha1 + i)) continue;
                if (i == _setIndex) continue;

                // 세트를 옮기기 전에 이전 세트의 편집 흔적을 지운다.
                var previous = CurrentSet;
                if (previous != null)
                {
                    previous.SuppressContent = false;
                    previous.HighlightMarkerId = -1;
                    previous.HighlightItemIndex = -1;
                    previous.View.ShowHud(null);
                    previous.View.BeginOverlay();
                    previous.View.EndOverlay();
                }

                _setIndex = i;
                _selectedMarkerId = -1;
                _selectedItemIndex = -1;
                _phase = CalibrationPhase.Idle;
                _showContent = _stage == Stage.Placement;
            }
        }

        void HandleStageSwitch(ArUcoSet set)
        {
            if (!Input.GetKeyDown(stageKey)) return;

            _stage = _stage == Stage.Placement ? Stage.Calibration : Stage.Placement;
            _phase = CalibrationPhase.Idle;

            // 단계마다 기본값으로 되돌린다. 뒤집어 둔 것을 다음 단계까지 끌고 가면
            // "왜 안 보이지" 를 단계 전환 때마다 다시 겪는다.
            _showContent = _stage == Stage.Placement;

            if (_stage == Stage.Placement)
            {
                set.View.BeginOverlay();
                set.View.EndOverlay();
            }
        }

        void HandleCommonKeys(ArUcoSet set)
        {
            if (Input.GetKeyDown(cameraPreviewKey))
            {
                set.CameraPreviewEnabled = !set.CameraPreviewEnabled;

                // 편집모드를 나가도 유지되어야 하므로 파일에 남긴다.
                MarkDirty();
            }

            if (Input.GetKeyDown(contentKey)) _showContent = !_showContent;
            if (Input.GetKeyDown(hudKey)) _hudSlot = (_hudSlot + 1) % HudSlotCount;

            // 두 단계 모두 여기서 정한다. 보정 중에도 콘텐츠를 띄워 두고
            // 조준점과 실제 투사 위치가 함께 맞는지 볼 수 있어야 한다.
            set.SuppressContent = !_showContent;

            if (Input.GetKeyDown(outlineKey))
                set.Tracker.DrawDetectedMarkers = !set.Tracker.DrawDetectedMarkers;

            if (Input.GetKeyDown(reloadContentKey))
                _app.ReloadContent();

            if (Input.GetKeyDown(saveKey))
                FlushIfDirty(true);
        }

        // ── 마커 배치 ────────────────────────────────────────────────────────────

        void UpdatePlacement(ArUcoSet set)
        {
            if (Input.GetKeyDown(nextMarkerKey)) SelectNextMarker(set, IsShiftHeld() ? -1 : 1);

            if (Input.GetKeyDown(nextItemKey)) SelectNextItem(1);
            if (Input.GetKeyDown(prevItemKey)) SelectNextItem(-1);

            // 폴더에서 파일이 빠졌을 수 있다(F5). 범위를 넘은 선택은 마커 전체로 되돌린다.
            if (_selectedItemIndex >= SelectedEntryCount) _selectedItemIndex = -1;

            set.HighlightMarkerId = _selectedMarkerId;
            set.HighlightItemIndex = _selectedItemIndex;

            if (Input.GetKeyDown(resetMarkerKey)) ResetPlacement(set);

            var config = _app.Config;
            float fine = Speed();

            // 키는 프레임당 한 번만 읽는다. KeyStep 이 홀드 상태를 갱신하므로 두 번 부르면 어긋난다.
            float dx = (KeyStep(KeyCode.RightArrow) - KeyStep(KeyCode.LeftArrow)) * config.adjustPositionStep * fine;
            float dy = (KeyStep(KeyCode.UpArrow) - KeyStep(KeyCode.DownArrow)) * config.adjustPositionStep * fine;

            float grow = KeyStep(KeyCode.Equals) + KeyStep(KeyCode.Plus) + KeyStep(KeyCode.KeypadPlus);
            float shrink = KeyStep(KeyCode.Minus) + KeyStep(KeyCode.KeypadMinus);
            float ds = (grow - shrink) * config.adjustScaleStep * fine;

            float dPage = (KeyStep(KeyCode.PageUp) - KeyStep(KeyCode.PageDown)) * config.adjustScaleStep * fine;

            // [ / ] — 책상 위 가로세로 비율(세트 공통이 아니라 전체 공통, 보정 사각형과 같은 값).
            // 영상을 돌렸을 때 한쪽으로 늘어나 보이면 책상 위에서 반듯해질 때까지 맞춘다.
            float dAspect = KeyStep(KeyCode.RightBracket) - KeyStep(KeyCode.LeftBracket);
            if (dAspect != 0f)
            {
                config.manualTargetAspect = Mathf.Clamp(
                    config.manualTargetAspect * (1f + dAspect * config.adjustScaleStep * 0.2f * fine),
                    ArUcoConfig.MinManualTargetAspect, ArUcoConfig.MaxManualTargetAspect);
                MarkDirty();
            }

            // 맨키는 세트 전체에 걸린다. 현장에서 가장 먼저, 가장 많이 하는 일이 "전체를 책자 위로 밀기" 라
            // 아무것도 고르지 않은 상태의 방향키가 그 일을 한다. 마커 하나만 손볼 때 Ctrl 을 더한다.
            if (!IsCtrlHeld())
            {
                if (dx == 0f && dy == 0f && ds == 0f && dPage == 0f) return;

                set.Config.globalOffsetX += dx;
                set.Config.globalOffsetY += dy;
                set.Config.globalScale = Mathf.Max(0.05f, set.Config.globalScale + ds + dPage);

                MarkDirty();
                return;
            }

            // Ctrl 병행 — 선택한 마커(또는 그 안의 파일 한 장)만.
            if (_selectedMarkerId < 0) return;
            if (dx == 0f && dy == 0f && ds == 0f) return;

            var marker = set.Config.GetOrCreate(_selectedMarkerId);

            // 파일 한 장을 고른 상태면 그 장만 움직인다. 마커 값은 그대로 두어
            // 나중에 "이 마커 전체를 조금 더 위로" 가 여전히 가능하다.
            var item = SelectedItem(marker);

            if (item != null)
            {
                item.offsetX += dx;
                item.offsetY += dy;
                item.scale = Mathf.Max(0.05f, item.scale + ds);
            }
            else
            {
                marker.offsetX += dx;
                marker.offsetY += dy;
                marker.scale = Mathf.Max(0.05f, marker.scale + ds);
            }

            MarkDirty();
        }

        /// <summary>선택한 마커 폴더 안의 콘텐츠들. 마커를 고르지 않았으면 빈 목록이다.</summary>
        List<ContentEntry> SelectedEntries
            => _selectedMarkerId >= 0 && _app.Content != null
                ? _app.Content.GetEntries(_selectedMarkerId)
                : null;

        int SelectedEntryCount => SelectedEntries?.Count ?? 0;

        /// <summary>지금 조정 중인 파일의 배치값. 마커 전체를 고른 상태면 null 이다.</summary>
        ContentPlacement SelectedItem(MarkerConfig marker)
        {
            var entries = SelectedEntries;
            if (entries == null || _selectedItemIndex < 0 || _selectedItemIndex >= entries.Count) return null;

            return marker.GetOrCreateItem(entries[_selectedItemIndex].fileName, _selectedItemIndex);
        }

        /// <summary>
        /// 마커 전체 → 첫 장 → 둘째 장 → … → 다시 마커 전체 순으로 돈다.
        /// 콘텐츠가 한 장뿐이면 마커 전체와 그 장이 사실상 같으므로 오가도 헷갈릴 것이 없다.
        /// </summary>
        void SelectNextItem(int direction)
        {
            int count = SelectedEntryCount;
            if (count == 0)
            {
                _selectedItemIndex = -1;
                return;
            }

            // -1(마커 전체)을 목록의 한 자리로 넣어 순환시킨다.
            int index = _selectedItemIndex + 1 + direction;
            int size = count + 1;

            _selectedItemIndex = ((index % size) + size) % size - 1;
        }

        /// <summary>Backspace 는 세트 공통 배치를, Ctrl+Backspace 는 선택한 마커를 되돌린다(이동 키와 같은 규칙).</summary>
        void ResetPlacement(ArUcoSet set)
        {
            if (!IsCtrlHeld())
            {
                set.Config.ResetGlobalPlacement();
                MarkDirty();
                return;
            }

            if (_selectedMarkerId < 0) return;

            var marker = set.Config.GetOrCreate(_selectedMarkerId);
            var item = SelectedItem(marker);

            // 파일 한 장을 고른 상태면 그 장만 처음 자리로 되돌린다(둘째 장부터는 오른쪽 기본 자리).
            if (item != null) item.ResetPlacement(_selectedItemIndex);
            else marker.ResetPlacement();

            MarkDirty();
        }

        void SelectNextMarker(ArUcoSet set, int direction)
        {
            var visible = set.VisibleMarkerIds;
            if (visible.Count == 0) return;

            int index = IndexOf(visible, _selectedMarkerId);
            index = index < 0
                ? (direction > 0 ? 0 : visible.Count - 1)
                : ((index + direction) % visible.Count + visible.Count) % visible.Count;

            _selectedMarkerId = visible[index];

            // 다른 마커로 옮기면 장 선택은 의미가 없다. 마커 전체부터 다시 시작한다.
            _selectedItemIndex = -1;
        }

        // ── 코너 보정 ────────────────────────────────────────────────────────────

        void UpdateCalibration(ArUcoSet set)
        {
            // 이 단계에서는 마커를 고르지 않는다. 콘텐츠를 띄워 둔 채로 넘어오면
            // 배치에서 켜 둔 하이라이트가 그대로 남는다.
            set.HighlightMarkerId = -1;
            set.HighlightItemIndex = -1;

            set.Config.calibration.Validate();

            if (Input.GetKeyDown(actionKey)) AdvanceCalibration(set);
            else if (Input.GetKeyDown(manualKey) && _phase != CalibrationPhase.Manual) BeginManual(set);

            if (Input.GetKeyDown(resetMarkerKey)) ResetCalibration(set);

            switch (_phase)
            {
                case CalibrationPhase.Auto: EditAutoLayout(set); CollectAuto(set); break;
                case CalibrationPhase.Manual: EditManualLayout(set); UpdateManualMessage(set); break;
                default: EditFineAdjust(set); break;
            }

            DrawCalibrationOverlay(set);
        }

        void AdvanceCalibration(ArUcoSet set)
        {
            switch (_phase)
            {
                case CalibrationPhase.Idle:
                    BeginAuto(set);
                    break;

                case CalibrationPhase.Auto:
                    if (IsAutoReady()) ApplyAuto(set);
                    else if (AutoSeenCount() == 4) _calibrationMessage = "네 개 모두 보입니다. 값을 모으는 중이니 잠시 뒤 다시 Space.";
                    else BeginManual(set);   // 자동이 안 잡히면 사람이 짚는 방식으로 넘어간다.
                    break;

                case CalibrationPhase.Manual:
                    ConfirmManualPoint(set);
                    break;

                case CalibrationPhase.Verify:
                    _phase = CalibrationPhase.Idle;
                    _calibrationMessage = "보정을 마쳤습니다.";
                    break;
            }
        }

        /// <summary>
        /// 맨키는 보정값을 지운다(사각형 자리는 남김). Ctrl 은 자리까지 기본값으로.
        /// 수동 보정 도중에는 직전에 짚은 점 하나만 되돌린다 — 한 점 잘못 짚었다고 처음부터 다시 할 일이 없게.
        /// </summary>
        void ResetCalibration(ArUcoSet set)
        {
            var calibration = set.Config.calibration;
            bool all = IsCtrlHeld();

            if (!all && _phase == CalibrationPhase.Manual && _manualIndex > 0)
            {
                _manualIndex--;
                _pairCamera.RemoveAt(_pairCamera.Count - 1);
                _pairProjector.RemoveAt(_pairProjector.Count - 1);
                _calibrationMessage = $"{_manualIndex + 1}번 점을 다시 짚습니다.";
                return;
            }

            if (all) calibration.Reset();
            else calibration.ClearCameraPoints();

            set.RebuildProjection();
            _phase = CalibrationPhase.Idle;
            _calibrationMessage = all ? "보정값과 사각형 자리를 기본값으로 돌렸습니다." : "보정값을 지웠습니다. 사각형 자리는 남겨 둡니다.";
            MarkDirty();
        }

        /// <summary>
        /// 보정 위에 세부 조정을 덧댄다. 방향키 이동 · [ ] 회전 · + - 크기, Shift 미세, R 초기화.
        /// 보정 결과 전체가 통째로 움직이므로 모양(정사각)은 그대로다. 마커 꼭짓점 표시가 바로 따라오니 보면서 맞춘다.
        /// </summary>
        void EditFineAdjust(ArUcoSet set)
        {
            var calibration = set.Config.calibration;
            if (!calibration.IsUsable) return;

            if (Input.GetKeyDown(fineResetKey))
            {
                calibration.ResetFineAdjust();
                set.RebuildProjection();
                MarkDirty();
                _calibrationMessage = "세부 조정을 0 으로 되돌렸습니다.";
                return;
            }

            if (Input.GetKeyDown(modelKey))
            {
                CycleModel(set);
                return;
            }

            float fine = Speed();

            // 키는 프레임당 한 번만 읽는다(KeyStep 이 홀드 상태를 갱신한다). Ctrl 여부로 이동 / 기울기를 가른다.
            float h = KeyStep(KeyCode.RightArrow) - KeyStep(KeyCode.LeftArrow);
            float v = KeyStep(KeyCode.UpArrow) - KeyStep(KeyCode.DownArrow);
            float dr = (KeyStep(KeyCode.RightBracket) - KeyStep(KeyCode.LeftBracket)) * fineRotationStep * fine;

            float grow = KeyStep(KeyCode.Equals) + KeyStep(KeyCode.Plus) + KeyStep(KeyCode.KeypadPlus);
            float shrink = KeyStep(KeyCode.Minus) + KeyStep(KeyCode.KeypadMinus);
            float ds = (grow - shrink) * fineScaleStep * fine;

            if (h == 0f && v == 0f && dr == 0f && ds == 0f) return;

            if (IsCtrlHeld())
            {
                // Ctrl + → 오른쪽을 넓힘(왼쪽은 좁아짐), Ctrl + ↑ 위쪽을 넓힘.
                Vector2 tilt = calibration.fineTilt + new Vector2(h, v) * fineTiltStep * fine;
                float max = CalibrationConfig.MaxFineTilt;
                calibration.fineTilt = new Vector2(Mathf.Clamp(tilt.x, -max, max), Mathf.Clamp(tilt.y, -max, max));
            }
            else
            {
                calibration.fineOffset += new Vector2(h, -v) * calibrationPointStep * fine;   // y 는 아래로 커진다
            }

            calibration.fineRotation = Mathf.Clamp(calibration.fineRotation + dr, -45f, 45f);
            calibration.fineScale = Mathf.Clamp(calibration.fineScale + ds, 0.5f, 2f);

            set.RebuildProjection();
            MarkDirty();
        }

        /// <summary>
        /// 맞춤 방식을 정사각 → 평면 → 평면+휨 으로 돌린다. 저장된 대응점으로 곧바로 다시 푸므로
        /// 현장에서 같은 보정 점으로 세 방식을 바로 비교할 수 있다.
        /// </summary>
        void CycleModel(ArUcoSet set)
        {
            var calibration = set.Config.calibration;

            calibration.model = calibration.model == CalibrationConfig.ModelSquare ? CalibrationConfig.ModelPlane
                : calibration.model == CalibrationConfig.ModelPlane ? CalibrationConfig.ModelWarp
                : CalibrationConfig.ModelSquare;

            set.RebuildProjection();
            MarkDirty();

            string line = $"맞춤 방식을 '{ModelName(calibration.model)}' 으로 바꿨습니다.";

            if (calibration.HasPairs && set.View.Projection.TryGetPlaneError(set.View.CanvasSize, out float mean, out float max, out int worst))
                line += $"\n이 방식으로 남는 어긋남: 평균 {mean:0.0}px · 최대 {max:0.0}px ({worst + 1}번 점)";
            else if (!calibration.HasPairs)
                line += "\n예전 4점 보정값이라 방식이 적용되지 않습니다. 한 번 다시 보정하세요.";

            _calibrationMessage = line;
        }

        /// <summary>설정의 보정 자리를 작업용으로 복사한다. 보정을 새로 시작할 때 한 번.</summary>
        void BeginLayout(ArUcoSet set)
        {
            var points = set.Config.calibration.projectorPoints;
            for (int i = 0; i < 4; i++) _quad[i] = points[i];

            _selection = -1;
        }

        /// <summary>
        /// 옮긴 자리를 설정에 남긴다. 새 방식 보정은 대응점이 자기 프로젝터 좌표를 따로 들고 있으므로
        /// 자리를 옮겨도 기존 보정이 깨지지 않는다. 예전 4점 보정만은 이 자리와 짝이라 적용할 때까지 미룬다.
        /// </summary>
        void PersistLayout(ArUcoSet set)
        {
            var calibration = set.Config.calibration;
            if (calibration.valid && !calibration.HasPairs) return;

            calibration.projectorPoints = (Vector2[])_quad.Clone();
            MarkDirty();
        }

        /// <summary>
        /// 보정 자리 사각형을 옮긴다. Tab 으로 전체 / 귀퉁이 하나를 고르고, 방향키로 옮기고,
        /// PageUp/PageDown 으로 가운데를 기준으로 넓히거나 좁힌다. 자동 · 수동이 같은 조작을 쓴다.
        /// </summary>
        /// <returns>자리가 바뀌었으면 true.</returns>
        bool EditQuad(ArUcoSet set)
        {
            if (Input.GetKeyDown(nextMarkerKey))
            {
                // -1(전체) → 0 → 1 → 2 → 3 → -1
                int next = _selection + 1 + (IsShiftHeld() ? -1 : 1);
                _selection = (next % 5 + 5) % 5 - 1;
            }

            float fine = Speed();
            Vector2 step = PointStep(set) * fine;

            float dx = (KeyStep(KeyCode.RightArrow) - KeyStep(KeyCode.LeftArrow)) * step.x;
            float dy = (KeyStep(KeyCode.DownArrow) - KeyStep(KeyCode.UpArrow)) * step.y;   // 정규화 좌표는 위가 0
            float spread = (KeyStep(KeyCode.PageUp) - KeyStep(KeyCode.PageDown)) * _app.Config.adjustScaleStep * 0.2f * fine;

            bool changed = false;

            if (dx != 0f || dy != 0f)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (_selection >= 0 && i != _selection) continue;
                    _quad[i] = Clamp01(_quad[i] + new Vector2(dx, dy));
                }
                changed = true;
            }

            if (spread != 0f)
            {
                Vector2 center = (_quad[0] + _quad[1] + _quad[2] + _quad[3]) * 0.25f;
                for (int i = 0; i < 4; i++) _quad[i] = Clamp01(center + (_quad[i] - center) * (1f + spread));
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// 방향키 한 번에 움직일 양(정규화 좌표). 정규화 좌표는 가로 1 이 1920px, 세로 1 이 1080px 처럼 단위가 달라
        /// 같은 값을 쓰면 좌우가 상하보다 화면비만큼 더 움직인다. 가로를 화면비로 나눠 화면에서 같은 거리를 움직이게 한다
        /// (calibrationPointStep 은 화면 높이 기준이 된다 — 1080p 에서 약 5px).
        /// </summary>
        Vector2 PointStep(ArUcoSet set)
        {
            Vector2 canvas = set.View.CanvasSize;
            float aspect = canvas.y > 0f ? canvas.x / canvas.y : 1f;
            return new Vector2(calibrationPointStep / aspect, calibrationPointStep);
        }

        static Vector2 Clamp01(Vector2 p) => new Vector2(Mathf.Clamp01(p.x), Mathf.Clamp01(p.y));

        // ── 자동 보정 ──

        void BeginAuto(ArUcoSet set)
        {
            BeginLayout(set);
            _phase = CalibrationPhase.Auto;
            ResetAutoSampling();

            _calibrationMessage = "네 귀퉁이에 마커를 투사하고 있습니다. 자리와 크기를 맞추고, 모두 읽히면 Space.";
        }

        // 자리를 옮기면 그 전에 모은 값은 옛 자리의 것이다. 버리고, 카메라가 새 자리를 따라올 때까지 기다린다
        // (카메라 지연 + 검출 스무딩으로 몇 프레임 늦게 도착한다).
        void ResetAutoSampling()
        {
            for (int i = 0; i < 4; i++)
            {
                _autoCount[i] = 0;
                _autoSeen[i] = false;
                for (int m = 0; m < 4; m++) _autoAccum[i * 4 + m] = Vector2.zero;
            }

            _autoSettleUntil = Time.unscaledTime + autoSettleSeconds;
        }

        void EditAutoLayout(ArUcoSet set)
        {
            var calibration = set.Config.calibration;
            bool changed = EditQuad(set);

            float grow = KeyStep(KeyCode.Equals) + KeyStep(KeyCode.Plus) + KeyStep(KeyCode.KeypadPlus);
            float shrink = KeyStep(KeyCode.Minus) + KeyStep(KeyCode.KeypadMinus);
            float delta = (grow - shrink) * _app.Config.adjustScaleStep * 0.2f * Speed();

            if (delta != 0f)
            {
                calibration.autoMarkerSize = Mathf.Clamp(calibration.autoMarkerSize + delta,
                    CalibrationConfig.MinAutoMarkerSize, CalibrationConfig.MaxAutoMarkerSize);
                MarkDirty();
                changed = true;
            }

            if (!changed) return;

            PersistLayout(set);
            ResetAutoSampling();
        }

        /// <summary>
        /// 투사된 캘리브레이션 마커를 카메라가 읽어 꼭짓점을 모은다.
        /// 마커가 미세하게 떨리므로 여러 프레임을 평균낸다.
        ///
        /// 중심 하나가 아니라 꼭짓점 넷을 쓴다 — 마커 하나가 대응점 넷을 주므로 16쌍으로 풀게 되고,
        /// 원근이 걸린 사각형의 "꼭짓점 평균" 은 실제 중심과 어긋나는 문제도 없어진다.
        /// </summary>
        void CollectAuto(ArUcoSet set)
        {
            var ids = _app.Config.calibrationMarkerIds;
            if (ids == null || ids.Length < 4)
            {
                _calibrationMessage = "calibrationMarkerIds 에 마커 ID 4개가 필요합니다.";
                _phase = CalibrationPhase.Idle;
                return;
            }

            bool settled = Time.unscaledTime >= _autoSettleUntil;
            var markers = set.Tracker.Markers;

            for (int corner = 0; corner < 4; corner++)
            {
                _autoSeen[corner] = false;

                for (int m = 0; m < markers.Count; m++)
                {
                    var marker = markers[m];
                    if (!marker.visible || marker.id != ids[corner]) continue;

                    _autoSeen[corner] = true;

                    if (settled && _autoCount[corner] < autoSampleFrames)
                    {
                        int b = corner * 4;
                        _autoAccum[b] += marker.corner0;
                        _autoAccum[b + 1] += marker.corner1;
                        _autoAccum[b + 2] += marker.corner2;
                        _autoAccum[b + 3] += marker.corner3;
                        _autoCount[corner]++;
                    }
                    break;
                }
            }

            int seen = AutoSeenCount();

            if (IsAutoReady())
                _calibrationMessage = "네 개 모두 읽었습니다. Space 로 적용합니다.";
            else if (seen == 4)
                _calibrationMessage = settled ? "네 개 모두 보입니다. 값을 모으는 중…" : "자리를 옮겼습니다. 카메라가 따라오기를 기다립니다…";
            else
                _calibrationMessage =
                    $"읽힌 마커 {seen}/4 (테두리 빨강 = 안 읽힘).\n" +
                    "카메라 시야 안으로 옮기거나 크기를 키워 보세요. 투사면이 밝으면 인식되지 않습니다.\n" +
                    $"Space 또는 {manualKey} — 실물 마커로 짚는 수동 보정으로 넘어갑니다.";
        }

        int AutoSeenCount()
        {
            int seen = 0;
            for (int i = 0; i < 4; i++) if (_autoSeen[i]) seen++;
            return seen;
        }

        bool IsAutoReady()
        {
            for (int i = 0; i < 4; i++) if (_autoCount[i] < autoSampleFrames) return false;
            return true;
        }

        void ApplyAuto(ArUcoSet set)
        {
            var calibration = set.Config.calibration;
            Vector2 canvas = set.View.CanvasSize;

            // 투사한 마커의 꼭짓점이 화면 어디에 있는지. 텍스처는 흰 여백을 포함하므로 마커 자체는 그 안쪽이다.
            float half = 0.5f * calibration.autoMarkerSize * Mathf.Min(canvas.x, canvas.y) * ArUcoMarkerTexture.MarkerFraction();
            float hx = half / Mathf.Max(1f, canvas.x);
            float hy = half / Mathf.Max(1f, canvas.y);

            // 검출 꼭짓점 순서(좌상 → 우상 → 우하 → 좌하)와 같게. 정규화 좌표는 y 가 아래로 커진다.
            var offsets = new[] { new Vector2(-hx, -hy), new Vector2(hx, -hy), new Vector2(hx, hy), new Vector2(-hx, hy) };

            var cameras = new Vector2[16];
            var projectors = new Vector2[16];

            for (int corner = 0; corner < 4; corner++)
            {
                for (int m = 0; m < 4; m++)
                {
                    int i = corner * 4 + m;
                    cameras[i] = _autoAccum[i] / _autoCount[corner];
                    projectors[i] = _quad[corner] + offsets[m];
                }
            }

            ApplyCalibration(set, cameras, projectors, 4);
        }

        // ── 수동 보정 ──

        void BeginManual(ArUcoSet set)
        {
            // 자동에서 넘어오면 거기서 옮겨 둔 자리를 그대로 이어 쓴다.
            if (_phase != CalibrationPhase.Auto) BeginLayout(set);

            _phase = CalibrationPhase.Manual;
            _manualIndex = 0;
            _selection = -1;
            _pairCamera.Clear();
            _pairProjector.Clear();

            RebuildManualPoints(set);

            _calibrationMessage = "자리를 맞춘 뒤, 밝은 사각형 안에 마커를 맞춰 놓고 Space 를 누르세요.";
        }

        /// <summary>
        /// 보정 자리 사각형을 gridSize × gridSize 점으로 나눈다(좌상단부터 행 순서).
        /// 사각형을 사다리꼴로 옮겨도 점들이 원근에 맞게 고르게 퍼지도록 단위 정사각형의 사영으로 나눈다.
        /// </summary>
        void RebuildManualPoints(ArUcoSet set)
        {
            int n = set.Config.calibration.gridSize;
            var grid = ArUcoHomography.FromUnitSquare(_quad[0], _quad[1], _quad[2], _quad[3]);

            _points.Clear();

            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    var uv = new Vector2((float)i / (n - 1), (float)j / (n - 1));

                    if (!grid.IsValid || !grid.TryMap(uv, out Vector2 p))
                    {
                        // 사각형이 찌그러져 사영이 안 서면 단순 보간으로 대신한다.
                        Vector2 top = Vector2.Lerp(_quad[0], _quad[1], uv.x);
                        Vector2 bottom = Vector2.Lerp(_quad[3], _quad[2], uv.x);
                        p = Vector2.Lerp(top, bottom, uv.y);
                    }

                    _points.Add(Clamp01(p));
                }
            }
        }

        /// <summary>귀퉁이(0~3)가 격자 점 목록의 몇 번째인지.</summary>
        static int CornerPointIndex(int corner, int n)
        {
            switch (corner)
            {
                case 0: return 0;
                case 1: return n - 1;
                case 2: return n * n - 1;
                default: return n * (n - 1);
            }
        }

        /// <summary>
        /// 첫 점을 짚기 전에는 격자 전체(자리 · 넓이 · 점 수)를 고친다.
        /// 짚기 시작한 뒤에는 이미 짚은 점과의 짝이 깨지지 않도록 격자를 고정하고, 지금 짚을 점만 미세하게 옮긴다.
        /// </summary>
        void EditManualLayout(ArUcoSet set)
        {
            AdjustTargetSize();
            AdjustTargetAspect();

            if (_manualIndex >= _points.Count) return;

            if (_manualIndex == 0)
            {
                var calibration = set.Config.calibration;
                bool changed = EditQuad(set);

                int dg = (Input.GetKeyDown(KeyCode.RightBracket) ? 1 : 0) - (Input.GetKeyDown(KeyCode.LeftBracket) ? 1 : 0);
                if (dg != 0)
                {
                    calibration.gridSize = Mathf.Clamp(calibration.gridSize + dg,
                        CalibrationConfig.MinGridSize, CalibrationConfig.MaxGridSize);
                    MarkDirty();
                    changed = true;
                }

                if (!changed) return;

                RebuildManualPoints(set);
                PersistLayout(set);
                return;
            }

            Vector2 step = PointStep(set) * Speed();
            float dx = (KeyStep(KeyCode.RightArrow) - KeyStep(KeyCode.LeftArrow)) * step.x;
            float dy = (KeyStep(KeyCode.DownArrow) - KeyStep(KeyCode.UpArrow)) * step.y;
            if (dx == 0f && dy == 0f) return;

            _points[_manualIndex] = Clamp01(_points[_manualIndex] + new Vector2(dx, dy));
        }

        /// <summary>
        /// 마커 놓을 자리 사각형의 크기를 맞춘다.
        /// 실물 마커 크기와 프로젝터 거리에 따라 화면상 크기가 달라지므로 현장에서 정한다.
        /// 값은 세트가 아니라 전체 공통이다 — 같은 마커를 들고 세트를 옮겨 다니며 보정하기 때문이다.
        /// </summary>
        void AdjustTargetSize()
        {
            float grow = KeyStep(KeyCode.Equals) + KeyStep(KeyCode.Plus) + KeyStep(KeyCode.KeypadPlus);
            float shrink = KeyStep(KeyCode.Minus) + KeyStep(KeyCode.KeypadMinus);

            float delta = (grow - shrink) * _app.Config.adjustScaleStep * 0.2f * Speed();
            if (delta == 0f) return;

            _app.Config.manualTargetSize = Mathf.Clamp(
                _app.Config.manualTargetSize + delta,
                ArUcoConfig.MinManualTargetSize, ArUcoConfig.MaxManualTargetSize);

            MarkDirty();
        }

        /// <summary>
        /// 마커 놓을 자리 사각형의 가로세로 비율. 프로젝터가 비스듬히 비추면 화면의 정사각형이 책상 위에서
        /// 직사각형으로 늘어나므로, 책상 위에서 실물 마커와 같은 정사각형으로 보일 때까지 , / . 로 맞춘다.
        /// </summary>
        void AdjustTargetAspect()
        {
            float wider = KeyStep(KeyCode.Period) - KeyStep(KeyCode.Comma);
            if (wider == 0f) return;

            float step = _app.Config.adjustScaleStep * 0.2f * Speed();
            _app.Config.manualTargetAspect = Mathf.Clamp(
                _app.Config.manualTargetAspect * (1f + wider * step),
                ArUcoConfig.MinManualTargetAspect, ArUcoConfig.MaxManualTargetAspect);

            MarkDirty();
        }

        /// <summary>마커 놓을 자리 사각형의 가로 · 세로 크기(화면 짧은 변 = 1).</summary>
        Vector2 TargetSize()
        {
            float size = _app.Config.manualTargetSize;
            return new Vector2(size * _app.Config.manualTargetAspect, size);
        }

        /// <summary>
        /// 지금 짚을 자리에 "마커가 정확히 여기 놓이면 네 꼭짓점이 올 자리" 를 구한다.
        /// 보이는 마커의 꼭짓점을 지금 보정으로 옮긴 모양을 그대로 쓰고, 그 중심만 짚을 자리로 옮긴다 —
        /// 원근 · 회전이 반영된 모양이라 책상 위에서 실물 마커와 겹친다. 꼭짓점을 맞추면 중심이 정확히 그 자리에 온다.
        /// 보정값이 아직 없거나 마커가 안 보이면 false.
        /// </summary>
        bool TryGetCornerGuide(ArUcoSet set, Vector2 target, Vector2[] corners)
        {
            if (!set.Config.calibration.IsUsable) return false;
            if (!TryGetLargestVisible(set, out var marker)) return false;

            var projection = set.View.Projection;
            if (!projection.TryMap(ProjectiveCenter(marker), out Vector2 center)) return false;
            if (!projection.TryMap(marker.corner0, out corners[0])) return false;
            if (!projection.TryMap(marker.corner1, out corners[1])) return false;
            if (!projection.TryMap(marker.corner2, out corners[2])) return false;
            if (!projection.TryMap(marker.corner3, out corners[3])) return false;

            for (int i = 0; i < 4; i++) corners[i] = corners[i] - center + target;
            return true;
        }

        /// <summary>
        /// 화면에서 가장 크게 잡힌, 지금 실제로 보이는 마커. 어떤 ID 든 상관없다.
        /// (Tracker 가 크기 내림차순으로 정렬해 둔다. 놓친 뒤 붙잡아 두는 중인 마커는 건너뛴다.)
        /// </summary>
        static bool TryGetLargestVisible(ArUcoSet set, out ArUcoMarkerTracker.TrackedMarker marker)
        {
            var markers = set.Tracker.Markers;
            for (int i = 0; i < markers.Count; i++)
            {
                if (!markers[i].visible) continue;

                marker = markers[i];
                return true;
            }

            marker = default;
            return false;
        }

        /// <summary>
        /// 마커의 실제 중심 = 두 대각선의 교점. 원근이 걸리면 꼭짓점 넷의 평균은 중심에서 벗어난다
        /// (카메라에서 먼 쪽이 작게 찍히므로 평균이 가까운 쪽으로 끌려간다).
        /// </summary>
        static Vector2 ProjectiveCenter(in ArUcoMarkerTracker.TrackedMarker m)
        {
            Vector2 d1 = m.corner2 - m.corner0;
            Vector2 d2 = m.corner3 - m.corner1;
            float den = d1.x * d2.y - d1.y * d2.x;
            if (Mathf.Abs(den) < 1e-6f) return m.center;

            Vector2 w = m.corner1 - m.corner0;
            float t = (w.x * d2.y - w.y * d2.x) / den;
            return m.corner0 + d1 * t;
        }

        void UpdateManualMessage(ArUcoSet set)
        {
            if (_manualIndex >= _points.Count) return;

            string progress = $"{_manualIndex + 1}/{_points.Count}";
            _calibrationMessage = TryGetLargestVisible(set, out _)
                ? $"{progress} — 마커가 보입니다. 사각형에 맞았으면 Space 를 누르세요."
                : $"{progress} — 사각형 안에 마커를 놓으세요. (마커가 보이지 않습니다)";
        }

        void ConfirmManualPoint(ArUcoSet set)
        {
            if (_manualIndex >= _points.Count) return;

            if (!TryGetLargestVisible(set, out var marker))
            {
                _calibrationMessage = "마커가 보이지 않습니다.";
                return;
            }

            _pairCamera.Add(ProjectiveCenter(marker));
            _pairProjector.Add(_points[_manualIndex]);
            _manualIndex++;

            if (_manualIndex < _points.Count) return;

            // 짚는 동안 귀퉁이 점을 미세하게 옮겼다면 다음 보정의 자리도 그쪽으로 따라간다.
            int n = set.Config.calibration.gridSize;
            for (int c = 0; c < 4; c++) _quad[c] = _points[CornerPointIndex(c, n)];

            ApplyCalibration(set, _pairCamera.ToArray(), _pairProjector.ToArray(), 1);
        }

        // ── 적용 · 검증 ──

        void ApplyCalibration(ArUcoSet set, Vector2[] cameras, Vector2[] projectors, int pairsPerPoint)
        {
            var calibration = set.Config.calibration;
            Vector2 canvas = set.View.CanvasSize;

            calibration.projectorPoints = (Vector2[])_quad.Clone();
            calibration.cameraPoints = new Vector2[0];   // 예전 4점 값은 더 쓰지 않는다
            calibration.pairCamera = cameras;
            calibration.pairProjector = projectors;
            calibration.pairsPerPoint = pairsPerPoint;
            if (canvas.x > 0f && canvas.y > 0f) calibration.projectorAspect = canvas.x / canvas.y;
            calibration.valid = true;

            set.RebuildProjection();
            MarkDirty();

            _phase = CalibrationPhase.Verify;

            int points = cameras.Length / pairsPerPoint;
            var message = new StringBuilder();
            message.AppendLine($"보정을 적용했습니다 ({points}점).");

            // 평면 변환은 4점이면 정확히 지나가므로 잴 것이 없다. 정사각 모델은 4점이어도 남는다.
            bool square = calibration.model == CalibrationConfig.ModelSquare;
            if ((square || points > 4) && set.View.Projection.TryGetPlaneError(canvas, out float mean, out float max, out int worst))
            {
                string model = square ? "정사각 맞춤(이동 · 회전 · 크기)" : "평면 맞춤";
                string tail = calibration.model == CalibrationConfig.ModelWarp ? " — 점별 휨 보정으로 흡수했습니다." : "";
                message.AppendLine($"{model} 후 남는 어긋남: 평균 {mean:0.0}px · 최대 {max:0.0}px ({worst + 1}번 점){tail}");

                if (square && mean > 15f)
                    message.AppendLine("<color=#ffd633>전체적으로 크면 카메라나 프로젝터가 비스듬히 달린 것입니다. 정면 위에서 수직으로 달수록 줄어듭니다.</color>");

                if (max > Mathf.Max(20f, mean * 3f))
                    message.AppendLine($"<color=#ffd633>{worst + 1}번 점만 유독 큽니다. 잘못 짚었을 수 있으니 다시 잡아 보세요.</color>");
            }

            message.Append("책자 마커를 여기저기 놓아 보세요. 꼭짓점 표시가 실물 꼭짓점에 맞으면 Space, 어긋나면 Backspace 로 지우고 다시.");
            _calibrationMessage = message.ToString();
        }

        void DrawCalibrationOverlay(ArUcoSet set)
        {
            var view = set.View;
            view.BeginOverlay();

            switch (_phase)
            {
                case CalibrationPhase.Auto:
                {
                    var ids = _app.Config.calibrationMarkerIds;
                    float size = set.Config.calibration.autoMarkerSize;
                    float frameSize = size * 1.3f;

                    for (int i = 0; i < 4; i++)
                    {
                        // 테두리는 마커 바깥에 그린다. 마커 면 위로 빛이 겹치면 인식이 떨어진다.
                        // 빨강 = 안 읽힘, 초록 = 읽힘, 노랑 = 지금 옮기는 귀퉁이.
                        Color tint = i == _selection ? new Color(1f, 0.85f, 0.2f)
                            : _autoSeen[i] ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.3f, 0.3f);
                        view.DrawOverlay(_quad[i], frameSize, TargetFrame(frameSize, view.CanvasSize), tint);

                        // 프로젝터가 마커를 직접 쏜다. 카메라는 이 빛을 읽는다.
                        if (ids == null || ids.Length <= i) continue;

                        var texture = ArUcoMarkerTexture.Get(_app.Config.dictionaryId, ids[i]);
                        view.DrawOverlay(_quad[i], size, texture, Color.white);
                    }
                    break;
                }

                case CalibrationPhase.Manual:
                {
                    // 지금 짚어야 할 점만 밝게, 나머지는 흐리게.
                    // 모든 자리를 같은 크기로 그린다 — 실물 마커를 맞춰 놓을 자리이기 때문이다.
                    Vector2 size = TargetSize();
                    var frame = TargetFrame(Mathf.Min(size.x, size.y), view.CanvasSize);
                    int n = set.Config.calibration.gridSize;
                    int selected = _manualIndex == 0 && _selection >= 0 ? CornerPointIndex(_selection, n) : -1;

                    // 보정값이 있으면 지금 점에 꼭짓점 안내 십자를 띄운다. 그때는 사각형을 흐리게 둔다(기준은 십자다).
                    bool guide = _manualIndex < _points.Count && TryGetCornerGuide(set, _points[_manualIndex], _guide);

                    for (int i = 0; i < _points.Count; i++)
                    {
                        Color tint = i < _manualIndex ? new Color(0.3f, 1f, 0.4f, 0.9f)
                            : i == selected ? new Color(1f, 0.85f, 0.2f)
                            : i == _manualIndex ? (guide ? new Color(1f, 1f, 1f, 0.35f) : Color.white)
                            : new Color(1f, 1f, 1f, 0.25f);

                        view.DrawOverlay(_points[i], size, frame, tint);
                    }

                    if (guide)
                    {
                        var color = new Color(0.3f, 0.9f, 1f, 0.95f);
                        for (int i = 0; i < 4; i++) view.DrawOverlay(_guide[i], 0.03f, Crosshair(), color);
                    }
                    break;
                }

                default:
                    DrawVerifyMarks(set);
                    break;
            }

            view.EndOverlay();
        }

        /// <summary>
        /// 카메라에 보이는 마커마다 네 꼭짓점을 지금 보정으로 옮겨 표시한다.
        /// 실물 마커의 꼭짓점과 표시가 겹치면 그 자리의 보정이 맞은 것이다 — 책자를 여기저기 옮겨 가며
        /// 가장자리까지 맞는지 본다. 보정 점 위만이 아니라 아무 자리에서나 확인할 수 있다.
        /// </summary>
        void DrawVerifyMarks(ArUcoSet set)
        {
            var view = set.View;
            var projection = view.Projection;
            var markers = set.Tracker.Markers;
            var color = new Color(0.3f, 0.9f, 1f, 0.95f);

            // 보정에 쓴 자리를 흐리게 다시 띄운다. 그 자리에 마커를 놓고 꼭짓점 표시가 실물 마커 꼭짓점에
            // 겹치는지 본다 — 보정 점 위에서 실제로 맞는지 바로 확인한다.
            var calibration = set.Config.calibration;
            if (calibration.IsUsable && calibration.HasPairs)
            {
                Vector2 size = TargetSize();
                var frame = TargetFrame(Mathf.Min(size.x, size.y), view.CanvasSize);
                var faint = new Color(1f, 1f, 1f, 0.3f);
                int per = calibration.pairsPerPoint;

                for (int k = 0; k < calibration.pairProjector.Length / per; k++)
                {
                    Vector2 node = Vector2.zero;
                    for (int m = 0; m < per; m++) node += calibration.pairProjector[k * per + m];
                    view.DrawOverlay(node / per, size, frame, faint);
                }
            }

            for (int i = 0; i < markers.Count; i++)
            {
                var m = markers[i];
                if (!m.visible) continue;

                if (projection.TryMap(m.corner0, out var p0)) view.DrawOverlay(p0, 0.03f, Crosshair(), color);
                if (projection.TryMap(m.corner1, out var p1)) view.DrawOverlay(p1, 0.03f, Crosshair(), color);
                if (projection.TryMap(m.corner2, out var p2)) view.DrawOverlay(p2, 0.03f, Crosshair(), color);
                if (projection.TryMap(m.corner3, out var p3)) view.DrawOverlay(p3, 0.03f, Crosshair(), color);
            }
        }

        // 조준점. 가운데가 비어 있어야 마커를 정확히 겹칠 수 있다.
        Texture2D Crosshair()
        {
            if (_crosshair != null) return _crosshair;

            const int size = 64;
            const int thickness = 4;
            const int gap = 10;

            _crosshair = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _crosshair.filterMode = FilterMode.Bilinear;
            _crosshair.wrapMode = TextureWrapMode.Clamp;

            var pixels = new Color32[size * size];
            int center = size / 2;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool onVertical = Mathf.Abs(x - center) < thickness && Mathf.Abs(y - center) >= gap;
                    bool onHorizontal = Mathf.Abs(y - center) < thickness && Mathf.Abs(x - center) >= gap;
                    bool visible = onVertical || onHorizontal;

                    pixels[y * size + x] = visible ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
                }
            }

            _crosshair.SetPixels32(pixels);
            _crosshair.Apply();
            return _crosshair;
        }

        /// <summary>
        /// 마커를 놓을 자리를 표시하는 사각형. 네 모서리만 그려 마커 면 위에 빛이 겹치지 않게 한다
        /// (마커 위로 투사광이 떨어지면 흑백 대비가 무너져 인식률이 떨어진다).
        ///
        /// 사각형을 키우면 텍스처가 함께 늘어나 선까지 굵어지므로, 화면에 그려질 두께가
        /// 일정해지도록 텍스처 안에서의 두께를 역으로 구한다. 그 값이 달라질 때만 다시 만든다.
        /// </summary>
        /// <param name="sizeNormalized">화면 짧은 변을 1 로 보는 사각형 크기</param>
        Texture2D TargetFrame(float sizeNormalized, Vector2 canvasSize)
        {
            const int size = 128;
            const float linePixels = 3f;    // 화면에 그려질 선 두께
            const float armRatio = 0.28f;   // 모서리에서 뻗어 나가는 길이(한 변 대비)

            float sidePixels = Mathf.Max(1f, sizeNormalized * Mathf.Min(canvasSize.x, canvasSize.y));
            int thickness = Mathf.Clamp(Mathf.RoundToInt(linePixels / sidePixels * size), 1, size / 6);

            if (_frame != null && thickness == _frameThickness) return _frame;

            if (_frame == null)
            {
                _frame = new Texture2D(size, size, TextureFormat.RGBA32, false);
                _frame.filterMode = FilterMode.Bilinear;
                _frame.wrapMode = TextureWrapMode.Clamp;
            }

            int arm = Mathf.RoundToInt(size * armRatio);
            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                bool onHorizontalEdge = y < thickness || y >= size - thickness;
                bool withinVerticalArm = y < arm || y >= size - arm;

                for (int x = 0; x < size; x++)
                {
                    bool onVerticalEdge = x < thickness || x >= size - thickness;
                    bool withinHorizontalArm = x < arm || x >= size - arm;

                    bool visible = (onHorizontalEdge && withinHorizontalArm)
                                || (onVerticalEdge && withinVerticalArm);

                    pixels[y * size + x] = visible
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(255, 255, 255, 0);
                }
            }

            _frame.SetPixels32(pixels);
            _frame.Apply();
            _frameThickness = thickness;

            return _frame;
        }

        // ── 저장 ─────────────────────────────────────────────────────────────────

        void MarkDirty()
        {
            _dirty = true;
            _dirtySince = Time.unscaledTime;
        }

        void FlushIfDirty(bool force)
        {
            if (!_dirty) return;
            if (!force && Time.unscaledTime - _dirtySince < autoSaveDelay) return;

            _app.SaveConfig();
            _dirty = false;
        }

        // ── HUD ──────────────────────────────────────────────────────────────────

        void UpdateHud(ArUcoSet set)
        {
            _builder.Clear();
            // 같은 방향키가 단계마다 다른 것을 움직이므로(배치: 영상 / 보정: 보정 결과 전체) 지금 단계를 맨 앞에 크게 둔다.
            string stageLabel = _stage == Stage.Placement
                ? "<color=#88ff88><b>[마커 배치 — 영상 위치]</b></color>"
                : "<color=#ff9966><b>[코너 보정 — 방향키가 보정 전체를 움직임]</b></color>";

            _builder.Append($"<color=#ffd633><b>편집모드</b></color> {stageLabel}   <color=#888888>{toggleKey} 나가기 · {stageKey} 단계 · " +
                            $"{hudKey} 안내판 {(_hudSlot == HudSlotCompact ? "펼치기" : "옮기기")}" +
                            $"{(_help != null && !_help.IsVisible ? $" · {_help.ToggleKey} 단축키" : "")}</color>");

            // 접은 상태 — 마커를 가리지 않게 첫 줄만 남긴다. 저장 대기 표시는 놓치면 안 되므로 같이 둔다.
            if (_hudSlot == HudSlotCompact)
            {
                if (_dirty) _builder.Append("   <color=#ffd633>* 저장 대기</color>");
                set.View.ShowHud(_builder.ToString(), HudAnchorFor(_hudSlot));
                HideOtherHuds();
                return;
            }

            _builder.AppendLine();

            AppendSetLine();
            _builder.AppendLine();

            if (_stage == Stage.Placement) AppendPlacement(set);
            else AppendCalibration(set);

            _builder.AppendLine();
            _builder.AppendLine($"<color=#888888>{cameraPreviewKey} 카메라영상 {(set.CameraPreviewEnabled ? "끄기" : "켜기")}" +
                                $" · {contentKey} 콘텐츠 {(_showContent ? "숨기기" : "보이기")}" +
                                $" · {outlineKey} 마커테두리 · {reloadContentKey} 콘텐츠 다시읽기</color>");

            // 나가도 계속 투사되는 값이라 켜 둔 채로 자리를 뜨기 쉽다. 눈에 띄게 남긴다.
            if (set.CameraPreviewEnabled)
                _builder.AppendLine("<color=#ffd633>카메라영상이 켜져 있습니다 — 편집모드를 나가도 계속 투사됩니다.</color>");

            AppendCaptureLine(set);

            var visible = set.VisibleMarkerIds;
            _builder.Append(visible.Count > 0
                ? $"<color=#88ff88>보이는 마커: {string.Join(", ", visible)}</color>"
                : $"<color=#ff8888>마커가 보이지 않습니다 (후보 {set.Tracker.RejectedCount}개)</color>");

            if (_dirty) _builder.Append("   <color=#ffd633>* 저장 대기</color>");

            // 편집 중에는 HUD 가 이 세트의 화면을 쓰고 있으므로 단축키 안내를 따로 띄우지 않고 여기에 이어 붙인다.
            if (_help != null && _help.IsVisible)
            {
                _builder.AppendLine();
                _builder.AppendLine();
                _help.AppendHelp(_builder);
            }

            set.View.ShowHud(_builder.ToString(), HudAnchorFor(_hudSlot));
            HideOtherHuds();
        }

        /// <summary>
        /// 편집모드 안에서 쓰는 키 전부. 단축키 안내(F1)가 읽는다.
        /// 필드 값을 그대로 쓰므로 인스펙터에서 키를 바꿔도 안내가 따라온다.
        /// </summary>
        public void AppendKeyHelp(StringBuilder b)
        {
            const string k = "<color=#ffd633>";
            const string d = "</color><pos=46%>";

            b.AppendLine($"{k}{stageKey}{d}단계 전환 (마커 배치 ↔ 코너 보정)");
            b.AppendLine($"{k}1 ~ 9{d}조정할 세트 선택");
            b.AppendLine($"{k}{nextMarkerKey} / Shift+{nextMarkerKey}{d}마커 선택");
            b.AppendLine($"{k}, / .{d}그 마커 안에서 파일 선택 (마커 전체 ↔ 파일 한 장)");
            b.AppendLine($"{k}← → ↑ ↓  + -{d}세트 전체 위치 · 크기");
            b.AppendLine($"{k}Ctrl + ← → ↑ ↓  + -{d}선택한 대상 위치 · 크기");
            b.AppendLine($"{k}PageUp / PageDown{d}세트 전체 배율");
            b.AppendLine($"{k}[ ]  (마커 배치){d}책상 위 가로세로 비율 (영상이 돌 때 늘어나 보이면)");
            b.AppendLine($"{k}{actionKey}{d}코너 보정 진행 (자동 → 적용 · 안 읽히면 수동 → 점 확정 → 완료)");
            b.AppendLine($"{k}{manualKey}{d}곧바로 수동 보정 (실물 마커로 격자 점 짚기)");
            b.AppendLine($"{k}{nextMarkerKey}  ← → ↑ ↓  PageUp/Down  (보정 중){d}자리 사각형: 전체/귀퉁이 고르기 · 옮기기 · 넓히기");
            b.AppendLine($"{k}+ -  (보정 중){d}투사 마커 크기 (자동) / 놓을 자리 크기 (수동)");
            b.AppendLine($"{k}[ ]  (수동, 첫 점 전){d}격자 점 수 (2×2 ~ 5×5)");
            b.AppendLine($"{k}, / .  (수동 보정 중){d}놓을 자리 가로세로 비율 (책상 위에서 정사각형이 되게)");
            b.AppendLine($"{k}{resetMarkerKey}  (수동 보정 중){d}직전에 짚은 점 다시");
            b.AppendLine($"{k}← → ↑ ↓  [ ]  + -  (보정 후){d}보정 세부 조정: 전체 이동 · 회전 · 크기 ({fineResetKey} 초기화)");
            b.AppendLine($"{k}Ctrl + ← → ↑ ↓  (보정 후){d}원근 기울기: 그쪽 끝을 넓힘 (반대쪽은 좁아짐)");
            b.AppendLine($"{k}{modelKey}  (보정 단계){d}맞춤 방식 전환: 정사각 → 평면(원근) → 평면+휨");
            b.AppendLine($"{k}{resetMarkerKey} / Ctrl+{resetMarkerKey}{d}세트 전체 초기화 / 선택한 대상 초기화");
            b.AppendLine($"{k}{contentKey}  {outlineKey}  {reloadContentKey}{d}콘텐츠 표시 · 마커 테두리 · 콘텐츠 다시 읽기");
            b.AppendLine($"{k}{hudKey}{d}안내판 자리 옮기기 (네 귀퉁이 → 한 줄로 접기)");
            b.AppendLine($"{k}{KeyName(saveKey)}{d}즉시 저장 (그 밖에는 {autoSaveDelay:0.#}초 무입력 시 자동 저장)");
            b.AppendLine($"{k}Shift 병행{d}{fastMultiplier:0}배속");
        }

        // KeyCode 이름 중 키캡과 다른 것만 바꾼다.
        public static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Return: return "Enter";
                case KeyCode.Escape: return "Esc";
                default: return key.ToString();
            }
        }

        // HUD 는 선택한 세트에만 띄운다. 다른 세트는 전시 상태를 유지한다.
        void HideOtherHuds()
        {
            for (int i = 0; i < _app.Sets.Count; i++)
            {
                if (i != _setIndex) _app.Sets[i].View.ShowHud(null);
            }
        }

        HudAnchor HudAnchorFor(int slot)
        {
            switch (slot)
            {
                case 1: return HudAnchor.TopRight;
                case 2: return HudAnchor.BottomRight;
                case 3: return HudAnchor.BottomLeft;
                case 4: return HudAnchor.TopLeft;
                // 기본과 접기 — 코너 보정 중에는 네 귀퉁이 조준점을 가리지 않도록 가운데로 둔다.
                default: return _stage == Stage.Calibration ? HudAnchor.Center : HudAnchor.TopLeft;
            }
        }

        /// <summary>
        /// 카메라가 실제로 어떻게 열렸고 몇 fps 로 들어오는지. 캡처는 워커 스레드에서 도므로
        /// 이 값이 낮아도 화면은 멈추지 않지만, 마커 반응 속도는 그대로 이 값을 따라간다.
        /// 현장에서 "반응이 굼뜨다" 의 원인이 카메라인지 화면인지 여기서 바로 갈린다.
        /// </summary>
        void AppendCaptureLine(ArUcoSet set)
        {
            var tracker = set.Tracker;
            if (tracker == null || !tracker.IsInitialized) return;

            float captureFps = tracker.CaptureFps;
            string color = captureFps >= 20f ? "#888888" : "#ffd633";

            _builder.AppendLine($"<color={color}>카메라 {tracker.CaptureFormat} — " +
                                $"실측 {captureFps:0}fps · 검출 {tracker.DetectMs:0.0}ms · " +
                                $"대기 {tracker.CaptureWaitMs:0.0}ms (워커 스레드)</color>");
            _builder.AppendLine($"<color=#888888>화면 {1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f):0}fps</color>");

            // 편집모드 안에서는 세지 않으므로 들어오기 전까지 센 수다.
            // "기록이 되고 있나" 를 파일을 열지 않고 확인하는 용도다.
            int views = set.ViewsCountedThisRun;
            _builder.AppendLine(views < 0
                ? "<color=#888888>관람 기록 꺼짐 (recordViews)</color>"
                : $"<color=#888888>이번 실행 관람 {views}회 기록</color>");
        }

        void AppendSetLine()
        {
            if (_app.Sets.Count <= 1)
            {
                _builder.AppendLine($"세트 <b>{CurrentSet.Config.name}</b>");
                return;
            }

            _builder.Append("세트 ");
            for (int i = 0; i < _app.Sets.Count; i++)
            {
                string name = _app.Sets[i].Config.name;
                _builder.Append(i == _setIndex
                    ? $"<color=#ffd633><b>[{i + 1}:{name}]</b></color> "
                    : $"<color=#888888>{i + 1}:{name}</color> ");
            }
            _builder.AppendLine("  <color=#888888>숫자키로 전환</color>");
        }

        void AppendPlacement(ArUcoSet set)
        {
            _builder.AppendLine("<b>마커 배치</b>   <color=#888888>방향키는 책자 기준 (책자를 프로젝터 정면으로 놓고)</color>");

            _builder.AppendLine($"<color=#ffd633>세트 공통</color>  " +
                                $"크기 <b>{set.Config.globalScale:0.00}</b>  " +
                                $"좌우 <b>{set.Config.globalOffsetX:+0.00;-0.00; 0.00}</b>  " +
                                $"상하 <b>{set.Config.globalOffsetY:+0.00;-0.00; 0.00}</b>" +
                                $"   <color=#888888>← → ↑ ↓ · + - · {resetMarkerKey} 초기화</color>");
            _builder.AppendLine();

            if (_selectedMarkerId < 0)
            {
                _builder.AppendLine("마커 하나만 손보려면 카메라에 비춘 뒤 Tab 으로 고르고 <b>Ctrl</b> 을 누른 채 조정하세요.");
            }
            else
            {
                var marker = set.Config.GetOrCreate(_selectedMarkerId);
                bool onScreen = IndexOf(set.VisibleMarkerIds, _selectedMarkerId) >= 0;
                string folder = _app.Content?.GetFolderName(_selectedMarkerId);

                _builder.AppendLine($"선택  <b>{_selectedMarkerId}번</b>" +
                                    $"{(string.IsNullOrEmpty(folder) ? "" : $"  <color=#888888>{folder}</color>")}" +
                                    $"{(onScreen ? "" : "  <color=#ff8888>(화면에 없음)</color>")}   <color=#888888>Tab 다음</color>");

                AppendItemLine();

                // 지금 키가 걸리는 대상의 값만 보여준다. 마커 값과 파일 값을 함께 늘어놓으면
                // 어느 쪽이 움직이는지 헷갈린다.
                var item = SelectedItem(marker);

                float scale = item != null ? item.scale : marker.scale;
                float offsetX = item != null ? item.offsetX : marker.offsetX;
                float offsetY = item != null ? item.offsetY : marker.offsetY;

                _builder.AppendLine($"크기  <b>{scale:0.00}</b>   <color=#888888>Ctrl + / -</color>");
                _builder.AppendLine($"좌우  <b>{offsetX:+0.00;-0.00; 0.00}</b>   <color=#888888>Ctrl ← / →</color>");
                _builder.AppendLine($"상하  <b>{offsetY:+0.00;-0.00; 0.00}</b>   <color=#888888>Ctrl ↑ / ↓</color>");
                _builder.AppendLine($"<color=#888888>Ctrl 를 누른 동안만 이 마커에 걸립니다 · Ctrl+{resetMarkerKey} 초기화</color>");
            }

            _builder.AppendLine();
            _builder.AppendLine($"책상 위 가로세로  <b>{_app.Config.manualTargetAspect:0.000}</b>   " +
                                "<color=#888888>[ / ] — 영상을 돌렸을 때 한쪽으로 늘어나 보이면 반듯해질 때까지</color>");
            _builder.AppendLine("<color=#888888>영상은 위치 · 크기만 조정합니다(모양 · 비율 고정, 크기는 마커와 무관, 회전은 마커를 따름).</color>");
            _builder.AppendLine($"<color=#888888>Shift 병행 {fastMultiplier:0}배속 · PageUp/PageDown 세트 크기 · {KeyName(saveKey)} 즉시저장</color>");
        }

        /// <summary>
        /// 이 마커 폴더에 무엇이 들어 있고 그중 어느 장을 만지는 중인지 보여준다.
        /// 파일 이름이 곧 보정값의 키라(→ ContentPlacement.file) 이름을 그대로 띄운다.
        /// </summary>
        void AppendItemLine()
        {
            var entries = SelectedEntries;

            if (entries == null || entries.Count == 0)
            {
                _builder.AppendLine("<color=#ff8888>이 폴더에 콘텐츠가 없습니다.</color>");
                return;
            }

            _builder.Append("콘텐츠  ");
            _builder.Append(_selectedItemIndex < 0
                ? "<color=#ffd633><b>[마커 전체]</b></color> "
                : "<color=#888888>마커 전체</color> ");

            for (int i = 0; i < entries.Count; i++)
            {
                string name = entries[i].fileName;
                string kind = entries[i].IsVideo ? "▶" : "▣";

                _builder.Append(i == _selectedItemIndex
                    ? $"<color=#ffd633><b>[{kind} {name}]</b></color> "
                    : $"<color=#888888>{kind} {name}</color> ");
            }

            _builder.AppendLine($"  <color=#888888>{prevItemKey} / {nextItemKey} 로 고름</color>");
        }

        void AppendCalibration(ArUcoSet set)
        {
            bool calibrated = set.Config.calibration.IsUsable;

            _builder.AppendLine($"<b>코너 보정</b>   " +
                                (calibrated ? "<color=#88ff88>보정됨</color>" : "<color=#ff8888>보정 안 됨</color>"));

            var calibration = set.Config.calibration;

            switch (_phase)
            {
                case CalibrationPhase.Idle:
                    _builder.AppendLine("카메라가 본 자리를 프로젝터 좌표로 옮기는 표를 만듭니다.");
                    _builder.AppendLine($"<color=#888888>{actionKey} — 네 귀퉁이에 마커를 투사해 자동으로 잡습니다 · " +
                                        $"{manualKey} — 실물 마커로 {calibration.gridSize}×{calibration.gridSize} 점을 짚습니다</color>");
                    _builder.AppendLine($"<color=#888888>맞춤 방식  {ModelName(calibration.model)}  (aruco.json calibration.model)</color>");
                    if (calibrated)
                        _builder.AppendLine("<color=#888888>카메라에 보이는 마커의 네 꼭짓점에 표시가 찍힙니다 — 실물 꼭짓점과 겹치는지 보세요.</color>");
                    AppendFineAdjustLine(set);
                    break;

                case CalibrationPhase.Auto:
                {
                    int collected = autoSampleFrames;
                    for (int i = 0; i < 4; i++) collected = Mathf.Min(collected, _autoCount[i]);

                    _builder.AppendLine($"자동 보정  읽힘 <b>{AutoSeenCount()}/4</b> · 수집 {collected}/{autoSampleFrames}");
                    _builder.AppendLine($"마커 크기  <b>{calibration.autoMarkerSize:0.00}</b>   <color=#888888>+ / -</color>");
                    AppendSelectionLine();
                    break;
                }

                case CalibrationPhase.Manual:
                {
                    int n = calibration.gridSize;
                    _builder.AppendLine($"수동 보정  <b>{Mathf.Min(_manualIndex + 1, _points.Count)}/{_points.Count}</b>" +
                                        $"   격자 {n}×{n}{(_manualIndex == 0 ? "  <color=#888888>[ / ] 로 점 수</color>" : "")}");
                    _builder.AppendLine($"자리 비율  <b>{_app.Config.manualTargetAspect:0.00}</b>" +
                                        "   <color=#888888>, / . 로 책상 위에서 정사각형이 되게 (Shift 미세)</color>");
                    if (calibration.IsUsable)
                        _builder.AppendLine("<color=#88ddff>하늘색 십자 4개 = 마커가 이 자리에 정확히 놓였을 때 꼭짓점 자리. 마커 꼭짓점을 십자에 맞추세요.</color>");
                    _builder.AppendLine($"자리 크기  <b>{_app.Config.manualTargetSize:0.00}</b>" +
                                        $"   <color=#888888>+ / - 로 실물 마커에 맞춤 (Shift 미세)</color>");

                    if (_manualIndex == 0)
                    {
                        AppendSelectionLine();
                    }
                    else if (_manualIndex < _points.Count)
                    {
                        var p = _points[_manualIndex];
                        _builder.AppendLine($"자리  <b>{p.x:0.000}, {p.y:0.000}</b>" +
                                            $"   <color=#888888>← → ↑ ↓ 이 점만 미세 이동 · {resetMarkerKey} 직전 점 다시</color>");
                    }
                    break;
                }

                case CalibrationPhase.Verify:
                    _builder.AppendLine("<color=#88ff88>검증</color>");
                    AppendFineAdjustLine(set);
                    break;
            }

            if (!string.IsNullOrEmpty(_calibrationMessage)) _builder.AppendLine(_calibrationMessage);

            _builder.AppendLine($"<color=#888888>{resetMarkerKey} 보정값 지우기 · Ctrl+{resetMarkerKey} 사각형 자리까지 기본값</color>");
        }

        void AppendFineAdjustLine(ArUcoSet set)
        {
            var calibration = set.Config.calibration;
            if (!calibration.IsUsable) return;

            float px = set.View.CanvasSize.y;   // 이동량은 화면 높이 기준이라 세로 픽셀 수를 곱하면 px 다
            _builder.AppendLine($"세부 조정  이동 <b>{calibration.fineOffset.x * px:+0;-0;0}, {-calibration.fineOffset.y * px:+0;-0;0}px</b>" +
                                $" · 회전 <b>{calibration.fineRotation:+0.0;-0.0;0.0}°</b> · 크기 <b>{calibration.fineScale:0.000}</b>" +
                                $" · 기울기 좌우 <b>{calibration.fineTilt.x * 100f:+0;-0;0}</b> 상하 <b>{calibration.fineTilt.y * 100f:+0;-0;0}</b>");
            _builder.AppendLine($"<color=#888888>← → ↑ ↓ 이동 · Ctrl + ← → ↑ ↓ 기울기 · [ ] 회전 · + - 크기 (Shift 미세) · " +
                                $"{fineResetKey} 세부 조정 0 · {modelKey} 맞춤 방식</color>");
        }

        static string ModelName(string model)
        {
            switch (model)
            {
                case CalibrationConfig.ModelPlane: return "평면 (선은 곧게, 기울기 반영)";
                case CalibrationConfig.ModelWarp: return "평면 + 점별 휨 보정";
                default: return "정사각 유지 (이동 · 회전 · 크기만)";
            }
        }

        void AppendSelectionLine()
        {
            string target = _selection < 0 ? "전체" : $"{new[] { "좌상", "우상", "우하", "좌하" }[_selection]} 귀퉁이";

            _builder.AppendLine($"자리  <color=#ffd633><b>{target}</b></color>" +
                                $"   <color=#888888>{nextMarkerKey} 고르기 · ← → ↑ ↓ 옮기기 · PageUp/PageDown 넓히기/좁히기 (Shift 미세)</color>");
        }

        // ── 입력 보조 ────────────────────────────────────────────────────────────

        // 누르고 있으면 연속 조정되도록, 이번 프레임에 몇 번 눌린 것으로 칠지 계산한다.
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

            float before = Mathf.Floor(Mathf.Max(0f, previous - repeatDelay) * repeatRate);
            float after = Mathf.Floor((current - repeatDelay) * repeatRate);
            return after - before;
        }

        // Shift 를 누르면 fastMultiplier 배로 빠르게. 기본 한 번은 세밀하게 잡아 두었다.
        float Speed() => IsShiftHeld() ? fastMultiplier : 1f;

        static bool IsShiftHeld() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        static bool IsCtrlHeld() => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        static int IndexOf(IReadOnlyList<int> list, int value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == value) return i;
            }
            return -1;
        }
    }
}
