using System.Collections.Generic;
using UnityEngine;

namespace StoryRest.ArUco.Demo
{
    /// <summary>
    /// 콘텐츠 등장 연출 시연(AppearDemo 씬). 카메라 없이 가짜 마커 하나로 전시 코드의 연출을 그대로 돌려 본다.
    ///
    /// 전시와 같은 부품을 쓴다 — 상태는 <see cref="ArUcoAppearTracker"/>, 그리기는 <see cref="ArUcoProjectionView"/>.
    /// 여기서 보이는 것이 곧 전시에서 보이는 것이다. 다른 점은 마커 꼭짓점이 카메라가 아니라 마우스에서 온다는 것뿐이다.
    /// 카메라↔프로젝터 변환은 항등으로 둔다(화면 픽셀 = 카메라 픽셀).
    ///
    /// 마우스로 마커를 끌어 놓으면 전시와 같은 순서로 진행된다: 움직이는 중 → 멈춤 판정 → 읽는 중(고리) → 콘텐츠.
    /// Space 는 마커를 "처음 본 것" 으로 되돌려 다시 재생(새 관람), X 는 마커를 놓친 상황(holdSeconds 뒤 사라짐),
    /// R 은 놓쳤던 마커가 돌아온 상황 — 유예 안이면 연출 없이 바로 뜬다(전시와 같은 규칙).
    /// </summary>
    public class ArUcoAppearDemo : MonoBehaviour
    {
        static readonly string[] EffectNames =
        {
            "없음", "마커에서 솟아나옴", "페이드", "위에서부터 서서히", "회오리 모양으로", "물결치며 펴짐",
        };

        [Header("연출 (aruco.json appear 와 같은 값)")]
        [SerializeField] AppearConfig appear = new AppearConfig();
        [SerializeField] float holdSeconds = 0.3f;       // 놓친 뒤 붙잡아 두는 시간. aruco.json holdSeconds 와 맞춘다

        [Header("가짜 마커")]
        [SerializeField] int dictionaryId = 1;
        [SerializeField] int markerId = 23;
        [SerializeField] float markerSizePx = 160f;

        [Header("콘텐츠 자리 (마커 한 변 = 1)")]
        [SerializeField] float contentScale = 3.2f;
        [SerializeField] Vector2 contentOffset = new Vector2(0f, -1.7f);
        [SerializeField] float contentAspect = 9f / 16f;
        [SerializeField] int subdivisions = 16;

        [Header("자동 반복")]
        [SerializeField] float autoShowSeconds = 3.0f;
        [SerializeField] float autoHideSeconds = 1.2f;

        bool _tilted;
        bool _autoLoop = true;

        // 시연용 시계. P 로 멈추고 ←/→ 로 한 칸씩 움직여 중간 프레임을 살핀다.
        float _clock;
        bool _paused;

        // 가짜 추적기 출력. 마커가 있으면 1개, 놓쳐서 hold 가 끝나면 0개.
        readonly List<ArUcoMarkerTracker.TrackedMarker> _markers = new List<ArUcoMarkerTracker.TrackedMarker>();
        bool _present = true;        // 마커가 카메라 안에 있는가
        float _lostAt = -1f;         // X 를 누른 시각. hold 동안 visible=false 로 남는다
        float _shownAt = -1f;        // 자동 반복용: 콘텐츠가 뜬 시각
        float _removedAt = -1f;      // 자동 반복용: 목록에서 빠진 시각

        Vector2 _markerNorm = new Vector2(0.33f, 0.34f);  // 화면 비율(좌상단 원점, y 아래로). 오른쪽 위 HUD 를 피한다
        bool _dragging;
        Vector2 _dragOffset;
        Vector2 _prevCenter;
        bool _hasPrev;
        float _speed;                // 마커 한 변/초 (표시용)

        ArUcoAppearTracker _appear;
        ArUcoProjectionView _view;
        Vector2 _projectionSize;

        ArUcoWarpedImage _paper;
        ArUcoWarpedImage _marker;
        Texture2D _markerTexture;
        Texture2D _contentTexture;

