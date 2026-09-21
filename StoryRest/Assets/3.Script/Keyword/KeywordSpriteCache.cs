using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace StoryRest.Keyword
{
    /// <summary>
    /// 키워드 월에 떠다니는 스프라이트 텍스처를 든다. 월 화면들이 함께 쓴다.
    ///
    /// 층에 그림이 70장 넘게 있어도 화면에 뜬 것만 메모리에 올린다 — 항목이 태어날 때 빌리고(Acquire)
    /// 사라질 때 돌려준다(Release). 아무도 안 쓰는 것은 상한을 넘기면 오래된 것부터 버린다.
    ///
    /// ArUcoImageCache 와 나눈 이유 — 그쪽은 "이번 프레임에 요청됐는가" 로 쓰임을 판단하고
    /// 용량을 ArUco 설정에서 가져온다. 월은 항목이 몇 초씩 같은 그림을 들고 있으므로 참조 수가 맞고,
    /// all 역할에서 마커 투사 이미지와 자리를 다투면 안 된다.
    ///
    /// 읽기는 비동기다. 메인 스레드에서 디코딩하면 그림이 바뀔 때마다 화면이 끊기고,
    /// 1층 all PC 에서는 마커 투사까지 같이 끊긴다.
    /// </summary>
    public class KeywordSpriteCache
    {
        class Entry
        {
            public Texture2D texture;
            public int users;
            public bool loading;
            public bool failed;
            public float lastReleasedTime;
        }

        readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        readonly MonoBehaviour _runner;
        readonly int _idleCapacity;

        /// <param name="runner">코루틴을 돌릴 주체. 월이 살아 있는 동안 유지되는 것이어야 한다.</param>
        /// <param name="idleCapacity">쓰이지 않는 채로 들고 있을 텍스처 수. 곧 다시 나올 그림을 다시 읽지 않기 위한 여유다.</param>
        public KeywordSpriteCache(MonoBehaviour runner, int idleCapacity)
        {
            _runner = runner;
            _idleCapacity = Mathf.Max(0, idleCapacity);
        }

        /// <summary>이 그림을 쓰겠다고 알린다. 아직 없으면 읽기가 시작된다. 준비됐는지는 TryGet 으로 본다.</summary>
        public void Acquire(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            if (!_entries.TryGetValue(path, out var entry))
            {
                entry = new Entry();
                _entries[path] = entry;
            }

            entry.users++;

            if (entry.texture == null && !entry.loading && !entry.failed)
            {
                entry.loading = true;
                if (_runner != null) _runner.StartCoroutine(Load(path, entry));
                else entry.failed = true;
            }
        }

        /// <summary>더 쓰지 않는다. 마지막 사용자가 놓으면 버릴 후보가 된다.</summary>
        public void Release(string path)
        {
            if (string.IsNullOrEmpty(path) || !_entries.TryGetValue(path, out var entry)) return;

            entry.users = Mathf.Max(0, entry.users - 1);
            if (entry.users == 0)
            {
                entry.lastReleasedTime = Time.unscaledTime;
                Trim();
            }
        }

        /// <summary>텍스처가 준비됐으면 true. 읽는 중이면 false 이고, 실패한 파일은 failed 로 알린다.</summary>
        public bool TryGet(string path, out Texture2D texture, out bool failed)
        {
            texture = null;
            failed = false;

            if (string.IsNullOrEmpty(path) || !_entries.TryGetValue(path, out var entry)) return false;

            failed = entry.failed;
            if (entry.texture == null) return false;

            texture = entry.texture;
            return true;
        }

        IEnumerator Load(string path, Entry entry)
        {
            string url;
            try
            {
                // 한글 파일 이름이 흔하다. Uri 가 퍼센트 인코딩까지 해 준다.
                url = new Uri(path).AbsoluteUri;
            }
            catch (Exception e)
            {
                Fail(entry, path, e.Message);
                yield break;
            }

            using (var request = UnityWebRequestTexture.GetTexture(url, nonReadable: true))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Fail(entry, path, request.error);
                    yield break;
                }

                var texture = DownloadHandlerTexture.GetContent(request);
                if (texture == null)
                {
                    Fail(entry, path, "이미지를 해석하지 못했습니다");
                    yield break;
                }

                // 읽는 사이에 버려진 항목이면 아무도 참조하지 않는 텍스처가 남는다.
                if (!_entries.TryGetValue(path, out var current) || current != entry)
                {
                    UnityEngine.Object.Destroy(texture);
                    entry.loading = false;
                    yield break;
                }

                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;

                entry.texture = texture;
                entry.loading = false;

                // 읽는 동안 이미 놓였을 수 있다(수명이 짧거나 로딩이 느린 경우).
                if (entry.users == 0)
                {
                    entry.lastReleasedTime = Time.unscaledTime;
                    Trim();
                }
            }
        }

        void Fail(Entry entry, string path, string reason)
        {
            entry.loading = false;
            entry.failed = true;

            Debug.LogError($"[Keyword] 스프라이트를 읽지 못했습니다: {path}\n{reason}");
        }

        /// <summary>쓰이지 않는 텍스처가 여유분을 넘으면 오래 놓여 있던 것부터 버린다. 쓰이는 중인 것은 건드리지 않는다.</summary>
        void Trim()
        {
            while (true)
            {
                int idle = 0;
                string oldestKey = null;
                float oldestTime = float.MaxValue;

                foreach (var pair in _entries)
                {
                    var entry = pair.Value;
                    if (entry.users > 0 || entry.loading || entry.texture == null) continue;

                    idle++;
                    if (entry.lastReleasedTime < oldestTime)
                    {
                        oldestTime = entry.lastReleasedTime;
                        oldestKey = pair.Key;
                    }
                }

                if (idle <= _idleCapacity || oldestKey == null) return;

                var victim = _entries[oldestKey];
                UnityEngine.Object.Destroy(victim.texture);
                victim.texture = null;
                _entries.Remove(oldestKey);
            }
        }

        /// <summary>올려 둔 텍스처를 모두 버린다. 월이 내려갈 때 부른다.</summary>
        public void ReleaseAll()
        {
            foreach (var pair in _entries)
            {
                if (pair.Value.texture != null) UnityEngine.Object.Destroy(pair.Value.texture);
                pair.Value.texture = null;
            }
            _entries.Clear();
        }
    }
}
