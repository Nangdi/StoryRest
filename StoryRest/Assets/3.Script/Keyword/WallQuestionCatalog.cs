using System;
using System.Collections.Generic;
using System.IO;
using StoryRest.ArUco;
using UnityEngine;

namespace StoryRest.Keyword
{
    /// <summary>벽에 띄울 수 있는 전시물 하나 — 질문 그림과 해시태그 그림들.</summary>
    public class WallQuestion
    {
        public int number;              // 전시물 번호 n (원고 번호). 마커는 2n-1(핵심) · 2n(자세히)
        public string questionPath;     // question/question.png
        public List<string> tagPaths;   // 핵심 ∪ 자세히, 같은 글자는 하나
        public List<string> tagKeys;    // tagPaths 와 같은 순서의 글자(파일 이름에서 NN_ 과 확장자를 뗀 것)
    }

    /// <summary>
    /// 층 폴더에서 벽에 띄울 전시물을 모은다(→ WALL_CONTENT §2 · §6).
    ///
    ///   floor_N/&lt;마커&gt;/question/question.png    질문(핵심 · 자세히 같은 그림)
    ///   floor_N/&lt;마커&gt;/sprite/NN_&lt;태그&gt;.png       해시태그
    ///
    /// 전시물 n 은 마커 2n-1 · 2n 두 폴더를 합친 것이다. 해시태그는 102개 전시물에서 두 버전이 같으므로
    /// 글자로 중복을 걸러 한 목록으로 만든다. 질문 그림이 없는 전시물은 벽에 띄울 수 없어 넣지 않는다.
    /// 시작할 때 한 번 읽는다 — 그림을 바꾸면 재시작한다.
    /// </summary>
    public static class WallQuestionCatalog
    {
        public const string QuestionFolder = "question";
        public const string QuestionFile = "question.png";
        public const string TagFolder = ArUcoContentIndex.SpriteFolder;

        /// <summary>마커 ID → 전시물 번호. 1·2 → 1, 3·4 → 2 …</summary>
        public static int ExhibitOf(int markerId) => markerId <= 0 ? 0 : (markerId + 1) / 2;

        public static List<WallQuestion> Scan(string contentRoot)
        {
            var result = new List<WallQuestion>();
            var folders = ArUcoContentIndex.ScanFolderNames(contentRoot);

            var numbers = new SortedSet<int>();
            foreach (int markerId in folders.Keys)
                if (markerId > 0) numbers.Add(ExhibitOf(markerId));

            foreach (int n in numbers)
            {
                var question = new WallQuestion
                {
                    number = n,
                    tagPaths = new List<string>(),
                    tagKeys = new List<string>(),
                };

                // 핵심 폴더를 먼저 본다. 질문은 먼저 찾은 것, 태그는 둘을 합친다.
                foreach (int markerId in new[] { 2 * n - 1, 2 * n })
                {
                    if (!folders.TryGetValue(markerId, out string folder)) continue;
                    string root = Path.Combine(contentRoot, folder);

                    string questionPath = Path.Combine(root, QuestionFolder, QuestionFile);
                    if (question.questionPath == null && File.Exists(questionPath)) question.questionPath = questionPath;

                    AddTags(question, Path.Combine(root, TagFolder));
                }

                if (question.questionPath != null) result.Add(question);
            }

            return result;
        }

        static void AddTags(WallQuestion question, string directory)
        {
            if (!Directory.Exists(directory)) return;

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Keyword] 해시태그 폴더를 읽지 못했습니다: {directory}\n{e.Message}");
                return;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                if (!ArUcoContentIndex.IsImageFile(file)) continue;

                string key = TagKey(file);
                if (question.tagKeys.Contains(key)) continue;

                question.tagKeys.Add(key);
                question.tagPaths.Add(file);
            }
        }

        /// <summary>"03_상대 속도.png" → "상대 속도". 번호가 없으면 이름 그대로.</summary>
        public static string TagKey(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);

            int i = 0;
            while (i < name.Length && char.IsDigit(name[i])) i++;
            if (i > 0 && i < name.Length && name[i] == '_') name = name.Substring(i + 1);

            return name.Trim();
        }
    }
}
