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
        [SerializeField] KeyCode toggleKey = KeyCode.F1;
        [SerializeField] KeyCode stageKey = KeyCode.F2;
        [SerializeField] KeyCode nextMarkerKey = KeyCode.Tab;
        [SerializeField] KeyCode resetMarkerKey = KeyCode.Backspace;
        [SerializeField] KeyCode reloadContentKey = KeyCode.F5;
        [SerializeField] KeyCode outlineKey = KeyCode.D;
        [SerializeField] KeyCode cameraPreviewKey = KeyCode.C;
        [SerializeField] KeyCode contentKey = KeyCode.V;
        [SerializeField] KeyCode perspectiveKey = KeyCode.P;
        [SerializeField] KeyCode actionKey = KeyCode.Space;
        // GameManager 가 S 를 쓰고 SettingsPanelUI 가 Esc 를 쓰므로 겹치지 않게 Enter 로 둔다.
        [SerializeField] KeyCode saveKey = KeyCode.Return;

        [Header("반복 입력")]
        [SerializeField] float repeatDelay = 0.35f;
        [SerializeField] float repeatRate = 14f;
        [SerializeField] float fineMultiplier = 0.2f;

        [Header("저장")]
        [SerializeField] float autoSaveDelay = 1.5f;

        [Header("캘리브레이션")]
        [Tooltip("자동 인식에서 몇 프레임을 모아 평균낼지. 마커가 미세하게 떨리므로 여러 장을 겹친다.")]
        [SerializeField] int autoSampleFrames = 30;
        [Tooltip("투사할 마커의 화면상 크기(짧은 변 기준 비율).")]
        [SerializeField] float calibrationMarkerSize = 0.18f;

        StoryRestApp _app;

        bool _active;
        bool _cursorWasVisible;

        // 콘텐츠를 그릴지. 단계를 바꿀 때마다 그 단계의 기본값으로 돌아가고, 그 위에서 사람이 뒤집는다
        // (배치는 보면서 맞추고, 보정은 조준점만 남기는 것이 기본).
        bool _showContent = true;

        int _setIndex;
        Stage _stage = Stage.Placement;
        int _selectedMarkerId = -1;

        CalibrationPhase _phase = CalibrationPhase.Idle;
        int _manualIndex;
        int _autoFrames;
        readonly Vector2[] _accum = new Vector2[4];
        readonly int[] _accumCount = new int[4];
        readonly Vector2[] _collected = new Vector2[4];
        readonly bool[] _collectedOk = new bool[4];
        string _calibrationMessage = "";

        bool _dirty;
        float _dirtySince;

        readonly Dictionary<KeyCode, float> _holdTimes = new Dictionary<KeyCode, float>();
        readonly StringBuilder _builder = new StringBuilder(1024);

        Texture2D _crosshair;

        // 마커 놓을 자리 표시용 사각형. 화면상 선 두께를 일정하게 유지하려고
        // 크기가 바뀌면 다시 만든다(→ TargetFrame).
        Texture2D _frame;
        int _frameThickness = -1;

        public bool IsActive => _active;

        void Awake()
        {
            _app = GetComponent<StoryRestApp>();
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
            if (Input.GetKeyDown(toggleKey)) SetActive(!_active);

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

                _showContent = _stage == Stage.Placement;
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
                s.HighlightMarkerId = -1;
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
                    previous.View.ShowHud(null);
                    previous.View.BeginOverlay();
                    previous.View.EndOverlay();
                }

                _setIndex = i;
                _selectedMarkerId = -1;
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

            set.HighlightMarkerId = _selectedMarkerId;

            if (Input.GetKeyDown(perspectiveKey))
            {
                _app.Config.perspectiveMapping = !_app.Config.perspectiveMapping;
                MarkDirty();
            }

            if (Input.GetKeyDown(resetMarkerKey)) ResetPlacement(set);

            var config = _app.Config;
            float fine = IsShiftHeld() ? fineMultiplier : 1f;

            // 키는 프레임당 한 번만 읽는다. KeyStep 이 홀드 상태를 갱신하므로 두 번 부르면 어긋난다.
            float dx = (KeyStep(KeyCode.RightArrow) - KeyStep(KeyCode.LeftArrow)) * config.adjustPositionStep * fine;
            float dy = (KeyStep(KeyCode.UpArrow) - KeyStep(KeyCode.DownArrow)) * config.adjustPositionStep * fine;

            float grow = KeyStep(KeyCode.Equals) + KeyStep(KeyCode.Plus) + KeyStep(KeyCode.KeypadPlus);
            float shrink = KeyStep(KeyCode.Minus) + KeyStep(KeyCode.KeypadMinus);
            float ds = (grow - shrink) * config.adjustScaleStep * fine;

            float dr = (KeyStep(KeyCode.RightBracket) - KeyStep(KeyCode.LeftBracket)) * config.adjustRotationStep * fine;
            float dPage = (KeyStep(KeyCode.PageUp) - KeyStep(KeyCode.PageDown)) * config.adjustScaleStep * fine;

            // Ctrl 을 누르면 같은 키가 세트 전체에 걸린다.
            // 마커를 고르지 않아도 되므로, 콘텐츠를 채우기 전에 공통 배치부터 잡을 수 있다.
            if (IsCtrlHeld())
            {
                if (dx == 0f && dy == 0f && ds == 0f && dPage == 0f) return;

                set.Config.globalOffsetX += dx;
                set.Config.globalOffsetY += dy;
                set.Config.globalScale = Mathf.Max(0.05f, set.Config.globalScale + ds + dPage);

                MarkDirty();
                return;
            }

            // PageUp/PageDown 은 Ctrl 없이도 세트 배율을 바꾼다(기존 조작).
            if (dPage != 0f)
            {
                set.Config.globalScale = Mathf.Max(0.05f, set.Config.globalScale + dPage);
                MarkDirty();
            }

            if (_selectedMarkerId < 0) return;
            if (dx == 0f && dy == 0f && ds == 0f && dr == 0f) return;

            var marker = set.Config.GetOrCreate(_selectedMarkerId);

            marker.offsetX += dx;
            marker.offsetY += dy;
            marker.scale = Mathf.Max(0.05f, marker.scale + ds);
            marker.rotationOffset = Mathf.Repeat(marker.rotationOffset + dr + 180f, 360f) - 180f;

            MarkDirty();
        }

        /// <summary>Backspace 는 선택한 마커를, Ctrl+Backspace 는 세트 공통 배치를 되돌린다.</summary>
        void ResetPlacement(ArUcoSet set)
        {
            if (IsCtrlHeld())
            {
                set.Config.ResetGlobalPlacement();
                MarkDirty();
                return;
            }

            if (_selectedMarkerId < 0) return;

            set.Config.GetOrCreate(_selectedMarkerId).ResetPlacement();
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
        }

        // ── 코너 보정 ────────────────────────────────────────────────────────────

        void UpdateCalibration(ArUcoSet set)
        {
            // 이 단계에서는 마커를 고르지 않는다. 콘텐츠를 띄워 둔 채로 넘어오면
            // 배치에서 켜 둔 하이라이트가 그대로 남는다.
            set.HighlightMarkerId = -1;

            var calibration = set.Config.calibration;
            var projectorPoints = calibration.projectorPoints;

            if (projectorPoints == null || projectorPoints.Length != 4)
            {
                calibration.Reset();
                projectorPoints = calibration.projectorPoints;
            }

            if (Input.GetKeyDown(actionKey)) AdvanceCalibration(set);

            if (Input.GetKeyDown(resetMarkerKey))
            {
                calibration.Reset();
                set.RebuildProjection();
                _phase = CalibrationPhase.Idle;
                _calibrationMessage = "보정값을 지웠습니다.";
                MarkDirty();
            }

            switch (_phase)
            {
                case CalibrationPhase.Auto: CollectAuto(set, projectorPoints); break;
                case CalibrationPhase.Manual: CollectManual(set); AdjustTargetSize(); break;
            }

            DrawCalibrationOverlay(set, projectorPoints);
        }

        void AdvanceCalibration(ArUcoSet set)
        {
            switch (_phase)
            {
                case CalibrationPhase.Idle:
                    BeginAuto();
                    break;

                case CalibrationPhase.Auto:
                    // 자동이 안 잡히면 사람이 짚는 방식으로 넘어간다.
                    BeginManual();
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

        void BeginAuto()
        {
            _phase = CalibrationPhase.Auto;
            _autoFrames = 0;

            for (int i = 0; i < 4; i++)
            {
                _accum[i] = Vector2.zero;
                _accumCount[i] = 0;
                _collectedOk[i] = false;
            }

            _calibrationMessage = "네 귀퉁이에 마커를 투사하고 있습니다. 카메라가 읽을 때까지 기다리세요.";
        }

        void BeginManual()
        {
            _phase = CalibrationPhase.Manual;
            _manualIndex = 0;

            for (int i = 0; i < 4; i++) _collectedOk[i] = false;

            _calibrationMessage = "사각형 안에 마커를 맞춰 놓고 Space 를 누르세요.";
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

            float delta = (grow - shrink) * _app.Config.adjustScaleStep * (IsShiftHeld() ? fineMultiplier : 1f);
            if (delta == 0f) return;

            _app.Config.manualTargetSize = Mathf.Clamp(
                _app.Config.manualTargetSize + delta,
                ArUcoConfig.MinManualTargetSize, ArUcoConfig.MaxManualTargetSize);

            MarkDirty();
        }

        /// <summary>
        /// 투사된 캘리브레이션 마커를 카메라가 읽어 대응점을 모은다.
        /// 마커가 미세하게 떨리므로 여러 프레임을 평균낸다.
        /// </summary>
        void CollectAuto(ArUcoSet set, Vector2[] projectorPoints)
        {
            var ids = _app.Config.calibrationMarkerIds;
            if (ids == null || ids.Length < 4)
            {
                _calibrationMessage = "calibrationMarkerIds 에 마커 ID 4개가 필요합니다.";
                _phase = CalibrationPhase.Idle;
                return;
            }

            var markers = set.Tracker.Markers;

            for (int corner = 0; corner < 4; corner++)
            {
                for (int m = 0; m < markers.Count; m++)
                {
                    if (markers[m].id != ids[corner]) continue;

                    _accum[corner] += markers[m].center;
                    _accumCount[corner]++;
                    break;
                }
            }

            _autoFrames++;
            if (_autoFrames < autoSampleFrames) return;

            int found = 0;
            for (int i = 0; i < 4; i++)
            {
                if (_accumCount[i] <= 0) continue;

                _collected[i] = _accum[i] / _accumCount[i];
                _collectedOk[i] = true;
                found++;
            }

            if (found == 4)
            {
                ApplyCalibration(set, projectorPoints);
                return;
            }

            _calibrationMessage =
                $"자동 인식 실패 — {found}/4 개만 읽혔습니다.\n" +
                "투사면이 어둡거나 초점이 맞지 않으면 인식되지 않습니다.\n" +
                "Space 를 누르면 손으로 짚는 방식으로 넘어갑니다.";
        }

        /// <summary>수동 모드: 조준점 자리에 놓인 마커를 읽는다. 어떤 ID 든 상관없다.</summary>
        void CollectManual(ArUcoSet set)
        {
            var markers = set.Tracker.Markers;
            if (markers.Count == 0)
            {
                _calibrationMessage = $"{_manualIndex + 1}/4 — 사각형 안에 마커를 놓으세요. (마커가 보이지 않습니다)";
                return;
            }

            // 화면상 가장 크게 잡힌 것을 쓴다(Tracker 가 크기 내림차순으로 정렬해 둔다).
            _calibrationMessage = $"{_manualIndex + 1}/4 — 마커가 보입니다. 사각형에 맞았으면 Space 를 누르세요.";
        }

        void ConfirmManualPoint(ArUcoSet set)
        {
            var markers = set.Tracker.Markers;
            if (markers.Count == 0)
            {
                _calibrationMessage = "마커가 보이지 않습니다.";
                return;
            }

            _collected[_manualIndex] = markers[0].center;
            _collectedOk[_manualIndex] = true;
            _manualIndex++;

            if (_manualIndex < 4)
            {
                _calibrationMessage = $"{_manualIndex + 1}/4 — 다음 조준점으로 마커를 옮기세요.";
                return;
            }

            ApplyCalibration(set, set.Config.calibration.projectorPoints);
        }

        void ApplyCalibration(ArUcoSet set, Vector2[] projectorPoints)
        {
            var calibration = set.Config.calibration;

            calibration.projectorPoints = projectorPoints;
            calibration.cameraPoints = new[] { _collected[0], _collected[1], _collected[2], _collected[3] };
            calibration.valid = true;

            set.RebuildProjection();
            MarkDirty();

            _phase = CalibrationPhase.Verify;
            _calibrationMessage =
                "보정을 적용했습니다. 조준점과 실제 투사 위치가 겹치는지 확인하세요.\n" +
                "어긋나면 Backspace 로 지우고 다시 잡습니다. 맞으면 Space.";
        }

        void DrawCalibrationOverlay(ArUcoSet set, Vector2[] projectorPoints)
        {
            var view = set.View;
            view.BeginOverlay();

            var ids = _app.Config.calibrationMarkerIds;

            for (int i = 0; i < 4; i++)
            {
                switch (_phase)
                {
                    case CalibrationPhase.Auto:
                        // 프로젝터가 마커를 직접 쏜다. 카메라는 이 빛을 읽는다.
                        if (ids != null && ids.Length > i)
                        {
                            var texture = ArUcoMarkerTexture.Get(_app.Config.dictionaryId, ids[i]);
                            view.DrawOverlay(projectorPoints[i], calibrationMarkerSize, texture, Color.white);
                        }
                        break;

                    case CalibrationPhase.Manual:
                        // 지금 짚어야 할 점만 밝게, 나머지는 흐리게.
                        // 네 자리 모두 같은 크기로 그린다 — 실물 마커를 맞춰 놓을 자리이기 때문이다.
                        bool current = i == _manualIndex;
                        Color tint = _collectedOk[i]
                            ? new Color(0.3f, 1f, 0.4f, 0.9f)
                            : (current ? Color.white : new Color(1f, 1f, 1f, 0.25f));

                        float size = _app.Config.manualTargetSize;
                        view.DrawOverlay(projectorPoints[i], size, TargetFrame(size, view.CanvasSize), tint);
                        break;

                    default:
                        view.DrawOverlay(projectorPoints[i], 0.04f, Crosshair(), new Color(1f, 0.85f, 0.2f, 0.9f));
                        break;
                }
            }

            view.EndOverlay();
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
            _builder.AppendLine("<color=#ffd633><b>편집모드</b></color>   <color=#888888>F1 나가기 · F2 단계</color>");

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

            var visible = set.VisibleMarkerIds;
            _builder.Append(visible.Count > 0
                ? $"<color=#88ff88>보이는 마커: {string.Join(", ", visible)}</color>"
                : $"<color=#ff8888>마커가 보이지 않습니다 (후보 {set.Tracker.RejectedCount}개)</color>");

            if (_dirty) _builder.Append("   <color=#ffd633>* 저장 대기</color>");

            // 코너 보정 중에는 네 귀퉁이 조준점을 가리지 않도록 안내판을 가운데로 옮긴다.
            set.View.ShowHud(_builder.ToString(), _stage == Stage.Calibration);

            // 다른 세트에는 편집 UI 가 남지 않게 한다.
            for (int i = 0; i < _app.Sets.Count; i++)
            {
                if (i != _setIndex) _app.Sets[i].View.ShowHud(null);
            }
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
            _builder.AppendLine("<b>마커 배치</b>");

            if (_selectedMarkerId < 0)
            {
                _builder.AppendLine("카메라에 마커를 비춘 뒤 Tab 으로 고르세요.");
            }
            else
            {
                var marker = set.Config.GetOrCreate(_selectedMarkerId);
                bool onScreen = IndexOf(set.VisibleMarkerIds, _selectedMarkerId) >= 0;

                _builder.AppendLine($"선택  <b>{_selectedMarkerId}번</b>" +
                                    $"{(onScreen ? "" : "  <color=#ff8888>(화면에 없음)</color>")}   <color=#888888>Tab 다음</color>");
                _builder.AppendLine($"크기  <b>{marker.scale:0.00}</b>   <color=#888888>+ / -</color>");
                _builder.AppendLine($"좌우  <b>{marker.offsetX:+0.00;-0.00; 0.00}</b>   <color=#888888>← / →</color>");
                _builder.AppendLine($"상하  <b>{marker.offsetY:+0.00;-0.00; 0.00}</b>   <color=#888888>↑ / ↓</color>");
                _builder.AppendLine($"회전  <b>{marker.rotationOffset:+0.0;-0.0; 0.0}°</b>   <color=#888888>[ / ]</color>");
            }

            _builder.AppendLine();
            _builder.AppendLine($"<color=#ffd633>세트 공통</color>  " +
                                $"크기 <b>{set.Config.globalScale:0.00}</b>  " +
                                $"좌우 <b>{set.Config.globalOffsetX:+0.00;-0.00; 0.00}</b>  " +
                                $"상하 <b>{set.Config.globalOffsetY:+0.00;-0.00; 0.00}</b>");
            _builder.AppendLine($"<color=#888888>Ctrl 병행 — 모든 마커가 함께 움직입니다 " +
                                $"(Ctrl+{resetMarkerKey} 초기화 · PageUp/PageDown 도 크기)</color>");

            _builder.AppendLine();
            _builder.AppendLine($"원근  <b>{(_app.Config.perspectiveMapping ? "켜짐" : "꺼짐")}</b>   <color=#888888>{perspectiveKey}</color>");
            _builder.AppendLine($"<color=#888888>Shift 병행 미세조정 · {resetMarkerKey} 이 마커 초기화 · {saveKey} 즉시저장</color>");
        }

        void AppendCalibration(ArUcoSet set)
        {
            bool calibrated = set.Config.calibration.IsUsable;

            _builder.AppendLine($"<b>코너 보정</b>   " +
                                (calibrated ? "<color=#88ff88>보정됨</color>" : "<color=#ff8888>보정 안 됨</color>"));

            switch (_phase)
            {
                case CalibrationPhase.Idle:
                    _builder.AppendLine("카메라가 본 자리를 프로젝터 좌표로 옮기는 표를 만듭니다.");
                    _builder.AppendLine("<color=#888888>Space — 네 귀퉁이에 마커를 투사해 자동으로 잡습니다</color>");
                    break;

                case CalibrationPhase.Auto:
                    int progress = Mathf.Min(_autoFrames, autoSampleFrames);
                    _builder.AppendLine($"자동 인식 중… {progress}/{autoSampleFrames}");
                    break;

                case CalibrationPhase.Manual:
                    _builder.AppendLine($"수동 보정  <b>{Mathf.Min(_manualIndex + 1, 4)}/4</b>");
                    _builder.AppendLine($"자리 크기  <b>{_app.Config.manualTargetSize:0.00}</b>" +
                                        $"   <color=#888888>+ / - 로 실물 마커에 맞춤 (Shift 미세)</color>");
                    break;

                case CalibrationPhase.Verify:
                    _builder.AppendLine("<color=#88ff88>검증</color>");
                    break;
            }

            if (!string.IsNullOrEmpty(_calibrationMessage)) _builder.AppendLine(_calibrationMessage);

            _builder.AppendLine($"<color=#888888>{resetMarkerKey} 보정값 지우기</color>");
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
