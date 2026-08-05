using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 세트 하나의 출력을 담당한다. 전용 Camera + Canvas 를 만들고, 마커 평면 위에 콘텐츠를 눕혀 그린다.
    ///
    /// ScreenSpaceOverlay 대신 Camera.targetDisplay 를 쓰는 이유:
    /// Overlay 캔버스는 Screen.width/height 에 묶이는데 그 값은 주 디스플레이 기준이라
    /// 두 번째 프로젝터로 나가는 세트의 좌표가 어긋난다.
    ///
    /// 배경은 항상 검은색이다. 프로젝터는 실제 책자 위에 빛을 쏘므로
    /// 카메라 영상을 투사하면 실물과 겹쳐 보인다(→ docs/ARCHITECTURE.md §3).
    /// </summary>
    [DisallowMultipleComponent]
    public class ArUcoProjectionView : MonoBehaviour
    {
        // 세트마다 카메라를 멀찍이 떨어뜨려 서로의 캔버스가 시야에 들어오지 않게 한다.
        // (레이어를 나누는 대신 거리로 분리한다 — 프로젝트 레이어를 건드리지 않아도 된다.)
        const float SetSeparation = 10000f;

        Camera _camera;
        Canvas _canvas;
        RectTransform _canvasRect;
        TMP_Text _statusText;
        ArUcoWarpedImage _highlight;

        // 편집모드 전용. 평상시에는 만들지 않는다.
        RawImage _cameraPreview;
        RectTransform _hudPanel;
        Image _hudBackground;
        TMP_Text _hudText;

        readonly List<ArUcoWarpedImage> _pool = new List<ArUcoWarpedImage>();
        readonly List<RawImage> _overlayPool = new List<RawImage>();
        readonly List<Vector2> _nodes = new List<Vector2>();

        int _overlayUsed;

        ArUcoProjection _projection;
        int _used;
        int _gridDivisions;

        public Camera OutputCamera => _camera;

        /// <summary>이 세트가 그리는 화면의 픽셀 크기. 디스플레이 해상도(또는 에디터 분할 뷰포트)를 따른다.</summary>
        public Vector2 CanvasSize => _canvasRect != null ? _canvasRect.rect.size : Vector2.zero;

        /// <param name="displayIndex">투사할 Unity 디스플레이 번호</param>
        /// <param name="viewport">에디터에서 세트를 나눠 볼 때 쓰는 뷰포트. 빌드에서는 전체 화면.</param>
        /// <param name="setIndex">카메라를 서로 떨어뜨리는 데 쓴다</param>
        public void Setup(string setName, int displayIndex, Rect viewport, int setIndex)
        {
            var cameraGo = new GameObject($"Camera_{setName}");
            cameraGo.transform.SetParent(transform, false);

            // 세트끼리 겹쳐 보이지 않도록 축을 따라 크게 떨어뜨린다.
            cameraGo.transform.position = new Vector3(0f, (setIndex + 1) * SetSeparation, 0f);

            _camera = cameraGo.AddComponent<Camera>();
            _camera.targetDisplay = Mathf.Max(0, displayIndex);
            _camera.rect = viewport;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;   // 투사면에서 검은색 = 빛 없음
            _camera.orthographic = true;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 100f;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.depth = setIndex;

            var canvasGo = new GameObject($"Canvas_{setName}", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(cameraGo.transform, false);

            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceCamera;
            _canvas.worldCamera = _camera;
            _canvas.planeDistance = 1f;
            _canvas.sortingOrder = 0;

            // 상수 배율이라 RectTransform 1 단위 = 화면 1픽셀이 된다. 좌표 계산이 단순해진다.
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            _canvasRect = canvasGo.GetComponent<RectTransform>();

            // 편집 중인 마커를 표시할 판. 콘텐츠보다 먼저 만들어 항상 뒤에 깔리게 한다.
            _highlight = NewWarpedImage("Highlight");
            _highlight.color = new Color(1f, 0.85f, 0.2f, 0.35f);
            _highlight.enabled = false;

            _statusText = NewText("Status");
            _statusText.enabled = false;
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
            if (texture == null)
            {
                if (_cameraPreview != null) _cameraPreview.enabled = false;
                return;
            }

            if (_cameraPreview == null)
            {
                var go = new GameObject("CameraPreview", typeof(RectTransform));
                // 콘텐츠보다 뒤에 깔리도록 맨 앞에 넣는다(자식 순서 = 그리는 순서).
                go.transform.SetParent(_canvasRect, false);
                go.transform.SetAsFirstSibling();

                _cameraPreview = go.AddComponent<RawImage>();
                _cameraPreview.raycastTarget = false;
                _cameraPreview.color = new Color(1f, 1f, 1f, 0.5f);
                Stretch(_cameraPreview.rectTransform);
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
            _highlight.enabled = false;
        }

        /// <summary>
        /// 마커 평면 위에 콘텐츠 한 장을 눕혀 그린다.
        /// 평면이 서 있어(카메라와 거의 평행) 좌표가 발산하면 false 를 돌리고 아무것도 그리지 않는다.
        /// </summary>
        public bool Draw(ArUcoHomography markerPlane, MarkerConfig marker, ArUcoDrawStyle style)
        {
            if (!BuildGrid(markerPlane, marker, style, 0f)) return false;

            var view = GetView(_used);
            view.SetTexture(style.texture);
            view.FlipV = style.flipV;
            view.color = style.tint;
            view.SetNodes(_gridDivisions, _gridDivisions, _nodes);
            view.enabled = true;

            _used++;
            return true;
        }

        /// <summary>편집 중인 마커에 조금 더 큰 판을 깔아 어느 것을 조정 중인지 보이게 한다.</summary>
        public void DrawHighlight(ArUcoHomography markerPlane, MarkerConfig marker, ArUcoDrawStyle style)
        {
            if (!BuildGrid(markerPlane, marker, style, 0.06f)) return;

            _highlight.SetNodes(_gridDivisions, _gridDivisions, _nodes);
            _highlight.enabled = true;
        }

        public void EndFrame()
        {
            for (int i = _used; i < _pool.Count; i++) _pool[i].enabled = false;
        }

        // ── 편집모드용 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 편집모드 안내판. null 을 넘기면 숨긴다.
        /// 전시 중에는 어떤 편집 UI 도 보이지 않아야 하므로 필요할 때 처음 만든다.
        /// </summary>
        /// <param name="center">
        /// 화면 중앙에 놓는다. 코너 보정 중에는 네 귀퉁이의 조준점을 가리면 안 되므로
        /// 안내판을 비어 있는 가운데로 옮긴다.
        /// </param>
        public void ShowHud(string text, bool center = false)
        {
            if (string.IsNullOrEmpty(text))
            {
                if (_hudPanel != null) _hudPanel.gameObject.SetActive(false);
                return;
            }

            if (_hudPanel == null) BuildHud();

            if (center)
            {
                _hudPanel.anchorMin = _hudPanel.anchorMax = _hudPanel.pivot = new Vector2(0.5f, 0.5f);
                _hudPanel.anchoredPosition = Vector2.zero;
            }
            else
            {
                _hudPanel.anchorMin = _hudPanel.anchorMax = _hudPanel.pivot = new Vector2(0f, 1f);
                _hudPanel.anchoredPosition = new Vector2(24f, -24f);
            }

            _hudPanel.gameObject.SetActive(true);
            _hudText.text = text;
        }

        void BuildHud()
        {
            var panelGo = new GameObject("EditHud", typeof(RectTransform));
            panelGo.transform.SetParent(_canvasRect, false);

            _hudPanel = panelGo.GetComponent<RectTransform>();
            _hudPanel.anchorMin = _hudPanel.anchorMax = _hudPanel.pivot = new Vector2(0f, 1f);
            _hudPanel.sizeDelta = new Vector2(720f, 520f);
            _hudPanel.anchoredPosition = new Vector2(24f, -24f);

            _hudBackground = panelGo.AddComponent<Image>();
            _hudBackground.color = new Color(0f, 0f, 0f, 0.78f);
            _hudBackground.raycastTarget = false;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(_hudPanel, false);

            _hudText = textGo.AddComponent<TextMeshProUGUI>();
            _hudText.color = Color.white;
            _hudText.raycastTarget = false;
            _hudText.richText = true;
            _hudText.alignment = TextAlignmentOptions.TopLeft;
            _hudText.fontSize = 22f;
            _hudText.lineSpacing = 8f;

            var textRect = _hudText.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(28f, 24f);
            textRect.offsetMax = new Vector2(-28f, -24f);
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
                var go = new GameObject($"Overlay{_overlayPool.Count}", typeof(RectTransform));
                go.transform.SetParent(_canvasRect, false);

                var image = go.AddComponent<RawImage>();
                image.raycastTarget = false;

                var rect = image.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);

                _overlayPool.Add(image);
            }
            return _overlayPool[index];
        }

        /// <summary>
        /// 콘텐츠 사각형을 격자로 나눠 각 꼭짓점을 마커 평면 위로 옮긴 뒤,
        /// 카메라 픽셀 → 프로젝터 좌표 → 캔버스 로컬 좌표로 바꿔 _nodes 에 담는다.
        /// </summary>
        bool BuildGrid(ArUcoHomography plane, MarkerConfig marker, ArUcoDrawStyle style, float margin)
        {
            if (!plane.IsValid) return false;

            // 마커 한 변을 1 로 보는 상대 크기라 카메라 거리가 바뀌어도 값이 유지된다.
            float halfWidth = 0.5f * style.globalScale * marker.scale + margin;
            float halfHeight = halfWidth * style.aspect + margin;

            // 원근이 없으면 사각형 한 장으로도 정확하다. 쪼갤 이유가 없다.
            _gridDivisions = style.perspective ? Mathf.Clamp(style.subdivisions, 1, 32) : 1;

            float rad = marker.rotationOffset * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);

            Vector2 canvasSize = _canvasRect.rect.size;

            _nodes.Clear();

            for (int y = 0; y <= _gridDivisions; y++)
            {
                float py = Mathf.Lerp(-halfHeight, halfHeight, (float)y / _gridDivisions);

                for (int x = 0; x <= _gridDivisions; x++)
                {
                    float px = Mathf.Lerp(-halfWidth, halfWidth, (float)x / _gridDivisions);

                    // 마커 평면 안에서 회전·이동시킨다. 평면 위에서 처리하므로
                    // 책자를 어떻게 돌리고 눕혀도 콘텐츠는 페이지의 같은 자리에 머문다.
                    float rx = px * cos - py * sin + marker.offsetX;
                    float ry = px * sin + py * cos + marker.offsetY;

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
            while (_pool.Count <= index) _pool.Add(NewWarpedImage($"Content{_pool.Count}"));
            return _pool[index];
        }

        ArUcoWarpedImage NewWarpedImage(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(ArUcoWarpedImage));
            go.transform.SetParent(_canvasRect, false);

            var image = go.GetComponent<ArUcoWarpedImage>();
            image.raycastTarget = false;

            // 격자 꼭짓점을 캔버스 로컬 좌표로 직접 넣으므로 RectTransform 은 화면 전체를 덮게 둔다.
            Stretch(image.rectTransform);
            return image;
        }

        // 텍스트는 TextMeshPro 로 통일한다(기존 프로젝트 UI 와 같은 방식).
        // 한글 글리프는 TMP Settings 의 fallback(NotoSansKR)이 채우므로 OS 폰트에 의존하지 않는다 —
        // 전시 PC 에 어떤 폰트가 깔려 있든 같은 화면이 나온다.
        TMP_Text NewText(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_canvasRect, false);

            var text = go.AddComponent<TextMeshProUGUI>();
            text.color = Color.white;
            text.raycastTarget = false;
            text.richText = true;
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = 26f;
            text.enableWordWrapping = true;
            Stretch(text.rectTransform);
            return text;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
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
        public int subdivisions;
        public bool perspective;

        public static ArUcoDrawStyle Default(float globalScale, int subdivisions, bool perspective)
        {
            return new ArUcoDrawStyle
            {
                texture = null,
                tint = Color.white,
                flipV = false,
                aspect = 1f,
                globalScale = globalScale,
                subdivisions = subdivisions,
                perspective = perspective,
            };
        }
    }
}