        readonly List<Vector2> _nodes = new List<Vector2>(4);

        void Start()
        {
            _appear = new ArUcoAppearTracker(appear) { GraceSeconds = 20f };

            _view = gameObject.AddComponent<ArUcoProjectionView>();
            _view.Setup("AppearDemo", 0, new Rect(0f, 0f, 1f, 1f), 0);

            _markerTexture = ArUcoMarkerTexture.Get(dictionaryId, markerId, 256);
            _contentTexture = MakeContentTexture(640, Mathf.RoundToInt(640 * contentAspect));

            // 종이와 마커는 실제로는 프로젝터가 그리지 않는다. 화면 시연이라 캔버스 맨 뒤에 어둡게 깔아 자리를 보여 준다.
            // 나중에 만든 것이 맨 앞(SetAsFirstSibling)으로 가므로 종이를 마커보다 나중에 만든다.
            var canvas = _view.GetComponentInChildren<Canvas>().transform;
            _marker = NewBackdrop(canvas, "Marker", new Color(0.45f, 0.45f, 0.45f, 1f));
            _marker.SetTexture(_markerTexture);
            _paper = NewBackdrop(canvas, "Paper", new Color(0.13f, 0.13f, 0.12f, 1f));
        }

        void Update()
        {
            if (_view == null || _appear == null) return;   // 도메인 리로드 뒤

            EnsureProjection();
            HandleInput();
            if (!_paused) _clock += Time.unscaledDeltaTime;

            _appear.Config = appear;

            UpdateFakeTracker();
            _appear.Update(_markers, _clock);
            HandleAutoLoop();

            Draw();
        }

        // ───────────────────────────── 가짜 추적기 ─────────────────────────────

        /// <summary>마우스 위치에서 마커 꼭짓점을 만들고 hold/놓침을 흉내 낸다. 전시의 ArUcoMarkerTracker.Markers 와 같은 모양.</summary>
        void UpdateFakeTracker()
        {
            _markers.Clear();

            if (!_present) return;

            bool visible = _lostAt < 0f;
            if (!visible && _clock - _lostAt > holdSeconds)
            {
                // hold 가 끝나면 추적기 목록에서 빠진다.
                _present = false;
                _removedAt = _clock;
                return;
            }

            var plane = BuildMarkerPlane();
            plane.TryMap(new Vector2(0f, 0f), out Vector2 c0);
            plane.TryMap(new Vector2(1f, 0f), out Vector2 c1);
            plane.TryMap(new Vector2(1f, 1f), out Vector2 c2);
            plane.TryMap(new Vector2(0f, 1f), out Vector2 c3);

            var m = new ArUcoMarkerTracker.TrackedMarker
            {
                id = markerId,
                corner0 = c0, corner1 = c1, corner2 = c2, corner3 = c3,
                center = (c0 + c1 + c2 + c3) * 0.25f,
                angleDeg = 0f,
                sizePx = ((c1 - c0).magnitude + (c2 - c3).magnitude) * 0.5f,
                visible = visible,
            };
            _markers.Add(m);

            // 표시용 속도(추적기와 같은 식).
            if (visible)
            {
                float dt = Time.unscaledDeltaTime;
                if (_hasPrev && dt > 0f) _speed = (m.center - _prevCenter).magnitude / dt / m.sizePx;
                _prevCenter = m.center;
                _hasPrev = true;
            }
            else _hasPrev = false;
        }

        /// <summary>마커를 처음 본 것으로 되돌린다(새 관람) — 멈춤 판정부터 다시.</summary>
        void Replay()
        {
            Return();
            _appear.Forget(markerId);
        }

        /// <summary>놓쳤던 마커가 카메라에 돌아왔다. 상태는 그대로 — 유예 안이면 연출 없이 바로 뜬다.</summary>
        void Return()
        {
            _present = true;
            _lostAt = -1f;
            _shownAt = -1f;
            _removedAt = -1f;
        }

