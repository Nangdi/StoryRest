using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.Keyword
{
    /// <summary>
    /// 월 왼쪽 위에 KeywordSpotlight 의 카드를 한 장씩 보여 주는 조각. KeywordWall 프리팹 안에 하나 들어 있다.
    ///
    /// 카드는 제목("오늘의 추천 컨텐츠" 등)과 그 아래 그림뿐이다. 주제 이름은 설정(showTopicName)으로 켤 수 있고,
    /// 켜도 폴더에 제목이 없는 마커("17" 처럼 번호뿐)는 줄을 비운다 — "17번 주제" 같은 글은 관람객에게 아무 뜻이 없다.
    ///
    /// **자리·크기·글꼴·색은 전부 프리팹이 정한다.** 이 스크립트는 글을 채우고 밝기를 올렸다 내리는 일만 한다.
    /// 제목 · 주제 이름 · 그림 칸은 Spotlight 안에서 각자 자유롭게 놓인다(레이아웃 그룹 없음) — 씬이나 프리팹에서
    /// 하나씩 끌어 잡는다. 떠다니는 것들이 비켜 갈 자리는 Spotlight 루트의 사각형이므로, 조각을 밖으로 빼면 루트도 키운다.
    ///
    /// 상시 떠 있지 않다. intervalSeconds 마다 한 장이 떠서 cardSeconds 머물고 사라진다(→ SpotlightConfig).
    /// 카드 목록이 바뀌면(관람이 쌓여 순위가 바뀌면) 지금 카드는 끝까지 보여 주고 다음 장부터 새 목록을 따른다.
    /// </summary>
    [DisallowMultipleComponent]
    public class KeywordSpotlightView : MonoBehaviour
    {
        enum Phase { Waiting, FadeIn, Hold, FadeOut }

        [Header("카드 조각 (프리팹에서 연결)")]
        [SerializeField] RectTransform _root;
        [SerializeField] CanvasGroup _group;
        [SerializeField] TMP_Text _label;
        [SerializeField] TMP_Text _topic;
        [SerializeField] KeywordGradientImage _image;

        [Tooltip("그림이 들어갈 칸. 그림은 이 칸 안에 비율을 지켜 들어간다.")]
        [SerializeField] AspectRatioFitter _imageFitter;

        [Header("자리 비우기")]
        [Tooltip("떠다니는 것들이 카드에서 떨어져 있을 여백(기준 해상도 픽셀).")]
        [SerializeField] float _reservedPadding = 28f;

        SpotlightConfig _config;
        KeywordSpotlight _model;
        KeywordSpriteCache _cache;
        System.Collections.Generic.List<(Color from, Color to)> _gradients;
        float _gradientAngle;

        Phase _phase = Phase.Waiting;
        float _timer;
        int _index = -1;
        int _shownVersion = -1;
        bool _bound;

        SpotlightCard _current;
        string _acquiredImage;   // 캐시에서 빌린 그림. 카드가 바뀔 때 돌려준다

        /// <summary>
        /// 카드를 쓸 준비를 한다. KeywordWall 이 부른다.
        /// </summary>
        /// <param name="firstDelaySeconds">첫 장을 띄우기 전에 더 기다리는 시간. 월이 그 자리의 낱말을 먼저 내보낼 여유다.</param>
        public void Bind(KeywordWallConfig wall, KeywordSpotlight model, KeywordSpriteCache cache, float firstDelaySeconds)
        {
            _config = wall.spotlight;
            _model = model;
            _cache = cache;
            _gradients = wall.ResolveSpriteGradients();
            _gradientAngle = wall.spriteGradientAngle;

            if (_root == null) _root = (RectTransform)transform;

            if (_group == null || _label == null || _image == null)
            {
                Debug.LogError("[Keyword] 주제 카드의 연결이 비었습니다. " +
                               "Assets/9.Prefab/KeywordWall.prefab 의 Spotlight 에서 CanvasGroup·Label·Image 를 이어 주세요.");
                return;
            }

            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            // 주제 이름을 안 쓰면 줄을 숨긴다. 자리는 그대로 비어 있다 — 그림을 올려 붙이고 싶으면 프리팹에서 올린다.
            if (_topic != null) _topic.gameObject.SetActive(_config.showTopicName);

            _image.enabled = false;

            // 첫 장은 간격을 다 기다리지 않는다. 켜자마자 1분 동안 빈 벽이면 꺼진 줄 안다.
            _timer = PauseSeconds - Mathf.Max(0f, firstDelaySeconds);
            _bound = true;
        }

        /// <summary>설정이 바뀐 뒤 부른다. 시간값은 매 프레임 읽으므로 여기서는 켜고 끄는 것만 다시 맞춘다.</summary>
        public void ApplyConfig()
        {
            if (!_bound) return;
            if (_topic != null) _topic.gameObject.SetActive(_config.showTopicName);
        }

        /// <summary>한 장이 사라진 뒤 다음 장이 뜰 때까지 쉬는 시간. 간격에서 떠 있는 시간을 뺀 것. 상시 노출이면 0.</summary>
        float PauseSeconds => _config.alwaysOn
            ? 0f
            : Mathf.Max(0f, _config.intervalSeconds - (_config.fadeSeconds * 2f + _config.cardSeconds));

        /// <summary>지금 카드가 보이는 중인가(페이드 포함).</summary>
        public bool IsShowing => _phase != Phase.Waiting;

        /// <summary>
        /// 다음 카드가 뜨기까지 남은 시간(초). 보이는 중이면 0, 띄울 카드가 없으면 무한대.
        /// 월이 이걸 보고 카드 자리에 있는 낱말들을 카드보다 먼저 내보낸다.
        /// </summary>
        public float SecondsUntilShow
        {
            get
            {
                if (!_bound) return float.PositiveInfinity;
                if (_phase != Phase.Waiting) return 0f;
                if (_model.Cards.Count == 0) return float.PositiveInfinity;
                return Mathf.Max(0f, PauseSeconds - _timer);
            }
        }

        /// <summary>
        /// 떠다니는 것들이 비켜 갈 자리. 화면을 0~1 로 본 사각형이다(좌상단 원점).
        ///
        /// 프리팹에 놓인 카드의 실제 사각형을 읽어서 캔버스 비율로 바꾼다. 앵커를 어디에 두든,
        /// 카드를 오른쪽 아래로 옮기든 그대로 따라온다 — 월은 이 사각형만 보고 피한다.
        /// </summary>
        public Rect ReservedArea(RectTransform canvas)
        {
            if (!_bound || canvas == null || _root == null) return Rect.zero;

            Rect canvasRect = canvas.rect;
            if (canvasRect.width <= 0f || canvasRect.height <= 0f) return Rect.zero;

            var corners = new Vector3[4];   // 0 좌하 · 1 좌상 · 2 우상 · 3 우하
            _root.GetWorldCorners(corners);

            Vector2 min = canvas.InverseTransformPoint(corners[0]);
            Vector2 max = canvas.InverseTransformPoint(corners[2]);

            float pad = Mathf.Max(0f, _reservedPadding);

            // 캔버스 로컬(중앙 원점 · y 위로) → 정규화(좌상단 원점 · y 아래로)
            var area = Rect.MinMaxRect(
                (min.x - pad - canvasRect.xMin) / canvasRect.width,
                (canvasRect.yMax - max.y - pad) / canvasRect.height,
                (max.x + pad - canvasRect.xMin) / canvasRect.width,
                (canvasRect.yMax - min.y + pad) / canvasRect.height);

            // 화면 밖으로 나간 부분은 잘라 낸다. 그대로 두면 낱말이 있지도 않은 자리를 피한다.
            return Rect.MinMaxRect(
                Mathf.Clamp01(area.xMin), Mathf.Clamp01(area.yMin),
                Mathf.Clamp01(area.xMax), Mathf.Clamp01(area.yMax));
        }

        public void Tick(float dt)
        {
            // 플레이 중 스크립트가 다시 컴파일되면 _bound 만 남고 참조는 비는 수가 있다. 그때 매 프레임 터지지 않게.
            if (!_bound || _model == null || _config == null) return;

            var cards = _model.Cards;

            switch (_phase)
            {
                case Phase.Waiting:
                    _timer += dt;
                    if (_timer < PauseSeconds || cards.Count == 0) return;

                    ShowNext(cards);
                    _phase = Phase.FadeIn;
                    _timer = 0f;
                    break;

                case Phase.FadeIn:
                    _timer += dt;
                    _group.alpha = Fade(_timer);
                    if (_timer >= _config.fadeSeconds)
                    {
                        _group.alpha = 1f;
                        _phase = Phase.Hold;
                        _timer = 0f;
                    }
                    break;

                case Phase.Hold:
                    _timer += dt;
                    if (_timer < _config.cardSeconds) break;

                    // 상시 노출인데 넘길 다른 장이 없으면 그대로 둔다. 같은 장을 껐다 켜면 깜빡이는 것처럼 보인다.
                    // 목록이 바뀌었으면(관람이 쌓여 순위가 바뀜) 새 목록의 첫 장으로 넘어간다.
                    if (_config.alwaysOn && cards.Count <= 1 && _shownVersion == _model.Version) break;

                    _phase = Phase.FadeOut;
                    _timer = 0f;
                    break;

                case Phase.FadeOut:
                    _timer += dt;
                    _group.alpha = 1f - Fade(_timer);
                    if (_timer >= _config.fadeSeconds)
                    {
                        _group.alpha = 0f;
                        _phase = Phase.Waiting;
                        _timer = 0f;
                        Hide();
                    }
                    break;
            }

            if (_current != null && !_image.enabled && _acquiredImage != null) PollImage();
        }

        float Fade(float t)
        {
            float fade = _config.fadeSeconds;
            return fade <= 0f ? 1f : Mathf.SmoothStep(0f, 1f, t / fade);
        }

        void ShowNext(System.Collections.Generic.IReadOnlyList<SpotlightCard> cards)
        {
            // 목록이 바뀌었으면 처음부터. 아니면 다음 장.
            if (_shownVersion != _model.Version)
            {
                _shownVersion = _model.Version;
                _index = 0;
            }
            else
            {
                _index = (_index + 1) % cards.Count;
            }

            Show(cards[Mathf.Clamp(_index, 0, cards.Count - 1)]);
        }

        void Show(SpotlightCard card)
        {
            Hide();

            _current = card;
            _label.text = card.label;
            if (_topic != null && _topic.gameObject.activeSelf) _topic.text = card.topic ?? "";

            if (_gradients.Count > 0)
            {
                var gradient = _gradients[Random.Range(0, _gradients.Count)];
                _image.SetGradient(gradient.from, gradient.to, _gradientAngle);
            }
            else _image.ClearGradient();

            _acquiredImage = card.imagePath;
            _cache.Acquire(_acquiredImage);
            PollImage();
        }

        /// <summary>그림을 캐시에 돌려주고 카드를 비운다. 사라진 동안 그림을 들고 있을 이유가 없다.</summary>
        void Hide()
        {
            if (_acquiredImage != null)
            {
                _cache.Release(_acquiredImage);
                _acquiredImage = null;
            }

            _current = null;
            _image.enabled = false;
            _image.texture = null;
        }

        void PollImage()
        {
            if (!_cache.TryGet(_acquiredImage, out var texture, out bool failed))
            {
                if (failed)
                {
                    // 못 읽는 그림은 제목만 남긴다. 매 프레임 다시 묻지 않게 놓아 둔다.
                    _cache.Release(_acquiredImage);
                    _acquiredImage = null;
                }
                return;
            }

            // 칸 크기는 프리팹이 정한다. 그림은 그 안에 비율을 지켜 들어간다(AspectRatioFitter).
            if (_imageFitter != null && texture.height > 0)
                _imageFitter.aspectRatio = texture.width / (float)texture.height;

            _image.texture = texture;
            _image.enabled = true;
        }

        void OnDestroy()
        {
            if (_acquiredImage != null && _cache != null) _cache.Release(_acquiredImage);
        }
    }
}
