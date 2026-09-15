using System.Text;
using StoryRest.Stats;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 단축키 안내. F1 로 켜고 끈다.
    ///
    /// 현장에서 "무슨 키였더라" 를 문서 없이 화면에서 바로 본다. 세트마다 그 세트의 화면에 뜬다.
    /// 표시 전용이라 편집모드 밖에서 받는다(→ SPEC §6, C 키와 같은 예외).
    ///
    /// 편집모드가 켜져 있는 동안은 HUD 를 편집모드가 쓰므로 직접 그리지 않고,
    /// 편집모드가 자기 HUD 끝에 같은 내용을 이어 붙인다(→ ArUcoEditMode.UpdateHud).
    /// 키 이름은 각 컴포넌트의 필드에서 읽어 오므로 인스펙터에서 키를 바꿔도 안내가 따라온다.
    /// </summary>
    public class ArUcoHelpPanel : MonoBehaviour
    {
        [SerializeField] KeyCode toggleKey = KeyCode.F1;

        StoryRestApp _app;
        ArUcoEditMode _editMode;
        ArUcoStatsPanel _stats;
        ViewStatsLinkPanel _link;
        SettingsPanelUI _settings;

        bool _visible;
        readonly StringBuilder _builder = new StringBuilder(2048);

        public bool IsVisible => _visible;
        public KeyCode ToggleKey => toggleKey;

        void Awake()
        {
            _app = GetComponent<StoryRestApp>();
        }

        // 편집모드·관람 기록 패널은 이 컴포넌트 뒤에 붙으므로 Awake 에서는 아직 없다.
        void Start()
        {
            _editMode = GetComponent<ArUcoEditMode>();
            _stats = GetComponent<ArUcoStatsPanel>();
            _link = GetComponent<ViewStatsLinkPanel>();
            _settings = FindObjectOfType<SettingsPanelUI>(true);
        }

        void LateUpdate()
        {
            if (_app == null || _app.Sets.Count == 0) return;

            if (Input.GetKeyDown(toggleKey)) SetVisible(!_visible);
            if (!_visible) return;

            // 편집모드가 HUD 를 쓰는 동안은 편집모드가 이어 붙인다.
            if (_editMode != null && _editMode.IsActive) return;

            _builder.Clear();
            AppendHelp(_builder);
            string text = _builder.ToString();

            for (int i = 0; i < _app.Sets.Count; i++)
            {
                var view = _app.Sets[i].View;
                if (view != null) view.ShowHud(text);
            }
        }

        void SetVisible(bool visible)
        {
            _visible = visible;
            if (visible) return;

            // 편집모드가 켜져 있으면 HUD 는 편집모드 것이다. 건드리지 않는다.
            if (_editMode != null && _editMode.IsActive) return;
            for (int i = 0; i < _app.Sets.Count; i++) _app.Sets[i].View.ShowHud(null);
        }

        /// <summary>단축키 표 전체. 편집모드 HUD 도 이걸 이어 붙인다.</summary>
        public void AppendHelp(StringBuilder b)
        {
            const string k = "<color=#ffd633>";
            const string d = "</color><pos=46%>";

            b.AppendLine($"<b>단축키</b>   <color=#888888>({toggleKey} 로 닫기)</color>");
            b.AppendLine();

            b.AppendLine("<b>언제나</b>");
            b.AppendLine($"{k}{toggleKey}{d}이 안내");

            if (_editMode != null)
            {
                b.AppendLine($"{k}{_editMode.ToggleKey}{d}편집모드 {(_editMode.IsActive ? "나가기" : "들어가기")}");
            }

            if (_stats != null)
                b.AppendLine($"{k}{_stats.ToggleKey}{d}관람 기록 패널 {(_stats.IsVisible ? "닫기" : "열기")}");

            if (_link != null)
                b.AppendLine($"{k}{_link.ToggleKey}{d}관람 기록 링크 상태 {(_link.IsVisible ? "닫기" : "열기")} — 월 PC 연결 · 마지막 송수신");

            if (_editMode != null)
            {
                bool previewOn = false;
                for (int i = 0; i < _app.Sets.Count; i++) previewOn |= _app.Sets[i].DebugPreview;

                b.AppendLine($"{k}{_editMode.DebugPreviewKey}{d}디버그 격자 {(previewOn ? "끄기" : "켜기")} — " +
                             $"카메라 없이 콘텐츠 파일을 늘어놓고 점검");
            }

            b.AppendLine($"{k}C{d}카메라 영상 투사 (밖: 모든 세트 · 편집 중: 선택한 세트 · 설정에 저장됨)");

            if (_settings != null)
                b.AppendLine($"{k}{ArUcoEditMode.KeyName(_settings.ToggleKey)}{d}설정창");

            if (_editMode != null)
            {
                b.AppendLine();
                b.AppendLine($"<b>편집모드 안에서</b>   <color=#888888>({_editMode.ToggleKey} 로 들어간 뒤)</color>");
                _editMode.AppendKeyHelp(b);
            }

            b.Append("<color=#888888>맨키는 세트 전체, Ctrl 은 고른 마커 하나. 값은 aruco.json 에 곧바로 저장된다.</color>");
        }
    }
}
