using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 설정창 입력칸에 숫자를 치는 중인지. 단축키가 같은 키를 쓰므로(Backspace = 보정값 지우기,
    /// 숫자 = 세트 고르기, . = 다음 항목) 치는 동안에는 단축키를 건너뛴다.
    /// </summary>
    public static class UiTyping
    {
        public static bool IsTyping
        {
            get
            {
                var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                if (selected == null) return false;

                var input = selected.GetComponent<InputField>();
                return input != null && input.isFocused;
            }
        }
    }
}
