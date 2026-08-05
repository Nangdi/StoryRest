using System;
using System.IO;
using UnityEngine;

namespace StoryRest
{
    /// <summary>
    /// StreamingAssets/Setting.json 에서 읽는 앱 전역 설정.
    ///
    /// 빌드는 1~3층 공통으로 하나만 유지하고, 어느 층인지는 이 파일이 결정한다.
    /// 현장 설치 시 숫자만 바꾸면 그 층의 콘텐츠를 쓰게 된다.
    ///
    /// 같은 파일을 JsonManager 도 읽는다. FromJsonOverwrite 는 자기 필드만 채우고
    /// 모르는 항목은 건드리지 않으므로 서로 간섭하지 않는다.
    /// </summary>
    [Serializable]
    public class AppSettings
    {
        public const string FileName = "Setting.json";

        // 이 PC 가 설치될 층. StreamingAssets/floor_<N>/ 폴더를 고르는 데 쓴다.
        public int floor = 1;

        public static string Path => System.IO.Path.Combine(Application.streamingAssetsPath, FileName);

        /// <summary>이 층의 콘텐츠 폴더. 마커 ID 별 하위 폴더가 들어 있다.</summary>
        public string ContentRoot => ContentRootFor(floor);

        public static string ContentRootFor(int floor)
        {
            return System.IO.Path.Combine(Application.streamingAssetsPath, $"floor_{floor}");
        }

        public static AppSettings Load()
        {
            var settings = new AppSettings();
            string path = Path;

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[App] {FileName} 이 없어 기본값(floor={settings.floor})으로 진행합니다: {path}");
                return settings;
            }

            try
            {
                JsonUtility.FromJsonOverwrite(File.ReadAllText(path), settings);
            }
            catch (Exception e)
            {
                Debug.LogError($"[App] {FileName} 을 읽지 못해 기본값(floor={settings.floor})으로 진행합니다.\n{e.Message}");
            }

            return settings;
        }
    }
}
