using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

public class UnityAlwaysOnTop : MonoBehaviour
{
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    // 제목 대신 "현재 프로세스의 메인 창"을 직접 찾기 위한 API들
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private const uint GW_OWNER = 4;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const UInt32 SWP_NOSIZE = 0x0001;
    private const UInt32 SWP_NOMOVE = 0x0002;
    private const UInt32 SWP_NOACTIVATE = 0x0010;
    // 재설정 시 포커스를 뺏지 않도록 NOACTIVATE 포함.
    private const UInt32 TOPMOST_FLAGS = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;

    void Start()
    {
        if (Application.isEditor)
        {
            Debug.Log("에디터에서는 AlwaysOnTop 설정 생략");
            return;
        }
        // 멀티 디스플레이 활성화
        if (Display.displays.Length > 1)
        {
            Display.displays[1].Activate();
        }
        if (Display.displays.Length > 2)
        {
            Display.displays[2].Activate();
        }

        var jm = JsonManager.instance;

        bool onTop = jm != null && jm.gameSettingData.useUnityOnTop;
        ApplyAlwaysOnTop(onTop);
        // FullScreenWindow 전환/창 재생성 과정에서 topmost가 풀릴 수 있어 잠시 뒤 재적용
        if (onTop) StartCoroutine(ReapplyAlwaysOnTop());
        ApplyMouseCursor(jm == null || jm.gameSettingData.showMouseCursor);
    }

    /// <summary>
    /// 마우스 커서 표시(show=true)/숨김(show=false). 항상맨위와 독립.
    /// 외부에서 토글 시 즉시 반영하기 위해 public. (에디터/빌드 모두 동작)
    /// </summary>
    public void ApplyMouseCursor(bool show)
    {
        Cursor.visible = show;
        Debug.Log(show ? "🖱️ 마우스 커서 표시" : "🖱️ 마우스 커서 숨김");
    }

    /// <summary>
    /// Unity 빌드 창을 항상 맨 위로 올리거나(on=true) 해제(on=false)한다.
    /// on=true면 주기적으로 topmost를 재설정해, 실행 중 다른 창을 열어도 계속 맨 위를 유지한다.
    /// 에디터에서는 무시한다. 외부에서 토글 시 즉시 반영하기 위해 public.
    /// </summary>
    public void ApplyAlwaysOnTop(bool on)
    {
        if (Application.isEditor)
        {
            Debug.Log("에디터에서는 AlwaysOnTop 적용 생략 (빌드 전용)");
            return;
        }

        // 현재 프로세스의 모든 최상위 게임 창을 대상으로 한다.
        // (멀티디스플레이에서 같은 제목의 창이 여러 개일 수 있으므로 제목 조회 대신 열거 사용)
        var windows = GetProcessTopLevelWindows();
        // 폴백: 열거로 못 찾은 경우 제목으로 조회
        if (windows.Count == 0)
        {
            IntPtr byTitle = FindWindow(null, Application.productName);
            if (byTitle != IntPtr.Zero) windows.Add(byTitle);
        }
        if (windows.Count == 0)
        {
            Debug.LogWarning("Unity 창 핸들을 찾지 못했습니다. (다음 주기에 재시도)");
            return;
        }

        IntPtr after = on ? HWND_TOPMOST : HWND_NOTOPMOST;
        foreach (var w in windows)
        {
            SetWindowPos(w, after, 0, 0, 0, 0, TOPMOST_FLAGS);
        }
        Debug.Log(on
            ? $"🪟 Unity 창 {windows.Count}개를 항상 맨 위로 설정했습니다."
            : $"🪟 Unity 창 {windows.Count}개 항상 맨 위를 해제했습니다.");
    }

    // 현재 프로세스에 속한 "보이는 최상위(소유자 없는) 창" 전부를 수집해 반환한다.
    // 창 제목에 의존하지 않으므로 멀티디스플레이/제목 변경 상황에서도 모든 게임 창을 잡는다.
    private static uint _targetProcessId;
    private static readonly List<IntPtr> _foundWindows = new List<IntPtr>();

    public static List<IntPtr> GetProcessTopLevelWindows()
    {
        _targetProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        _foundWindows.Clear();
        EnumWindows(EnumWindowCallback, IntPtr.Zero);
        return _foundWindows;
    }

    [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
    private static bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
    {
        GetWindowThreadProcessId(hWnd, out uint windowPid);
        if (windowPid != _targetProcessId) return true;         // 다른 프로세스 → 계속
        if (!IsWindowVisible(hWnd)) return true;                 // 숨겨진 창 → 계속
        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true; // 대화상자 등 소유된 창 → 계속
        _foundWindows.Add(hWnd);                                 // 최상위 게임 창 수집
        return true;                                            // 모든 창을 모으기 위해 계속
    }

    // FullScreenWindow 전환/창 재생성 과정에서 topmost가 풀릴 수 있어 몇 차례 재적용한다.
    private static readonly WaitForSeconds _reapplyWait = new WaitForSeconds(0.5f);

    IEnumerator ReapplyAlwaysOnTop()
    {
        for (int i = 0; i < 6; i++)   // 약 3초에 걸쳐 재적용
        {
            yield return _reapplyWait;
            ApplyAlwaysOnTop(true);
        }
    }
}
