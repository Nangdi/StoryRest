using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// floor_&lt;N&gt;/&lt;마커ID&gt;/ 폴더를 훑어 "마커 ID → 영상 파일" 표를 만든다.
    ///
    /// 폴더 이름이 곧 마커 ID다. 폴더를 추가하는 것만으로 그 마커가 활성화되고,
    /// 설정 파일을 미리 손볼 필요가 없다. 파일명은 자유다.
    ///
    /// 여기서는 경로만 찾는다. 실제 재생(AVPro MediaPlayer)은 ArUcoContentLibrary 가 맡는다.
    /// </summary>
    public class ArUcoContentIndex
    {
        // AVPro Windows 백엔드(MediaFoundation)가 다루는 컨테이너들.
        // 하드웨어 디코딩이 도는 H.264/mp4 를 기본으로 삼는다.
        static readonly string[] VideoExtensions =
        {
            ".mp4", ".mov", ".m4v", ".wmv", ".avi", ".mkv", ".webm",
        };

        readonly Dictionary<int, string> _paths = new Dictionary<int, string>();

        public string Root { get; private set; }
        public int Count => _paths.Count;

        /// <summary>등록된 마커 ID 목록(오름차순).</summary>
        public List<int> MarkerIds
        {
            get
            {
                var ids = new List<int>(_paths.Keys);
                ids.Sort();
                return ids;
            }
        }

        public bool TryGetPath(int markerId, out string path) => _paths.TryGetValue(markerId, out path);

        public bool Has(int markerId) => _paths.ContainsKey(markerId);

        /// <summary>
        /// 콘텐츠 루트를 다시 훑는다. 현장에서 파일을 갈아끼운 뒤 편집모드에서 호출한다.
        /// 폴더가 없거나 비어 있어도 예외를 던지지 않는다 — 로그를 남기고 빈 상태로 둔다.
        /// </summary>
        public void Rescan(string contentRoot)
        {
            Root = contentRoot;
            _paths.Clear();

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

            foreach (string directory in directories)
            {
                string folderName = new DirectoryInfo(directory).Name;

                // 마커 ID 로 읽히지 않는 폴더는 그냥 지나간다.
                // 현장에서 "_보관" 같은 폴더를 만들어 두는 일이 있어 경고로 시끄럽게 하지 않는다.
                if (!int.TryParse(folderName, out int markerId)) continue;

                string video = FindVideo(directory);
                if (video == null)
                {
                    Debug.LogWarning($"[ArUco] {markerId}번 마커 폴더에 영상이 없습니다: {directory}");
                    continue;
                }

                _paths[markerId] = video;
            }

            Debug.Log($"[ArUco] 콘텐츠 {_paths.Count}개를 찾았습니다: {contentRoot}\n" +
                      $"마커 ID: [{string.Join(", ", MarkerIds)}]");
        }

        // 폴더당 영상은 1개다. 여러 개면 이름순 첫 번째를 쓴다.
        // (실행할 때마다 다른 것이 잡히지 않도록 정렬한 뒤 고른다.)
        static string FindVideo(string directory)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ArUco] 폴더를 읽지 못했습니다: {directory}\n{e.Message}");
                return null;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            string first = null;
            int found = 0;

            foreach (string file in files)
            {
                string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (Array.IndexOf(VideoExtensions, extension) < 0) continue;

                found++;
                first ??= file;
            }

            if (found > 1)
            {
                Debug.LogWarning($"[ArUco] 영상이 {found}개 있어 첫 번째만 씁니다: " +
                                 $"{System.IO.Path.GetFileName(first)} ({directory})");
            }

            return first;
        }
    }
}
