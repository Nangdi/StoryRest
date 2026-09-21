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

        // 이 화면을 쓰는 역할("all" / "wall"). 비워 두면 역할을 가리지 않는다.
        // 월 PC(wall)에는 ArUco 세트가 없어 월이 0번 디스플레이부터 시작하고,
        // 한 PC 가 다 맡는 all 에서는 세트 뒤 번호로 밀린다. 같은 층이라도 역할에 따라 번호가 다르다.
        public string[] roles = new string[0];

        public bool IsUsedOn(int floor, AppRole role)
        {
            if (floors != null && floors.Length > 0 && Array.IndexOf(floors, floor) < 0) return false;
            if (roles == null || roles.Length == 0) return true;

            foreach (string name in roles)
            {
                if (AppSettings.TryParseRole(name, out var parsed) && parsed == role) return true;
            }
            return false;
        }
    }

    /// <summary>흰 글씨 스프라이트에 입힐 두 색 그라데이션. from → to.</summary>
    [Serializable]
    public class SpriteGradient
    {
        public string from = "#FFFFFF";
        public string to = "#FFFFFF";

        public bool TryResolve(out Color a, out Color b)
        {
            a = b = Color.white;
            return !string.IsNullOrWhiteSpace(from) && ColorUtility.TryParseHtmlString(from.Trim(), out a)
                && !string.IsNullOrWhiteSpace(to) && ColorUtility.TryParseHtmlString(to.Trim(), out b);
        }
    }

    /// <summary>
    /// 월 왼쪽 위의 주제 카드(→ SPEC §3.1). "오늘의 추천 · 오늘 / 이번 주 / 이달의 인기 주제" 가 한 장씩 돌아가며 뜬다.
    /// 카드는 제목과 그림뿐이다. 이름과 시간은 현장에서 바꿀 수 있게 값으로 둔다.
    /// </summary>
    [Serializable]
    public class SpotlightConfig
    {
        public bool enabled = true;

        // 카드가 뜨는 간격(초). 한 장이 뜨고 나서 다음 장이 뜰 때까지. 상시 떠 있으면 배경이 아니라 광고판처럼 보여 띄엄띄엄 띄운다.
        public float intervalSeconds = 60f;

        // 한 장이 떠 있는 시간과 뜨고 질 때 페이드 시간(초). 둘을 합친 것이 간격보다 길면 쉬지 않고 이어진다.
        public float cardSeconds = 12f;
        public float fadeSeconds = 0.8f;

        // 제목 아래에 주제 이름(폴더 이름에서 번호를 뗀 것)을 한 줄 넣을지. 폴더에 제목이 없는 마커는 켜도 빈 줄이다.
        public bool showTopicName = false;

        // 관람 기록을 다시 세는 주기(초). 기록이 한 건 적힐 때마다도 다시 세지만, 자정을 넘길 때를 위해 주기도 둔다.
        public float refreshSeconds = 60f;

        // 카드 자리(1920x1200 기준 픽셀). 왼쪽 위에서 margin 만큼 띄우고, width 너비에 그림은 imageSize 정사각형 안에.
        public float margin = 56f;
        public float width = 560f;
        public float imageSize = 360f;

        // 카드 이름. 순서대로 추천 · 오늘 · 이번 주 · 이달.
        public string recommendLabel = "오늘의 추천";
        public string todayLabel = "오늘의 인기 주제";
        public string weekLabel = "이번 주 인기 주제";
        public string monthLabel = "이달의 인기 주제";

        // 카드 이름 글자색. 주제 이름은 흰색 고정이다.
        public string accentColor = "#8FE3FF";

        public Color ResolveAccentColor()
        {
            return !string.IsNullOrWhiteSpace(accentColor) && ColorUtility.TryParseHtmlString(accentColor.Trim(), out Color color)
                ? color
                : Color.white;
        }

        public void Sanitize()
        {
            intervalSeconds = Mathf.Max(0f, intervalSeconds);
            cardSeconds = Mathf.Max(2f, cardSeconds);
            fadeSeconds = Mathf.Clamp(fadeSeconds, 0f, cardSeconds * 0.5f);
            refreshSeconds = Mathf.Max(5f, refreshSeconds);
            margin = Mathf.Max(0f, margin);
            width = Mathf.Clamp(width, 200f, 1200f);
            imageSize = Mathf.Clamp(imageSize, 32f, 800f);
        }
    }

    [Serializable]
    public class KeywordWallConfig
    {
        public const string FileName = "keywordwall.json";

        public bool enabled = true;

        // 설치할 수 있는 화면을 전부 적어 두고 floors · roles 로 고른다(→ ArUcoConfig.sets 와 같은 방식).
        //
        //   1층 (all)    — PC 1대, 프로젝터 3대: ArUco 0번 + 키워드 월 1, 2번
        //   2·3층 (wall) — 월 PC, 프로젝터 2대: 키워드 월 0, 1번  (ArUco PC 는 월을 띄우지 않는다)
        //   2·3층 (all)  — 포트가 넉넉해 PC 1대로 돌릴 때: ArUco 0, 1번 + 키워드 월 2, 3번
        public List<WallScreen> walls = new List<WallScreen>
        {
            new WallScreen { displayIndex = 1, floors = new[] { 1 },       roles = new[] { "all" } },
            new WallScreen { displayIndex = 2, floors = new[] { 1, 2, 3 }, roles = new[] { "all" } },
            new WallScreen { displayIndex = 3, floors = new[] { 2, 3 },    roles = new[] { "all" } },
            new WallScreen { displayIndex = 0, floors = new[] { 2, 3 },    roles = new[] { "wall" } },
            new WallScreen { displayIndex = 1, floors = new[] { 2, 3 },    roles = new[] { "wall" } },
        };

        // 한 화면에 동시에 떠 있는 키워드 수.
        public int maxOnScreen = 16;

        // 글자 크기 범위(1920x1200 기준 픽셀). 크기를 섞으면 깊이감이 생긴다.
        public float minFontSize = 44f;
        public float maxFontSize = 132f;

        // 스프라이트 크기 범위(긴 변, 1920x1200 기준 픽셀). 종횡비는 원본을 따른다.
        // floor_<N>/<마커ID>/sprite/ 에 그림이 있으면 낱말 대신 이것이 떠다닌다(→ SPEC §3.1).
        // 스프라이트가 대개 가로로 긴 글자 이미지(예: 694x165)라 긴 변 기준을 넉넉히 잡는다 — 360 이면 글자 높이가 85px 뿐이다.
        public float minSpriteSize = 360f;
        public float maxSpriteSize = 760f;

        // 흰 글씨 스프라이트에 입힐 그라데이션 목록. 낱말마다 이 중 하나를 무작위로 고른다. 비워 두면 원본 색 그대로.
        // 그림에 이미 색이 있으면 곱해져 탁해지므로 흰색 글씨 이미지에만 쓴다. 주제 카드의 그림에도 같이 입힌다.
        public List<SpriteGradient> spriteGradients = new List<SpriteGradient>();

        // 그라데이션 방향(도). 0 = 왼쪽→오른쪽, 90 = 위→아래.
        public float spriteGradientAngle = 0f;

        // 그림을 가진 마커 폴더 중 몇 개를 띄울지. 층에 폴더가 100개면 전부 띄우기엔 너무 많다.
        // 시작할 때 이 범위에서 개수를 하나 뽑고, 그 수만큼 폴더를 무작위로 골라 그 안의 그림만 띄운다.
        // 두 값 다 0 이면 전부 띄운다. 폴더 수가 모자라면 있는 만큼만.
        public int minSpriteFolders = 5;
        public int maxSpriteFolders = 10;

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

        // 왼쪽 위 주제 카드(추천 · 인기 주제).
        public SpotlightConfig spotlight = new SpotlightConfig();

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

        /// <summary>이 층 · 이 역할에서 실제로 띄우는 화면만 골라 준다. 월을 안 띄우는 역할이면 비어 있다.</summary>
        public List<WallScreen> WallsFor(int floor, AppRole role)
        {
            var result = new List<WallScreen>();
            if (!enabled || walls == null || !AppSettings.HasWall(role)) return result;

            foreach (var wall in walls)
            {
                if (wall != null && wall.IsUsedOn(floor, role)) result.Add(wall);
            }
            return result;
        }

        /// <summary>손으로 고치다 뒤집힌 값(최소 &gt; 최대 등)을 바로잡는다.</summary>
        public void Sanitize()
        {
            maxOnScreen = Mathf.Clamp(maxOnScreen, 1, 200);

            if (minFontSize > maxFontSize) Swap(ref minFontSize, ref maxFontSize);
            if (minSpriteSize > maxSpriteSize) Swap(ref minSpriteSize, ref maxSpriteSize);
            if (minSpriteFolders > maxSpriteFolders) Swap(ref minSpriteFolders, ref maxSpriteFolders);
            minSpriteFolders = Mathf.Max(0, minSpriteFolders);
            maxSpriteFolders = Mathf.Max(0, maxSpriteFolders);
            if (minSpeed > maxSpeed) Swap(ref minSpeed, ref maxSpeed);
            if (minAlpha > maxAlpha) Swap(ref minAlpha, ref maxAlpha);
            if (minLifetime > maxLifetime) Swap(ref minLifetime, ref maxLifetime);

            minFontSize = Mathf.Max(8f, minFontSize);
            minSpriteSize = Mathf.Max(16f, minSpriteSize);
            minAlpha = Mathf.Clamp01(minAlpha);
            maxAlpha = Mathf.Clamp01(maxAlpha);
            minLifetime = Mathf.Max(1f, minLifetime);
            fadeSeconds = Mathf.Clamp(fadeSeconds, 0f, minLifetime * 0.5f);
            separation = Mathf.Max(0f, separation);
            separationPadding = Mathf.Clamp(separationPadding, 1f, 3f);
            edgeMargin = Mathf.Clamp(edgeMargin, 0f, 0.2f);

            if (spotlight == null) spotlight = new SpotlightConfig();
            spotlight.Sanitize();
        }

        static void Swap(ref float a, ref float b)
        {
            float t = a; a = b; b = t;
        }

        static void Swap(ref int a, ref int b)
        {
            int t = a; a = b; b = t;
        }

        /// <summary>
        /// 그림을 가진 폴더 중 띄울 것을 무작위로 고른다. 결과는 그 폴더들의 그림을 한 목록으로 편 것이다.
        /// picked 에는 고른 폴더가 마커 ID 순으로 남는다(로그용).
        /// </summary>
        public List<string> PickSprites(List<ArUco.SpriteFolder> folders, List<ArUco.SpriteFolder> picked)
        {
            picked.Clear();
            if (folders == null || folders.Count == 0) return new List<string>();

            // 0/0 이면 전부. 아니면 범위에서 개수를 뽑되 있는 폴더 수를 넘지 않는다.
            int count = maxSpriteFolders <= 0
                ? folders.Count
                : Mathf.Min(folders.Count, UnityEngine.Random.Range(Mathf.Max(1, minSpriteFolders), maxSpriteFolders + 1));

            // 앞에서 count 개만 섞는다(부분 Fisher-Yates). 뽑히지 않은 나머지는 순서가 상관없다.
            var pool = new List<ArUco.SpriteFolder>(folders);
            for (int i = 0; i < count; i++)
            {
                int j = UnityEngine.Random.Range(i, pool.Count);
                (pool[i], pool[j]) = (pool[j], pool[i]);
                picked.Add(pool[i]);
            }

            // 화면에 도는 순서는 마커 ID 순으로 되돌린다. 무작위는 "어느 폴더냐" 까지만이다.
            picked.Sort((a, b) => a.markerId.CompareTo(b.markerId));

            var sprites = new List<string>();
            foreach (var folder in picked) sprites.AddRange(folder.files);
            return sprites;
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

        /// <summary>설정에 적힌 그라데이션 중 읽을 수 있는 것만. 비어 있으면 그라데이션을 쓰지 않는다.</summary>
        public List<(Color from, Color to)> ResolveSpriteGradients()
        {
            var result = new List<(Color, Color)>();
            if (spriteGradients == null) return result;

            foreach (var gradient in spriteGradients)
            {
                if (gradient != null && gradient.TryResolve(out var a, out var b)) result.Add((a, b));
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
