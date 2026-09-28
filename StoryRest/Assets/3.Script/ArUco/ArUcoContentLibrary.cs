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
    /// 끝난 뒤 처음이 아니라 반복 구간 시작(기본 videoLoopStartSeconds, 파일 이름 "_loop초" 가 우선)으로 돌아간다(도입부는 한 번만).
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

            // 끝나면 돌아갈 시각(초). 0 이면 AVPro 루프에 맡겨 처음으로 돌아간다(→ SPEC §5).
            public float loopStart;

            // 반복 구간 처리용(→ KeepInLoop).
            public double lastTime;          // 직전 재생 위치. 0초로 감긴 것을 알아채는 데 쓴다
            public bool loopSeeking;         // 멈춰 두고 반복 구간으로 Seek 하는 중. 끝나면 다시 튼다
            public double loopSeekTarget;
            public float loopSeekStarted;
            public bool loggedLoop;

            // Seek 동안 텍스처가 잠깐 비는 경우에 대비해 마지막으로 받은 것을 들고 있는다.
            public Texture lastTexture;
            public bool lastFlipV;

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
        /// loopStart 는 끝난 뒤 돌아갈 시각(초, → ContentEntry.loopStart). 0 이면 처음으로.
        /// </summary>
        public bool TryGetFrame(string path, float loopStart, out Texture texture, out bool flipV, out float aspect)
        {
            texture = null;
            flipV = false;
            aspect = 1f;

            if (string.IsNullOrEmpty(path)) return false;

            var slot = Acquire(path, loopStart);
            if (slot == null) return false;

            slot.lastRequestedTime = Time.unscaledTime;

            var producer = slot.player.TextureProducer;
            texture = producer?.GetTexture();

            if (texture == null || texture.width <= 0 || texture.height <= 0)
            {
                // 반복 구간으로 Seek 하는 동안 텍스처가 비면 콘텐츠가 한 프레임 꺼져 깜빡인다. 직전 프레임으로 버틴다.
                if (!slot.loopSeeking || slot.lastTexture == null) return false;

                texture = slot.lastTexture;
                flipV = slot.lastFlipV;
            }
            else
            {
                flipV = producer.RequiresVerticalFlip();
                slot.lastTexture = texture;
                slot.lastFlipV = flipV;
            }

            aspect = (float)texture.height / texture.width;

            // 열자마자 자동 재생하지 않고 준비가 끝난 뒤에 시작한다.
            // 마커를 놓쳐 EndFrame 이 멈춰 둔 영상도 여기서 그 자리부터 다시 돈다.
            // 반복 구간 Seek 중에는 KeepInLoop 가 다시 틀 때를 정하므로 건드리지 않는다.
            var control = slot.player.Control;
            if (control != null && slot.loopStart > 0f) KeepInLoop(slot, control);
            if (control != null && !slot.loopSeeking && control.CanPlay() && !control.IsPlaying()) control.Play();

            return true;
        }

        Slot Acquire(string path, float loopStart)
        {
            if (_byPath.TryGetValue(path, out var existing)) return existing;

            var slot = FindFreeSlot();
            if (slot == null) return null;

            slot.path = path;
            _byPath[path] = slot;

            // 슬롯은 다른 파일에게도 빌려주므로 빌려줄 때마다 다시 정한다.
            // AVPro 루프는 반복 구간이 있어도 켜 둔다 — 이유는 KeepInLoop.
            slot.loopStart = loopStart;
            slot.lastTime = 0.0;
            slot.loopSeeking = false;
            slot.loggedLoop = false;
            slot.lastTexture = null;

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

        /// <summary>
        /// 반복 구간이 있는 영상을 끝에서 0초가 아니라 loopStart 로 이어 붙인다.
        ///
        /// loopStart 는 "끝 프레임 바로 다음 장면" 의 시각이다(→ SPEC §5). 즉 영상 끝(duration)과 loopStart 가
        /// 같은 자리라서, 끝에서 k 프레임 앞은 loopStart 에서 k 프레임 앞과 같은 장면이다.
        ///
        /// 이렇게 잇는다.
        ///   1) 끝나기 두 프레임 전쯤 **멈춘다.** 재생 중에 Seek 하면 Seek 가 끝나기 전에 영상이 끝에 닿아
        ///      AVPro 가 0초로 감긴 첫 장면이 한 번 비친다(깜빡임). 멈춰 두면 지금 장면이 그대로 남는다.
        ///   2) 지금 보이는 프레임의 **다음 장면**에 해당하는 반복 구간 프레임으로 Seek 한다.
        ///      loopStart 로 고정해 뛰면 멈춘 자리에 따라 1~2 프레임이 겹치거나 빠진다.
        ///   3) Seek 가 끝나면 다시 튼다. 그 사이에는 반복 구간과 같은 장면이 멈춰 있을 뿐이다.
        ///
        /// AVPro 루프는 켜 둔다. 끄고 "끝나면 Seek + Play" 로 하면 Media Foundation 이 끝난 영상의 Play 를
        /// 처음부터 다시 틀기로 받아 0초로 돌아간다. 켜 둔 채 위 틈을 놓쳐 0초로 감겼으면 곧바로 같은 방법으로 되돌린다.
        /// </summary>
        void KeepInLoop(Slot slot, IMediaControl control)
        {
            var info = slot.player.Info;
            if (info == null) return;

            double time = control.GetCurrentTime();

            if (slot.loopSeeking)
            {
                float fpsNow = info.GetVideoFrameRate();
                double tolerance = 1.5 / (fpsNow > 0f ? fpsNow : 30f);
                bool landed = !control.IsSeeking() && System.Math.Abs(time - slot.loopSeekTarget) < tolerance;

                // Seek 가 끝났다는 신호를 못 받아도 영상이 멈춘 채 남지 않게 한다.
                if (landed || Time.unscaledTime - slot.loopSeekStarted > 0.5f)
                {
                    slot.loopSeeking = false;
                    slot.lastTime = time;
                    control.Play();
                }
                return;
            }

            double duration = info.GetDuration();
            double last = slot.lastTime;
            slot.lastTime = time;

            // 영상보다 긴 값을 적었으면 무시하고 AVPro 루프(0초)에 맡긴다.
            if (duration <= 0.0 || slot.loopStart >= duration) return;
            if (control.IsSeeking() || !control.IsPlaying()) return;

            float fps = info.GetVideoFrameRate();
            if (fps <= 0f) fps = 30f;

            int totalFrames = Mathf.RoundToInt((float)(duration * fps));
            int loopFrame = Mathf.RoundToInt(slot.loopStart * fps);
            int currentFrame = Mathf.FloorToInt((float)(time * fps) + 0.01f);

            // 화면이 60fps 여도 두 프레임(24fps 에서 83ms) 창이면 넉넉히 잡힌다.
            bool nearEnd = currentFrame >= totalFrames - 2;
            bool wrapped = time < slot.loopStart && last >= slot.loopStart && last - time > 0.5;
            if (!nearEnd && !wrapped) return;

            // 지금 프레임의 다음 장면. 끝 쪽이면 loopFrame 에서 남은 만큼 앞, 0초로 감겼으면 loopFrame 에서 지난 만큼 뒤.
            int targetFrame = nearEnd
                ? loopFrame - (totalFrames - currentFrame) + 1
                : loopFrame + currentFrame + 1;
            targetFrame = Mathf.Clamp(targetFrame, 0, totalFrames - 1);

            // 프레임 경계에 딱 맞추면 반올림 오차로 한 장 앞이 잡힐 수 있어 조금 안쪽을 가리킨다.
            double target = (targetFrame + 0.2) / fps;

            control.Pause();
            control.Seek(target);
            slot.loopSeeking = true;
            slot.loopSeekTarget = target;
            slot.loopSeekStarted = Time.unscaledTime;

            if (!slot.loggedLoop)
            {
                slot.loggedLoop = true;
                Debug.Log($"[ArUco] 반복 구간으로 이음 {currentFrame}→{targetFrame} 프레임 " +
                          $"({(wrapped ? "0초로 감긴 뒤" : "끝 직전")}, loopStart {slot.loopStart:0.###}s): " +
                          System.IO.Path.GetFileName(slot.path));
            }
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
            slot.lastTime = 0.0;
            slot.loopSeeking = false;
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
