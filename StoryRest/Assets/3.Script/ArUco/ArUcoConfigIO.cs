using System;
using System.IO;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// aruco.json 을 읽고 쓴다.
    ///
    /// 설치 현장에서 맞춘 배치값이 여기에만 남으므로, 쓰다가 손상되면 설치를 처음부터 다시 해야 한다.
    /// 그래서 저장은 임시 파일에 쓴 뒤 바꿔치기하고, 직전 내용을 .bak 으로 남긴다.
    /// (전시장 PC 는 예고 없이 꺼진다 — 쓰기 도중 전원이 끊겨도 원본이 반쯤 지워지지 않아야 한다.)
    /// </summary>
    public static class ArUcoConfigIO
    {
        public const string FileName = "aruco.json";

        public static string Path => System.IO.Path.Combine(Application.streamingAssetsPath, FileName);

        static string TempPath => Path + ".tmp";
        static string BackupPath => Path + ".bak";

        /// <summary>
        /// 파일을 읽는다. 없으면 기본값으로 새로 만든다.
        /// 내용이 깨져 있으면 기본값으로 진행하되, 원본은 .broken 으로 남겨 복구할 수 있게 한다.
        /// </summary>
        public static ArUcoConfig Load()
        {
            var config = new ArUcoConfig();
            string path = Path;

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[ArUco] 설정 파일이 없어 기본값으로 새로 만듭니다: {path}");
                Save(config);
                return config;
            }

            try
            {
                // FromJsonOverwrite 라서 json 에 없는 항목은 기본값이 그대로 남는다.
                // 설정 항목을 나중에 추가해도 기존 파일을 손볼 필요가 없다.
                JsonUtility.FromJsonOverwrite(File.ReadAllText(path), config);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ArUco] 설정 파일을 읽지 못했습니다. 기본값으로 진행합니다: {path}\n{e.Message}");
                PreserveBroken(path);
                return new ArUcoConfig();
            }

            return config;
        }

        /// <summary>
        /// 임시 파일에 쓴 뒤 원본과 바꿔치기한다. 쓰기가 중간에 끊겨도 원본은 온전히 남는다.
        /// </summary>
        public static bool Save(ArUcoConfig config)
        {
            if (config == null) return false;

            string path = Path;

            try
            {
                string directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(TempPath, JsonUtility.ToJson(config, true));

                if (File.Exists(path))
                {
                    // 직전 내용을 .bak 으로 밀어내면서 교체한다.
                    File.Replace(TempPath, path, BackupPath);
                }
                else
                {
                    File.Move(TempPath, path);
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ArUco] 설정 파일을 쓰지 못했습니다: {path}\n{e.Message}");
                TryDeleteTemp();
                return false;
            }
        }

        // 깨진 파일을 덮어쓰지 않고 옆에 남겨 둔다. 손으로 고친 값을 되살릴 여지를 남기기 위함이다.
        static void PreserveBroken(string path)
        {
            try
            {
                string broken = path + ".broken";
                if (File.Exists(broken)) File.Delete(broken);
                File.Copy(path, broken);
                Debug.LogWarning($"[ArUco] 읽지 못한 원본을 남겨 두었습니다: {broken}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ArUco] 손상된 설정 파일을 보관하지 못했습니다: {e.Message}");
            }
        }

        static void TryDeleteTemp()
        {
            try
            {
                if (File.Exists(TempPath)) File.Delete(TempPath);
            }
            catch
            {
                // 정리 실패는 다음 저장에서 덮어쓰면 되므로 흘려보낸다.
            }
        }
    }
}
