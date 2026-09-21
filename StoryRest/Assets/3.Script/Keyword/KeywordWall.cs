using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.Keyword
{
    /// <summary>
    /// 벽면에 과학 낱말(또는 스프라이트 그림)이 떠다니는 화면. 프로젝터 한 대를 담당한다.
    ///
    /// 관람객 인터랙션이 없는 배경 화면이라 계속 돌아간다.
    /// 항목마다 다른 움직임·크기·투명도를 주어 같은 패턴이 반복되어 보이지 않게 한다.
    ///
    /// 낱말과 스프라이트는 "무엇을 그리는가" 만 다르다. 항목 하나가 TMP_Text 나 RawImage 중 하나를 갖고,
    /// 움직임·겹침·페이드는 같은 코드를 탄다. 어느 쪽인지는 Setup 에 스프라이트 캐시가 왔는지로 정해진다.
    ///
    /// 좌표는 화면을 0~1 로 보는 정규화 값이다. 해상도가 다른 프로젝터를 물려도 같은 그림이 나온다.
    /// </summary>
    [DisallowMultipleComponent]
    public class KeywordWall : MonoBehaviour
    {
        // ArUco 세트 카메라와 겹치지 않도록 반대 방향으로 떨어뜨린다.
        const float WallSeparation = -10000f;

        // 글자·그림 크기(픽셀)와 개수는 이 해상도를 기준으로 정한 값이다. 실제 화면이 다르면 통째로 배율을 맞춘다.
        // 두 프로젝터 해상도가 달라도(또는 창이 작아도) 같은 그림이 나오게 하기 위해서다 — 픽셀 그대로 두면
        // 작은 화면에서는 같은 낱말 16개가 들어갈 자리가 없어 서로 밀어내며 겹치고 떨린다.
        // 월 프로젝터는 16:10 이다(→ SPEC §3.1). 에디터에서 볼 때는 Game 뷰 비율도 16:10 으로 맞춘다.
        static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1200f);

        class Item
        {
            public TMP_Text label;             // 낱말 모드
            public KeywordGradientImage image; // 스프라이트 모드
            public Graphic graphic;       // 둘 중 실제로 붙은 것. 색·투명도는 이것으로 만진다
            public RectTransform rect;

            public KeywordMotion motion;
            public string word;           // 낱말, 또는 스프라이트 파일 경로

            // 스프라이트 텍스처가 아직 오지 않았다. 올 때까지 이미지를 꺼 둔다(켜 두면 흰 사각형이 뜬다).
            public bool awaitingTexture;
            public float spriteSize;      // 이 항목에 배정된 긴 변 크기(기준 해상도 픽셀)

            public Vector2 position;      // 화면 정규화 좌표
            public Vector2 velocity;      // 초당 이동량(정규화)
            public Vector2 center;        // Orbit / Bob 의 중심
            public Vector2 radius;        // Orbit / Bob 의 반경

            public float phase;
            public float frequency;
            public float amplitude;
            public float angle;

            public float age;
            public float lifetime;
            public float baseAlpha;
            public Vector2 halfSize;      // 화면 밖으로 나가지 않게 하려고 글자 크기를 정규화해 둔다

            public bool active;
        }

        KeywordWallConfig _config;
        List<string> _words;
        List<KeywordMotion> _motions;
        List<Color> _colors;
        List<(Color from, Color to)> _gradients;   // 비어 있으면 스프라이트를 원본 색 그대로

        // null 이면 낱말 모드. 있으면 _words 가 스프라이트 파일 경로 목록이다.
        KeywordSpriteCache _sprites;

        // 왼쪽 위 주제 카드. 없을 수 있다(설정에서 끔).
        KeywordSpotlightView _spotlight;

        // 카드가 차지하는 자리(화면 0~1). 카드가 떠 있을 때만 _reserved 에 들어가고, 없을 때는 낱말들이 화면 전체를 쓴다.
        Rect _cardArea;
        Rect _reserved;
        bool _reservedActive;

        Camera _camera;
        RectTransform _canvasRect;

        readonly List<Item> _items = new List<Item>();
        readonly HashSet<string> _onScreen = new HashSet<string>();

        int _wordCursor;

        /// <param name="entries">떠다닐 것들. 낱말 모드면 낱말, 스프라이트 모드면 그림 파일의 절대 경로.</param>
        /// <param name="sprites">스프라이트 모드일 때 텍스처를 빌려 줄 캐시. 낱말 모드면 null.</param>
        public void Setup(KeywordWallConfig config, List<string> entries, KeywordSpriteCache sprites,
                          int displayIndex, int wallIndex)
        {
            _config = config;
            _words = entries;
            _sprites = sprites;
            _motions = config.ResolveMotions();
            _colors = config.ResolveColors();
            _gradients = config.ResolveSpriteGradients();

            BuildScreen(displayIndex, wallIndex);

            if (_words == null || _words.Count == 0)
            {
                Debug.LogWarning(_sprites != null
                    ? "[Keyword] 스프라이트가 없어 빈 화면으로 둡니다. floor_<N>/<마커ID>/sprite/ 를 확인하세요."
                    : "[Keyword] 낱말이 없어 빈 화면으로 둡니다. floor_<N>/keywords.txt 를 확인하세요.");
                return;
            }

            // 낱말이 적으면 중복을 피할 수 없다. 그때는 같은 낱말이 여러 개 떠도 그대로 둔다.
            int count = Mathf.Min(config.maxOnScreen, Mathf.Max(1, _words.Count * 2));

            // 시작하자마자 한꺼번에 사라지지 않도록 수명을 흩어 놓는다.
            for (int i = 0; i < count; i++)
            {
                var item = CreateItem(i);
                _items.Add(item);
                Respawn(item);
                item.age = Random.Range(0f, item.lifetime * 0.8f);
            }
        }

        /// <summary>
        /// 왼쪽 위에 주제 카드를 붙인다. Setup 뒤에 부른다.
        /// 카드는 낱말들보다 나중에 만들어져 위에 그려진다. 자리 비우기는 카드가 뜰 때 UpdateReserved 가 한다.
        /// </summary>
        public void AttachSpotlight(KeywordSpotlight model, KeywordSpriteCache cache)
        {
            if (_canvasRect == null || model == null) return;

            // 첫 장도 그 자리의 낱말이 먼저 사라진 뒤에 뜨도록 낱말 페이드 시간만큼 늦춘다.
            _spotlight = new KeywordSpotlightView(_config.spotlight, _config, model, cache, _config.fadeSeconds + 0.5f);
            _spotlight.Build(_canvasRect);
            _cardArea = _spotlight.ReservedArea(_canvasRect.rect.size);
        }

        /// <summary>
        /// 카드가 뜨는 동안만 그 자리를 비운다. 평소에는 낱말들이 화면 전체를 쓴다 — 한 구석을 늘 비워 두면 허전하다.
        ///
        /// 카드가 뜨기 fadeSeconds 전에 미리 자리를 잡아, 그 안에 있던 것들은 카드보다 먼저 사라지게 한다.
        /// 사라진 것은 다른 자리에서 다시 태어난다(FindOpenSpot 이 카드 자리를 뺀다). 카드가 떠 있는 동안 다가오는 것은
        /// KeepOutOfReserved 가 튕겨낸다. 카드가 사라지면 자리도 풀린다.
        /// </summary>
        void UpdateReserved()
        {
            if (_spotlight == null) return;

            bool active = _spotlight.IsShowing || _spotlight.SecondsUntilShow <= _config.fadeSeconds;
            if (active == _reservedActive) return;

            _reservedActive = active;
            _reserved = active ? _cardArea : Rect.zero;
            if (!active) return;

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (!Overlaps(item, _reserved)) continue;

                // 지금 밝기에서 이어서 사라지게 수명을 당긴다. 아직 나타나는 중이면 그 밝기 그대로 되돌아간다 —
                // 수명만 줄이면 밝기가 1 로 튀었다가 꺼진다.
                item.lifetime = item.age + Mathf.Min(item.age, _config.fadeSeconds);
            }
        }

        void BuildScreen(int displayIndex, int wallIndex)
        {
            var cameraGo = new GameObject($"KeywordCamera{wallIndex}");
            cameraGo.transform.SetParent(transform, false);
            cameraGo.transform.position = new Vector3(0f, (wallIndex + 1) * WallSeparation, 0f);

            _camera = cameraGo.AddComponent<Camera>();
            _camera.targetDisplay = Mathf.Max(0, displayIndex);
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = _config.backgroundColor;
            _camera.orthographic = true;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 100f;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;

            var canvasGo = new GameObject($"KeywordCanvas{wallIndex}", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(cameraGo.transform, false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = _camera;
            canvas.planeDistance = 1f;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            _canvasRect = canvasGo.GetComponent<RectTransform>();

            // 캔버스 크기는 다음 레이아웃 때에야 정해진다. 지금 바로 낱말을 놓으려면 먼저 한 번 갱신해 두어야
            // 첫 낱말들의 크기(halfSize)가 실제 화면 기준으로 계산된다. 안 그러면 전부 대충값으로 태어난다.
            Canvas.ForceUpdateCanvases();
        }

        Item CreateItem(int index)
        {
            var go = new GameObject($"Keyword{index}", typeof(RectTransform));
            go.transform.SetParent(_canvasRect, false);

            var item = new Item();

            if (_sprites != null)
            {
                var image = go.AddComponent<KeywordGradientImage>();
                image.raycastTarget = false;
                image.enabled = false;   // 텍스처가 올 때까지

                item.image = image;
                item.graphic = image;
                item.rect = image.rectTransform;
            }
            else
            {
                var label = go.AddComponent<TextMeshProUGUI>();
                label.raycastTarget = false;
                label.alignment = TextAlignmentOptions.Center;
                label.enableWordWrapping = false;

                item.label = label;
                item.graphic = label;
                item.rect = label.rectTransform;
            }

            item.rect.anchorMin = item.rect.anchorMax = item.rect.pivot = new Vector2(0.5f, 0.5f);
            return item;
        }

        void Update()
        {
            if (_canvasRect == null) return;

            // 배경 화면이라 게임 시간 흐름(GameManager 의 timeScale)에 영향받지 않아야 한다.
            float dt = Time.unscaledDeltaTime;

            // 카드는 낱말이 하나도 없어도 돈다.
            _spotlight?.Tick(dt);
            UpdateReserved();

            if (_items.Count == 0) return;
            Vector2 canvasSize = _canvasRect.rect.size;
            if (canvasSize.x <= 0f || canvasSize.y <= 0f) return;

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];

                item.age += dt;
                if (item.age >= item.lifetime)
                {
                    Respawn(item);
                    continue;
                }

                if (item.awaitingTexture) PollTexture(item);

                Move(item, dt);
            }

            // 움직임을 다 계산한 뒤에 겹친 것만 떼어 놓는다.
            // 낱말이 뭉쳐 읽을 수 없게 되는 것이 이 화면에서 가장 눈에 거슬리는 문제다.
            Separate(dt);

            for (int i = 0; i < _items.Count; i++) Apply(_items[i], canvasSize);
        }

        /// <summary>항목의 글자 상자가 사각형(화면 0~1)에 걸치는가.</summary>
        static bool Overlaps(Item item, Rect area)
        {
            if (area.width <= 0f || area.height <= 0f) return false;

            return item.position.x - item.halfSize.x < area.xMax
                && item.position.x + item.halfSize.x > area.xMin
                && item.position.y - item.halfSize.y < area.yMax
                && item.position.y + item.halfSize.y > area.yMin;
        }

        /// <summary>
        /// 카드 자리에 들어온 항목을 가까운 변으로 밀어낸다. 낱말끼리 밀어내는 것과 같은 세기로 서서히.
        /// 카드가 왼쪽 위 모서리에 있으므로 실제로는 오른쪽 아니면 아래로 나간다.
        /// </summary>
        void KeepOutOfReserved(Item item, float dt)
        {
            if (!Overlaps(item, _reserved)) return;

            float pushRight = _reserved.xMax - (item.position.x - item.halfSize.x);
            float pushDown = _reserved.yMax - (item.position.y - item.halfSize.y);
            float push = Mathf.Max(_config.separation, 0.2f) * dt;

            Vector2 direction = pushRight < pushDown ? Vector2.right : Vector2.up;   // y 는 아래로 증가
            item.position += direction * push;
            item.center += direction * push;

            // 직선으로 오는 것은 방향도 꺾어 준다. 안 그러면 매 프레임 밀어내는 것과 들어오려는 것이 맞서 떨린다.
            if (direction.x > 0f && item.velocity.x < 0f) item.velocity.x = -item.velocity.x;
            if (direction.y > 0f && item.velocity.y < 0f) item.velocity.y = -item.velocity.y;
        }

        /// <summary>
        /// 서로 너무 가까운 낱말을 조금씩 밀어낸다.
        ///
        /// 글자 상자를 타원으로 보고 겹친 정도만큼만 밀어서, 튕기지 않고 서서히 벌어지게 한다.
        /// 항목이 수십 개 수준이라 모든 쌍을 확인해도 부담이 없다.
        /// </summary>
        void Separate(float dt)
        {
            float strength = _config.separation;
            float padding = _config.separationPadding;

            // strength 0 이면 서로 밀어내지 않는다. 카드 자리 비켜 가기와 화면 안 가두기는 그래도 한다.
            for (int i = 0; strength > 0f && i < _items.Count; i++)
            {
                var a = _items[i];

                for (int j = i + 1; j < _items.Count; j++)
                {
                    var b = _items[j];

                    Vector2 delta = b.position - a.position;
                    Vector2 need = (a.halfSize + b.halfSize) * padding;
                    if (need.x <= 0f || need.y <= 0f) continue;

                    // 글자는 가로로 길다. 원이 아니라 타원 기준으로 겹침을 판정한다.
                    float nx = delta.x / need.x;
                    float ny = delta.y / need.y;
                    float distance = Mathf.Sqrt(nx * nx + ny * ny);

                    if (distance >= 1f) continue;

                    // 완전히 같은 자리면 방향이 없다. 아무 방향으로나 떼어 놓는다.
                    Vector2 direction = distance > 1e-4f
                        ? new Vector2(nx * need.x, ny * need.y).normalized
                        : new Vector2(Random.value - 0.5f, Random.value - 0.5f).normalized;

                    float push = (1f - distance) * strength * dt * 0.5f;

                    a.position -= direction * push;
                    b.position += direction * push;

                    // 궤도를 도는 낱말은 중심도 같이 옮겨야 다음 프레임에 제자리로 돌아가지 않는다.
                    a.center -= direction * push;
                    b.center += direction * push;
                }
            }

            for (int i = 0; i < _items.Count; i++)
            {
                KeepOutOfReserved(_items[i], dt);
                ClampInside(_items[i]);
            }
        }

        void ClampInside(Item item)
        {
            Vector2 margin = EdgeLimit(item);

            item.position.x = Mathf.Clamp(item.position.x, margin.x, 1f - margin.x);
            item.position.y = Mathf.Clamp(item.position.y, margin.y, 1f - margin.y);
            item.center = ClampCenter(item.center, margin);
        }

        void Move(Item item, float dt)
        {
            switch (item.motion)
            {
                case KeywordMotion.Drift:
                    item.position += item.velocity * dt;
                    Bounce(item);
                    break;

                case KeywordMotion.Wave:
                    // 가로로 흐르면서 위아래로 물결친다. 세로 중심은 태어난 자리를 유지한다.
                    item.position.x += item.velocity.x * dt;
                    item.phase += item.frequency * dt;
                    item.position.y = item.center.y + Mathf.Sin(item.phase) * item.amplitude;
                    BounceHorizontal(item);
                    break;

                case KeywordMotion.Orbit:
                    item.angle += item.frequency * dt;
                    item.position = item.center + new Vector2(
                        Mathf.Cos(item.angle) * item.radius.x,
                        Mathf.Sin(item.angle) * item.radius.y);
                    break;

                case KeywordMotion.Bob:
                    // 거의 제자리. 아주 천천히 흔들려 배경처럼 깔린다.
                    item.angle += item.frequency * dt;
                    item.position = item.center + new Vector2(
                        Mathf.Sin(item.angle * 0.7f) * item.radius.x,
                        Mathf.Cos(item.angle) * item.radius.y);
                    break;

                case KeywordMotion.Zigzag:
                    item.position += item.velocity * dt;
                    item.phase += dt;

                    // 일정 시간마다 진행 방향을 꺾는다.
                    if (item.phase >= item.amplitude)
                    {
                        item.phase = 0f;
                        float speed = item.velocity.magnitude;
                        float turn = Random.Range(35f, 75f) * (Random.value < 0.5f ? -1f : 1f);
                        float current = Mathf.Atan2(item.velocity.y, item.velocity.x) * Mathf.Rad2Deg + turn;
                        item.velocity = new Vector2(
                            Mathf.Cos(current * Mathf.Deg2Rad),
                            Mathf.Sin(current * Mathf.Deg2Rad)) * speed;
                    }

                    Bounce(item);
                    break;
            }
        }

        // 화면 가장자리는 프로젝터 렌즈 왜곡이 크고 벽 모서리에 걸리기도 한다.
        // 글자 크기 외에 설정값만큼 더 안쪽으로 들여 놓는다.
        Vector2 EdgeLimit(Item item)
        {
            float margin = _config.edgeMargin;
            return Vector2.Min(item.halfSize + new Vector2(margin, margin), new Vector2(0.45f, 0.45f));
        }

        // 화면 안에서만 움직이게 한다. 글자 크기를 반영해 글씨가 잘리지 않는 선에서 튕긴다.
        void Bounce(Item item)
        {
            Vector2 min = EdgeLimit(item);
            Vector2 max = Vector2.one - min;

            if (item.position.x < min.x) { item.position.x = min.x; item.velocity.x = Mathf.Abs(item.velocity.x); }
            else if (item.position.x > max.x) { item.position.x = max.x; item.velocity.x = -Mathf.Abs(item.velocity.x); }

            if (item.position.y < min.y) { item.position.y = min.y; item.velocity.y = Mathf.Abs(item.velocity.y); }
            else if (item.position.y > max.y) { item.position.y = max.y; item.velocity.y = -Mathf.Abs(item.velocity.y); }
        }

        void BounceHorizontal(Item item)
        {
            Vector2 limit = EdgeLimit(item);
            float min = limit.x;
            float max = 1f - limit.x;

            if (item.position.x < min) { item.position.x = min; item.velocity.x = Mathf.Abs(item.velocity.x); }
            else if (item.position.x > max) { item.position.x = max; item.velocity.x = -Mathf.Abs(item.velocity.x); }

            item.position.y = Mathf.Clamp(item.position.y, limit.y, 1f - limit.y);
        }

        void Apply(Item item, Vector2 canvasSize)
        {
            // 정규화 좌표(좌상단 0,0 · y 아래로) → 캔버스 로컬(중앙 원점 · y 위로)
            item.rect.anchoredPosition = new Vector2(
                (item.position.x - 0.5f) * canvasSize.x,
                (0.5f - item.position.y) * canvasSize.y);

            float alpha = item.baseAlpha * FadeFactor(item);

            var color = item.graphic.color;
            color.a = alpha;
            item.graphic.color = color;
        }

        // 등장과 퇴장을 부드럽게. 갑자기 나타나면 배경이 아니라 알림처럼 보인다.
        float FadeFactor(Item item)
        {
            float fade = _config.fadeSeconds;
            if (fade <= 0f) return 1f;

            if (item.age < fade) return Mathf.SmoothStep(0f, 1f, item.age / fade);

            float remaining = item.lifetime - item.age;
            if (remaining < fade) return Mathf.SmoothStep(0f, 1f, remaining / fade);

            return 1f;
        }

        void Respawn(Item item)
        {
            if (!string.IsNullOrEmpty(item.word))
            {
                _onScreen.Remove(item.word);
                _sprites?.Release(item.word);
            }

            item.word = NextWord();
            item.motion = _motions[Random.Range(0, _motions.Count)];
            item.baseAlpha = Random.Range(_config.minAlpha, _config.maxAlpha);

            Vector2 size;

            if (_sprites != null)
            {
                // 그림은 원본 색 그대로 띄운다(투명도만 다르게). 그라데이션이 설정되어 있으면 그중 하나를 곱한다.
                item.graphic.color = new Color(1f, 1f, 1f, 0f);   // 첫 프레임부터 서서히 나타나게
                item.spriteSize = Random.Range(_config.minSpriteSize, _config.maxSpriteSize);

                if (_gradients.Count > 0)
                {
                    var gradient = _gradients[Random.Range(0, _gradients.Count)];
                    item.image.SetGradient(gradient.from, gradient.to, _config.spriteGradientAngle);
                }
                else item.image.ClearGradient();

                // 텍스처는 비동기로 온다. 올 때까지는 정사각형으로 자리를 잡아 두고, 도착하면 종횡비에 맞춘다.
                _sprites.Acquire(item.word);
                item.image.enabled = false;
                item.image.texture = null;
                item.awaitingTexture = true;

                size = new Vector2(item.spriteSize, item.spriteSize);
            }
            else
            {
                item.label.text = item.word;
                item.label.fontSize = Random.Range(_config.minFontSize, _config.maxFontSize);

                var color = _colors[Random.Range(0, _colors.Count)];
                color.a = 0f;   // 첫 프레임부터 서서히 나타나게
                item.label.color = color;

                // 글자가 차지하는 크기를 알아야 화면 밖으로 나가는 것을 막을 수 있다.
                size = item.label.GetPreferredValues();
            }

            SetSize(item, size);

            // 이미 캐시에 있는 그림이면 바로 실제 크기로 잡힌다. 그래야 자리 선택이 진짜 크기로 된다.
            if (item.awaitingTexture) PollTexture(item);

            item.age = 0f;
            item.lifetime = Random.Range(_config.minLifetime, _config.maxLifetime);

            float speed = Random.Range(_config.minSpeed, _config.maxSpeed);
            float direction = Random.Range(0f, Mathf.PI * 2f);
            item.velocity = new Vector2(Mathf.Cos(direction), Mathf.Sin(direction)) * speed;

            item.position = FindOpenSpot(item);
            item.center = item.position;
            item.phase = Random.Range(0f, Mathf.PI * 2f);
            item.angle = Random.Range(0f, Mathf.PI * 2f);

            switch (item.motion)
            {
                case KeywordMotion.Wave:
                    item.frequency = Random.Range(0.3f, 0.9f);
                    item.amplitude = Random.Range(0.04f, 0.12f);
                    // 물결 폭만큼 위아래 여유를 두고 중심을 잡는다.
                    item.center.y = Mathf.Clamp(item.center.y,
                        item.halfSize.y + item.amplitude, 1f - item.halfSize.y - item.amplitude);
                    break;

                case KeywordMotion.Orbit:
                    item.frequency = Random.Range(0.15f, 0.45f) * (Random.value < 0.5f ? -1f : 1f);
                    item.radius = new Vector2(Random.Range(0.06f, 0.18f), Random.Range(0.04f, 0.12f));
                    item.center = ClampCenter(item.center, item.halfSize + item.radius);
                    break;

                case KeywordMotion.Bob:
                    item.frequency = Random.Range(0.25f, 0.6f);
                    item.radius = new Vector2(Random.Range(0.008f, 0.025f), Random.Range(0.008f, 0.03f));
                    item.center = ClampCenter(item.center, item.halfSize + item.radius);
                    break;

                case KeywordMotion.Zigzag:
                    item.amplitude = Random.Range(1.2f, 3.0f);   // 방향을 꺾는 주기(초)
                    item.phase = Random.Range(0f, item.amplitude);
                    break;
            }

            item.active = true;
        }

        /// <summary>
        /// 항목이 차지하는 크기(기준 해상도 픽셀)를 정하고, 화면 밖으로 나가지 않게 정규화한 반크기를 계산한다.
        /// </summary>
        void SetSize(Item item, Vector2 size)
        {
            item.rect.sizeDelta = size;

            Vector2 canvasSize = _canvasRect.rect.size;
            item.halfSize = canvasSize.x > 0f && canvasSize.y > 0f
                ? new Vector2(size.x * 0.5f / canvasSize.x, size.y * 0.5f / canvasSize.y)
                : new Vector2(0.05f, 0.03f);

            // 화면보다 크면 경계 계산이 뒤집힌다. 여유를 남겨 둔다.
            item.halfSize = Vector2.Min(item.halfSize, new Vector2(0.45f, 0.45f));
        }

        /// <summary>
        /// 스프라이트 텍스처가 도착했는지 본다. 왔으면 그림을 켜고 종횡비에 맞춰 크기를 다시 잡는다.
        /// 읽기에 실패한 파일은 남은 수명 동안 빈 자리로 두고 다음 차례에 다른 그림으로 넘어간다 — 매 프레임 재시도하지 않는다.
        /// </summary>
        void PollTexture(Item item)
        {
            if (!_sprites.TryGet(item.word, out var texture, out bool failed))
            {
                if (failed) item.awaitingTexture = false;
                return;
            }

            item.awaitingTexture = false;
            item.image.texture = texture;
            item.image.enabled = true;

            // 긴 변을 배정된 크기에 맞추고 짧은 변은 원본 비율을 따른다.
            float longest = Mathf.Max(texture.width, texture.height, 1);
            float scale = item.spriteSize / longest;
            SetSize(item, new Vector2(texture.width * scale, texture.height * scale));
        }

        /// <summary>
        /// 이미 떠 있는 낱말들과 가장 덜 붙는 자리를 고른다.
        /// 무작위로만 놓으면 태어나자마자 남의 글자 위에 겹쳐 읽을 수 없게 된다.
        /// </summary>
        Vector2 FindOpenSpot(Item self)
        {
            Vector2 best = new Vector2(0.5f, 0.5f);
            float bestClearance = -1f;

            Vector2 limit = EdgeLimit(self);

            for (int attempt = 0; attempt < 12; attempt++)
            {
                var candidate = new Vector2(
                    Random.Range(limit.x, 1f - limit.x),
                    Random.Range(limit.y, 1f - limit.y));

                // 카드 자리는 후보에서 뺀다. 마지막 시도까지 못 피했으면 아래에서 밀려 나간다.
                if (_reserved.width > 0f && attempt < 11
                    && candidate.x - self.halfSize.x < _reserved.xMax && candidate.y - self.halfSize.y < _reserved.yMax)
                    continue;

                float clearance = float.MaxValue;

                for (int i = 0; i < _items.Count; i++)
                {
                    var other = _items[i];
                    if (other == self || !other.active) continue;

                    Vector2 need = (self.halfSize + other.halfSize) * _config.separationPadding;
                    if (need.x <= 0f || need.y <= 0f) continue;

                    Vector2 delta = other.position - candidate;
                    float nx = delta.x / need.x;
                    float ny = delta.y / need.y;

                    clearance = Mathf.Min(clearance, Mathf.Sqrt(nx * nx + ny * ny));
                }

                if (clearance > bestClearance)
                {
                    bestClearance = clearance;
                    best = candidate;
                }

                // 충분히 떨어진 자리를 찾았으면 더 볼 것 없다.
                if (bestClearance >= 1.5f) break;
            }

            return best;
        }

        static Vector2 ClampCenter(Vector2 center, Vector2 margin)
        {
            margin = Vector2.Min(margin, new Vector2(0.45f, 0.45f));
            return new Vector2(
                Mathf.Clamp(center.x, margin.x, 1f - margin.x),
                Mathf.Clamp(center.y, margin.y, 1f - margin.y));
        }

        /// <summary>
        /// 목록을 순서대로 돌면서 낱말(또는 스프라이트)을 고른다. 매번 무작위로 뽑으면 어떤 것은 계속 안 나온다.
        /// </summary>
        string NextWord()
        {
            if (_words == null || _words.Count == 0) return "";

            for (int attempt = 0; attempt < _words.Count; attempt++)
            {
                string word = _words[_wordCursor % _words.Count];
                _wordCursor++;

                if (!_config.avoidDuplicates || !_onScreen.Contains(word))
                {
                    _onScreen.Add(word);
                    return word;
                }
            }

            // 낱말이 화면 수보다 적으면 중복을 피할 수 없다. 그대로 내보낸다.
            string fallback = _words[_wordCursor % _words.Count];
            _wordCursor++;
            return fallback;
        }
    }
}
