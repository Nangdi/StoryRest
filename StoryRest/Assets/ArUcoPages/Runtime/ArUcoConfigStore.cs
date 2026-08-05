using System;
using System.IO;
using UnityEngine;

/// <summary>
/// aruco.json 을 읽고 쓰는 곳. 패키지가 다른 프로젝트에서도 그대로 돌아가도록
/// 설정 입출력을 여기 한 군데로 모아 두었다.
///
/// 아무것도 안 해도 StreamingAssets/aruco.json 을 직접 읽고 쓴다.
/// 프로젝트에 이미 자체 설정 관리자가 있다면 Loader / Saver 를 끼워
/// 그쪽 인스턴스를 공유하게 만들 수 있다. (설정창과 편집모드 값이 어긋나지 않게 하려는 용도)
///
/// 끼우는 예:
///   [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
///   static void Install()
///   {
///       ArUcoConfigStore.Loader = () => MyManager.instance?.arucoJson;
///       ArUcoConfigStore.Saver  = config => MyManager.instance.SaveArUcoJson();
///   }
/// </summary>
public static class ArUcoConfigStore
{
    /// <summary>
    /// 설정 인스턴스를 대신 넘겨준다. null 을 돌리면 아래 파일 로더가 처리한다.
    /// (관리자가 아직 Awake 되지 않았을 수 있으므로 null 을 돌려도 안전하게 동작해야 한다.)
    /// </summary>
    public static Func<ArUcoJson> Loader;

    /// <summary>저장을 대신 처리한다. 지정하지 않으면 파일에 직접 쓴다.</summary>
    public static Action<ArUcoJson> Saver;

    public static string DefaultPath => Path.Combine(Application.streamingAssetsPath, "aruco.json");

    public static ArUcoJson Load(string path)
    {
        var shared = Loader?.Invoke();
        if (shared != null) return shared;

        return LoadFromFile(path);
    }

    public static void Save(ArUcoJson config, string path)
    {
        if (Saver != null)
        {
            Saver(config);
            return;
        }

        SaveToFile(config, path);
    }

    /// <summary>
    /// 파일에서 직접 읽는다. 파일이 없으면 기본값으로 새로 만든다.
    /// FromJsonOverwrite 라서 json 에 없는 항목은 기본값이 그대로 남는다.
    /// (구버전 aruco.json 에 perspectiveMapping 이 없어도 true 가 유지된다.)
    /// </summary>
    public static ArUcoJson LoadFromFile(string path)
    {
        var config = new ArUcoJson();

        if (!File.Exists(path))
        {
            Debug.LogWarning($"[ArUco] 설정 파일이 없어 기본값으로 새로 만듭니다: {path}");
            SaveToFile(config, path);
            return config;
        }

        try
        {
            JsonUtility.FromJsonOverwrite(File.ReadAllText(path), config);
        }
        catch (Exception e)
        {
            // 현장에서 메모장으로 고치다 깨뜨리는 일이 있다. 기본값으로 계속 돌아가게 한다.
            Debug.LogError($"[ArUco] 설정 파일을 읽지 못해 기본값으로 진행합니다: {path}\n{e.Message}");
        }

        return config;
    }

    public static void SaveToFile(ArUcoJson config, string path)
    {
        if (config == null) return;

        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonUtility.ToJson(config, true));
        }
        catch (Exception e)
        {
            Debug.LogError($"[ArUco] 설정 파일을 쓰지 못했습니다: {path}\n{e.Message}");
        }
    }
}
