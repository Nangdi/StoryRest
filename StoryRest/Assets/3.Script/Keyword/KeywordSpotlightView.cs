using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.Keyword
{
    /// <summary>
    /// 월 왼쪽 위에 KeywordSpotlight 의 카드를 한 장씩 보여 주는 화면 조각. 월 화면 하나에 하나씩 붙는다.
    ///
    /// 카드는 제목("오늘의 추천" 등)과 그 아래 그림뿐이다. 주제 이름은 설정(showTopicName)으로 켤 수 있고,
    /// 켜도 폴더에 제목이 없는 마커("17" 처럼 번호뿐)는 줄을 비운다 — "17번 주제" 같은 글은 관람객에게 아무 뜻이 없다.
    ///
    /// 상시 떠 있지 않다. intervalSeconds 마다 한 장이 떠서 cardSeconds 머물고 사라진다(→ SpotlightConfig).
    /// 카드 목록이 바뀌면(관람이 쌓여 순위가 바뀌면) 지금 카드는 끝까지 보여 주고 다음 장부터 새 목록을 따른다.
    ///
    /// 크기는 1920x1200 기준 픽셀이다(→ KeywordWall.ReferenceResolution). 떠다니는 낱말·그림은 이 자리를 비켜 간다.
    /// </summary>
    public class KeywordSpotlightView
    {
        enum Phase { Waiting, FadeIn, Hold, FadeOut }

        // 카드 안 배치(기준 해상도 픽셀). 설정으로 빼지 않은 값 — 글자 크기 균형이 깨지면 카드가 지저분해진다.
        const float LabelSize = 40f;
        const float LabelHeight = 60f;
        const float TopicSize = 56f;
        const float TopicHeight = 80f;
        const float Gap = 16f;

        readonly SpotlightConfig _config;
        readonly KeywordSpotlight _model;
        readonly KeywordSpriteCache _cache;
        readonly System.Collections.Generic.List<(Color from, Color to)> _gradients;
        readonly float _gradientAngle;

        RectTransform _root;
        CanvasGroup _group;
        TMP_Text _label;
        TMP_Text _topic;     // showTopicName 이 꺼져 있으면 null
        KeywordGradientImage _image;

        Phase _phase = Phase.Waiting;
        float _timer;
        int _index = -1;
        int _shownVersion = -1;

        SpotlightCard _current;
        string _acquiredImage;   // 캐시에서 빌린 그림. 카드가 바뀔 때 돌려준다

        /// <param name="firstDelaySeconds">첫 장을 띄우기 전에 기다리는 시간. 월이 그 자리의 낱말을 먼저 내보낼 여유다.</param>
        /// <param name="wall">월 설정. 카드 그림에 떠다니는 것과 같은 그라데이션을 입히려고 받는다.</param>
        public KeywordSpotlightView(SpotlightConfig config, KeywordWallConfig wall, KeywordSpotlight model,
                                    KeywordSpriteCache cache, float firstDelaySeconds)
        {
            _config = config;
            _model = model;
            _cache = cache;
            _gradients = wall.ResolveSpriteGradients();
            _gradientAngle = wall.spriteGradientAngle;

            // 첫 장은 간격을 다 기다리지 않는다. 켜자마자 1분 동안 빈 벽이면 꺼진 줄 안다.
            _timer = PauseSeconds - Mathf.Max(0f, firstDelaySeconds);
        }

        /// <summary>카드가 차지하는 전체 높이(기준 픽셀).</summary>
        public float Height => LabelHeight + Gap + (_config.showTopicName ? TopicHeight + Gap : 0f) + _config.imageSize;

        /// <summary>한 장이 사라진 뒤 다음 장이 뜰 때까지 쉬는 시간. 간격에서 떠 있는 시간을 뺀 것.</summary>
        float PauseSeconds => Mathf.Max(0f, _config.intervalSeconds - (_config.fadeSeconds * 2f + _config.cardSeconds));

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
                if (_phase != Phase.Waiting) return 0f;
                if (_model.Cards.Count == 0) return float.PositiveInfinity;
                return Mathf.Max(0f, PauseSeconds - _timer);
            }
        }

        /// <summary>떠다니는 것들이 비켜 갈 자리. 화면을 0~1 로 본 사각형이다(좌상단 원점).</summary>
        public Rect ReservedArea(Vector2 canvasSize)
        {
            if (canvasSize.x <= 0f || canvasSize.y <= 0f) return Rect.zero;

            float pad = _config.margin * 0.5f;   // 카드 가장자리에 바짝 붙지 않게
            return new Rect(
                0f, 0f,
                (_config.margin + _config.width + pad) / canvasSize.x,
                (_config.margin + Height + pad) / canvasSize.y);
        }

        public void Build(RectTransform canvas)
        {
            var go = new GameObject("Spotlight", typeof(RectTransform), typeof(CanvasGroup));
            go.transform.SetParent(canvas, false);

            _root = go.GetComponent<RectTransform>();
            _root.anchorMin = _root.anchorMax = new Vector2(0f, 1f);
            _root.pivot = new Vector2(0f, 1f);
            _root.anchoredPosition = new Vector2(_config.margin, -_config.margin);
            _root.sizeDelta = new Vector2(_config.width, Height);

            _group = go.GetComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            float y = 0f;
            _label = MakeText("Label", LabelSize, LabelHeight, ref y, _config.ResolveAccentColor());
            if (_config.showTopicName) _topic = MakeText("Topic", TopicSize, TopicHeight, ref y, Color.white);

            var imageGo = new GameObject("Image", typeof(RectTransform));
            imageGo.transform.SetParent(_root, false);
            _image = imageGo.AddComponent<KeywordGradientImage>();
            _image.raycastTarget = false;
            _image.enabled = false;

            var imageRect = _image.rectTransform;
            imageRect.anchorMin = imageRect.anchorMax = new Vector2(0f, 1f);
            imageRect.pivot = new Vector2(0f, 1f);
            imageRect.anchoredPosition = new Vector2(0f, -y);
            imageRect.sizeDelta = new Vector2(_config.imageSize, _config.imageSize);
        }

        TMP_Text MakeText(string name, float fontSize, float height, ref float y, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_root, false);

            var text = go.AddComponent<TextMeshProUGUI>();
            text.raycastTarget = false;
            text.fontSize = fontSize;
            text.fontStyle = FontStyles.Bold;
            text.color = color;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.enableWordWrapping = false;

            // 잘라내기(Ellipsis/Truncate)를 쓰면 안 된다 — 한글 대체 폰트는 줄 높이가 글자 크기의 1.4배를 넘어
            // 상자보다 조금만 커도 첫 줄째부터 통째로 잘려 아무것도 안 뜬다. 한 줄짜리라 넘쳐도 그냥 그린다.
            text.overflowMode = TextOverflowModes.Overflow;

            var rect = text.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(_config.width, height);

            y += height + Gap;
            return text;
        }

        public void Tick(float dt)
        {
            if (_root == null) return;

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
            if (_topic != null) _topic.text = card.topic ?? "";

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

            // 정사각형 칸 안에 비율을 지켜 넣는다. 왼쪽 위 기준이라 가로로 긴 그림은 위쪽에 붙는다.
            float longest = Mathf.Max(texture.width, texture.height, 1);
            float scale = _config.imageSize / longest;
            _image.rectTransform.sizeDelta = new Vector2(texture.width * scale, texture.height * scale);
            _image.texture = texture;
            _image.enabled = true;
        }
    }
}
