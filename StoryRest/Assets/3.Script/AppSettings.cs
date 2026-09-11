using System;
using System.IO;
using UnityEngine;

namespace StoryRest
{
    /// <summary>
    /// 이 PC 가 맡는 일. 빌드는 하나고, 어느 역할로 뜰지는 Setting.json 의 role 이 정한다(→ SPEC §2).
    ///
    /// 그래픽카드 출력이 3개뿐이라 2·3층(프로젝터 4대)은 PC 를 둘로 나눈다.
    /// 나누는 기준은 "하는 일" 이다 — 카메라·마커·영상은 한 PC, 키워드 월은 다른 PC.
    /// </summary>
    public enum AppRole
    {
        /// <summary>PC 1대가 전부 맡는다(1층). 통신 없음.</summary>
        All,

        /// <summary>카메라·마커·영상만. 관람 기록을 월 PC 로 보내는 서버가 된다(2·3층).</summary>
        ArUco,

        /// <summary>키워드 월만. ArUco PC 에 붙어 관람 기록을 받아 온다(2·3층).</summary>
        Wall,
    }

    /// <summary>
    /// Setting.json 에서 읽는 앱 전역 설정.
    ///
    /// 빌드는 1~3층 공통으로 하나만 유지하고, 어느 층인지 · 어느 역할인지는 이 파일이 결정한다.
    /// 현장 설치 시 이 파일의 값만 바꾸면 그 층 · 그 역할로 뜬다.
    ///
    /// 실제로 읽는 파일은 persistentDataPath 에 있다. 층 번호는 PC 마다 다르게 맞추는 값이라
    /// StreamingAssets 에 두면 빌드를 새로 넣을 때마다 다시 고쳐야 한다(→ ArUcoConfigIO 와 같은 이유).
    /// StreamingAssets/Setting.json 은 첫 실행 때 한 번 복사해 오는 기본값 원본이다.
    ///
    /// 같은 파일을 JsonManager 도 읽는다. FromJsonOverwrite 는 자기 필드만 채우고
    /// 모르는 항목은 건드리지 않으므로 서로 간섭하지 않는다.
    /// </summary>
    [Serializable]
    public class AppSettings
    {
        public const string FileName = "Setting.json";

        // JSON 에는 주석이 없다. 설치자가 파일을 열었을 때 무엇을 고칠지 바로 알 수 있게 설명을 값으로 싣는다.
        // 필드라서 Save() 때도 같이 써지므로 설정창에서 층을 바꿔 저장해도 사라지지 않는다.
        // 읽을 때는 무시된다(값을 바꿔도 아무 일도 없다). 맨 위에 두어 파일을 열면 먼저 보이게 한다.
        public string[] help =
        {
            "이 파일은 이 PC 의 층과 역할을 정한다. 값을 바꾸면 프로그램을 다시 시작해야 적용된다.",
            "floor: 1~3. 이 PC 가 설치된 층. StreamingAssets/floor_<N>/ 의 콘텐츠를 쓴다.",
            "role: all / aruco / wall.",
            "  all   = PC 1대가 카메라·마커·영상 + 키워드 월을 다 맡는다 (1층). 통신 없음.",
            "  aruco = 카메라·마커·영상만. 관람 기록을 statsPort 로 월 PC 에 보낸다 (2·3층 ArUco PC).",
            "  wall  = 키워드 월만. statsHost:statsPort 의 ArUco PC 에 붙어 관람 기록을 받는다 (2·3층 월 PC).",
            "statsHost: wall 일 때 같은 층 ArUco PC 의 IP. all 은 쓰지 않는다.",
            "statsPort: aruco 가 여는 포트 = wall 이 붙는 포트. 양쪽 같아야 하고 ArUco PC 방화벽에서 열어 둔다.",
            "카메라 수는 여기가 아니라 aruco.json 의 sets[].floors 가 정한다. 이 층 번호가 든 세트만 만들어진다.",
        };

        // 이 PC 가 설치될 층. StreamingAssets/floor_<N>/ 폴더를 고르는 데 쓴다.
        public int floor = 1;

        // 이 PC 의 역할 — "all" / "aruco" / "wall". 대소문자는 가리지 않는다.
        // enum 을 바로 직렬화하면 숫자로 저장돼 설치자가 알아볼 수 없으므로 글자로 둔다.
        public string role = "all";

        // 관람 기록 링크(→ ViewStatsServer / ViewStatsClient).
        // aruco 역할은 statsPort 로 대기하고, wall 역할은 statsHost:statsPort 의 ArUco PC 에 붙는다.
        // all 역할은 쓰지 않는다 — 같은 프로세스 안에서 파일을 바로 읽는다.
        public string statsHost = "127.0.0.1";
        public int statsPort = 5100;