        /// <summary>마커를 놓친 상황. holdSeconds 동안 그대로 있다가 목록에서 빠진다.</summary>
        void Lose()
        {
            if (!_present || _lostAt >= 0f) return;
            _lostAt = _clock;
        }

        // ───────────────────────────── 입력 ─────────────────────────────

        void HandleInput()
        {
            for (int i = 0; i < EffectNames.Length; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha0 + i) || Input.GetKeyDown(KeyCode.Keypad0 + i))
                {
                    appear.SetEffect((AppearEffect)i);
                    Replay();
                }
            }

            if (Input.GetKeyDown(KeyCode.Space)) Replay();
            if (Input.GetKeyDown(KeyCode.X)) Lose();
            if (Input.GetKeyDown(KeyCode.R)) Return();
            if (Input.GetKeyDown(KeyCode.H)) appear.ring = !appear.ring;
            if (Input.GetKeyDown(KeyCode.T)) _tilted = !_tilted;
            if (Input.GetKeyDown(KeyCode.L)) _autoLoop = !_autoLoop;
            if (Input.GetKeyDown(KeyCode.P)) _paused = !_paused;
            if (Input.GetKeyDown(KeyCode.LeftArrow)) _clock -= 0.05f;
            if (Input.GetKeyDown(KeyCode.RightArrow)) _clock += 0.05f;

            if (Input.GetKeyDown(KeyCode.LeftBracket)) markerSizePx = Mathf.Max(60f, markerSizePx - 20f);
            if (Input.GetKeyDown(KeyCode.RightBracket)) markerSizePx = Mathf.Min(400f, markerSizePx + 20f);

