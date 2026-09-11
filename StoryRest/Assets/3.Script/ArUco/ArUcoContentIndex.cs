using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>콘텐츠 한 장의 종류. 확장자로 정해지며, 표시 경로가 여기서만 갈라진다.</summary>
    public enum ContentKind
    {
        Video,
        Image,
    }

    /// <summary>마커 폴더 안의 파일 한 개.</summary>
    public class ContentEntry
    {
        public ContentKind kind;

        /// <summary>재생·로드에 쓰는 절대 경로.</summary>
        public string path;

        /// <summary>보정값을 찾는 키(→ MarkerConfig.items[].file). 파일 이름이라 사람이 읽을 수 있다.</summary>
        public string fileName;

        public bool IsVideo => kind == ContentKind.Video;
    }

    /// <summary>
    /// floor_&lt;N&gt;/&lt;마커ID&gt;/ 폴더를 훑어 "마커 ID → 콘텐츠 목록" 표를 만든다.
    ///
    /// 폴더 이름 앞자리 숫자가 곧 마커 ID다. 뒤에 제목을 붙여도 되므로(17_별의일생)
    /// 폴더가 층마다 70개씩 되어도 탐색기에서 무엇이 무엇인지 알 수 있다.
    ///
    /// 폴더 안의 영상·이미지를 **전부** 등록한다. 한 마커가 영상과 이미지를 함께 띄운다.
    /// 여기서는 경로만 찾는다. 실제 표시는 ArUcoContentLibrary(영상)와 ArUcoImageCache(이미지)가 맡는다.
    /// </summary>
    public class ArUcoContentIndex
    {
        // AVPro Windows 백엔드(MediaFoundation)가 다루는 컨테이너들.
        // 하드웨어 디코딩이 도는 H.264/mp4 를 기본으로 삼는다.
        static readonly string[] VideoExtensions =
        {
            ".mp4", ".mov", ".m4v", ".wmv", ".avi", ".mkv", ".webm",
        };

        // UnityWebRequestTexture 가 읽는 형식.
        static readonly string[] ImageExtensions =
        {
            ".png", ".jpg", ".jpeg",
        };

        static readonly List<ContentEntry> Empty = new List<ContentEntry>();

        readonly Dictionary<int, List<ContentEntry>> _entries = new Dictionary<int, List<ContentEntry>>();

        // 같은 ID 를 가리키는 폴더가 둘일 때 어느 것을 썼는지 알려주려고 남긴다.
        readonly Dictionary<int, string> _folders = new Dictionary<int, string>();

        public string Root { get; private set; }

        /// <summary>콘텐츠를 가진 마커 수. 파일 개수가 아니다.</summary>
        public int Count => _entries.Count;

        public int VideoCount { get; private set; }
        public int ImageCount { get; private set; }

        /// <summary>등록된 마커 ID 목록(오름차순).</summary>
        public List<int> MarkerIds
        {
            get
            {
                var ids = new List<int>(_entries.Keys);
                ids.Sort();
                return ids;
            }
        }

        /// <summary>이 마커에 매핑된 콘텐츠들. 없으면 빈 목록이다(null 을 돌려주지 않는다).</summary>
        public List<ContentEntry> GetEntries(int markerId)
            => _entries.TryGetValue(markerId, out var list) ? list : Empty;

        public bool Has(int markerId) => _entries.ContainsKey(markerId);

        /// <summary>이 마커 폴더의 실제 이름. 편집모드에서 어느 폴더를 보고 있는지 알려준다.</summary>
        public string GetFolderName(int markerId)
            => _folders.TryGetValue(markerId, out string name) ? name : null;

        /// <summary>
        /// 콘텐츠 루트를 다시 훑는다. 현장에서 파일을 갈아끼운 뒤 편집모드에서 호출한다.
        /// 폴더가 없거나 비어 있어도 예외를 던지지 않는다 — 로그를 남기고 빈 상태로 둔다.
        /// </summary>
        public void Rescan(string contentRoot)
        {
            Root = contentRoot;
            _entries.Clear();
            _folders.Clear();
            VideoCount = 0;
            ImageCount = 0;

            if (string.IsNullOrEmpty(contentRoot) || !Directory.Exists(contentRoot))
            {
                // 층 설정이 잘못됐거나 콘텐츠를 아직 복사하지 않은 상태다. 조용히 넘어가지 않는다.
                Debug.LogError($"[ArUco] 콘텐츠 폴더가 없습니다: {contentRoot}\n" +
                               $"Setting.json 의 floor 값과 실제 폴더 이름(floor_<N>)을 확인하세요.");
                return;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(contentRoot);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ArUco] 콘텐츠 폴더를 읽지 못했습니다: {contentRoot}\n{e.Message}");
                return;
            }

            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);

            int empty = 0;

            foreach (string directory in directories)
            {
                string folderName = new DirectoryInfo(directory).Name;

                // 마커 ID 로 읽히지 않는 폴더는 그냥 지나간다.
                // 현장에서 "_보관" 같은 폴더를 만들어 두는 일이 있어 경고로 시끄럽게 하지 않는다.
                if (!TryParseMarkerId(folderName, out int markerId)) continue;

                if (_folders.TryGetValue(markerId, out string previous))
                {
                    // 폴더 이름에 제목을 붙일 수 있으므로 "17" 과 "17_별의일생" 이 함께 생기기 쉽다.
                    // 둘 다 읽으면 어느 파일이 뜨는지 예측할 수 없으니 먼저 온 것만 쓰고 짚어 준다.
                    Debug.LogWarning($"[ArUco] {markerId}번 마커 폴더가 둘입니다. " +
                                     $"[{previous}] 만 쓰고 [{folderName}] 는 무시합니다.");
                    continue;
                }

                _folders[markerId] = folderName;

                var entries = CollectEntries(directory);
                if (entries.Count == 0)
                {
                    // 마커 ID 폴더는 미리 만들어 두고 콘텐츠를 나중에 채운다. 아직 빈 것은 정상이므로
                    // 폴더마다 경고하지 않는다. 대신 그 마커가 잡히면 화면에 안내를 띄운다(→ ArUcoSet).
                    empty++;
                    continue;
                }

                _entries[markerId] = entries;

                foreach (var entry in entries)
                {
                    if (entry.IsVideo) VideoCount++;
                    else ImageCount++;
                }
            }

            Debug.Log($"[ArUco] 콘텐츠를 찾았습니다: 마커 {_entries.Count}개 " +
                      $"(영상 {VideoCount} · 이미지 {ImageCount})  {contentRoot}\n" +
                      $"마커 ID: [{string.Join(", ", MarkerIds)}]" +
                      (empty > 0 ? $"\n콘텐츠가 아직 없는 폴더: {empty}개" : ""));
        }

        /// <summary>
        /// 폴더 이름에서 마커 ID를 읽는다. 앞자리 숫자만 보고 나머지는 무시한다.
        /// "17" · "17_별의일생" · "017 별의 일생" 이 모두 17번이 된다.
        ///
        /// 숫자만 강제하면 폴더가 70개일 때 무엇이 무엇인지 알 방법이 없다.
        /// 제목을 붙일 수 있게 열어 두는 편이 현장에서 실수를 줄인다.
        /// </summary>
        public static bool TryParseMarkerId(string folderName, out int markerId)
        {
            markerId = -1;
            if (string.IsNullOrEmpty(folderName)) return false;

            int length = 0;
            while (length < folderName.Length && char.IsDigit(folderName[length])) length++;

            if (length == 0) return false;

            // 앞자리가 지나치게 길면 마커 번호가 아니라 날짜나 코드다. int 로 넘치기 전에 거른다.
            if (length > 9) return false;

            return int.TryParse(folderName.Substring(0, length), out markerId);
        }

        /// <summary>
        /// 폴더 안의 영상·이미지를 이름순으로 모은다.
        /// 그리는 순서가 곧 이 순서이고, 나중 것이 위에 올라간다(→ SPEC §4).
        /// </summary>
        static List<ContentEntry> CollectEntries(string directory)
        {
            var entries = new List<ContentEntry>();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ArUco] 폴더를 읽지 못했습니다: {directory}\n{e.Message}");
                return entries;
            }

            // 실행할 때마다 순서가 달라지지 않도록 정렬해 둔다.
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                string extension = Path.GetExtension(file).ToLowerInvariant();

                if (Array.IndexOf(VideoExtensions, extension) >= 0)
                    entries.Add(NewEntry(ContentKind.Video, file));
                else if (Array.IndexOf(ImageExtensions, extension) >= 0)
                    entries.Add(NewEntry(ContentKind.Image, file));
            }

            return entries;
        }

        static ContentEntry NewEntry(ContentKind kind, string path)
        {
            return new ContentEntry
            {
                kind = kind,
                path = path,
                fileName = Path.GetFileName(path),
            };
        }
    }
}
