using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class UnityAlwaysOnTop : MonoBehaviour
{
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const UInt32 SWP_NOSIZE = 0x0001;
    private const UInt32 SWP_NOMOVE = 0x0002;
    private const UInt32 TOPMOST_FLAGS = SWP_NOMOVE | SWP_NOSIZE;

    [SerializeField] private Camera cam1;
    [SerializeField] private Camera cam2;
    [SerializeField] private Camera cam3;

    void Start()
    {


        if (Application.isEditor)
        {
            Debug.Log("에디터에서는 AlwaysOnTop 설정 생략");
            return;
        }
        // 에디터에선 무시
        if (Display.displays.Length > 1)
        {
            Display.displays[1].Activate();
        }
        if (Display.displays.Length > 2)
        {
            Display.displays[2].Activate();
        }

        if (!JsonManager.instance.gameSettingData.useUnityOnTop)
        {
            return;
        }

        Cursor.visible = false;
        // 빌드 실행 시 최상단 설정
        var windowName = Application.productName;
        IntPtr hWnd = FindWindow(null, windowName);
        if (hWnd != IntPtr.Zero)
        {
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, TOPMOST_FLAGS);
            Debug.Log("🪟빌드 실행파일에서 Unity 창을 항상 위로 설정했습니다.");
        }
        else
        {
            Debug.LogError(" Unity 창 핸들을 찾지 못했습니다.");
        }



        //화면

    }
}
