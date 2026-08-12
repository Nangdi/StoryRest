using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StoryRest.Keyword
{
    /// <summary>키워드 하나가 움직이는 방식.</summary>
    public enum KeywordMotion
    {
        Drift,      // 직선으로 흐르다 화면 가장자리에서 튕긴다
        Wave,       // 가로로 흐르면서 위아래로 물결친다
        Orbit,      // 한 점을 중심으로 타원을 그린다
        Bob,        // 제자리에서 천천히 흔들린다
        Zigzag,     // 대각선으로 가다 꺾인다
    }

    /// <summary>
    /// StreamingAssets/keywordwall.json — 벽면 키워드 화면의 동작 설정.
    ///
    /// 관심사가 다르므로 aruco.json 과 파일을 나눈다(프로젝트가 port/tcp/Setting 을 나눠 쓰는 방식과 같다).
    /// </summary>
    /// <summary>키워드 월 화면 하나. 프로젝터 한 대에 대응한다.</summary>
    [Serializable]
    public class WallScreen
    {
        public int displayIndex = 1;

        // 이 화면을 쓰는 층. 비워 두면 모든 층에서 쓴다.
        // 층마다 프로젝터 수가 달라 ArUco 세트가 차지하는 번호가 밀리므로 층별로 지정한다.
        public int[] floors = new int[0];

        public bool IsUsedOnFloor(int floor)
        {
            if (floors == null || floors.Length == 0) return true;
            return Array.IndexOf(floors, floor) >= 0;
        }
    }

    [Serializable]
    public class KeywordWallConfig
    {
        public const string FileName = "keywordwall.json";

        public bool enabled = true;

        // 설치할 수 있는 화면을 전부 적어 두고 floors 로 층을 고른다(→ ArUcoConfig.sets 와 같은 방식).
        //
        //   1층 — 프로젝터 3대: ArUco 0번      + 키워드 월 1, 2번
        //   2·3층 — 프로젝터 4대: ArUco 0, 1번 + 키워드 월 2, 3번
        public List<WallScreen> walls = new List<WallScreen>
        {
            new WallScreen { displayIndex = 1, floors = new[] { 1 } },
            new WallScreen { displayIndex = 2, floors = new[] { 1, 2, 3 } },
            new WallScreen { displayIndex = 3, floors = new[] { 2, 3 } },
        };

        // 한 화면에 동시에 떠 있는 키워드 수.
        public int maxOnScreen = 16;

        // 글자 크기 범위(픽셀). 크기를 섞으면 깊이감이 생긴다.
        public float minFontSize = 44f;
        public float maxFontSize = 132f;

        // 이동 속도 범위. 화면 짧은 변을 1 로 보는 초당 이동량이라 해상도가 달라도 같게 보인다.
        public float minSpeed = 0.012f;
        public float maxSpeed = 0.055f;

        // 투명도 범위. 옅은 것과 진한 것이 섞여야 배경처럼 깔린다.
        public float minAlpha = 0.30f;
        public float maxAlpha = 0.95f;

        // 한 키워드가 머무는 시간(초). 지나면 사라지고 다른 낱말이 등장한다.
        public float minLifetime = 14f;
        public float maxLifetime = 30f;

        // 나타나고 사라지는 데 걸리는 시간. 갑자기 튀어나오지 않게 한다.
        public float fadeSeconds = 1.8f;

        // 쓸 움직임 패턴. 비워 두면 전부 사용한다.
        public string[] motions = { "Drift", "Wave", "Orbit", "Bob", "Zigzag" };

        // 글자 색. 현장에서 고치기 쉽도록 16진수 문자열로 둔다.
        public string[] colors = { "#FFFFFF", "#8FE3FF", "#B9C7FF", "#9FFFE0", "#FFE9A8" };

        public Color backgroundColor = Color.black;

        // 같은 낱말이 화면에 두 개 뜨지 않게 한다. 키워드가 적을 때는 자동으로 완화된다.
        public bool avoidDuplicates = true;

        // 글자끼리 겹쳤을 때 서로 밀어내는 세기(초당 정규화 이동량).
        // 0 이면 밀어내지 않는다. 너무 크면 낱말이 튕기듯 움직여 부자연스럽다.
        public float separation = 0.5f;

        // 밀어낼 때 확보할 여백. 글자 상자 크기에 이만큼 곱한 만큼 거리를 둔다.
        public float separationPadding = 1.15f;

        // 화면 가장자리에서 띄울 여백(화면 비율). 프로젝터 렌즈 왜곡이 크거나
        // 투사면 모서리가 벽에 걸리는 자리에서는 값을 올린다.
        public float edgeMargin = 0.03f;

        public static string Path => System.IO.Path.Combine(Application.streamingAssetsPath, FileName);

        public static KeywordWallConfig Load()
        {
            var config = new KeywordWallConfig();
            string path = Path;

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[Keyword] 설정 파일이 없어 기본값으로 새로 만듭니다: {path}");
                Save(config);
                return config;
            }

            try
            {
                JsonUtility.FromJsonOverwrite(File.ReadAllText(path), config);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Keyword] 설정 파일을 읽지 못해 기본값으로 진행합니다: {path}\n{e.Message}");
            }

            config.Sanitize();
            return config;
        }

        public static void Save(KeywordWallConfig config)
        {
            try
            {
                File.WriteAllText(Path, JsonUtility.ToJson(config, true));
            }
            catch (Exception e)
            {
                Debug.LogError($"[Keyword] 설정 파일을 쓰지 못했습니다: {Path}\n{e.Message}");
            }
        }

        /// <summary>이 층에서 실제로 띄우는 화면만 골라 준다.</summary>
        public List<WallScreen> WallsForFloor(int floor)
        {
            var result = new List<WallScreen>();
            if (!enabled || walls == null) return result;

            foreach (var wall in walls)
            {
                if (wall != null && wall.IsUsedOnFloor(floor)) result.Add(wall);
            }
            return result;
        }

        /// <summary>손으로 고치다 뒤집힌 값(최소 &gt; 최대 등)을 바로잡는다.</summary>
        public void Sanitize()
        {
            maxOnScreen = Mathf.Clamp(maxOnScreen, 1, 200);

            if (minFontSize > maxFontSize) Swap(ref minFontSize, ref maxFontSize);
            if (minSpeed > maxSpeed) Swap(ref minSpeed, ref maxSpeed);
            if (minAlpha > maxAlpha) Swap(ref minAlpha, ref maxAlpha);
            if (minLifetime > maxLifetime) Swap(ref minLifetime, ref maxLifetime);

            minFontSize = Mathf.Max(8f, minFontSize);
            minAlpha = Mathf.Clamp01(minAlpha);
            maxAlpha = Mathf.Clamp01(maxAlpha);
            minLifetime = Mathf.Max(1f, minLifetime);
            fadeSeconds = Mathf.Clamp(fadeSeconds, 0f, minLifetime * 0.5f);
            separation = Mathf.Max(0f, separation);
            separationPadding = Mathf.Clamp(separationPadding, 1f, 3f);
            edgeMargin = Mathf.Clamp(edgeMargin, 0f, 0.2f);
        }

        static void Swap(ref float a, ref float b)
        {
            float t = a; a = b; b = t;
        }

        /// <summary>설정에 적힌 이름을 실제 패턴 목록으로 바꾼다. 모르는 이름은 건너뛴다.</summary>
        public List<KeywordMotion> ResolveMotions()
        {
            var result = new List<KeywordMotion>();

            if (motions != null)
            {
                foreach (string name in motions)
                {
                    if (Enum.TryParse(name, true, out KeywordMotion motion) && !result.Contains(motion))
                        result.Add(motion);
                }
            }

            // 하나도 못 알아들었으면 전부 쓴다. 화면이 멈춰 있는 것보다 낫다.
            if (result.Count == 0)
            {
                foreach (KeywordMotion motion in Enum.GetValues(typeof(KeywordMotion))) result.Add(motion);
            }

            return result;
        }

        public List<Color> ResolveColors()
        {
            var result = new List<Color>();

            if (colors != null)
            {
                foreach (string hex in colors)
                {
                    if (!string.IsNullOrWhiteSpace(hex) && ColorUtility.TryParseHtmlString(hex.Trim(), out Color color))
                        result.Add(color);
                }
            }

            if (result.Count == 0) result.Add(Color.white);
            return result;
        }
    }

    /// <summary>
    /// floor_&lt;N&gt;/keywords.txt 에서 낱말을 읽는다. 한 줄에 하나, `#` 로 시작하는 줄은 주석.
    ///
    /// 콘텐츠와 같은 층 폴더에 두어 층마다 다른 낱말을 쓸 수 있게 한다.
    /// 메모장으로 고칠 수 있어야 하므로 json 이 아니라 평문이다.
    /// </summary>
    public static class KeywordList
    {
        public const string FileName = "keywords.txt";

        public static List<string> Load(string contentRoot)
        {
            var words = new List<string>();

            if (string.IsNullOrEmpty(contentRoot)) return words;

            string path = System.IO.Path.Combine(contentRoot, FileName);

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[Keyword] 낱말 파일이 없어 예시로 새로 만듭니다: {path}");
                CreateSample(path);
            }

            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    words.Add(line);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Keyword] 낱말 파일을 읽지 못했습니다: {path}\n{e.Message}");
            }

            Debug.Log($"[Keyword] 낱말 {words.Count}개를 읽었습니다: {path}");
            return words;
        }

        static void CreateSample(string path)
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(path,
                    "# 벽면에 떠다닐 낱말을 한 줄에 하나씩 적습니다.\n" +
                    "# 이 줄처럼 #으로 시작하면 주석입니다.\n" +
                    "# 파일을 고친 뒤에는 프로그램을 다시 시작하세요.\n" +
                    "\n" +
                    "중력\n원소\n분자\n결정\n파장\n스펙트럼\n대류\n마찰\n관성\n" +
                    "광합성\n세포\n유전자\n진화\n생태계\n" +
                    "지층\n화산\n대기\n해류\n" +
                    "은하\n항성\n중성자\n블랙홀\n");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Keyword] 예시 낱말 파일을 만들지 못했습니다: {e.Message}");
            }
        }
    }
}
