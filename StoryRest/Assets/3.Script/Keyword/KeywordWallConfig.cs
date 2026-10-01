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

        // 이 화면에 "지금 누군가가 궁금해한 질문" 을 띄울 ArUco 세트 이름. 관람객이 마주 보는 벽에 뜨게 정한다(→ WALL_CONTENT §5).
        // 첫 번째 월(오른쪽 벽) = 세트 A(왼쪽 전시대), 두 번째 월(왼쪽 벽) = 세트 B. 배선이 반대면 이 값만 바꾼다.
        public string[] liveSets = new string[0];

        // 이 화면에만 뜨는 순위 분류. "week" = 이번 주 호기심 1위, "month" = 이번 달 호기심 1위, 비우면 없음.
        public string rank = "";

        public bool ShowsLiveFrom(string setName)
        {
            if (liveSets == null || string.IsNullOrEmpty(setName)) return false;
            foreach (string name in liveSets)
                if (string.Equals(name?.Trim(), setName.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

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

    /// <summary>분류 하나와 그 분류 문구 그림들. 그림이 둘 이상이면 뜰 때마다 무작위로 하나를 붙인다.</summary>
    [Serializable]
    public class QuestionTitleSet
    {
        // week · month · live · least · random (→ WallQuestionDirector)
        public string kind = "random";

        // StreamingAssets/<titleFolder>/ 안의 파일 이름.
        public string[] files = new string[0];
    }

    /// <summary>
    /// 질문 · 해시태그 벽의 동작(→ WALL_CONTENT §5). 규칙의 숫자들을 현장에서 바꿀 수 있게 값으로 둔다.
    /// </summary>
    [Serializable]
    public class QuestionWallConfig
    {
        // 한 칸의 질문이 머무는 시간(초). 이 범위에서 칸마다 뽑는다.
        public float minRotateSeconds = 120f;
        public float maxRotateSeconds = 180f;

        // "지금 누군가가 궁금해한 질문" 이 머무는 시간(초). 다른 분류보다 짧다.
        public float liveSeconds = 30f;

        // 한 화면에 함께 떠다닐 해시태그 수.
        public int minTags = 3;
        public int maxTags = 4;

        // 관람 기록을 다시 세는 주기(초). 기록이 적힐 때도 다시 세지만, 자정을 넘길 때를 위해 주기도 둔다.
        public float refreshSeconds = 60f;

        // 크기(1920x1200 기준 픽셀). 질문 · 분류 문구는 그림 원본 크기에 배율을 곱하고, 해시태그는 높이를 이 범위에서 뽑는다.
        public float questionScale = 0.9f;
        public float titleScale = 1f;
        public float titleGap = 10f;
        public float minTagHeight = 56f;
        public float maxTagHeight = 72f;

        // 질문은 읽혀야 하므로 느리게, 튀지 않는 움직임만 쓴다.
        public float minQuestionSpeed = 0.004f;
        public float maxQuestionSpeed = 0.012f;
        public string[] questionMotions = { "Drift", "Bob" };

        // 분류 문구 그림이 든 폴더(StreamingAssets 아래)와 분류별 파일.
        public string titleFolder = "wall_titles";
        public List<QuestionTitleSet> titles = new List<QuestionTitleSet>
        {
            new QuestionTitleSet { kind = "week",   files = new[] { "09_이번 주 호기심 1위.png" } },
            new QuestionTitleSet { kind = "month",  files = new[] { "08_이번 달 호기심 1위.png" } },
            new QuestionTitleSet { kind = "live",   files = new[] { "07_지금 누군가가 궁금해한 질문.png" } },
            new QuestionTitleSet { kind = "least",  files = new[] { "06_아직 만나보지 못한 질문.png", "05_이 질문의 첫 탐험가를 찾습니다.png" } },
            new QuestionTitleSet { kind = "random", files = new[]
            {
                "04_이 질문도 함께 만나보세요.png", "03_쉬는 동안 궁금증 하나.png",
                "02_잠깐, 그게 정말이야.png", "01_어디 한번 알아볼까요.png",
            } },
        };

        public string[] TitleFiles(string kind)
        {
            if (titles == null) return new string[0];
            foreach (var set in titles)
                if (set != null && string.Equals(set.kind, kind, StringComparison.OrdinalIgnoreCase))
                    return set.files ?? new string[0];
            return new string[0];
        }

        public void Sanitize()
        {
            if (minRotateSeconds > maxRotateSeconds) (minRotateSeconds, maxRotateSeconds) = (maxRotateSeconds, minRotateSeconds);
            minRotateSeconds = Mathf.Max(10f, minRotateSeconds);
            maxRotateSeconds = Mathf.Max(minRotateSeconds, maxRotateSeconds);
            liveSeconds = Mathf.Max(5f, liveSeconds);

            if (minTags > maxTags) (minTags, maxTags) = (maxTags, minTags);
            minTags = Mathf.Max(0, minTags);
            maxTags = Mathf.Clamp(maxTags, minTags, 12);

            refreshSeconds = Mathf.Max(5f, refreshSeconds);
            questionScale = Mathf.Clamp(questionScale, 0.2f, 3f);
            titleScale = Mathf.Clamp(titleScale, 0.2f, 3f);
            titleGap = Mathf.Max(0f, titleGap);

            if (minTagHeight > maxTagHeight) (minTagHeight, maxTagHeight) = (maxTagHeight, minTagHeight);
            minTagHeight = Mathf.Max(8f, minTagHeight);
            maxTagHeight = Mathf.Max(minTagHeight, maxTagHeight);

            if (minQuestionSpeed > maxQuestionSpeed) (minQuestionSpeed, maxQuestionSpeed) = (maxQuestionSpeed, minQuestionSpeed);
            minQuestionSpeed = Mathf.Max(0f, minQuestionSpeed);

            if (string.IsNullOrWhiteSpace(titleFolder)) titleFolder = "wall_titles";
            if (titles == null || titles.Count == 0) titles = new QuestionWallConfig().titles;
        }
    }

    /// <summary>
    /// StreamingAssets/keywordwall.json — 벽면 키워드 화면의 **동작** 설정.
    ///
    /// 관심사가 다르므로 aruco.json 과 파일을 나눈다(프로젝트가 port/tcp/Setting 을 나눠 쓰는 방식과 같다).
    ///
    /// 여기와 프리팹의 경계는 이렇다(→ ARCHITECTURE §3):
    ///   - **프리팹**(`Assets/9.Prefab/KeywordWall.prefab`) — 보이는 모양. 자리·크기·글꼴·색·배경.
    ///     에디터에서 보면서 잡는 값들이다.
    ///   - **이 파일** — 시간과 개수, 그리고 항목마다 다르게 뽑는 범위(크기 범위, 그라데이션 목록).
    ///     "화면마다 다르게 · 매번 다르게" 라서 프리팹의 값 하나로는 담을 수 없는 것들과,
    ///     현장에서 빌드를 다시 만들지 않고 바꿔야 하는 것들(층·디스플레이 배정, 교체 주기)이다.
    /// </summary>
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
        //
        // 층마다 첫 번째 월이 오른쪽 벽(세트 A 의 "지금 누군가" · 이번 달 1위), 두 번째가 왼쪽 벽(세트 B · 이번 주 1위)이다.
        public List<WallScreen> walls = new List<WallScreen>
        {
            new WallScreen { displayIndex = 1, floors = new[] { 1 },    roles = new[] { "all" },  liveSets = new[] { "A" }, rank = "month" },
            new WallScreen { displayIndex = 2, floors = new[] { 1 },    roles = new[] { "all" },  liveSets = new[] { "B" }, rank = "week" },
            new WallScreen { displayIndex = 2, floors = new[] { 2, 3 }, roles = new[] { "all" },  liveSets = new[] { "A" }, rank = "month" },
            new WallScreen { displayIndex = 3, floors = new[] { 2, 3 }, roles = new[] { "all" },  liveSets = new[] { "B" }, rank = "week" },
            new WallScreen { displayIndex = 0, floors = new[] { 2, 3 }, roles = new[] { "wall" }, liveSets = new[] { "A" }, rank = "month" },
            new WallScreen { displayIndex = 1, floors = new[] { 2, 3 }, roles = new[] { "wall" }, liveSets = new[] { "B" }, rank = "week" },
        };

        // 해시태그에 입힐 그라데이션 목록. 태그마다 이 중 하나를 무작위로 고른다. 비워 두면 흰색 그대로.
        // 그림에 이미 색이 있으면 곱해져 탁해지므로 흰 글씨 이미지에만 쓴다. 질문 · 분류 문구는 흰색 그대로 둔다.
        public List<SpriteGradient> spriteGradients = new List<SpriteGradient>();

        // 그라데이션 방향(도). 0 = 왼쪽→오른쪽, 90 = 위→아래.
        public float spriteGradientAngle = 0f;

        // 해시태그 이동 속도 범위. 화면 짧은 변을 1 로 보는 초당 이동량이라 해상도가 달라도 같게 보인다.
        public float minSpeed = 0.012f;
        public float maxSpeed = 0.055f;

        // 해시태그 투명도 범위. 질문 · 분류 문구는 늘 불투명하다.
        public float minAlpha = 0.55f;
        public float maxAlpha = 0.95f;

        // 나타나고 사라지는 데 걸리는 시간. 갑자기 튀어나오지 않게 한다.
        public float fadeSeconds = 1.8f;

        // 해시태그가 쓸 움직임 패턴. 비워 두면 전부 사용한다.
        public string[] motions = { "Drift", "Wave", "Orbit", "Bob", "Zigzag" };

        // 글자끼리 겹쳤을 때 서로 밀어내는 세기(초당 정규화 이동량).
        // 0 이면 밀어내지 않는다. 너무 크면 낱말이 튕기듯 움직여 부자연스럽다.
        public float separation = 0.5f;

        // 밀어낼 때 확보할 여백. 글자 상자 크기에 이만큼 곱한 만큼 거리를 둔다.
        public float separationPadding = 1.15f;

        // 화면 가장자리에서 띄울 여백(화면 비율). 프로젝터 렌즈 왜곡이 크거나
        // 투사면 모서리가 벽에 걸리는 자리에서는 값을 올린다.
        public float edgeMargin = 0.03f;

        // 질문 · 해시태그 벽(→ WALL_CONTENT §5).
        public QuestionWallConfig questions = new QuestionWallConfig();

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
            if (minSpeed > maxSpeed) Swap(ref minSpeed, ref maxSpeed);
            if (minAlpha > maxAlpha) Swap(ref minAlpha, ref maxAlpha);

            minAlpha = Mathf.Clamp01(minAlpha);
            maxAlpha = Mathf.Clamp01(maxAlpha);
            fadeSeconds = Mathf.Clamp(fadeSeconds, 0f, 5f);
            separation = Mathf.Max(0f, separation);
            separationPadding = Mathf.Clamp(separationPadding, 1f, 3f);
            edgeMargin = Mathf.Clamp(edgeMargin, 0f, 0.2f);

            if (questions == null) questions = new QuestionWallConfig();
            questions.Sanitize();
        }

        static void Swap(ref float a, ref float b)
        {
            float t = a; a = b; b = t;
        }

        /// <summary>설정에 적힌 이름을 실제 패턴 목록으로 바꾼다. 모르는 이름은 건너뛴다.</summary>
        public List<KeywordMotion> ResolveMotions() => ResolveMotions(motions);

        public static List<KeywordMotion> ResolveMotions(string[] names)
        {
            var result = new List<KeywordMotion>();

            if (names != null)
            {
                foreach (string name in names)
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
    }
}
