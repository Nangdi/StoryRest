using System;
using System.Collections.Generic;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>콘텐츠가 나타나는 방식. aruco.json 의 appear.effect 문자열과 1:1 이다.</summary>
    public enum AppearEffect
    {
        None,       // 연출 없음 — 잡히는 즉시 뜬다
        Grow,       // 마커에서 솟아나옴
        Fade,       // 페이드
        TopReveal,  // 제자리에서 위에서부터 서서히 드러남
        Spiral,     // 제자리에서 회오리(나선) 모양으로 드러남
        Wave,       // 물결치며 펴짐
    }

    /// <summary>ArUcoReveal 셰이더의 _Mode 값. 콘텐츠를 제자리에 둔 채 알파만 드러내는 연출이 쓴다.</summary>
    public enum RevealMode { None = 0, Top = 1, Spiral = 2 }

    /// <summary>
    /// 등장 연출 설정(→ aruco.json `appear`, ARCHITECTURE §5 등장 연출).
    ///
    /// 흐름: 마커가 잡히면 곧바로 띄우지 않는다. 사람이 책을 내려놓는 동안(움직이는 동안)은 기다렸다가
    /// settleSeconds 동안 멈춰 있으면 "놓았다" 고 보고, recognizeSeconds 동안 마커 자리에서 빛 고리가
    /// 퍼져 나간 뒤(읽는 중) appearSeconds 에 걸쳐 콘텐츠가 effect 로 나타난다.
    /// 퇴장 연출은 없다 — 놓치면 holdSeconds 동안 그대로 있다가 꺼진다(추적기의 hold 와 같다).
    /// </summary>
    [Serializable]
    public class AppearConfig
    {
        public bool enabled = true;

        // grow | fade | topReveal | spiral | wave | none
        public string effect = "grow";

        // 읽는 중 표시(빛 고리)를 띄울지.
        public bool ring = true;

        // 읽기 시작 → 콘텐츠 등장까지. 고리 하나가 이 시간에 걸쳐 퍼진다.
        public float recognizeSeconds = 1.5f;

        // 콘텐츠 등장에 걸리는 시간.
        public float appearSeconds = 1.6f;

        // 마커 속도(마커 한 변/초)가 이보다 빠르면 "움직이는 중" 으로 보고 읽지 않는다.
        public float moveThreshold = 0.6f;

        // 이 시간 동안 느리면 놓은 것으로 보고 읽기 시작한다.
        public float settleSeconds = 0.35f;

        // topReveal / spiral 의 경계가 부드러운 폭(마스크 값 단위, 0~0.5).
        public float revealSoft = 0.12f;

        // spiral 이 감기는 바퀴 수.
        public float spiralTurns = 2f;

        public AppearEffect Effect => TryParseEffect(effect, out var e) ? e : AppearEffect.Grow;

        public static readonly string[] EffectNames = { "none", "grow", "fade", "topReveal", "spiral", "wave" };

        public static bool TryParseEffect(string text, out AppearEffect effect)
        {
            effect = AppearEffect.Grow;
            if (string.IsNullOrWhiteSpace(text)) return false;
            for (int i = 0; i < EffectNames.Length; i++)
            {
                if (string.Equals(EffectNames[i], text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    effect = (AppearEffect)i;
                    return true;
                }
            }
            return false;
        }

        public void SetEffect(AppearEffect e) => effect = EffectNames[(int)e];

        public void Validate()
        {
            if (!TryParseEffect(effect, out _)) effect = "grow";
            recognizeSeconds = Mathf.Clamp(recognizeSeconds, 0f, 10f);
            appearSeconds = Mathf.Clamp(appearSeconds, 0.05f, 10f);
            moveThreshold = Mathf.Clamp(moveThreshold, 0.05f, 10f);
            settleSeconds = Mathf.Clamp(settleSeconds, 0f, 5f);
            revealSoft = Mathf.Clamp(revealSoft, 0.005f, 0.5f);
            spiralTurns = Mathf.Clamp(spiralTurns, 0.5f, 8f);
        }
    }

    /// <summary>마커 하나의 이번 프레임 연출 상태. ArUcoAppearTracker.TryGet 이 채운다.</summary>
    public struct AppearFrame
    {
        /// <summary>콘텐츠를 그릴지. 움직이는 중 · 멈춤 판정 중 · 읽는 중이면 false.</summary>
        public bool drawContent;

        /// <summary>빛 고리 진행도 0~1. 음수면 안 그린다.</summary>
        public float ringT;

        /// <summary>등장 진행도 0~1. 다 뜨면 1 에 머문다.</summary>
        public float appearT;

    }

    /// <summary>
    /// 마커마다 "움직이는 중 → 멈춤 판정 → 읽는 중 → 뜸" 을 따라가며 프레임마다 연출 진행도를 내놓는다.
    /// 시간은 밖에서 넣는다(Update 의 now) — 시연 씬이 시계를 멈추고 스크럽할 수 있어야 한다.
    ///
    /// 놓침은 두 단계로 본다. holdSeconds 안(추적기가 visible=false 로 붙잡아 두는 동안)은 그대로 그리고,
    /// 추적기에서 아예 빠진 뒤에도 graceSeconds 동안은 상태를 기억한다 — 그 안에 돌아오면 같은 관람이므로
    /// 다시 연출하지 않고 바로 뜬다(영상이 멈춘 자리에서 이어지는 것과 같은 규칙, → ArUcoViewCounter).
    /// </summary>
    public class ArUcoAppearTracker
    {
        public enum Phase { Settling, Reading, Shown }

        class State
        {
            public Phase phase;
            public bool hasPrev;
            public Vector2 prevCenter;
            public float lastMovedAt;    // 마지막으로 빠르게 움직인 시각
            public float settledSince;   // 느려진 시각(멈춤 판정의 시작)
            public float readingAt;      // 읽기 시작한 시각
            public float lastVisibleAt;  // 마지막으로 실제로 보인 시각
            public float lastListedAt;   // 마지막으로 추적기 목록에 있던 시각(유예의 기준)
            public bool listed;          // 이번 프레임 목록에 있었나
            public bool listedPrev;      // 지난 프레임 목록에 있었나
        }

        readonly Dictionary<int, State> _states = new Dictionary<int, State>();
        readonly List<int> _expired = new List<int>();

        float _lastNow = float.NaN;

        public AppearConfig Config { get; set; }

        /// <summary>목록에서 빠진 뒤 상태를 기억하는 시간(→ ArUcoConfig.viewResumeGraceSeconds).</summary>
        public float GraceSeconds { get; set; } = 20f;

        public ArUcoAppearTracker(AppearConfig config)
        {
            Config = config;
        }

        public void Reset()
        {
            _states.Clear();
            _lastNow = float.NaN;
        }

        /// <summary>이 마커를 처음 보는 것으로 되돌린다(시연에서 다시 재생할 때).</summary>
        public void Forget(int id) => _states.Remove(id);

        public Phase? GetPhase(int id) => _states.TryGetValue(id, out var s) ? s.phase : (Phase?)null;

        /// <summary>프레임마다 한 번. 추적기의 마커 목록을 그대로 넘긴다(visible=false 인 hold 상태 포함).</summary>
        public void Update(IReadOnlyList<ArUcoMarkerTracker.TrackedMarker> markers, float now)
        {
            float dt = float.IsNaN(_lastNow) ? 0f : now - _lastNow;
            _lastNow = now;

            foreach (var s in _states.Values)
            {
                s.listedPrev = s.listed;
                s.listed = false;
            }

            for (int i = 0; i < markers.Count; i++)
            {
                var m = markers[i];
                if (!_states.TryGetValue(m.id, out var s))
                {
                    s = new State { phase = Phase.Settling, settledSince = now, lastMovedAt = -1f, lastVisibleAt = now };
                    _states[m.id] = s;
                }

                // 목록에서 빠졌다 돌아온 마커: 이미 떠 있던 것은 그대로 뜨고(같은 관람),
                // 읽던 중이었으면 놓은 순간부터 다시 시작한다.
                if (!s.listedPrev && s.phase != Phase.Shown)
                {
                    s.phase = Phase.Settling;
                    s.settledSince = now;
                    s.hasPrev = false;
                }

                s.listed = true;
                s.lastListedAt = now;

                if (!m.visible)
                {
                    // hold 중 — 자리를 알 수 없으니 속도는 재지 않는다. 다음에 보이면 이어서 잰다.
                    s.hasPrev = false;
                    continue;
                }

                s.lastVisibleAt = now;
                Track(s, m, now, dt);
            }

            // 목록에서 빠진 마커는 유예 동안만 기억한다.
            _expired.Clear();
            foreach (var kv in _states)
            {
                if (!kv.Value.listed && now - kv.Value.lastListedAt > GraceSeconds) _expired.Add(kv.Key);
            }
            for (int i = 0; i < _expired.Count; i++) _states.Remove(_expired[i]);
        }

        void Track(State s, in ArUcoMarkerTracker.TrackedMarker m, float now, float dt)
        {
            var cfg = Config;

            float speed = 0f;
            if (s.hasPrev && dt > 0f && m.sizePx > 1f)
                speed = (m.center - s.prevCenter).magnitude / dt / m.sizePx;
            s.prevCenter = m.center;
            s.hasPrev = true;

            bool moving = cfg != null && cfg.enabled && speed > cfg.moveThreshold;

            switch (s.phase)
            {
                case Phase.Settling:
                    if (moving)
                    {
                        s.lastMovedAt = now;
                        s.settledSince = now;
                    }
                    else if (cfg == null || !cfg.enabled || now - s.settledSince >= cfg.settleSeconds)
                    {
                        s.phase = Phase.Reading;
                        s.readingAt = now;
                    }
                    break;

                case Phase.Reading:
                    if (moving)
                    {
                        // 읽는 중에 다시 움직이면 무효. 멈춘 뒤 처음부터 읽는다.
                        s.phase = Phase.Settling;
                        s.lastMovedAt = now;
                        s.settledSince = now;
                    }
                    else if (cfg == null || !cfg.enabled || now - s.readingAt >= cfg.recognizeSeconds)
                    {
                        s.phase = Phase.Shown;
                    }
                    break;

                case Phase.Shown:
                    // 뜬 뒤에는 움직여도 따라만 간다 — 매 프레임 마커 평면 위에 그리므로 따로 할 일이 없다.
                    break;
            }
        }

        /// <summary>
        /// 이 마커의 이번 프레임 연출 상태. 추적기 목록에 있는 마커만 넘긴다(그 외는 false).
        /// </summary>
        public bool TryGet(in ArUcoMarkerTracker.TrackedMarker m, float now, out AppearFrame frame)
        {
            frame = new AppearFrame { drawContent = false, ringT = -1f, appearT = 1f };

            if (!_states.TryGetValue(m.id, out var s)) return false;

            var cfg = Config;
            if (cfg == null || !cfg.enabled)
            {
                frame.drawContent = true;
                return true;
            }

            switch (s.phase)
            {
                case Phase.Settling:
                    return true;   // 아무것도 그리지 않는다

                case Phase.Reading:
                    if (cfg.ring && m.visible)
                        frame.ringT = Mathf.Clamp01((now - s.readingAt) / Mathf.Max(0.01f, cfg.recognizeSeconds));
                    return true;

                case Phase.Shown:
                    frame.drawContent = true;
                    frame.appearT = Mathf.Clamp01((now - s.readingAt - cfg.recognizeSeconds) / cfg.appearSeconds);
                    return true;
            }

            return true;
        }

        // ───────────────────────────── 연출 자체 ─────────────────────────────

        /// <summary>
        /// 등장 진행도 t(0→1)에 따라 배치값과 그리기 스타일을 바꾼다. 여기가 연출의 전부다.
        /// "마커에서 나온다" 류는 마커 중심 기준 최종 위치(offset·globalOffset)를 0 에서 키운다.
        /// 제자리 연출(topReveal/spiral)은 style 의 revealMode/reveal 만 채우고 셰이더가 알파를 깎는다.
        /// </summary>
        public static void Apply(AppearEffect effect, AppearConfig cfg, float t,
                                 ref ArUcoPlacement p, ref ArUcoDrawStyle style)
        {
            t = Mathf.Clamp01(t);
            if (t >= 1f) return;

            switch (effect)
            {
                case AppearEffect.Grow:
                {
                    // 마커 중심에서 최종 자리까지 커지며 밀려 나온다. 살짝 넘쳤다 돌아오는 back-out.
                    float s = EaseOutBack(t);
                    p.scale *= s;
                    p.offsetX *= s;
                    p.offsetY *= s;
                    style.globalOffset *= s;
                    style.tint.a *= Mathf.Clamp01(t * 3f);
                    break;
                }
                case AppearEffect.Fade:
                {
                    style.tint.a *= EaseOutQuad(t);
                    break;
                }
                case AppearEffect.TopReveal:
                {
                    // 영상은 제자리. 위 가장자리부터 아래로 커튼을 걷듯 드러난다.
                    style.revealMode = RevealMode.Top;
                    style.reveal = EaseInOutQuad(t);
                    style.revealSoft = cfg != null ? cfg.revealSoft : 0.12f;
                    break;
                }
                case AppearEffect.Spiral:
                {
                    // 영상은 제자리. 가운데서 바깥으로 나선을 그리며 드러난다. 속도가 일정해야 감기는 느낌이 산다.
                    style.revealMode = RevealMode.Spiral;
                    style.reveal = t;
                    style.revealSoft = cfg != null ? cfg.revealSoft : 0.12f;
                    style.spiralTurns = cfg != null ? cfg.spiralTurns : 2f;
                    break;
                }
                case AppearEffect.Wave:
                {
                    // 종이가 펴지듯 물결이 잦아든다. 격자를 쓰므로 warpSubdivisions 가 1 이면 티가 안 난다.
                    float s = EaseOutCubic(t);
                    p.scale *= Mathf.Lerp(0.9f, 1f, s);
                    style.wave = (1f - t) * (1f - t) * 0.22f;
                    style.tint.a *= Mathf.Clamp01(t * 2f);
                    break;
                }
            }
        }

        /// <summary>빛 고리의 크기(마커 한 변 = 1)와 알파. 마커보다 조금 작은 데서 시작해 두 배 남짓까지 퍼지며 사라진다.</summary>
        public static void Ring(float t, out float scale, out float alpha)
        {
            t = Mathf.Clamp01(t);
            scale = Mathf.Lerp(0.7f, 2.6f, EaseOutCubic(t));
            alpha = 1f - EaseInQuad(t);
        }

        public static float EaseOutQuad(float t) => 1f - (1f - t) * (1f - t);
        public static float EaseInQuad(float t) => t * t;
        public static float EaseInOutQuad(float t) => t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) * 0.5f;
        public static float EaseOutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);

        public static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }
    }

    /// <summary>빛 고리 텍스처. 가운데가 비고 테두리가 부드럽게 빛나며 밖으로 여운이 남는다. 한 번만 만든다.</summary>
    public static class ArUcoRingTexture
    {
        static Texture2D _texture;

        public static Texture2D Get()
        {
            if (_texture != null) return _texture;

            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ArUcoRing",
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            var px = new Color32[size * size];
            float c = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;    // 0~1.41
                float band = Mathf.Exp(-Mathf.Pow((r - 0.42f) / 0.05f, 2f));         // 선명한 고리
                float glow = Mathf.Exp(-Mathf.Pow((r - 0.42f) / 0.16f, 2f)) * 0.35f; // 번짐
                float a = Mathf.Clamp01(band + glow);
                if (r > 0.98f) a = 0f;
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            _texture = tex;
            return tex;
        }
    }
}
