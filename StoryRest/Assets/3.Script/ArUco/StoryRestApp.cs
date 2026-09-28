using System.Collections.Generic;
using System.Text;
using StoryRest.Keyword;
using StoryRest.Stats;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 전시 시스템의 진입점. 설정을 읽고, 검증하고, 세트를 만든다.
    ///
    /// 씬에는 이 컴포넌트 하나만 두면 된다. 세트 수와 장비 배정은 전부 aruco.json 이 결정하므로
    /// 층마다 다른 씬이나 다른 빌드를 만들지 않는다.
    ///
    /// 이 PC 가 무엇을 맡는지는 Setting.json 의 role 이 정한다(→ AppRole).
    /// all 은 세트와 키워드 월을 다 띄우고, aruco 는 세트만 + 관람 기록 서버, wall 은 월만 + 관람 기록 클라이언트다.
    /// 역할별 분기는 여기서 끝난다 — 아래 컴포넌트들은 자기가 어느 역할로 떴는지 모른다.
    /// </summary>
    [DisallowMultipleComponent]
    public class StoryRestApp : MonoBehaviour
    {
        public static StoryRestApp Instance { get; private set; }

        public AppSettings Settings { get; private set; }

        /// <summary>
        /// 이번 실행이 실제로 쓰는 층. Settings.floor 는 설정 패널이 바꿀 수 있지만 층은 시작할 때만 읽으므로,
        /// "저장은 됐는데 아직 적용 전" 을 구분하려면 시작 시점의 값을 따로 들고 있어야 한다.
        /// </summary>
        public int RunningFloor { get; private set; }

        /// <summary>이번 실행의 역할. 층과 같은 이유로 시작 시점 값을 따로 든다.</summary>
        public AppRole RunningRole { get; private set; }

        public ArUcoConfig Config { get; private set; }
        public ArUcoContentIndex Content { get; private set; }

        /// <summary>이미지 텍스처 캐시. 읽기 전용이라 세트들이 함께 쓴다(→ ARCHITECTURE §5).</summary>
        public ArUcoImageCache Images { get; private set; }

        /// <summary>설정에 문제가 있으면 여기에 남는다. 편집모드 HUD 에서 보여줄 수 있다.</summary>
        public IReadOnlyList<string> Problems => _problems;

        /// <summary>만들어진 세트들. 개수는 aruco.json 의 sets 배열이 결정한다.</summary>
        public IReadOnlyList<ArUcoSet> Sets => _sets;

        public KeywordWallConfig KeywordConfig { get; private set; }

        /// <summary>이번 실행이 띄운 키워드 월들. 설정창이 값을 바꾼 뒤 ApplyConfig 를 돌리는 데 쓴다.</summary>
        public IReadOnlyList<KeywordWall> Walls => _walls;

        /// <summary>월의 그림 텍스처(떠다니는 스프라이트 + 주제 카드 그림). 월이 있을 때만 만들어지고 월 화면들이 함께 쓴다.</summary>
        public KeywordSpriteCache Sprites { get; private set; }

        /// <summary>월 왼쪽 위 주제 카드의 재료(추천 · 인기 순위). 월 화면들이 함께 본다.</summary>
        public KeywordSpotlight Spotlight { get; private set; }

        /// <summary>관람 기록 링크. aruco 역할이면 서버, wall 역할이면 클라이언트, all 이면 둘 다 null.</summary>
        public ViewStatsServer StatsServer { get; private set; }
        public ViewStatsClient StatsClient { get; private set; }

        [Header("씬에 미리 놓아 둔 화면들 (→ ARCHITECTURE §3)")]
        [Tooltip("씬에 미리 만들어 둔 ArUco 세트들(ArUcoSet.prefab 인스턴스). aruco.json 의 sets[] 중 이 층·역할 것을 " +
                 "순서대로 하나씩 맡기고, 남는 것은 끈다. 설정이 더 많이 요구하면 씬에 하나 더 놓아야 한다.")]
        [SerializeField] ArUcoSet[] _arUcoSets = new ArUcoSet[0];

        [Tooltip("씬에 미리 만들어 둔 키워드 월들(KeywordWall.prefab 인스턴스). keywordwall.json 의 walls[] 중 이 층·역할 것을 " +
                 "순서대로 하나씩 맡기고, 남는 것은 끈다. 자리·글꼴·색은 여기서 직접 잡는다.")]
        [SerializeField] KeywordWall[] _keywordWalls = new KeywordWall[0];

        [Tooltip("Assets/9.Prefab/StatsTrafficPanel.prefab — ESC 설정창 오른쪽에 서는 TCP 패널.")]
        [SerializeField] ViewStatsTrafficPanel _trafficPanelPrefab;

        readonly List<string> _problems = new List<string>();
        readonly List<ArUcoSet> _sets = new List<ArUcoSet>();
        readonly List<KeywordWall> _walls = new List<KeywordWall>();

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            Settings = AppSettings.Load();
            RunningFloor = Settings.floor;
            RunningRole = Settings.Role;

            if (!Settings.IsRoleValid)
                _problems.Add($"Setting.json 의 role '{Settings.role}' 을 모릅니다(all / aruco / wall). all 로 진행합니다.");

            Config = ArUcoConfigIO.Load();
            Content = new ArUcoContentIndex();
            Images = new ArUcoImageCache(this, Config.maxCachedImages);

            // 월 PC 에는 카메라도 콘텐츠도 없다. ArUco 쪽 검증을 돌리면 "세트가 없다" 같은 헛된 경고만 남는다.
            if (AppSettings.HasArUco(RunningRole))
            {
                Config.Validate(_problems, Settings.floor);
                ValidateCalibrationMarkers();
                Content.Rescan(Settings.ContentRoot);
                ValidateContentMarkers();
            }

            KeywordConfig = KeywordWallConfig.Load();
            ValidateDisplayAssignment();

            ActivateDisplays();
            LogSummary();
            CreateSets();
            CreateKeywordWalls();
            SleepUnusedScreens();
            CreateStatsLink();
            AttachSettingsPanel();
        }

        /// <summary>
        /// 씬에 미리 놓아 둔 화면 중 이번 실행이 맡기지 않은 것을 끈다. 켜 둔 채 두면 카메라가 0번 디스플레이에
        /// 검은 화면을 덮어 그린다. 월 PC(wall)에서는 세트 전부, 세트가 하나뿐인 층에서는 두 번째 세트가 여기서 꺼진다.
        /// </summary>
        void SleepUnusedScreens()
        {
            foreach (var set in _arUcoSets)
                if (set != null && !_sets.Contains(set)) set.gameObject.SetActive(false);

            foreach (var wall in _keywordWalls)
                if (wall != null && !_walls.Contains(wall)) wall.gameObject.SetActive(false);
        }

        /// <summary>
        /// ESC 설정창(씬에 있는 SettingsPanelUI)에 현장 조절값과 층·역할 줄을 덧붙인다.
        /// 세트가 없는 월 PC 에서도 층·역할은 바꿀 수 있어야 하므로 역할과 무관하게 붙인다. 창이 없는 씬이면 조용히 건너뛴다.
        ///
        /// 판을 만드는 쪽(ArUcoSettingsPanel)은 **씬에 미리 붙여 둔다** — 줄 원본 프리팹을 인스펙터에서 물려야 하기 때문이다.
        /// TCP 패널은 판 자체가 프리팹이라 여기서 복제한다.
        /// </summary>
        void AttachSettingsPanel()
        {
            var settingsUi = FindObjectOfType<SettingsPanelUI>(true);
            if (settingsUi == null || settingsUi.PanelRoot == null) return;

            var rows = GetComponent<ArUcoSettingsPanel>();
            if (rows == null)
                Debug.LogError("[StoryRest] 씬의 StoryRestApp 에 ArUcoSettingsPanel 이 없어 현장 조절 줄을 띄우지 못합니다.");
            else
                rows.Attach(settingsUi);

            // 관람 기록 링크의 연결 상태 · 송수신 로그. 설정창과 같이 열리고 닫히며 오른쪽에 따로 선다.
            if (_trafficPanelPrefab == null)
            {
                Debug.LogError("[StoryRest] StoryRestApp 에 TCP 패널 프리팹이 안 물려 있습니다. " +
                               "Assets/9.Prefab/StatsTrafficPanel.prefab 을 넣으세요.");
                return;
            }

            Instantiate(_trafficPanelPrefab, settingsUi.PanelRoot.transform.parent).Attach(this, settingsUi);
        }

        /// <summary>월 설정을 keywordwall.json 에 쓴다. ESC 설정창의 월 항목이 바뀔 때 부른다.</summary>
        public void SaveKeywordConfig()
        {
            if (KeywordConfig != null) KeywordWallConfig.Save(KeywordConfig);
        }

        /// <summary>이번 실행에서 실제로 만드는 세트. 월 PC(wall)면 비어 있다.</summary>
        List<SetConfig> ActiveSets()
        {
            return AppSettings.HasArUco(RunningRole)
                ? Config.SetsForFloor(Settings.floor)
                : new List<SetConfig>();
        }

        /// <summary>이번 실행에서 실제로 띄우는 키워드 월. ArUco PC(aruco)면 비어 있다.</summary>
        List<WallScreen> ActiveWalls()
        {
            return KeywordConfig != null
                ? KeywordConfig.WallsFor(Settings.floor, RunningRole)
                : new List<WallScreen>();
        }

        /// <summary>
        /// 관람 기록을 PC 사이에 나르는 링크. 2·3층은 세트와 월이 다른 PC 라 기록이 월 PC 로 건너가야 한다.
        /// all 은 한 프로세스 안이라 파일을 바로 읽으면 되므로 아무것도 만들지 않는다.
        /// </summary>
        void CreateStatsLink()
        {
            switch (RunningRole)
            {
                case AppRole.ArUco:
                    StatsServer = gameObject.AddComponent<ViewStatsServer>();
                    StatsServer.Setup(Settings.statsPort);
                    break;

                case AppRole.Wall:
                    StatsClient = gameObject.AddComponent<ViewStatsClient>();
                    StatsClient.Setup(Settings.statsHost, Settings.statsPort);
                    break;
            }

            // 링크 상태 패널(F7). all 에서도 보여 준다 — 역할을 잘못 잡은 것을 여기서 알아챈다.
            // 판 프리팹을 인스펙터에서 물려야 하므로 씬에 미리 붙여 둔다.
            if (GetComponent<ViewStatsLinkPanel>() == null)
                Debug.LogError("[StoryRest] 씬의 StoryRestApp 에 ViewStatsLinkPanel 이 없어 F7 링크 상태를 띄우지 못합니다.");
        }

        /// <summary>
        /// 키워드 월과 ArUco 세트가 같은 프로젝터를 요구하면 한쪽이 가려진다.
        /// 이 층에서 실제로 뜨는 것끼리만 비교한다 — 다른 층 항목과 겹치는 것은 문제가 아니다.
        /// </summary>
        void ValidateDisplayAssignment()
        {
            if (KeywordConfig == null || !KeywordConfig.enabled) return;

            var walls = ActiveWalls();
            var sets = ActiveSets();

            foreach (var wall in walls)
            {
                foreach (var set in sets)
                {
                    if (set.displayIndex != wall.displayIndex) continue;

                    _problems.Add($"키워드 월과 ArUco 세트 '{set.name}' 이 같은 디스플레이({wall.displayIndex})를 씁니다. " +
                                  $"keywordwall.json 의 walls[].displayIndex 를 확인하세요.");
                }
            }

            // 같은 프로젝터에 키워드 월을 두 번 띄우려는 경우도 잡는다.
            for (int i = 0; i < walls.Count; i++)
            {
                for (int j = i + 1; j < walls.Count; j++)
                {
                    if (walls[i].displayIndex != walls[j].displayIndex) continue;

                    _problems.Add($"키워드 월이 {walls[i].displayIndex}번 디스플레이에 두 번 배정되어 있습니다.");
                }
            }
        }

        void CreateKeywordWalls()
        {
            var walls = ActiveWalls();
            if (walls.Count == 0) return;

            // 무엇이 떠다니는지는 층 폴더가 정한다(→ SPEC §3.1). <마커ID>/sprite/ 에 그림이 하나라도 있으면
            // 그 층은 스프라이트, 없으면 keywords.txt 의 낱말. 화면끼리 같은 목록을 공유한다.
            var spriteFolders = ArUcoContentIndex.ScanSprites(Settings.ContentRoot);
            bool useSprites = spriteFolders.Count > 0;
            List<string> entries;

            // 여유분은 한 화면 분량 — 방금 사라진 그림이 곧 다시 나올 때 다시 읽지 않을 만큼이면 된다.
            // 낱말 모드여도 주제 카드가 그림을 쓰므로 월이 있으면 늘 만든다.
            Sprites = new KeywordSpriteCache(this, KeywordConfig.maxOnScreen);

            if (useSprites)
            {
                // 폴더가 100개면 전부 띄우기엔 너무 많다. 설정 범위만큼 폴더를 무작위로 골라 그 그림만 띄운다.
                var picked = new List<SpriteFolder>();
                entries = KeywordConfig.PickSprites(spriteFolders, picked);

                Debug.Log($"[Keyword] 그림이 있는 폴더 {spriteFolders.Count}개 중 {picked.Count}개를 골라 " +
                          $"그림 {entries.Count}장을 띄웁니다(낱말 대신). " +
                          $"고른 폴더: [{string.Join(", ", picked.ConvertAll(f => f.folderName))}]  " +
                          $"(keywordwall.json 의 minSpriteFolders~maxSpriteFolders)");
            }
            else
            {
                entries = KeywordList.Load(Settings.ContentRoot);
            }

            if (KeywordConfig.spotlight.enabled)
                Spotlight = new KeywordSpotlight(KeywordConfig.spotlight, Settings.ContentRoot);

            int wallCount = walls.Count;
            if (wallCount > _keywordWalls.Length)
            {
                _problems.Add($"이 층·역할은 키워드 월이 {wallCount}개 필요한데 씬에는 {_keywordWalls.Length}개뿐입니다. " +
                              "StartScene 의 StoryRestApp 아래에 KeywordWall 프리팹을 하나 더 놓고 Inspector 의 Keyword Walls 에 이어 주세요. " +
                              $"앞의 {_keywordWalls.Length}개만 띄웁니다.");
                Debug.LogError($"[Keyword] {_problems[_problems.Count - 1]}");
                wallCount = _keywordWalls.Length;
            }

            for (int i = 0; i < wallCount; i++)
            {
                var wall = _keywordWalls[i];
                if (wall == null)
                {
                    Debug.LogError($"[Keyword] StoryRestApp 의 Keyword Walls[{i}] 가 비어 있습니다.");
                    continue;
                }

                wall.gameObject.SetActive(true);
                wall.Setup(KeywordConfig, entries, useSprites ? Sprites : null, walls[i].displayIndex, i);
                if (Spotlight != null) wall.AttachSpotlight(Spotlight, Sprites);
                _walls.Add(wall);
            }

            Debug.Log($"[Keyword] {Settings.floor}층 키워드 월 {_walls.Count}개 " +
                      $"({(useSprites ? "스프라이트" : "낱말")} {entries.Count}개 · " +
                      $"디스플레이 {string.Join(", ", walls.ConvertAll(w => w.displayIndex))})");
        }

        /// <summary>
        /// 이 층에서 쓰는 세트만 만든다. 어느 세트를 쓸지는 sets[].floors 가 정한다 —
        /// 층수로 개수를 추론하지 않으므로 설치가 바뀌어도 코드를 고칠 일이 없다.
        /// </summary>
        void CreateSets()
        {
            var active = ActiveSets();
            if (active.Count == 0) return;

            int count = active.Count;

            // 에디터에는 디스플레이가 하나뿐이라 두 번째 세트를 볼 방법이 없다.
            // 그래서 에디터에서만 화면을 가로로 나눠 모든 세트를 한 번에 띄운다.
            bool splitPreview = Application.isEditor && Config.editorPreview && count > 1;

            if (count > _arUcoSets.Length)
            {
                _problems.Add($"이 층은 ArUco 세트가 {count}개 필요한데 씬에는 {_arUcoSets.Length}개뿐입니다. " +
                              "StartScene 의 StoryRestApp 아래에 ArUcoSet 프리팹을 하나 더 놓고 Inspector 의 ArUco Sets 에 이어 주세요. " +
                              $"앞의 {_arUcoSets.Length}개만 띄웁니다.");
                Debug.LogError($"[ArUco] {_problems[_problems.Count - 1]}");
                count = _arUcoSets.Length;
            }

            for (int i = 0; i < count; i++)
            {
                var setConfig = active[i];
                var set = _arUcoSets[i];
                if (set == null)
                {
                    Debug.LogError($"[ArUco] StoryRestApp 의 ArUco Sets[{i}] 가 비어 있습니다.");
                    continue;
                }

                Rect viewport = splitPreview
                    ? new Rect(i / (float)count, 0f, 1f / count, 1f)
                    : new Rect(0f, 0f, 1f, 1f);

                int displayIndex = splitPreview ? 0 : setConfig.displayIndex;

                set.gameObject.SetActive(true);
                set.Initialize(Config, setConfig, Content, Images, Settings.floor, i, viewport, displayIndex);
                _sets.Add(set);
            }

            if (splitPreview)
                Debug.Log($"[StoryRest] 에디터 프리뷰: 세트 {count}개를 한 화면에 나눠 그립니다. " +
                          $"(빌드에서는 각자의 디스플레이로 나갑니다)");

            // 편집모드는 세트가 다 만들어진 뒤에 붙인다. 평상시에는 아무것도 그리지 않는다.
            // 순서가 곧 LateUpdate 순서다 — 안내(F1) → 편집모드(F2) → 관람 기록(F4). 뒤의 것이 앞의 HUD 에 양보한다.
            if (GetComponent<ArUcoHelpPanel>() == null) gameObject.AddComponent<ArUcoHelpPanel>();
            if (GetComponent<ArUcoEditMode>() == null) gameObject.AddComponent<ArUcoEditMode>();
            if (GetComponent<ArUcoStatsPanel>() == null) gameObject.AddComponent<ArUcoStatsPanel>();
        }

        void Update()
        {
            // 카드 재료는 월 화면 수와 무관하게 한 번만 센다.
            Spotlight?.Tick();
        }

        void OnDestroy()
        {
            Spotlight?.Dispose();
            Sprites?.ReleaseAll();
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// 캘리브레이션용 마커가 실제로 만들 수 있는 ID 인지 확인한다.
        /// 딕셔너리마다 가진 개수가 다르다 — DICT_4X4_100 은 0~99 뿐이고 DICT_4X4_250 은 0~249 다.
        /// 여기서 걸러내지 않으면 설치 현장에서 보정을 시도할 때에야 알게 된다.
        /// </summary>
        void ValidateCalibrationMarkers()
        {
            int size = ArUcoMarkerTexture.GetDictionarySize(Config.dictionaryId);
            if (size <= 0)
            {
                _problems.Add($"dictionaryId={Config.dictionaryId} 를 읽지 못했습니다.");
                return;
            }

            var ids = Config.calibrationMarkerIds;
            if (ids == null || ids.Length < 4)
            {
                _problems.Add("calibrationMarkerIds 에는 마커 ID 4개가 필요합니다.");
                return;
            }

            for (int i = 0; i < 4; i++)
            {
                if (ids[i] >= 0 && ids[i] < size) continue;

                _problems.Add($"캘리브레이션 마커 {ids[i]}번은 딕셔너리 {Config.dictionaryId} " +
                              $"(쓸 수 있는 ID: 0~{size - 1}) 에 없습니다. 자동 보정이 동작하지 않습니다.");
            }
        }

        /// <summary>
        /// 콘텐츠 폴더 이름(= 마커 ID)이 실제로 인식될 수 있는 번호인지 확인한다.
        ///
        /// 폴더를 만드는 것만으로 마커가 늘어나는 구조라(→ ArUcoContentIndex), 딕셔너리 범위를
        /// 넘는 번호나 캘리브레이션용으로 예약한 번호로 폴더를 만들어도 아무 말 없이 무시된다.
        /// 그 폴더의 영상은 전시 내내 한 번도 뜨지 않으므로, 시작할 때 짚어 준다.
        /// </summary>
        void ValidateContentMarkers()
        {
            int size = ArUcoMarkerTexture.GetDictionarySize(Config.dictionaryId);
            if (size <= 0) return; // 딕셔너리 자체의 문제는 ValidateCalibrationMarkers 가 이미 보고했다.

            var outOfRange = new List<int>();
            var reserved = new List<int>();

            foreach (int id in Content.MarkerIds)
            {
                if (id < 0 || id >= size) outOfRange.Add(id);
                else if (Config.IsCalibrationMarker(id)) reserved.Add(id);
            }

            if (outOfRange.Count > 0)
            {
                _problems.Add($"콘텐츠 폴더 [{string.Join(", ", outOfRange)}] 는 딕셔너리 {Config.dictionaryId} " +
                              $"(쓸 수 있는 ID: 0~{size - 1}) 밖이라 인식되지 않습니다. " +
                              $"더 큰 딕셔너리로 바꾸거나 폴더 번호를 옮기세요.");
            }

            if (reserved.Count > 0)
            {
                _problems.Add($"콘텐츠 폴더 [{string.Join(", ", reserved)}] 가 캘리브레이션 예약 ID 와 겹칩니다. " +
                              $"이 마커는 콘텐츠를 띄우지 않습니다. " +
                              $"aruco.json 의 calibrationMarkerIds 나 폴더 번호를 옮기세요.");
            }
        }

        /// <summary>편집모드에서 값을 바꾼 뒤 호출한다. 실패해도 앱은 계속 돈다.</summary>
        public bool SaveConfig() => ArUcoConfigIO.Save(Config);

        /// <summary>현장에서 콘텐츠 파일을 갈아끼운 뒤 재실행 없이 다시 읽는다.</summary>
        public void ReloadContent()
        {
            Content.Rescan(Settings.ContentRoot);

            // 열려 있던 파일을 놓아주지 않으면 교체된 파일이 반영되지 않는다.
            Images?.ReleaseAll();
            for (int i = 0; i < _sets.Count; i++) _sets[i].ReloadContent();
        }

        /// <summary>
        /// 이 층에서 쓰는 디스플레이만 켠다. 활성화하지 않은 디스플레이에는 아무것도 출력되지 않는다.
        /// 에디터에는 디스플레이가 하나뿐이라 이 호출은 무시된다.
        /// </summary>
        void ActivateDisplays()
        {
            foreach (var set in ActiveSets())
            {
                ActivateDisplay(set.displayIndex, $"세트 '{set.name}'");
            }

            foreach (var wall in ActiveWalls())
            {
                ActivateDisplay(wall.displayIndex, "키워드 월");
            }
        }

        void ActivateDisplay(int index, string owner)
        {
            // 0번은 주 디스플레이라 이미 켜져 있다.
            if (index <= 0) return;

            // 에디터는 모니터가 몇 대든 Display.displays 를 1개로 보고한다. 여기서 검사하면 늘 거짓 경고가 되므로
            // 빌드에서만 따진다. 에디터에서는 Game 뷰의 Display 드롭다운으로 각 화면을 본다.
            if (Application.isEditor) return;

            if (index >= Display.displays.Length)
            {
                _problems.Add($"{owner} 이 {index}번 디스플레이를 요구하지만 " +
                              $"연결된 디스플레이는 {Display.displays.Length}개뿐입니다.");
                return;
            }

            Display.displays[index].Activate();
        }

        void LogSummary()
        {
            var text = new StringBuilder(512);

            var active = ActiveSets();
            var walls = ActiveWalls();

            text.AppendLine($"[StoryRest] {Settings.floor}층 · 역할 {AppSettings.RoleName(RunningRole)} " +
                            $"· 세트 {active.Count}개 · 키워드 월 {walls.Count}개 · 콘텐츠 마커 {Content.Count}개 " +
                            $"(영상 {Content.VideoCount} · 이미지 {Content.ImageCount})");

            switch (RunningRole)
            {
                case AppRole.ArUco:
                    text.AppendLine($"  관람 기록 서버: 포트 {Settings.statsPort} 에서 월 PC 를 기다림");
                    break;
                case AppRole.Wall:
                    text.AppendLine($"  관람 기록 클라이언트: ArUco PC {Settings.statsHost}:{Settings.statsPort} 에 접속");
                    break;
            }

            foreach (var set in active)
            {
                text.AppendLine($"  세트 {set.name}  카메라 {set.camera.deviceId}  " +
                                $"디스플레이 {set.displayIndex}  " +
                                $"캘리브레이션 {(set.calibration.IsUsable ? "완료" : "미완료")}  " +
                                $"저장된 마커 {set.markers.Count}개" +
                                (set.showCameraPreview ? "  · 카메라영상 켜짐" : ""));
            }

            // 이 층에서 안 쓰는 세트가 있으면 "왜 안 뜨지" 를 바로 알 수 있게 함께 남긴다.
            if (AppSettings.HasArUco(RunningRole) && Config.sets != null && Config.sets.Count > active.Count)
            {
                foreach (var set in Config.sets)
                {
                    if (set == null || set.IsUsedOnFloor(Settings.floor)) continue;

                    text.AppendLine($"  (세트 {set.name} 은 이 층에서 쓰지 않음 — " +
                                    $"floors: {string.Join(",", set.floors)})");
                }
            }

            text.AppendLine($"  설정 파일: {ArUcoConfigIO.Path}");
            text.AppendLine($"  층 설정: {AppSettings.Path}");
            if (RunningRole == AppRole.Wall)
                text.Append($"  관람 기록 미러: {ViewLog.Directory}");
            else
                text.Append(Config.recordViews
                    ? $"  관람 기록: {ViewLog.Directory} " +
                      $"(유지 {Config.viewMinDwellSeconds:0.#}초 이상 · 복귀 {Config.viewResumeGraceSeconds:0.#}초 이내)"
                    : "  관람 기록: 꺼짐 (recordViews)");

            Debug.Log(text.ToString());

            if (RunningRole == AppRole.ArUco && !Config.recordViews)
                Debug.LogWarning("[StoryRest] recordViews 가 꺼져 있어 월 PC 로 보낼 관람 기록이 없습니다. " +
                                 "aruco.json 의 recordViews 를 확인하세요.");

            // 점검용 값이라 켜 둔 채로 전시가 시작되기 쉽다. 설정 문제는 아니므로 problems 와 따로 남긴다.
            foreach (var set in active)
            {
                if (!set.showCameraPreview) continue;

                Debug.LogWarning($"[StoryRest] 세트 '{set.name}' 이 카메라 영상을 함께 투사합니다(점검용). " +
                                 $"전시 전에 편집모드에서 C 로 끄거나 " +
                                 $"aruco.json 의 sets[].showCameraPreview 를 false 로 되돌리세요.");
            }

            if (_problems.Count == 0) return;

            // 설정이 어긋난 채로 전시가 돌면 원인을 찾기 어렵다. 눈에 띄게 한 번에 모아 남긴다.
            var warning = new StringBuilder(256);
            warning.AppendLine($"[StoryRest] 설정에 문제가 {_problems.Count}건 있습니다:");
            foreach (string problem in _problems) warning.AppendLine($"  · {problem}");

            Debug.LogError(warning.ToString());
        }
    }
}
