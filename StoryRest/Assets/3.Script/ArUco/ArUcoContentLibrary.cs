using System.Collections.Generic;
using RenderHeads.Media.AVProVideo;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 마커에 매핑된 영상을 재생한다. Unity 기본 VideoPlayer 대신 AVPro Video 를 쓴다.
    ///
    /// 마커마다 플레이어를 상시 열어두면 디코딩 비용이 마커 수에 비례해 늘어난다.
    /// 층당 PC 한 대가 최대 4화면 + 카메라 2대를 함께 감당해야 하므로(→ PROJECT_SPEC §2)
    /// 정해진 개수만 만들어 두고 보이는 마커에게 빌려주는 방식으로 상한을 건다.
    ///
    /// 재생 정책: 마커가 보이는 동안 루프, 사라지면 정지, 다시 잡히면 처음부터.
    /// </summary>
    public class ArUcoContentLibrary
    {
        class Slot
        {
            public MediaPlayer player;

            // 지금 이 슬롯을 쓰는 마커. -1 이면 비어 있다.
            public int markerId = -1;

            // 마지막으로 요청된 시각. 슬롯이 모자랄 때 회수 대상을 고르는 데 쓴다.
            public float lastRequestedTime;

            // 현재 열려 있는 파일. 같은 파일을 다시 빌려줄 때는 여는 과정을 건너뛴다.
            public string openedPath;

            public bool InUse => markerId >= 0;
        }

        readonly List<Slot> _slots = new List<Slot>();
        readonly Dictionary<int, Slot> _byMarker = new Dictionary<int, Slot>();

        readonly ArUcoContentIndex _index;
        readonly Transform _root;
        readonly float _releaseDelay;

        /// <param name="releaseDelay">
        /// 마커가 사라진 뒤 슬롯을 붙잡아 두는 시간. 손이 스쳐 잠깐 놓쳤을 때
        /// 영상을 껐다 켜며 깜빡이는 것을 막는다. holdSeconds 보다 넉넉히 잡는다.
        /// </param>
        public ArUcoContentLibrary(ArUcoContentIndex index, Transform root, int maxConcurrent, float releaseDelay)
        {
            _index = index;
            _root = root;
            _releaseDelay = Mathf.Max(0.1f, releaseDelay);

            int count = Mathf.Max(1, maxConcurrent);
            for (int i = 0; i < count; i++) _slots.Add(CreateSlot(i));
        }

        Slot CreateSlot(int index)
        {
            var go = new GameObject($"MediaPlayer{index}");
            go.transform.SetParent(_root, false);

            // 비활성 상태에서 컴포넌트를 붙여야 Awake 전에 설정을 넣을 수 있다.
            // (활성 상태로 붙이면 Awake 가 즉시 돌면서 빈 경로를 열려고 한다.)
            go.SetActive(false);

            var player = go.AddComponent<MediaPlayer>();
            player.AutoOpen = false;
            player.AutoStart = false;
            player.Loop = true;

            // 콘텐츠 영상에는 소리가 없다(→ PROJECT_SPEC §4).
            // 그래도 여러 슬롯이 동시에 소리를 낼 여지를 아예 없애 둔다.
            player.AudioMuted = true;
            player.AudioVolume = 0f;

            go.SetActive(true);

            return new Slot { player = player };
        }

        /// <summary>
        /// 이 마커의 이번 프레임 영상 텍스처를 얻는다.
        /// 콘텐츠가 없거나, 슬롯이 모자라거나, 아직 디코딩 준비 전이면 false 를 돌린다.
        /// </summary>
        public bool TryGetFrame(int markerId, out Texture texture, out bool flipV, out float aspect)
        {
            texture = null;
            flipV = false;
            aspect = 1f;

            if (_index == null || !_index.TryGetPath(markerId, out string path)) return false;

            var slot = Acquire(markerId, path);
            if (slot == null) return false;

            slot.lastRequestedTime = Time.unscaledTime;

            var producer = slot.player.TextureProducer;
            if (producer == null) return false;

            texture = producer.GetTexture();
            if (texture == null || texture.width <= 0 || texture.height <= 0) return false;

            flipV = producer.RequiresVerticalFlip();
            aspect = (float)texture.height / texture.width;

            // 열자마자 자동 재생하지 않고, 준비가 끝난 뒤에 시작한다.
            var control = slot.player.Control;
            if (control != null && control.CanPlay() && !control.IsPlaying()) control.Play();

            return true;
        }

        Slot Acquire(int markerId, string path)
        {
            if (_byMarker.TryGetValue(markerId, out var existing)) return existing;

            var slot = FindFreeSlot();
            if (slot == null) return null;

            slot.markerId = markerId;
            _byMarker[markerId] = slot;

            if (slot.openedPath == path)
            {
                // 같은 파일을 다시 빌려준다. 여는 비용 없이 처음부터 재생한다.
                slot.player.Control?.Rewind();
                slot.player.Control?.Play();
            }
            else
            {
                slot.openedPath = path;
                if (!slot.player.OpenMedia(MediaPathType.AbsolutePathOrURL, path, autoPlay: true))
                {
                    Debug.LogError($"[ArUco] {markerId}번 마커의 영상을 열지 못했습니다: {path}");

                    // 열기에 실패한 경로를 기억해 두면 다음에도 같은 실패를 반복한다.
                    slot.openedPath = null;
                    Release(slot);
                    return null;
                }
            }

            return slot;
        }

        Slot FindFreeSlot()
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (!_slots[i].InUse) return _slots[i];
            }

            // 모두 사용 중이면 가장 오래 요청되지 않은 것을 회수한다.
            // 단 이번 프레임에 쓰인 슬롯은 건드리지 않는다(서로 뺏느라 깜빡이게 된다).
            Slot oldest = null;
            float now = Time.unscaledTime;

            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (now - slot.lastRequestedTime < Mathf.Epsilon) continue;
                if (oldest == null || slot.lastRequestedTime < oldest.lastRequestedTime) oldest = slot;
            }

            if (oldest == null) return null;

            Release(oldest);
            return oldest;
        }

        /// <summary>
        /// 프레임 끝에 호출한다. 한동안 요청되지 않은 슬롯을 정지시켜 디코딩 부하를 돌려준다.
        /// </summary>
        public void EndFrame()
        {
            float now = Time.unscaledTime;

            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (!slot.InUse) continue;
                if (now - slot.lastRequestedTime < _releaseDelay) continue;

                Release(slot);
            }
        }

        void Release(Slot slot)
        {
            if (slot.markerId >= 0) _byMarker.Remove(slot.markerId);
            slot.markerId = -1;

            // 파일은 닫지 않는다. 같은 마커가 다시 올라올 때 여는 비용을 아끼기 위해서다.
            // 다음 재생이 처음부터 시작되도록 되감아 둔다(→ PROJECT_SPEC §7 재생 정책).
            var control = slot.player.Control;
            if (control == null) return;

            control.Pause();
            control.Rewind();
        }

        /// <summary>현장에서 콘텐츠 파일을 갈아끼운 뒤 호출한다. 열려 있던 파일을 모두 닫는다.</summary>
        public void ReleaseAll()
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                slot.markerId = -1;
                slot.openedPath = null;
                slot.player.CloseMedia();
            }
            _byMarker.Clear();
        }
    }
}
