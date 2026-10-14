using System;
using StoryRest.Keyword;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// aruco.json 의 "idle" — 테이블에 마커가 하나도 없을 때 띄우는 대기 안내(→ ArUcoIdleGuide).
    /// 보이는 모양(자리 · 색 · 움직임)은 프리팹이 갖고, 여기에는 현장에서 켜고 끄고 맞추는 값만 둔다.
    /// </summary>
    [Serializable]
    public class IdleConfig
    {
        public bool enabled = true;

        // 마지막 마커가 사라진 뒤 대기 안내가 뜨기까지(초). 책장을 넘기는 사이에 깜빡 뜨지 않을 만큼.
        public float delaySeconds = 3f;

        public void Validate()
        {
            delaySeconds = Mathf.Clamp(delaySeconds, 0f, 60f);
        }
    }

    /// <summary>
    /// 바닥(테이블) 대기 안내 — "알아보고 싶은 아카이브 북을 골라 테이블 위에 올려보세요".
    /// 세트 화면(ArUcoSet.prefab)의 한 레이어이고, 그 세트 카메라에 마커가 하나도 없을 때만 뜬다.
    ///
    /// 디자인은 바탕화면 `전북과_스토리쉼터-대기화면_260928.psd` 다. 같은 문구를 테이블 위아래 가장자리에 두고
    /// 위쪽은 180° 돌려 맞은편 관람객이 읽게 한다. 글자는 음절마다 흰 글씨 한 장(`4.Sprite/IdleGuide/`,
    /// `Tools/idle_export/export_idle.py`)이고, 층별 그라데이션을 여기서 입힌다 — 한 줄 전체에 걸친 그라데이션을
    /// 글자마다 그 자리에 해당하는 두 색으로 나눠 칠하므로 줄로 보면 PSD 처럼 이어진다.
    ///
    /// 움직임(한 주기 loopSeconds):
    ///   등장 — 글자가 앞에서부터 차례로 아래에서 솟으며 커진다(back-out)
    ///   머묾 — 글자들이 잔물결처럼 위아래로 흔들리고, waveEvery 마다 큰 물결이 앞에서 뒤로 지나간다
    ///   퇴장 — 앞에서부터 차례로 작아지며 사라진 뒤 다시 등장
    /// 마커가 잡히면 판 전체가 hideSeconds 동안 사라진다. 다시 대기가 되면 등장부터 시작한다.
    ///
    /// 크기는 PSD 기준 1920x1080 판(frame)을 출력 화면에 맞춰 통째로 배율한다.
    /// </summary>
    [DisallowMultipleComponent]
    public class ArUcoIdleGuide : MonoBehaviour
    {
        [Serializable]
        public class Line
        {
            [Tooltip("줄 하나. 글자 칸(Slot_NN)들의 부모. 회전(위쪽 줄 180°)은 여기서 준다.")]
            public RectTransform root;

            [Tooltip("읽는 순서대로. 각 글자는 칸(부모) 안 가운데에 있고, 움직임은 이 글자 자체에만 준다.")]
            public KeywordGradientImage[] glyphs;

            [Tooltip("이 줄의 등장 시작을 늦출 시간(초). 두 줄이 똑같이 움직이지 않게.")]
            public float delay;
        }

        [Serializable]
        public class FloorGradient
        {
            public int floor = 1;

            [Tooltip("줄 왼쪽(첫 글자) → 오른쪽(끝 글자). PSD 에서 읽은 색.")]
            public Gradient gradient = new Gradient();
        }

        [Header("화면 — 프리팹 안의 조각들")]
        [SerializeField] CanvasGroup _group;

        [Tooltip("PSD 기준 크기(referenceSize)의 판. 출력 화면에 맞춰 통째로 배율한다.")]
        [SerializeField] RectTransform _frame;

        [SerializeField] Vector2 _referenceSize = new Vector2(1920f, 1080f);
        [SerializeField] Line[] _lines;

        [Header("층별 색")]
        [SerializeField] FloorGradient[] _floorGradients;

        [Header("한 주기")]
        [SerializeField] float _loopSeconds = 18f;

        [Header("등장")]
        [SerializeField] float _introStagger = 0.06f;    // 글자 사이 간격(초)
        [SerializeField] float _introSeconds = 0.7f;     // 글자 하나가 다 나타나기까지
        [SerializeField] float _introRise = 36f;         // 아래에서 솟는 거리(px)

        [Header("머묾 — 잔물결 · 큰 물결")]
        [SerializeField] float _bobAmplitude = 4f;       // 잔물결 높이(px)
        [SerializeField] float _bobPeriod = 2.6f;        // 잔물결 한 번(초)
        [SerializeField] float _bobPhasePerGlyph = 0.45f;
        [SerializeField] float _waveEvery = 4.5f;        // 큰 물결 간격(초). 등장이 끝난 뒤부터 센다
        [SerializeField] float _waveStagger = 0.045f;
        [SerializeField] float _waveSeconds = 0.55f;
        [SerializeField] float _waveScale = 0.14f;       // 물결이 지날 때 커지는 비율
        [SerializeField] float _waveLift = 12f;          // 물결이 지날 때 솟는 높이(px)

        [Header("퇴장")]
        [SerializeField] float _outroSeconds = 0.45f;    // 글자 하나가 사라지기까지. 퇴장 간격은 등장 간격과 같다
        [SerializeField] float _outroGap = 0.6f;         // 다 사라진 뒤 다시 나타나기까지 비워 두는 시간

        [Header("마커가 잡혔을 때")]
        [SerializeField] float _hideSeconds = 0.35f;

        bool _idle;
        float _since;          // 지금 주기가 시작된 시각
        int _floor = -1;

        public bool IsShowing => _idle || (_group != null && _group.alpha > 0.001f);

        /// <summary>층별 색을 입힌다. 세트가 시작할 때 한 번.</summary>
        public void Setup(int floor)
        {
            _floor = floor;
            ApplyGradient();
            _idle = false;
            if (_group != null) _group.alpha = 0f;
            gameObject.SetActive(false);
        }

        /// <summary>매 프레임 세트가 부른다. true 면 대기 — 등장부터 시작해 주기를 돈다.</summary>
        public void SetIdle(bool idle)
        {
            if (idle == _idle) return;
            _idle = idle;

            if (idle)
            {
                _since = Time.unscaledTime;
                if (_group != null) _group.alpha = 1f;
                gameObject.SetActive(true);
                Animate(0f);   // 첫 프레임에 완성된 글자가 한 번 비치지 않게
            }
        }

        void Update()
        {
            FitToCanvas();

            if (!_idle)
            {
                // 사라지는 중. 글자 움직임은 그대로 두고 판만 흐려진다.
                if (_group == null) { gameObject.SetActive(false); return; }
                _group.alpha = Mathf.MoveTowards(_group.alpha, 0f, Time.unscaledDeltaTime / Mathf.Max(0.01f, _hideSeconds));
                if (_group.alpha <= 0.001f) gameObject.SetActive(false);
                return;
            }

            float t = Time.unscaledTime - _since;
            if (_loopSeconds > 1f && t >= _loopSeconds)
            {
                _since += Mathf.Floor(t / _loopSeconds) * _loopSeconds;
                t = Time.unscaledTime - _since;
            }
            Animate(t);
        }

        void FitToCanvas()
        {
            if (_frame == null) return;
            var parent = _frame.parent as RectTransform;
            if (parent == null) return;

            Vector2 size = parent.rect.size;
            if (size.x < 1f || size.y < 1f) return;

            float s = Mathf.Min(size.x / _referenceSize.x, size.y / _referenceSize.y);
            _frame.localScale = new Vector3(s, s, 1f);
        }

        void Animate(float t)
        {
            if (_lines == null) return;

            foreach (var line in _lines)
            {
                if (line?.glyphs == null) continue;

                int count = line.glyphs.Length;
                float lt = t - line.delay;

                float introEnd = (count - 1) * _introStagger + _introSeconds;
                float outroLength = (count - 1) * _introStagger + _outroSeconds;
                float outroStart = _loopSeconds > 1f ? _loopSeconds - _outroGap - outroLength - line.delay : float.MaxValue;

                for (int i = 0; i < count; i++)
                {
                    var glyph = line.glyphs[i];
                    if (glyph == null) continue;

                    // 등장: 0 → 1
                    float p = Mathf.Clamp01((lt - i * _introStagger) / Mathf.Max(0.01f, _introSeconds));
                    float alpha = Mathf.Clamp01(p * 2.5f);
                    float scale = BackOut(p);
                    float y = (1f - EaseOut(p)) * -_introRise;

                    // 퇴장: 앞에서부터 작아지며 사라진다
                    float q = Mathf.Clamp01((lt - outroStart - i * _introStagger) / Mathf.Max(0.01f, _outroSeconds));
                    if (q > 0f)
                    {
                        float e = q * q;
                        alpha *= 1f - e;
                        scale *= 1f - 0.6f * e;
                        y += e * _introRise * 0.5f;
                    }

                    // 잔물결 — 글자마다 위상을 밀어 줄이 물결처럼 보인다. 등장하면서 서서히 붙는다
                    if (_bobPeriod > 0.01f)
                        y += Mathf.Sin((lt / _bobPeriod) * Mathf.PI * 2f - i * _bobPhasePerGlyph) * _bobAmplitude * p;

                    // 큰 물결 — 등장이 끝난 뒤 waveEvery 마다 앞에서 뒤로 지나간다
                    if (_waveEvery > 0.01f && lt > introEnd)
                    {
                        float wt = Mathf.Repeat(lt - introEnd, _waveEvery) - i * _waveStagger;
                        if (wt > 0f && wt < _waveSeconds)
                        {
                            float bump = Mathf.Sin(wt / _waveSeconds * Mathf.PI);
                            scale *= 1f + _waveScale * bump;
                            y += _waveLift * bump;
                        }
                    }

                    var rt = glyph.rectTransform;
                    rt.localScale = new Vector3(scale, scale, 1f);
                    rt.anchoredPosition = new Vector2(0f, y);

                    var c = glyph.color;
                    c.a = alpha;
                    glyph.color = c;
                }
            }
        }

        // 한 줄에 걸친 그라데이션을 글자마다 그 자리의 두 색으로 칠한다.
        void ApplyGradient()
        {
            var gradient = GradientFor(_floor);
            if (_lines == null) return;

            foreach (var line in _lines)
            {
                if (line?.glyphs == null || line.root == null) continue;

                float half = line.root.rect.width * 0.5f;
                if (half < 1f) continue;

                foreach (var glyph in line.glyphs)
                {
                    if (glyph == null) continue;

                    var slot = glyph.rectTransform.parent as RectTransform;
                    float cx = slot != null ? slot.anchoredPosition.x : 0f;
                    float hw = (slot != null ? slot.rect.width : glyph.rectTransform.rect.width) * 0.5f;

                    if (gradient == null)
                    {
                        glyph.ClearGradient();
                        continue;
                    }

                    Color from = gradient.Evaluate(Mathf.InverseLerp(-half, half, cx - hw));
                    Color to = gradient.Evaluate(Mathf.InverseLerp(-half, half, cx + hw));
                    glyph.SetGradient(from, to, 0f);
                }
            }
        }

        Gradient GradientFor(int floor)
        {
            if (_floorGradients == null || _floorGradients.Length == 0) return null;

            foreach (var g in _floorGradients)
                if (g != null && g.floor == floor) return g.gradient;

            Debug.LogWarning($"[ArUco] 대기 안내에 {floor}층 색이 없어 첫 번째 색을 씁니다(ArUcoSet.prefab › IdleGuide).");
            return _floorGradients[0]?.gradient;
        }

        static float EaseOut(float x) => 1f - (1f - x) * (1f - x) * (1f - x);

        static float BackOut(float x)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            float k = x - 1f;
            return 1f + c3 * k * k * k + c1 * k * k;
        }
    }
}
