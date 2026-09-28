using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 세트 하나의 출력을 담당한다. 전용 Camera + Canvas 위에, 마커 평면에 맞춰 콘텐츠를 눕혀 그린다.
    ///
    /// **화면은 프리팹이 만든다**(→ ARCHITECTURE §3). 카메라 · 캔버스 · 레이어 · 안내판 · 풀 원본이
    /// `Assets/9.Prefab/ArUcoSet.prefab` 안에 있고, 이 스크립트는 그것들을 켜고 끄고 좌표만 넣는다.
    /// 레이어 순서(무엇이 무엇 위에 오는가)도 프리팹의 자식 순서로 보인다.
    ///
    /// 콘텐츠가 놓이는 **자리는 에디터에서 잡을 수 없다** — 카메라가 읽은 마커 평면과 호모그래피가 정한다.
    /// 프리팹이 정하는 것은 레이어 순서 · 글꼴 · 색 · 안내판 모양이다.
    ///
    /// ScreenSpaceOverlay 대신 Camera.targetDisplay 를 쓰는 이유:
    /// Overlay 캔버스는 Screen.width/height 에 묶이는데 그 값은 주 디스플레이 기준이라
    /// 두 번째 프로젝터로 나가는 세트의 좌표가 어긋난다.
    ///
    /// 배경은 항상 검은색이다. 프로젝터는 실제 책자 위에 빛을 쏘므로
    /// 카메라 영상을 투사하면 실물과 겹쳐 보인다(→ docs/ARCHITECTURE.md §3).
    /// </summary>
    /// <summary>안내판(HUD)을 화면 어디에 둘지. 설치자가 보려는 자리를 가리지 않게 옮겨 다닌다.</summary>
    public enum HudAnchor { TopLeft, TopRight, BottomRight, BottomLeft, Center }

    [DisallowMultipleComponent]
    public class ArUcoProjectionView : MonoBehaviour
    {
        // 세트마다 카메라를 멀찍이 떨어뜨려 서로의 캔버스가 시야에 들어오지 않게 한다.
        // (레이어를 나누는 대신 거리로 분리한다 — 프로젝트 레이어를 건드리지 않아도 된다.)
        const float SetSeparation = 10000f;

        [Header("화면 — 프리팹 안의 조각들")]
        [SerializeField] Camera _camera;
        [SerializeField] Canvas _canvas;
        [SerializeField] RectTransform _canvasRect;

        [Header("레이어 — 자식 순서가 그리는 순서다")]
        [Tooltip("점검용 카메라 영상. 맨 뒤에 깔린다. 전시 중에는 꺼져 있다.")]
        [SerializeField] RawImage _cameraPreview;

        [Tooltip("편집 중인 마커를 가리키는 판.")]
        [SerializeField] ArUcoWarpedImage _highlight;

        [SerializeField] RectTransform _ringLayer;      // 읽는 중 빛 고리
        [SerializeField] RectTransform _contentLayer;   // 콘텐츠 판
        [SerializeField] RectTransform _labelLayer;     // 마커 안내 문구. 늘 콘텐츠 위
        [SerializeField] RectTransform _overlayLayer;   // 조준점 · 투사 마커
        [SerializeField] TMP_Text _statusText;

        [Header("편집모드 안내판")]
        [SerializeField] RectTransform _hudPanel;
        [SerializeField] TMP_Text _hudText;

        [Header("풀 원본 — 꺼진 자리에 두고 복제해 쓴다")]
        [SerializeField] ArUcoWarpedImage _warpedTemplate;
        [SerializeField] TMP_Text _labelTemplate;
        [SerializeField] RawImage _overlayTemplate;

        readonly List<ArUcoWarpedImage> _pool = new List<ArUcoWarpedImage>();

        // 제자리 드러내기(topReveal/spiral)용 머티리얼. 판마다 진행도가 다르므로 판마다 하나씩 둔다.
        readonly List<Material> _revealMaterials = new List<Material>();
        static Shader _revealShader;
        static bool _revealShaderMissing;

        readonly List<ArUcoWarpedImage> _ringPool = new List<ArUcoWarpedImage>();
        int _ringUsed;
        readonly List<RawImage> _overlayPool = new List<RawImage>();
        readonly List<TMP_Text> _labelPool = new List<TMP_Text>();
        readonly List<Vector2> _nodes = new List<Vector2>();

        int _overlayUsed;
        int _labelUsed;

        ArUcoProjection _projection;
        int _used;
        int _gridDivisions;

        public Camera OutputCamera => _camera;

        /// <summary>이 세트가 그리는 화면의 픽셀 크기. 디스플레이 해상도(또는 에디터 분할 뷰포트)를 따른다.</summary>
        public Vector2 CanvasSize => _canvasRect != null ? _canvasRect.rect.size : Vector2.zero;

        /// <param name="displayIndex">투사할 Unity 디스플레이 번호</param>
        /// <param name="viewport">에디터에서 세트를 나눠 볼 때 쓰는 뷰포트. 빌드에서는 전체 화면.</param>
        /// <param name="setIndex">카메라를 서로 떨어뜨리는 데 쓴다</param>
        /// <returns>프리팹 연결이 온전해 쓸 수 있으면 true.</returns>
        public bool Setup(string setName, int displayIndex, Rect viewport, int setIndex)
        {
            if (_camera == null || _canvas == null || _canvasRect == null || _statusText == null
                || _highlight == null || _ringLayer == null || _contentLayer == null
                || _labelLayer == null || _overlayLayer == null
                || _warpedTemplate == null || _labelTemplate == null || _overlayTemplate == null)
            {
                Debug.LogError($"[ArUco] 세트 화면 프리팹의 연결이 비었습니다({name}). " +
                               "Assets/9.Prefab/ArUcoSet.prefab 의 ArUcoProjectionView 에서 " +
                               "카메라·캔버스·레이어·풀 원본을 이어 주세요.");
                return false;
            }

            _camera.name = $"Camera_{setName}";
            _canvas.name = $"Canvas_{setName}";

            // 세트끼리 겹쳐 보이지 않도록 축을 따라 크게 떨어뜨린다.
            _camera.transform.position = new Vector3(0f, (setIndex + 1) * SetSeparation, 0f);

            // 여기서 정하는 것은 "어느 프로젝터로 · 화면의 어느 칸에 · 몇 번째로 그리는가" 뿐이다.
            // 배경색·투영방식·클리핑은 프리팹의 카메라가 이미 정해 두었다.
            _camera.targetDisplay = Mathf.Max(0, displayIndex);
            _camera.rect = viewport;
            _camera.depth = setIndex;

            _canvas.worldCamera = _camera;

            _highlight.enabled = false;
            _statusText.enabled = false;
            if (_cameraPreview != null) _cameraPreview.enabled = false;
            if (_hudPanel != null) _hudPanel.gameObject.SetActive(false);

            return true;
        }

        public void SetProjection(ArUcoProjection projection)
        {
            _projection = projection;
        }

        /// <summary>
        /// 카메라 영상을 배경에 깐다. 편집모드에서 마커가 실제로 잡히는지 보려는 용도다.
        /// 전시 중에는 절대 켜지 않는다 — 실물 책자 위에 카메라 영상이 겹쳐 투사된다.
        /// </summary>
        public void ShowCameraPreview(Texture texture)
        {
            if (_cameraPreview == null) return;

            if (texture == null)
            {
                _cameraPreview.enabled = false;
                return;
            }

            _cameraPreview.texture = texture;
            _cameraPreview.enabled = true;
        }

        /// <summary>카메라를 열지 못했을 때처럼 사람이 조치해야 하는 상황만 화면에 띄운다.</summary>
        public void ShowStatus(string message)
        {
            bool show = !string.IsNullOrEmpty(message);
            _statusText.enabled = show;
            if (show) _statusText.text = message;
        }

        public void BeginFrame()
        {
            _used = 0;
            _labelUsed = 0;
            _ringUsed = 0;
            _highlight.enabled = false;
        }

        /// <summary>
        /// 마커 평면 위에 콘텐츠 한 장을 눕혀 그린다. 배치값은 세트 공통 → 마커 → 파일을 이미 합친 것이다.
        /// 평면이 서 있어(카메라와 거의 평행) 좌표가 발산하면 false 를 돌리고 아무것도 그리지 않는다.
        /// </summary>
        public bool Draw(ArUcoHomography markerPlane, ArUcoPlacement placement, ArUcoDrawStyle style)
        {
            if (!BuildGrid(markerPlane, placement, style, 0f)) return false;

            var view = GetView(_used);
            view.SetTexture(style.texture);
            view.FlipV = style.flipV;
            view.color = style.tint;
            view.SetNodes(_gridDivisions, _gridDivisions, _nodes);
            ApplyReveal(_used, view, style);
            view.enabled = true;

            _used++;
            return true;
        }

        /// <summary>
        /// 읽는 중 빛 고리. 마커 자리에서 t(0→1)에 따라 퍼지며 사라진다(→ ArUcoAppearTracker.Ring).
        /// 세트 공통 배율·위치는 무시한다 — 고리는 콘텐츠가 아니라 마커 자체를 가리키는 표시다.
        /// </summary>
        public void DrawRing(ArUcoHomography markerPlane, float t, ArUcoDrawStyle style)
        {
            ArUcoAppearTracker.Ring(t, out float scale, out float alpha);
            if (alpha <= 0.001f) return;

            var ringStyle = ArUcoDrawStyle.Default(1f, Vector2.zero, 4, style.perspective);
            var placement = ArUcoPlacement.Identity;
            placement.scale = scale;

            if (!BuildGrid(markerPlane, placement, ringStyle, 0f)) return;

            while (_ringPool.Count <= _ringUsed)
                _ringPool.Add(NewWarpedImage("Ring" + _ringPool.Count, _ringLayer));

            var image = _ringPool[_ringUsed];
            image.SetTexture(ArUcoRingTexture.Get());
            image.FlipV = false;
            image.color = new Color(1f, 0.95f, 0.8f, alpha);
            image.SetNodes(_gridDivisions, _gridDivisions, _nodes);
            image.enabled = true;
            _ringUsed++;
        }

        // 제자리 드러내기는 셰이더가 알파를 깎는다. 그 밖의 판은 기본 UI 머티리얼로 되돌린다.
        // 셰이더가 없으면(Resources 누락) 알파 페이드로 대신해 화면이 비지는 않게 한다.
        void ApplyReveal(int index, ArUcoWarpedImage view, ArUcoDrawStyle style)
        {
            while (_revealMaterials.Count <= index) _revealMaterials.Add(null);

            if (style.revealMode == RevealMode.None)
            {
                if (view.material != view.defaultMaterial) view.material = null;
                return;
            }

            if (_revealShader == null && !_revealShaderMissing)
            {
                _revealShader = Resources.Load<Shader>("ArUcoReveal");
                if (_revealShader == null)
                {
                    _revealShaderMissing = true;
                    Debug.LogWarning("[ArUco] Resources/ArUcoReveal 셰이더가 없어 제자리 드러내기 대신 페이드로 그립니다.");
                }
            }

            if (_revealShader == null)
            {
                var tint = view.color;
                tint.a *= Mathf.Clamp01(style.reveal);
                view.color = tint;
                if (view.material != view.defaultMaterial) view.material = null;
                return;
            }

            var material = _revealMaterials[index];
            if (material == null)
            {
                material = new Material(_revealShader) { hideFlags = HideFlags.DontSave };
                _revealMaterials[index] = material;
            }

            material.SetFloat("_Mode", (int)style.revealMode);
            material.SetFloat("_Reveal", style.reveal);
            material.SetFloat("_Soft", style.revealSoft);
            material.SetFloat("_Aspect", style.aspect);
            material.SetFloat("_Turns", style.spiralTurns);
            material.SetFloat("_Flip", style.flipV ? 1f : 0f);

            if (view.material != material) view.material = material;
        }

        /// <summary>편집 중인 마커에 조금 더 큰 판을 깔아 어느 것을 조정 중인지 보이게 한다.</summary>
        public void DrawHighlight(ArUcoHomography markerPlane, ArUcoPlacement placement, ArUcoDrawStyle style)
        {
            if (!BuildGrid(markerPlane, placement, style, 0.06f)) return;

            _highlight.SetNodes(_gridDivisions, _gridDivisions, _nodes);
            _highlight.enabled = true;
        }

        public void EndFrame()
        {
            for (int i = _used; i < _pool.Count; i++) _pool[i].enabled = false;
            for (int i = _labelUsed; i < _labelPool.Count; i++) _labelPool[i].enabled = false;
            for (int i = _ringUsed; i < _ringPool.Count; i++) _ringPool[i].enabled = false;
        }

        /// <summary>
        /// 마커 자리에 안내 문구를 띄운다. 콘텐츠가 뜰 자리를 그대로 덮되 워프하지 않는다 —
        /// 글자는 읽히는 것이 목적이라 원근을 주면 오히려 알아보기 어렵다.
        /// </summary>
        public bool DrawLabel(ArUcoHomography markerPlane, ArUcoPlacement placement, ArUcoDrawStyle style, string text)
        {
            if (!BuildGrid(markerPlane, placement, style, 0f)) return false;

            // 워프된 사각형을 감싸는 박스를 잡는다. 마커가 기울어도 글자는 화면과 나란히 남는다.
            Vector2 min = _nodes[0];
            Vector2 max = _nodes[0];

            for (int i = 1; i < _nodes.Count; i++)
            {
                min = Vector2.Min(min, _nodes[i]);
                max = Vector2.Max(max, _nodes[i]);
            }

            var label = GetLabel(_labelUsed);
            label.rectTransform.anchoredPosition = (min + max) * 0.5f;
            label.rectTransform.sizeDelta = max - min;
            label.text = text;
            label.enabled = true;

            _labelUsed++;
            return true;
        }

        TMP_Text GetLabel(int index)
        {
            while (_labelPool.Count <= index)
            {
                var text = Instantiate(_labelTemplate, _labelLayer);
                text.gameObject.SetActive(true);
                text.name = $"Label{_labelPool.Count}";
                text.enabled = false;

                _labelPool.Add(text);
            }
            return _labelPool[index];
        }

        // ── 편집모드용 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 편집모드 안내판. null 을 넘기면 숨긴다. 전시 중에는 꺼져 있다.
        /// 높이는 글에 맞춰 늘고 줄어든다(프리팹의 ContentSizeFitter) — 고정 크기면 짧은 안내에도 한 구석을 통째로 가린다.
        /// </summary>
        /// <param name="anchor">
        /// 놓을 자리. 코너 보정은 네 귀퉁이의 조준점을 가리면 안 되므로 가운데에 두고,
        /// 배치 중에는 설치자가 H 로 옮겨 가며 맞추려는 마커를 비켜 준다.
        /// </param>
        public void ShowHud(string text, HudAnchor anchor = HudAnchor.TopLeft)
        {
            if (_hudPanel == null || _hudText == null) return;

            if (string.IsNullOrEmpty(text))
            {
                _hudPanel.gameObject.SetActive(false);
                return;
            }

            const float margin = 24f;
            Vector2 a;
            Vector2 pos;

            switch (anchor)
            {
                case HudAnchor.TopRight:    a = new Vector2(1f, 1f);     pos = new Vector2(-margin, -margin); break;
                case HudAnchor.BottomRight: a = new Vector2(1f, 0f);     pos = new Vector2(-margin, margin);  break;
                case HudAnchor.BottomLeft:  a = new Vector2(0f, 0f);     pos = new Vector2(margin, margin);   break;
                case HudAnchor.Center:      a = new Vector2(0.5f, 0.5f); pos = Vector2.zero;                  break;
                default:                    a = new Vector2(0f, 1f);     pos = new Vector2(margin, -margin);  break;
            }

            _hudPanel.anchorMin = _hudPanel.anchorMax = _hudPanel.pivot = a;
            _hudPanel.anchoredPosition = pos;

            _hudPanel.gameObject.SetActive(true);
            _hudText.text = text;
        }

        public void BeginOverlay() => _overlayUsed = 0;

        /// <summary>
        /// 프로젝터 좌표(0~1)에 사각형을 하나 그린다. 캘리브레이션 조준점과 투사 마커에 쓴다.
        /// 워프 없이 화면과 나란한 사각형이다 — 프로젝터가 쏘는 원본 좌표를 그대로 보여줘야 하기 때문이다.
        /// </summary>
        /// <param name="sizeNormalized">화면 짧은 변을 1 로 보는 크기</param>
        public void DrawOverlay(Vector2 projectorPoint, float sizeNormalized, Texture texture, Color tint)
        {
            var quad = GetOverlay(_overlayUsed);
            Vector2 canvasSize = _canvasRect.rect.size;

            float side = sizeNormalized * Mathf.Min(canvasSize.x, canvasSize.y);

            quad.texture = texture;
            quad.color = tint;
            quad.rectTransform.sizeDelta = new Vector2(side, side);
            quad.rectTransform.anchoredPosition = new Vector2(
                (projectorPoint.x - 0.5f) * canvasSize.x,
                (0.5f - projectorPoint.y) * canvasSize.y);
            quad.enabled = true;

            _overlayUsed++;
        }

        public void EndOverlay()
        {
            for (int i = _overlayUsed; i < _overlayPool.Count; i++) _overlayPool[i].enabled = false;
        }

        RawImage GetOverlay(int index)
        {
            while (_overlayPool.Count <= index)
            {
                var image = Instantiate(_overlayTemplate, _overlayLayer);
                image.gameObject.SetActive(true);
                image.name = $"Overlay{_overlayPool.Count}";
                image.enabled = false;

                _overlayPool.Add(image);
            }
            return _overlayPool[index];
        }

        /// <summary>
        /// 콘텐츠 사각형을 격자로 나눠 각 꼭짓점을 마커 평면 위로 옮긴 뒤,
        /// 카메라 픽셀 → 프로젝터 좌표 → 캔버스 로컬 좌표로 바꿔 _nodes 에 담는다.
        /// </summary>
        bool BuildGrid(ArUcoHomography plane, ArUcoPlacement placement, ArUcoDrawStyle style, float margin)
        {
            if (!plane.IsValid) return false;

            // 마커 한 변을 1 로 보는 상대 크기라 카메라 거리가 바뀌어도 값이 유지된다.
            float halfWidth = 0.5f * style.globalScale * placement.scale + margin;
            float halfHeight = halfWidth * style.aspect + margin;

            // 원근이 없으면 사각형 한 장으로도 정확하다. 쪼갤 이유가 없다 — 물결 연출만은 격자가 있어야 보인다.
            _gridDivisions = style.perspective || style.wave > 0f ? Mathf.Clamp(style.subdivisions, 1, 32) : 1;

            float rad = placement.rotationOffset * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);

            Vector2 canvasSize = _canvasRect.rect.size;

            _nodes.Clear();

            // 물결 연출(→ ArUcoAppearTracker.Apply, Wave). 평면 위에서 노드를 세로로 흔든다.
            float wave = style.wave > 0f ? style.wave * halfHeight * 2f : 0f;
            float wavePhase = Time.unscaledTime * 14f;

            for (int y = 0; y <= _gridDivisions; y++)
            {
                float py = Mathf.Lerp(-halfHeight, halfHeight, (float)y / _gridDivisions);

                for (int x = 0; x <= _gridDivisions; x++)
                {
                    float fx = (float)x / _gridDivisions;
                    float px = Mathf.Lerp(-halfWidth, halfWidth, fx);
                    float wy = wave > 0f ? py + Mathf.Sin(fx * Mathf.PI * 3f - wavePhase) * wave : py;

                    // 마커 평면 안에서 회전·이동시킨다. 평면 위에서 처리하므로
                    // 책자를 어떻게 돌리고 눕혀도 콘텐츠는 페이지의 같은 자리에 머문다.
                    //
                    // 세트 공통 위치는 개별 회전 뒤에 더한다. 마커별 offset 과 같은 좌표계에 있어야
                    // "전체를 밀어 둔 자리에서 이 마커만 조금 더" 가 예상대로 동작한다.
                    // 파일별 offset 도 같은 이유로 마커 offset 에 그냥 더해 둔 상태로 들어온다.
                    float rx = px * cos - wy * sin + placement.offsetX + style.globalOffset.x;
                    float ry = px * sin + wy * cos + placement.offsetY + style.globalOffset.y;

                    // 마커 중심이 (0.5, 0.5), 한 변이 1. v 는 아래로 증가하므로 y 부호를 뒤집는다.
                    if (!plane.TryMap(new Vector2(0.5f + rx, 0.5f - ry), out Vector2 cameraPixel))
                        return false;

                    if (!_projection.TryMap(cameraPixel, out Vector2 projector))
                        return false;

                    // 프로젝터 정규화(좌상단 원점, y 아래로) → 캔버스 로컬(중심 원점, y 위로)
                    _nodes.Add(new Vector2(
                        (projector.x - 0.5f) * canvasSize.x,
                        (0.5f - projector.y) * canvasSize.y));
                }
            }

            return true;
        }

        ArUcoWarpedImage GetView(int index)
        {
            while (_pool.Count <= index) _pool.Add(NewWarpedImage($"Content{_pool.Count}", _contentLayer));
            return _pool[index];
        }

        /// <summary>
        /// 프리팹의 원본을 복제해 판 하나를 만든다. 격자 꼭짓점을 캔버스 로컬 좌표로 직접 넣으므로
        /// RectTransform 은 원본에서 화면 전체를 덮게 잡아 두었다.
        ///
        /// 레이어(부모)가 순서를 정한다 — 고리는 Rings, 콘텐츠는 Content 아래로 간다.
        /// 예전처럼 판이 늘 때마다 라벨 레이어를 맨 뒤로 옮길 필요가 없다.
        /// </summary>
        ArUcoWarpedImage NewWarpedImage(string name, RectTransform parent)
        {
            var image = Instantiate(_warpedTemplate, parent);
            image.gameObject.SetActive(true);
            image.name = name;
            image.enabled = false;

            return image;
        }
    }

    /// <summary>
    /// 한 장을 그리는 데 필요한, 마커별 설정 바깥의 값들.
    /// 인자가 늘어나 호출부가 읽기 어려워지는 것을 막으려고 묶었다.
    /// </summary>
    public struct ArUcoDrawStyle
    {
        public Texture texture;
        public Color tint;
        public bool flipV;

        // 세로/가로 비율. 텍스처가 없으면 1(정사각형).
        public float aspect;

        public float globalScale;

        // 세트 공통 위치. 마커별 offset 위에 더해진다(→ SetConfig.globalOffsetX/Y).
        public Vector2 globalOffset;

        public int subdivisions;
        public bool perspective;

        // ---- 등장 연출(→ ArUcoAppearTracker.Apply) ----
        // 격자 노드를 세로로 흔드는 진폭(콘텐츠 높이 = 1). 0 이면 흔들지 않는다.
        public float wave;

        // 제자리 드러내기. None 이 아니면 ArUcoReveal 셰이더가 reveal(0→1)만큼만 보이게 한다.
        public RevealMode revealMode;
        public float reveal;
        public float revealSoft;
        public float spiralTurns;

        public static ArUcoDrawStyle Default(float globalScale, Vector2 globalOffset,
                                             int subdivisions, bool perspective)
        {
            return new ArUcoDrawStyle
            {
                texture = null,
                tint = Color.white,
                flipV = false,
                aspect = 1f,
                globalScale = globalScale,
                globalOffset = globalOffset,
                subdivisions = subdivisions,
                perspective = perspective,
                wave = 0f,
                revealMode = RevealMode.None,
                reveal = 1f,
                revealSoft = 0.12f,
                spiralTurns = 2f,
            };
        }
    }
}
