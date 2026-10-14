using System.Text;
using StoryRest.ArUco;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StoryRest
{
    /// <summary>
    /// 글씨 선명도 점검 화면(F8). 프로젝터로 쏜 글씨가 흐릴 때 어디가 원인인지 가른다.
    ///
    /// 프로젝터가 붙은 모든 화면(ArUco 세트 · 키워드 월)의 캔버스에 같은 프리팹(`9.Prefab/ScreenTestPattern.prefab`)이 들어 있고,
    /// 상태(켜짐 · 모드 · 글자 크기 · 글꼴)는 모든 화면이 함께 쓴다. 한 번 누르면 모든 프로젝터에 같은 것이 뜬다.
    ///
    /// 모드(Tab)
    ///   글자 빼곡   — 화면 전체를 고른 크기의 글자로 채운다. ↑↓ 로 실제 화면 픽셀 높이를 바꾼다.
    ///   크기 사다리 — 10px ~ 96px 를 한 화면에 줄줄이. 어디서부터 읽히는지 한눈에.
    ///   픽셀 패턴   — 1 · 2 · 4px 세로줄 · 가로줄 · 체커. 화면 픽셀과 1:1 로 찍는다(Unity 가 늘리거나 줄이지 않는다).
    ///
    /// 판단(안내 줄에도 뜬다)
    ///   1px 줄이 번진다          → 프로젝터 · 케이블 · Windows 쪽(초점, 키스톤 · 디지털 줌, 기본 해상도가 아닌 입력, 배율).
    ///   줄은 또렷한데 글자만 흐리다 → Unity 글자 그리기(크기 · 글꼴 · 캔버스 배율).
    ///   둘 다 또렷한데 영상 속 글씨만 흐리다 → 영상 파일(해상도 · 인코딩).
    /// </summary>
    public class ScreenTestPattern : MonoBehaviour
    {
        enum Mode { Fill, Ladder, Pixels }
        static readonly string[] ModeNames = { "글자 빼곡", "크기 사다리", "픽셀 패턴" };
        static readonly int[] LadderSizes = { 10, 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 64, 80, 96 };

        const string Sample = "알아보고 싶은 아카이브 북을 골라 테이블 위에 올려보세요 · 밤하늘을 수놓는 오로라는 어떻게 만들어질까? · " +
                              "ABCDEFGHIJ abcdefghij 0123456789 · ";
        const string LadderSample = "알아보고 싶은 아카이브 북 · 오로라 · ABC abc 0123";

        [SerializeField] KeyCode toggleKey = KeyCode.F8;

        [Header("화면 — 프리팹 안의 조각들")]
        [Tooltip("비워두면 놓인 캔버스를 쓴다.")]
        [SerializeField] Canvas _canvas;
        [Tooltip("켜고 끄는 묶음. 이 스크립트가 붙은 뿌리는 늘 켜져 있어 키를 받는다.")]
        [SerializeField] GameObject _body;
        [SerializeField] TMP_Text _text;
        [SerializeField] RawImage _pattern;
        [SerializeField] GameObject _patternLabels;
        [SerializeField] TMP_Text _info;

        [Tooltip("F 로 돌아가며 고른다. 첫 번째가 기본.")]
        [SerializeField] TMP_FontAsset[] _fonts;

        // 모든 화면이 함께 쓰는 상태. 프로젝터마다 따로 맞추면 비교가 안 된다.
        static bool s_active;
        static Mode s_mode;
        static int s_fontPx = 32;
        static int s_fontIndex;
        static bool s_showInfo = true;
        static int s_inputFrame = -1;

        public static KeyCode ActiveToggleKey { get; private set; } = KeyCode.F8;
        public static bool IsOn => s_active;

        static ArUcoEditMode s_editMode;
        static bool s_editModeSearched;

        // 다시 그릴 필요가 있는지 보려고 지난번 값을 들고 있는다.
        int _builtPx = -1, _builtFont = -1, _builtW, _builtH;
        Mode _builtMode = (Mode)(-1);
        Texture2D _patternTexture;
        readonly StringBuilder _builder = new StringBuilder(8192);

        void Awake()
        {
            ActiveToggleKey = toggleKey;
            if (_body != null) _body.SetActive(false);

            // 같은 프리팹을 여러 화면 캔버스에 넣어 쓰므로, 비워 두면 놓인 캔버스를 쓴다.
            if (_canvas == null) _canvas = GetComponentInParent<Canvas>(true);
        }

        void OnDestroy()
        {
            if (_patternTexture != null) Destroy(_patternTexture);
        }

        void Update()
        {
            HandleKeys();

            bool show = s_active && _canvas != null && _canvas.worldCamera != null
                        && _canvas.worldCamera.isActiveAndEnabled;
            if (_body != null && _body.activeSelf != show) _body.SetActive(show);
            if (!show) return;

            Refresh();
        }

        // 화면이 여럿이라 이 스크립트도 여럿이다. 키는 한 프레임에 한 번만 받는다.
        void HandleKeys()
        {
            if (s_inputFrame == Time.frameCount) return;
            s_inputFrame = Time.frameCount;

            if (UiTyping.IsTyping) return;

            // 편집모드와 키(방향키 · Tab)가 겹친다. 편집모드 중에는 열지 않고, 열려 있으면 닫는다.
            if (!s_editModeSearched)
            {
                s_editMode = FindObjectOfType<ArUcoEditMode>();
                s_editModeSearched = true;
            }
            bool editing = s_editMode != null && s_editMode.IsActive;

            if (Input.GetKeyDown(toggleKey))
            {
                if (editing && !s_active)
                    Debug.Log("[TestPattern] 편집모드 중에는 글씨 점검 화면을 열지 않습니다(키가 겹침). F2 로 나온 뒤 누르세요.");
                else
                {
                    s_active = !s_active;
                    Debug.Log($"[TestPattern] 글씨 점검 화면 {(s_active ? "켬" : "끔")}");
                }
            }

            if (editing) s_active = false;
            if (!s_active) return;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            int step = shift ? 10 : 1;

            if (Input.GetKeyDown(KeyCode.Tab)) s_mode = (Mode)(((int)s_mode + 1) % ModeNames.Length);
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))
                s_fontPx = Mathf.Min(300, s_fontPx + step);
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
                s_fontPx = Mathf.Max(6, s_fontPx - step);
            if (Input.GetKeyDown(KeyCode.F) && _fonts != null && _fonts.Length > 0) s_fontIndex = (s_fontIndex + 1) % _fonts.Length;
            if (Input.GetKeyDown(KeyCode.I)) s_showInfo = !s_showInfo;
        }

        void Refresh()
        {
            var cam = _canvas.worldCamera;
            int w = cam.pixelWidth, h = cam.pixelHeight;
            float scale = Mathf.Max(0.0001f, _canvas.scaleFactor);

            bool pixels = s_mode == Mode.Pixels;
            _text.gameObject.SetActive(!pixels);
            _pattern.gameObject.SetActive(pixels);
            if (_patternLabels != null) _patternLabels.SetActive(pixels);

            var font = CurrentFont();
            bool dirty = _builtMode != s_mode || _builtPx != s_fontPx || _builtFont != s_fontIndex || _builtW != w || _builtH != h;

            if (dirty)
            {
                _builtMode = s_mode; _builtPx = s_fontPx; _builtFont = s_fontIndex; _builtW = w; _builtH = h;

                if (font != null) _text.font = font;

                if (s_mode == Mode.Fill) BuildFill(w, h, scale);
                else if (s_mode == Mode.Ladder) BuildLadder(scale);
                else BuildPattern(w, h);
            }

            // 안내판(글 + 바탕)을 통째로 숨긴다. 글만 끄면 바탕 띠가 남아 아래 칸을 가린다.
            var infoBox = _info.transform.parent != null ? _info.transform.parent.gameObject : _info.gameObject;
            infoBox.SetActive(s_showInfo);
            if (s_showInfo) _info.text = InfoText(cam, w, h, scale, font);
        }

        TMP_FontAsset CurrentFont()
        {
            if (_fonts == null || _fonts.Length == 0) return null;
            return _fonts[Mathf.Clamp(s_fontIndex, 0, _fonts.Length - 1)];
        }

        // TMP 의 fontSize 는 캔버스 단위다. 화면 픽셀 = fontSize x scaleFactor 라서 거꾸로 나눠 넣는다.
        void BuildFill(int w, int h, float scale)
        {
            _text.fontSize = s_fontPx / scale;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.enableWordWrapping = true;
            _text.overflowMode = TextOverflowModes.Truncate;

            // 화면을 넘칠 만큼만 넉넉히. 넘치는 것은 잘린다(overflow Truncate).
            int perLine = Mathf.CeilToInt(w / (s_fontPx * 0.5f));
            int lines = Mathf.CeilToInt(h / (s_fontPx * 1.1f)) + 1;
            int need = Mathf.Min(perLine * lines, 60000);

            _builder.Clear();
            while (_builder.Length < need) _builder.Append(Sample);
            _text.text = _builder.ToString();
        }

        void BuildLadder(float scale)
        {
            _text.fontSize = 24f / scale;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.enableWordWrapping = false;   // 한 줄 = 한 크기. 넘치는 끝은 화면 밖으로 나간다
            // Truncate 는 넘친 글자 뒤를 전부 버려 다음 크기 줄까지 사라진다.
            _text.overflowMode = TextOverflowModes.Overflow;

            _builder.Clear();
            foreach (int px in LadderSizes)
            {
                // <size> 도 캔버스 단위라 화면 픽셀로 맞춰 넣는다.
                _builder.Append("<size=").Append((px / scale).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                        .Append(">").Append(px).Append("px  ").Append(LadderSample).Append("</size>\n");
            }
            _text.text = _builder.ToString();
        }

        // 화면 픽셀과 같은 크기의 텍스처를 점 필터로 화면 전체에 깐다 — 텍셀 하나가 화면 픽셀 하나다.
        // 3x3 칸: 윗줄 세로줄 1·2·4px, 가운데 가로줄 1·2·4px, 아랫줄 체커 1·2·4px. 가장자리에 1px 테두리.
        void BuildPattern(int w, int h)
        {
            if (w < 16 || h < 16) return;

            if (_patternTexture == null || _patternTexture.width != w || _patternTexture.height != h)
            {
                if (_patternTexture != null) Destroy(_patternTexture);
                _patternTexture = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "ScreenTestPattern",
                };
            }

            var px = new Color32[w * h];
            var white = new Color32(255, 255, 255, 255);
            var black = new Color32(0, 0, 0, 255);
            const int labelBand = 56;   // 칸 위쪽은 이름표 자리로 비운다
            const int margin = 16;

            for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                int period = col == 0 ? 1 : col == 1 ? 2 : 4;
                // 텍스처 y 는 아래에서 위로. 화면 위쪽 줄(row 0)이 텍스처의 위쪽이다.
                int x0 = col * w / 3 + margin, x1 = (col + 1) * w / 3 - margin;
                int yTop = h - row * h / 3 - labelBand, yBottom = h - (row + 1) * h / 3 + margin;

                for (int y = Mathf.Max(0, yBottom); y < Mathf.Min(h, yTop); y++)
                for (int x = Mathf.Max(0, x0); x < Mathf.Min(w, x1); x++)
                {
                    bool on;
                    if (row == 0) on = (x / period) % 2 == 0;
                    else if (row == 1) on = (y / period) % 2 == 0;
                    else on = ((x / period) + (y / period)) % 2 == 0;
                    px[y * w + x] = on ? white : black;
                }
            }

            for (int x = 0; x < w; x++) { px[x] = white; px[(h - 1) * w + x] = white; }
            for (int y = 0; y < h; y++) { px[y * w] = white; px[y * w + w - 1] = white; }

            for (int i = 0; i < px.Length; i++) if (px[i].a == 0) px[i] = black;

            _patternTexture.SetPixels32(px);
            _patternTexture.Apply(false);
            _pattern.texture = _patternTexture;
            _pattern.uvRect = new Rect(0, 0, 1, 1);
        }

        string InfoText(Camera cam, int w, int h, float scale, TMP_FontAsset font)
        {
            int index = cam.targetDisplay;
            int sysW = 0, sysH = 0, renW = 0, renH = 0;
            if (index < Display.displays.Length)
            {
                var d = Display.displays[index];
                sysW = d.systemWidth; sysH = d.systemHeight;
                renW = d.renderingWidth; renH = d.renderingHeight;
            }

            var b = new StringBuilder(1024);
            b.Append("<b>글씨 선명도 점검</b> — ").Append(ModeNames[(int)s_mode])
             .Append("    <color=#ffd633>Tab</color> 모드  <color=#ffd633>Up/Down · + -</color> 글자 크기(Shift x10)  ")
             .Append("<color=#ffd633>F</color> 글꼴  <color=#ffd633>I</color> 이 안내  <color=#ffd633>").Append(toggleKey).Append("</color> 닫기\n");

            b.Append("디스플레이 ").Append(index + 1)
             .Append(" · 이 화면 출력 ").Append(w).Append('x').Append(h)
             .Append(" · 윈도우 해상도 ").Append(sysW).Append('x').Append(sysH)
             .Append(" · Unity 렌더 ").Append(renW).Append('x').Append(renH)
             .Append(" · 캔버스 배율 ").Append(scale.ToString("0.###")).Append('\n');

            if (s_mode == Mode.Fill)
                b.Append("글자 높이 <b>").Append(s_fontPx).Append("px</b>(화면 픽셀) · ");
            b.Append("글꼴 ").Append(font != null ? font.name : _text.font != null ? _text.font.name : "-").Append('\n');

            if (sysW > 0 && (renW != sysW || renH != sysH))
                b.Append("<color=#ff6655>! Unity 렌더 해상도가 윈도우 해상도와 다릅니다 — Unity 가 늘리거나 줄여 그리고 있습니다.</color>\n");
            if (Application.isEditor)
                b.Append("<color=#888888>(에디터에서는 Game 뷰 크기 기준. 판단은 빌드로 실제 프로젝터에서)</color>\n");

            b.Append("<color=#aaaaaa>1px 줄이 번짐 → 프로젝터·케이블·Windows(초점, 키스톤·디지털 줌, 프로젝터 기본 해상도가 아님, 배율)\n")
             .Append("줄은 또렷한데 글자만 흐림 → Unity 글자 그리기(크기·글꼴)   ·   둘 다 또렷한데 영상 속 글씨만 흐림 → 영상 파일 해상도·인코딩</color>");
            return b.ToString();
        }
    }
}