            // 마우스로 마커(책자)를 끌어 옮긴다. 놓으면 전시와 같은 순서로 읽기 시작한다.
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            Vector2 center = MarkerCenter;
            if (Input.GetMouseButtonDown(0))
            {
                float half = markerSizePx * 0.5f;
                if (Mathf.Abs(mouse.x - center.x) <= half * 1.5f &&
                    Mathf.Abs(mouse.y - center.y) <= half * 1.5f)
                {
                    _dragging = true;
                    _dragOffset = center - mouse;
                    if (!_present) Return();   // 끌어서 다시 들이는 것은 "돌아옴"
                }
            }
            if (Input.GetMouseButtonUp(0)) _dragging = false;
            if (_dragging)
            {
                Vector2 size = _projectionSize;
                Vector2 next = mouse + _dragOffset;
                _markerNorm = new Vector2(next.x / size.x, next.y / size.y);
            }
        }

        void HandleAutoLoop()
        {
            if (!_autoLoop || _paused) return;

            var phase = _appear.GetPhase(markerId);
            if (_present && _lostAt < 0f && phase == ArUcoAppearTracker.Phase.Shown)
            {
                if (_shownAt < 0f) _shownAt = _clock;
                else if (_clock - _shownAt > appear.appearSeconds + autoShowSeconds) Lose();
            }
            else if (!_present && _removedAt >= 0f && _clock - _removedAt > autoHideSeconds)
            {
                Replay();
            }
        }

        // ───────────────────────────── 그리기 ─────────────────────────────

        void Draw()
        {
            var plane = BuildMarkerPlane();
            LayoutBackdrop(plane);

            _view.BeginFrame();

            var style = ArUcoDrawStyle.Default(1f, Vector2.zero, subdivisions, true);
            var effect = appear.Effect;

            if (_markers.Count > 0 && _appear.TryGet(_markers[0], _clock, out var frame))
            {
                if (frame.ringT >= 0f) _view.DrawRing(plane, frame.ringT, style);

                if (frame.drawContent)
                {
                    var placement = new ArUcoPlacement
                    {
                        scale = contentScale,
                        offsetX = contentOffset.x,
                        offsetY = contentOffset.y,
                        rotationOffset = 0f,
                    };
                    style.texture = _contentTexture;
                    style.aspect = contentAspect;
                    style.tint = Color.white;

                    if (frame.appearT < 1f)
                        ArUcoAppearTracker.Apply(effect, appear, frame.appearT, ref placement, ref style);

                    if (placement.scale > 0.001f && style.tint.a > 0.001f)
                        _view.Draw(plane, placement, style);
                }
            }

            _view.EndFrame();
            _view.ShowHud(BuildHudText(), HudAnchor.TopRight);
        }

        /// <summary>
        /// 항등 변환. 캔버스 크기가 곧 "카메라 해상도" 라 마커 픽셀이 그대로 화면 픽셀이 된다.
        /// 창 크기가 바뀌면 다시 세운다.
        /// </summary>
        void EnsureProjection()
        {
            Vector2 size = _view.CanvasSize;
            if (size.x < 1f || size.y < 1f) size = new Vector2(Screen.width, Screen.height);
            if (size == _projectionSize) return;

            _projectionSize = size;
            _view.SetProjection(ArUcoProjection.Build(null, Mathf.RoundToInt(size.x), Mathf.RoundToInt(size.y), "AppearDemo"));
        }

        /// <summary>마커 중심(캔버스 픽셀, 좌상단 원점). 창 크기가 바뀌어도 같은 자리에 머문다.</summary>
        Vector2 MarkerCenter => new Vector2(_markerNorm.x * _projectionSize.x, _markerNorm.y * _projectionSize.y);

        /// <summary>가짜 마커의 네 꼭짓점(화면 픽셀). T 로 기울이면 사다리꼴이 되어 원근이 보인다.</summary>
        ArUcoHomography BuildMarkerPlane()
        {
            float h = markerSizePx * 0.5f;
            Vector2 c = MarkerCenter;

            if (!_tilted)
            {
                return ArUcoHomography.FromUnitSquare(
                    c + new Vector2(-h, -h), c + new Vector2(h, -h),
                    c + new Vector2(h, h), c + new Vector2(-h, h));
            }

            // 윗변을 좁히고 위로 눌러 책을 눕혀 본 모양을 흉내 낸다.
            // 콘텐츠가 마커 아래에 있어 원근이 아래로 갈수록 커지므로 조금만 기울인다.
            float top = h * 0.84f;
            return ArUcoHomography.FromUnitSquare(
                c + new Vector2(-top, -h * 0.78f), c + new Vector2(top, -h * 0.78f),
                c + new Vector2(h, h * 0.88f), c + new Vector2(-h, h * 0.88f));
        }

        void LayoutBackdrop(ArUcoHomography plane)
        {
            // 종이: 마커 아래로 길게 깔린 직사각형. 마커: 단위 정사각형 그대로. 둘 다 기울이면 같이 눕는다.
            const float paperWidth = 4.4f, paperHeight = 5.1f;
            SetQuad(_paper, plane, -paperWidth * 0.5f, 0.8f - paperHeight, paperWidth, paperHeight);
            SetQuad(_marker, plane, -0.5f, -0.5f, 1f, 1f);
        }

        // 마커 좌표(중심 0, 한 변 1, y 위로)의 직사각형을 캔버스 로컬 좌표 4점으로 바꿔 넣는다.
        void SetQuad(ArUcoWarpedImage image, ArUcoHomography plane, float left, float bottom, float width, float height)
        {
            Vector2 size = _projectionSize;
            _nodes.Clear();
            for (int y = 0; y <= 1; y++)
            for (int x = 0; x <= 1; x++)
            {
                float rx = left + width * x;
                float ry = bottom + height * y;
                plane.TryMap(new Vector2(0.5f + rx, 0.5f - ry), out Vector2 p);
                _nodes.Add(new Vector2(p.x - size.x * 0.5f, size.y * 0.5f - p.y));
            }
            image.SetNodes(1, 1, _nodes);
            image.enabled = true;
        }

        // ───────────────────────────── HUD ─────────────────────────────

        string BuildHudText()
        {
            var sb = new System.Text.StringBuilder(640);
            sb.Append("<b>콘텐츠 등장 연출 시연</b>\n");
            sb.Append("마커: ").Append(StateLabel()).Append("   <color=#9a9a9a>(마커를 끌어다 놓아 보세요)</color>\n\n");

            int current = (int)appear.Effect;
            for (int i = 0; i < EffectNames.Length; i++)
            {
                sb.Append(current == i ? "<color=#ffd633>▶ " : "<color=#9a9a9a>  ");
                sb.Append(i).Append("  ").Append(EffectNames[i]).Append("</color>\n");
            }
            sb.Append('\n');
            sb.Append("Space  처음부터 다시(고리 ").Append(appear.recognizeSeconds).Append("초 → 등장 ").Append(appear.appearSeconds).Append("초)\n");
            sb.Append("X      마커 놓침(").Append(holdSeconds).Append("초 뒤 사라짐)\n");
            sb.Append("R      놓쳤던 마커 돌아옴 — 연출 없이 바로\n");
            sb.Append("H      빛 고리 ").Append(appear.ring ? "<color=#7fe07f>켬</color>" : "<color=#ff8080>끔</color>").Append('\n');
            sb.Append("T      책 기울이기 ").Append(_tilted ? "<color=#7fe07f>켬</color>" : "끔").Append('\n');
            sb.Append("L      자동 반복 ").Append(_autoLoop ? "<color=#7fe07f>켬</color>" : "끔").Append('\n');
            sb.Append("P      일시정지 ").Append(_paused ? "<color=#ffd633>멈춤</color>  ←/→ 0.05초씩" : "").Append('\n');
            sb.Append("[ ]    마커 크기   마우스 끌기  마커 옮기기\n");
            return sb.ToString();
        }

        string StateLabel()
        {
            if (!_present) return "<color=#9a9a9a>카메라 밖</color>";
            if (_lostAt >= 0f) return "놓침 (hold)";

            var phase = _appear.GetPhase(markerId);
            if (phase == null) return "대기";
            if (_speed > appear.moveThreshold) return "<color=#ff8080>움직이는 중</color>";

            switch (phase.Value)
            {
                case ArUcoAppearTracker.Phase.Settling:
                    return $"<color=#ffd633>멈춤 판정 중 (≤ {appear.settleSeconds:0.00}s)</color>";
                case ArUcoAppearTracker.Phase.Reading:
                    return $"<color=#ffd633>읽는 중 (고리 {appear.recognizeSeconds:0.0}s)</color>";
                default:
                    return "<color=#7fe07f>콘텐츠</color>";
            }
        }

        // ───────────────────────────── 만들기 ─────────────────────────────

        ArUcoWarpedImage NewBackdrop(Transform canvas, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(ArUcoWarpedImage));
            go.transform.SetParent(canvas, false);
            go.transform.SetAsFirstSibling();   // 콘텐츠·고리보다 뒤에

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;

            var img = go.GetComponent<ArUcoWarpedImage>();
            img.raycastTarget = false;
            img.color = color;
            img.enabled = false;
            return img;
        }

        /// <summary>콘텐츠 대용. 어두운 화면에서도 형태·회전·물결이 보이도록 그라데이션에 굵은 무늬를 넣는다.</summary>
        static Texture2D MakeContentTexture(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (float)x / (w - 1);
                float v = (float)y / (h - 1);
                Color col = Color.HSVToRGB(Mathf.Repeat(0.55f + u * 0.35f, 1f), 0.75f, 0.95f);

                // 사선 줄무늬 + 가운데 큰 원 + 흰 테두리
                float stripe = Mathf.Sin((u * 8f + v * 4.5f) * Mathf.PI) > 0.6f ? 0.18f : 0f;
                float dx = (u - 0.5f) * ((float)w / h), dy = v - 0.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d < 0.30f) col = Color.Lerp(col, Color.white, 0.55f);
                if (d < 0.30f && d > 0.27f) col = Color.white;
                col = Color.Lerp(col, Color.black, stripe);

                int border = 6;
                if (x < border || y < border || x >= w - border || y >= h - border) col = Color.white;

                px[y * w + x] = col;
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
