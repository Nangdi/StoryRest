using System.Collections.Generic;
using RenderHeads.Media.AVProVideo;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 콘텐츠 영상을 재생한다. Unity 기본 VideoPlayer 대신 AVPro Video 를 쓴다.
    ///
    /// 영상마다 플레이어를 상시 열어두면 디코딩 비용이 파일 수에 비례해 늘어난다.
    /// 층당 PC 한 대가 최대 4화면 + 카메라 2대를 함께 감당해야 하므로(→ PROJECT_SPEC §2)
    /// 정해진 개수만 만들어 두고 보이는 영상에게 빌려주는 방식으로 상한을 건다.
    ///
    /// 슬롯은 마커가 아니라 **파일 경로**로 빌려준다. 한 마커가 영상을 여러 개 가질 수 있기 때문이다.
    ///
    /// 재생 정책(→ PROJECT_SPEC §5): 마커가 보이는 동안 루프. 놓치면 멈춰 두고,
    /// resumeGrace 안에 돌아오면 그 자리에서 이어서, 넘기면 되감아 다음엔 처음부터.
    /// </summary>
    public class ArUcoContentLibrary
    {
        class Slot
        {
            public MediaPlayer player;

            // 지금 이 슬롯을 쓰는 영상 경로. null 이면 비어 있다.
            public string path;

            // 마지막으로 요청된 시각. 슬롯이 모자랄 때 회수 대상을 고르는 데 쓴다.
            public float lastRequestedTime;

            // 현재 열려 있는 파일. 같은 파일을 다시 빌려줄 때는 여는 과정을 건너뛴다.
            public string openedPath;

            public bool InUse => path != null;
        }

        readonly List<Slot> _slots = new List<Slot>();
        readonly Dictionary<string, Slot> _byPath = new Dictionary<string, Slot>();

        readonly Transform _root;
        float _resumeGrace;

        /// <summary>
        /// 마커가 사라진 뒤 슬롯을 붙잡아 두는 시간(→ ArUcoConfig.viewResumeGraceSeconds).
        /// 그 안에 돌아오면 멈춰 있던 자리에서 이어서 돌고, 넘기면 되감아 다음 관람은 처음부터 본다.
        /// </summary>
        public float ResumeGraceSeconds
        {
            get => _resumeGrace;
            set => _resumeGrace = Mathf.Max(0.1f, value);
        }

        public ArUcoContentLibrary(Transform root, int maxConcurrent, float resumeGraceSeconds)
        {
            _root = root;
            ResumeGraceSeconds = resumeGraceSeconds;

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
        /// 이 영상의 이번 프레임 텍스처를 얻는다.
        /// 슬롯이 모자라거나 아직 디코딩 준비 전이면 false 를 돌린다.
        /// </summary>
        public bool TryGetFrame(string path, out Texture texture, out bool flipV, out float aspect)
        {
            texture = null;
            flipV = false;
            aspect = 1f;

            if (string.IsNullOrEmpty(path)) return false;

            var slot = Acquire(path);
            if (slot == null) return false;

            slot.lastRequestedTime = Time.unscaledTime;

            var producer = slot.player.TextureProducer;
            if (producer == null) return false;

            texture = producer.GetTexture();
            if (texture == null || texture.width <= 0 || texture.height <= 0) return false;

            flipV = producer.RequiresVerticalFlip();
            aspect = (float)texture.height / texture.width;

            // 열자마자 자동 재생하지 않고 준비가 끝난 뒤에 시작한다.
            // 마커를 놓쳐 EndFrame 이 멈춰 둔 영상도 여기서 그 자리부터 다시 돈다.
            var control = slot.player.Control;
            if (control != null && control.CanPlay() && !control.IsPlaying()) control.Play();

            return true;
        }

        Slot Acquire(string path)
        {
            if (_byPath.TryGetValue(path, out var existing)) return existing;

            var slot = FindFreeSlot();
            if (slot == null) return null;

            slot.path = path;
            _byPath[path] = slot;

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
                    Debug.LogError($"[ArUco] 영상을 열지 못했습니다: {path}");

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
        /// 프레임 끝에 호출한다. 이번 프레임에 그려지지 않은 영상은 멈춰 두고,
        /// resumeGrace 를 넘긴 것은 되감아 슬롯을 돌려준다(→ SPEC §5 재생 정책).
        /// </summary>
        public void EndFrame()
        {
            float now = Time.unscaledTime;

            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (!slot.InUse) continue;

                float idle = now - slot.lastRequestedTime;
                if (idle < Mathf.Epsilon) continue;   // 이번 프레임에 그려졌다

                if (idle > _resumeGrace)
                {
                    Release(slot);
                    continue;
                }

                // 마커를 놓쳐 콘텐츠가 숨은 동안이다. 돌아오면 이 자리에서 이어 봐야 하므로 멈춰만 둔다.
                var control = slot.player.Control;
                if (control != null && control.IsPlaying()) control.Pause();
            }
        }

        void Release(Slot slot)
        {
            if (slot.path != null) _byPath.Remove(slot.path);
            slot.path = null;

            // 파일은 닫지 않는다. 같은 마커가 다시 올라올 때 여는 비용을 아끼기 위해서다.
            // 다음 재생이 처음부터 시작되도록 되감아 둔다(→ PROJECT_SPEC §5 재생 정책).
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
                slot.path = null;
                slot.openedPath = null;
                slot.player.CloseMedia();
            }
            _byPath.Clear();
        }
    }
}
