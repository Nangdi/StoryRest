using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.ArUco
{
    /// <summary>
    /// ESC 설정창에 덧붙이는 줄들의 원본 모음(→ ARCHITECTURE §3).
    /// `Assets/9.Prefab/SettingsRows.prefab` 의 뿌리에 붙어 있고, ArUcoSettingsPanel 이 여기서 복제해 간다.
    ///
    /// 줄의 **개수와 내용**은 코드가 정한다(역할에 따라 달라지므로). 줄의 **모양**은 여기, 즉 에디터가 정한다.
    /// </summary>
    public class SettingsRowTemplates : MonoBehaviour
    {
        [Tooltip("구역 제목 줄.")]
        public Text header;

        [Tooltip("설명·파일 경로처럼 조작하지 않는 줄.")]
        public Text note;

        [Tooltip("이름 + 슬라이더 + 숫자.")]
        public SettingsRow sliderRow;

        [Tooltip("이름 + 드롭다운.")]
        public SettingsRow dropdownRow;

        [Tooltip("켜고 끄는 줄.")]
        public SettingsRow toggleRow;

        public bool IsComplete =>
            header != null && note != null && sliderRow != null && dropdownRow != null && toggleRow != null;
    }
}