        /// <summary>role 문자열을 해석한 값. 모르는 글자면 All 로 보고 경고를 남긴다.</summary>
        public AppRole Role
        {
            get
            {
                if (TryParseRole(role, out var parsed)) return parsed;
                return AppRole.All;
            }
        }

        /// <summary>role 값이 알아볼 수 있는 글자인지. 시작할 때 문제 목록에 올리는 용도다.</summary>
        public bool IsRoleValid => TryParseRole(role, out _);

        public static bool TryParseRole(string text, out AppRole result)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "all":   result = AppRole.All;   return true;
                case "aruco": result = AppRole.ArUco; return true;
                case "wall":  result = AppRole.Wall;  return true;
                default:      result = AppRole.All;   return false;
            }
        }

        public static string RoleName(AppRole role)
        {
            switch (role)
            {
                case AppRole.ArUco: return "aruco";
                case AppRole.Wall:  return "wall";
                default:            return "all";
            }
        }

        /// <summary>카메라 · 마커 검출 · 영상 투사를 이 PC 가 맡는가.</summary>
        public static bool HasArUco(AppRole role) => role != AppRole.Wall;

        /// <summary>키워드 월을 이 PC 가 띄우는가.</summary>
        public static bool HasWall(AppRole role) => role != AppRole.ArUco;

        /// <summary>실제로 읽는 파일. 빌드를 새로 넣어도 남는다.</summary>
        public static string Path => System.IO.Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>빌드에 실려 오는 기본값 원본. Path 에 파일이 없을 때 한 번 복사해 온다.</summary>
        public static string DefaultPath => System.IO.Path.Combine(Application.streamingAssetsPath, FileName);

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

            if (!File.Exists(path) && !TrySeedFromDefault(path))
            {
                Debug.LogWarning($"[App] {FileName} 이 없어 기본값(floor={settings.floor})으로 새로 만듭니다: {path}");
                TryWriteDefault(path, settings);
                return settings;
            }

            try
            {
                JsonUtility.FromJsonOverwrite(StripLineComments(File.ReadAllText(path)), settings);
            }
            catch (Exception e)
            {
                Debug.LogError($"[App] {FileName} 을 읽지 못해 기본값(floor={settings.floor})으로 진행합니다.\n{e.Message}");
            }

            return settings;
        }

        /// <summary>
        /// "//" 로 시작하는 줄을 걷어낸다. JSON 은 주석을 허용하지 않아 설치자가 메모를 적으면 파일 전체가 안 읽히는데,
        /// 그러면 기본값(1층 · all)으로 조용히 떠서 원인을 찾기 어렵다. 줄 단위 주석 정도는 받아 준다.
        /// 단, 설정창에서 저장하면 다시 쓰이므로 주석은 사라진다 — 남겨야 할 설명은 help 에 있다.
        /// </summary>
        static string StripLineComments(string json)
        {
            if (json.IndexOf("//", StringComparison.Ordinal) < 0) return json;

            var lines = json.Split('\n');
            var kept = new System.Text.StringBuilder(json.Length);

            foreach (string line in lines)
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                kept.Append(line).Append('\n');
            }

            return kept.ToString();
        }

        // 첫 실행 — 빌드에 실려 온 기본값을 persistentDataPath 로 복사한다. 이후로는 복사본만 읽는다.
        static bool TrySeedFromDefault(string path)
        {
            string source = DefaultPath;
            if (!File.Exists(source)) return false;

            try
            {
                EnsureDirectory(path);
                File.Copy(source, path);
                Debug.Log($"[App] {FileName} 이 없어 기본값 원본을 복사했습니다: {source} → {path}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[App] 기본값 원본을 복사하지 못했습니다: {source}\n{e.Message}");
                return false;
            }
        }

        /// <summary>설정 패널에서 층을 바꾼 뒤 호출한다. 층은 시작할 때만 읽으므로 다음 실행부터 적용된다.</summary>
        public bool Save()
        {
            try
            {
                EnsureDirectory(Path);
                File.WriteAllText(Path, JsonUtility.ToJson(this, true));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[App] {FileName} 을 쓰지 못했습니다: {Path}\n{e.Message}");
                return false;
            }
        }

        // 원본도 없을 때 — 설치자가 열어서 고칠 파일은 있어야 하므로 기본값으로라도 만들어 둔다.
        static void TryWriteDefault(string path, AppSettings settings)
        {
            if (!settings.Save())
                Debug.LogWarning($"[App] {FileName} 을 만들지 못했습니다: {path}");
        }

        static void EnsureDirectory(string path)
        {
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }
    }
}
