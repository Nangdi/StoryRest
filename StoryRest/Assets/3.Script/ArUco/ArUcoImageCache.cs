using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 콘텐츠 이미지를 읽어 텍스처로 들고 있는다. 세트들이 함께 쓴다.
    ///
    /// 이미지는 읽기 전용이라 세트끼리 공유해도 독립성이 깨지지 않는다(→ SPEC §2 는 카메라·마커
    /// **상태**를 공유하지 말라는 것이다). 같은 파일을 세트 수만큼 올리면 메모리만 배로 든다.
    ///
    /// 읽기는 비동기다. File.ReadAllBytes + LoadImage 는 디코딩이 메인 스레드에서 돌아
    /// 마커가 처음 잡히는 순간 화면이 눈에 띄게 끊긴다. UnityWebRequestTexture 는 디코딩을
    /// 워커 스레드에서 하므로, 준비될 때까지 자리표시 색판을 띄우고 다음 프레임에 바꿔 끼운다.
    /// </summary>
    public class ArUcoImageCache
    {
        // 이 크기를 넘으면 투사에 쓰지 않을 화소까지 메모리에 올린다. 사진 원본을 그대로
        // 넣는 실수가 흔해 시작할 때 짚어 준다(4096x4096 = 64MB).
        const int WarnPixelSize = 4096;

        class Entry
        {
            public Texture2D texture;

            // 마지막으로 요청된 시각. 캐시가 넘칠 때 버릴 것을 고르는 데 쓴다.
            public float lastRequestedTime;

            public bool loading;

            // 읽기에 실패한 파일. 매 프레임 다시 시도하면 로그만 쌓이므로 한 번만 알린다.
            public bool failed;
        }

        readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        readonly MonoBehaviour _runner;
        readonly int _capacity;

        /// <param name="runner">코루틴을 돌릴 주체. 앱이 살아 있는 동안 유지되는 것이어야 한다.</param>
        /// <param name="capacity">메모리에 올려 둘 이미지 수(→ ArUcoConfig.maxCachedImages)</param>
        public ArUcoImageCache(MonoBehaviour runner, int capacity)
        {
            _runner = runner;
            _capacity = Mathf.Max(1, capacity);
        }

        /// <summary>
        /// 이 이미지의 텍스처를 얻는다. 아직 읽는 중이거나 읽기에 실패했으면 false 를 돌린다.
        /// 처음 불린 파일은 여기서 읽기가 시작된다.
        /// </summary>
        public bool TryGet(string path, out Texture texture, out float aspect)
        {
            texture = null;
            aspect = 1f;

            if (string.IsNullOrEmpty(path)) return false;

            if (!_entries.TryGetValue(path, out var entry))
            {
                entry = new Entry { loading = true };
                _entries[path] = entry;

                // 코루틴을 돌릴 수 없으면(테스트 등) 조용히 비어 있는 상태로 둔다.
                if (_runner != null) _runner.StartCoroutine(Load(path, entry));
                else entry.failed = true;
            }

            entry.lastRequestedTime = Time.unscaledTime;

            if (entry.failed) return false;

            // 캐시에서 버려진 뒤(Destroy) 다시 요청된 경우다. 한 번 더 읽는다.
            if (!entry.loading && entry.texture == null)
            {
                entry.loading = true;
                if (_runner != null) _runner.StartCoroutine(Load(path, entry));
                return false;
            }

            if (entry.texture == null) return false;

            texture = entry.texture;
            aspect = (float)entry.texture.height / entry.texture.width;
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

            // 화소를 다시 읽을 일이 없으므로 CPU 쪽 사본을 만들지 않는다(메모리 절반).
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

                // 읽는 동안 F5(콘텐츠 다시읽기)가 눌리면 이 항목은 이미 캐시에서 빠져 있다.
                // 그대로 들고 있으면 아무도 참조하지 않는 텍스처가 메모리에 남는다.
                if (!_entries.TryGetValue(path, out var current) || current != entry)
                {
                    UnityEngine.Object.Destroy(texture);
                    entry.loading = false;
                    yield break;
                }

                // UV 가 0~1 을 넘지 않지만, 가장자리에서 반대편 화소가 배어 나오지 않게 막아 둔다.
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;

                entry.texture = texture;
                entry.loading = false;

                if (texture.width > WarnPixelSize || texture.height > WarnPixelSize)
                {
                    Debug.LogWarning($"[ArUco] 이미지가 큽니다({texture.width}x{texture.height}): " +
                                     $"{System.IO.Path.GetFileName(path)}\n" +
                                     $"투사 크기에 맞춰 줄여 넣으면 메모리와 로딩 시간이 크게 줄어듭니다.");
                }
            }

            Trim();
        }

        void Fail(Entry entry, string path, string reason)
        {
            entry.loading = false;
            entry.failed = true;

            Debug.LogError($"[ArUco] 이미지를 읽지 못했습니다: {path}\n{reason}");
        }

        /// <summary>
        /// 캐시가 넘치면 오래 쓰이지 않은 것부터 놓아준다.
        /// 이번 프레임에 요청된 것은 건드리지 않는다 — 지금 화면에 떠 있는 그림이다.
        /// </summary>
        void Trim()
        {
            while (_entries.Count > _capacity)
            {
                string oldestKey = null;
                float oldestTime = float.MaxValue;
                float now = Time.unscaledTime;

                foreach (var pair in _entries)
                {
                    var entry = pair.Value;
                    if (entry.loading) continue;
                    if (now - entry.lastRequestedTime < Mathf.Epsilon) continue;

                    if (entry.lastRequestedTime < oldestTime)
                    {
                        oldestTime = entry.lastRequestedTime;
                        oldestKey = pair.Key;
                    }
                }

                // 전부 지금 쓰이는 중이면 더 버릴 것이 없다. 상한보다 조금 넘긴 채로 둔다.
                if (oldestKey == null) return;

                Destroy(_entries[oldestKey]);
                _entries.Remove(oldestKey);
            }
        }

        /// <summary>현장에서 콘텐츠 파일을 갈아끼운 뒤 호출한다. 올려 둔 텍스처를 모두 버린다.</summary>
        public void ReleaseAll()
        {
            foreach (var pair in _entries) Destroy(pair.Value);
            _entries.Clear();
        }

        static void Destroy(Entry entry)
        {
            if (entry?.texture == null) return;

            UnityEngine.Object.Destroy(entry.texture);
            entry.texture = null;
        }
    }
}
