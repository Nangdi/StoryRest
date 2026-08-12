using System.Collections.Generic;
using System.Text;
using StoryRest.Keyword;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 전시 시스템의 진입점. 설정을 읽고, 검증하고, 세트를 만든다.
    ///
    /// 씬에는 이 컴포넌트 하나만 두면 된다. 세트 수와 장비 배정은 전부 aruco.json 이 결정하므로
    /// 층마다 다른 씬이나 다른 빌드를 만들지 않는다.
    /// </summary>
    [DisallowMultipleComponent]
    public class StoryRestApp : MonoBehaviour
    {
        public static StoryRestApp Instance { get; private set; }

        public AppSettings Settings { get; private set; }
        public ArUcoConfig Config { get; private set; }
        public ArUcoContentIndex Content { get; private set; }

        /// <summary>설정에 문제가 있으면 여기에 남는다. 편집모드 HUD 에서 보여줄 수 있다.</summary>
        public IReadOnlyList<string> Problems => _problems;

        /// <summary>만들어진 세트들. 개수는 aruco.json 의 sets 배열이 결정한다.</summary>
        public IReadOnlyList<ArUcoSet> Sets => _sets;

        public KeywordWallConfig KeywordConfig { get; private set; }

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

            Config = ArUcoConfigIO.Load();
            Config.Validate(_problems, Settings.floor);
            ValidateCalibrationMarkers();

            Content = new ArUcoContentIndex();
            Content.Rescan(Settings.ContentRoot);
            ValidateContentMarkers();

            KeywordConfig = KeywordWallConfig.Load();
            ValidateDisplayAssignment();

            ActivateDisplays();
            LogSummary();
            CreateSets();
            CreateKeywordWalls();
        }

        /// <summary>
        /// 키워드 월과 ArUco 세트가 같은 프로젝터를 요구하면 한쪽이 가려진다.
        /// 이 층에서 실제로 뜨는 것끼리만 비교한다 — 다른 층 항목과 겹치는 것은 문제가 아니다.
        /// </summary>
        void ValidateDisplayAssignment()
        {
            if (KeywordConfig == null || !KeywordConfig.enabled) return;

            var walls = KeywordConfig.WallsForFloor(Settings.floor);
            var sets = Config.SetsForFloor(Settings.floor);

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
            var walls = KeywordConfig != null
                ? KeywordConfig.WallsForFloor(Settings.floor)
                : new List<WallScreen>();

            if (walls.Count == 0) return;

            // 낱말 목록은 층 폴더에서 읽는다. 화면끼리 같은 목록을 공유한다.
            var words = KeywordList.Load(Settings.ContentRoot);

            for (int i = 0; i < walls.Count; i++)
            {
                var go = new GameObject($"KeywordWall_{walls[i].displayIndex}");
                go.transform.SetParent(transform, false);

                var wall = go.AddComponent<KeywordWall>();
                wall.Setup(KeywordConfig, words, walls[i].displayIndex, i);
                _walls.Add(wall);
            }

            Debug.Log($"[Keyword] {Settings.floor}층 키워드 월 {_walls.Count}개 " +
                      $"(디스플레이 {string.Join(", ", walls.ConvertAll(w => w.displayIndex))})");
        }

        /// <summary>
        /// 이 층에서 쓰는 세트만 만든다. 어느 세트를 쓸지는 sets[].floors 가 정한다 —
        /// 층수로 개수를 추론하지 않으므로 설치가 바뀌어도 코드를 고칠 일이 없다.
        /// </summary>
        void CreateSets()
        {
            var active = Config.SetsForFloor(Settings.floor);
            if (active.Count == 0) return;

            int count = active.Count;

            // 에디터에는 디스플레이가 하나뿐이라 두 번째 세트를 볼 방법이 없다.
            // 그래서 에디터에서만 화면을 가로로 나눠 모든 세트를 한 번에 띄운다.
            bool splitPreview = Application.isEditor && Config.editorPreview && count > 1;

            for (int i = 0; i < count; i++)
            {
                var setConfig = active[i];

                var go = new GameObject($"Set_{setConfig.name}");
                go.transform.SetParent(transform, false);

                Rect viewport = splitPreview
                    ? new Rect(i / (float)count, 0f, 1f / count, 1f)
                    : new Rect(0f, 0f, 1f, 1f);

                int displayIndex = splitPreview ? 0 : setConfig.displayIndex;

                var set = go.AddComponent<ArUcoSet>();
                set.Initialize(Config, setConfig, Content, i, viewport, displayIndex);
                _sets.Add(set);
            }

            if (splitPreview)
                Debug.Log($"[StoryRest] 에디터 프리뷰: 세트 {count}개를 한 화면에 나눠 그립니다. " +
                          $"(빌드에서는 각자의 디스플레이로 나갑니다)");

            // 편집모드는 세트가 다 만들어진 뒤에 붙인다. 평상시에는 아무것도 그리지 않는다.
            if (GetComponent<ArUcoEditMode>() == null) gameObject.AddComponent<ArUcoEditMode>();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// 캘리브레이션용 마커가 실제로 만들 수 있는 ID 인지 확인한다.
        /// 딕셔너리마다 가진 개수가 다르다 — DICT_4X4_100 은 0~99 뿐이다.
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
            for (int i = 0; i < _sets.Count; i++) _sets[i].ReloadContent();
        }

        /// <summary>
        /// 이 층에서 쓰는 디스플레이만 켠다. 활성화하지 않은 디스플레이에는 아무것도 출력되지 않는다.
        /// 에디터에는 디스플레이가 하나뿐이라 이 호출은 무시된다.
        /// </summary>
        void ActivateDisplays()
        {
            foreach (var set in Config.SetsForFloor(Settings.floor))
            {
                ActivateDisplay(set.displayIndex, $"세트 '{set.name}'");
            }

            if (KeywordConfig == null) return;

            foreach (var wall in KeywordConfig.WallsForFloor(Settings.floor))
            {
                ActivateDisplay(wall.displayIndex, "키워드 월");
            }
        }

        void ActivateDisplay(int index, string owner)
        {
            // 0번은 주 디스플레이라 이미 켜져 있다.
            if (index <= 0) return;

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

            var active = Config.SetsForFloor(Settings.floor);
            var walls = KeywordConfig != null
                ? KeywordConfig.WallsForFloor(Settings.floor)
                : new List<WallScreen>();

            text.AppendLine($"[StoryRest] {Settings.floor}층 · 세트 {active.Count}개 " +
                            $"· 키워드 월 {walls.Count}개 · 콘텐츠 {Content.Count}개");

            foreach (var set in active)
            {
                text.AppendLine($"  세트 {set.name}  카메라 {set.camera.deviceId}  " +
                                $"디스플레이 {set.displayIndex}  " +
                                $"캘리브레이션 {(set.calibration.IsUsable ? "완료" : "미완료")}  " +
                                $"저장된 마커 {set.markers.Count}개");

            // 이 층에서 안 쓰는 세트가 있으면 "왜 안 뜨지" 를 바로 알 수 있게 함께 남긴다.
            if (Config.sets != null && Config.sets.Count > active.Count)
            {
                foreach (var set in Config.sets)
                {
                    if (set == null || set.IsUsedOnFloor(Settings.floor)) continue;

                    text.AppendLine($"  (세트 {set.name} 은 이 층에서 쓰지 않음 — " +
                                    $"floors: {string.Join(",", set.floors)})");
                }
            }

            text.Append($"  설정 파일: {ArUcoConfigIO.Path}");

            Debug.Log(text.ToString());

            if (_problems.Count == 0) return;

            // 설정이 어긋난 채로 전시가 돌면 원인을 찾기 어렵다. 눈에 띄게 한 번에 모아 남긴다.
            var warning = new StringBuilder(256);
            warning.AppendLine($"[StoryRest] 설정에 문제가 {_problems.Count}건 있습니다:");
            foreach (string problem in _problems) warning.AppendLine($"  · {problem}");

            Debug.LogError(warning.ToString());
        }
    }
}
