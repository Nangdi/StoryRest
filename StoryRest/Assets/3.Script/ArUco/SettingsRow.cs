using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 설정창에 덧붙일 줄 하나. 어떤 조각을 쓰는지는 원본마다 다르다 — 슬라이더 줄은 slider + value 를,
    /// 드롭다운 줄은 dropdown 을, 토글 줄은 toggle 을 채워 두고 나머지는 비운다.
    ///
    /// 복제한 줄에서 조각을 이름으로 찾지 않으려고 둔다. 이름으로 찾으면 프리팹에서 이름을 바꾸는 순간 조용히 깨진다.
    /// </summary>
    public class SettingsRow : MonoBehaviour
    {
        [Tooltip("줄 왼쪽의 이름과 도움말.")]
        public Text label;

        public Slider slider;

        [Tooltip("슬라이더 오른쪽의 숫자.")]
        public Text value;

        public Dropdown dropdown;
        public Toggle toggle;
    }
}
