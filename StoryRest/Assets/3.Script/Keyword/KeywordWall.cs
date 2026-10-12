using System.Collections.Generic;
using UnityEngine;

namespace StoryRest.Keyword
{
    /// <summary>
    /// 벽면 화면 하나. 질문 2개(각각 위에 분류 문구)와 두 질문의 해시태그가 떠다닌다. 프로젝터 한 대를 담당한다.
    ///
    /// **무엇을 띄울지는 WallQuestionDirector 가 정한다**(→ WALL_CONTENT §5). 이 스크립트는 디렉터의 화면 하나를 보고
    /// 칸 · 태그 목록의 version 이 바뀌었을 때만 항목을 바꾼다. 바뀔 때는 교차 페이드다 — 나가는 것은 그 자리에서 사라지고
    /// 들어오는 것은 빈 자리에서 나타난다.
    ///
    /// 질문 · 분류 문구 · 해시태그는 모두 흰 글씨 PNG 라 프리팹의 `Templates/SpriteItem` 하나를 복제해 그린다.
    /// 분류 문구는 질문 그림의 자식으로 붙어 함께 움직이고, 겹침 계산은 둘을 합친 상자로 한다.
    /// 색은 질문 · 해시태그가 층별 그라데이션, 분류 문구가 PSD 회색 단색이다(→ WALL_CONTENT §5 "색과 배치").
    ///
    /// **화면은 프리팹이 만든다**(→ ARCHITECTURE §3). 카메라·캔버스·항목 원본이 `Assets/9.Prefab/KeywordWall.prefab` 안에 있다.
    /// 좌표는 화면을 0~1 로 보는 정규화 값이다. 해상도가 다른 프로젝터를 물려도 같은 그림이 나온다.
    /// </summary>
    [DisallowMultipleComponent]
    public class KeywordWall : MonoBehaviour
    {
        // ArUco 세트 카메라와 겹치지 않도록 반대 방향으로 떨어뜨린다.
        const float WallSeparation = -10000f;

        // 텍스처가 오기 전 자리 잡기용 크기(기준 해상도 픽셀). 도착하면 실제 비율로 바뀐다.
        static readonly Vector2 QuestionPlaceholder = new Vector2(1100f, 170f);

        class Item
        {
            public bool isQuestion;
            public KeywordGradientImage image;
            public RectTransform rect;
            public string path;
            public bool awaitingTexture;
            public Vector2 imageSize;       // 그릴 크기(기준 해상도 픽셀)

            // 질문에만 — 위에 붙는 분류 문구.
            public KeywordGradientImage title;
            public string titlePath;
            public bool awaitingTitle;
            public Vector2 titleSize;

            public float tagHeight;         // 해시태그에만 — 배정된 높이

            public KeywordMotion motion;
            public Vector2 position;        // 화면 정규화 좌표(질문은 문구까지 합친 상자의 가운데)
            public Vector2 velocity;
            public Vector2 center;
            public Vector2 radius;
            public float phase;
            public float frequency;
            public float amplitude;
            public float angle;

            public float fade;              // 0~1. 들어올 때 오르고 나갈 때 내려간다
            public bool leaving;
            public float baseAlpha;
            public Vector2 halfSize;        // 합친 상자의 반크기(정규화)
            public float drawOffsetY;       // 질문 그림을 상자 가운데에서 아래로 내리는 양(캔버스 픽셀)
            public float mass;              // 밀어내기 가중치 — 큰 것이 덜 비킨다
        }

        [Header("화면 — 프리팹 안의 조각들")]
        [SerializeField] Camera _camera;
        [SerializeField] Canvas _canvas;

        [Tooltip("떠다니는 것들이 들어갈 자리. 크기 기준(1920x1200 등)은 캔버스의 CanvasScaler 가 정한다.")]
        [SerializeField] RectTransform _canvasRect;
        [SerializeField] RectTransform _itemRoot;

        [Header("떠다니는 것 원본 — 꺼진 자리에 두고 복제해 쓴다")]
        [Tooltip("질문 · 분류 문구 · 해시태그 그림 원본. 크기는 설정에서 정한다.")]
        [SerializeField] KeywordGradientImage _spriteTemplate;

        KeywordWallConfig _config;
        WallQuestionDirector _director;
        int _screenIndex;
        int _floor;
        KeywordSpriteCache _sprites;

        List<KeywordMotion> _tagMotions;
        List<KeywordMotion> _questionMotions;
        List<(Color from, Color to)> _gradients;

        readonly List<Item> _items = new List<Item>();
        readonly Item[] _slotItems = new Item[2];
        readonly int[] _slotVersions = { -1, -1 };
        int _tagVersion = -1;

        public void Setup(KeywordWallConfig config, WallQuestionDirector director, KeywordSpriteCache sprites,
                          int floor, int displayIndex, int wallIndex)
        {
            _config = config;
            _director = director;
            _screenIndex = wallIndex;
            _floor = floor;
            _sprites = sprites;

            if (!BindScreen(displayIndex, wallIndex)) return;
            ApplyConfig();
        }

        /// <summary>ESC 설정창에서 값이 바뀐 뒤 부른다. 움직임 · 그라데이션 목록을 다시 읽는다. 나머지는 다음 항목부터 바로 쓴다.</summary>
        public void ApplyConfig()
        {
            if (_config == null) return;

            _tagMotions = _config.ResolveMotions();
            _questionMotions = KeywordWallConfig.ResolveMotions(_config.questions.questionMotions);
            _gradients = _config.ResolveSpriteGradients(_floor);
        }

        /// <summary>
        /// 프리팹으로 들어온 카메라·캔버스를 이번 화면에 맞춘다. 무엇을 만들지는 프리팹이 이미 정했고,
        /// 여기서 정하는 것은 "어느 프로젝터로 나가는가" 와 세트 카메라에 걸리지 않을 자리뿐이다.
        /// </summary>
        bool BindScreen(int displayIndex, int wallIndex)
        {
            if (_camera == null || _canvas == null || _canvasRect == null || _itemRoot == null || _spriteTemplate == null)
            {
                Debug.LogError($"[Keyword] 월 프리팹의 연결이 비었습니다({name}). " +
                               "Assets/9.Prefab/KeywordWall.prefab 의 KeywordWall 컴포넌트에서 " +
                               "카메라·캔버스·Items·SpriteItem 원본을 이어 주세요.");
                _config = null;
                return false;
            }

            // ArUco 세트 카메라와 겹치지 않도록, 월끼리도 겹치지 않도록 떨어뜨린다.
            transform.position = new Vector3(0f, (wallIndex + 1) * WallSeparation, 0f);

            _camera.targetDisplay = Mathf.Max(0, displayIndex);
            _canvas.worldCamera = _camera;

            // 캔버스 크기는 다음 레이아웃 때에야 정해진다. 지금 바로 항목을 놓으려면 먼저 한 번 갱신해 두어야
            // 첫 항목들의 크기(halfSize)가 실제 화면 기준으로 계산된다.
            Canvas.ForceUpdateCanvases();
            return true;
        }

        void Update()
        {
            // 씬에 미리 놓인 채 아직 Setup 을 못 받았으면(StoryRestApp 이 없는 씬 등) 아무것도 하지 않는다.
            if (_config == null || _director == null || _canvasRect == null) return;
            if (_screenIndex >= _director.Screens.Count) return;

            // 배경 화면이라 게임 시간 흐름(GameManager 의 timeScale)에 영향받지 않아야 한다.
            float dt = Time.unscaledDeltaTime;

            Vector2 canvasSize = _canvasRect.rect.size;
            if (canvasSize.x <= 0f || canvasSize.y <= 0f) return;

            SyncWithDirector(_director.Screens[_screenIndex]);

            float fadeStep = _config.fadeSeconds > 0f ? dt / _config.fadeSeconds : 1f;

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];

                PollTextures(item);
                item.fade = Mathf.MoveTowards(item.fade, item.leaving ? 0f : 1f, fadeStep);

                // 나가는 것은 그 자리에서 사라진다. 움직이며 사라지면 들어오는 것과 엇갈려 어지럽다.
                if (!item.leaving) Move(item, dt);
            }

            // 움직임을 다 계산한 뒤에 겹친 것만 떼어 놓는다.
            Separate(dt);

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                if (item.leaving && item.fade <= 0f)
                {
                    Remove(item);
                    _items.RemoveAt(i);
                    continue;
                }

                Apply(item, canvasSize);
            }
        }

        // ── 디렉터 따라가기 ────────────────────────────────────────────────────

        void SyncWithDirector(QuestionScreen screen)
        {
            for (int i = 0; i < _slotItems.Length; i++)
            {
                var slot = screen.slots[i];
                if (slot.version == _slotVersions[i]) continue;
                _slotVersions[i] = slot.version;

                if (_slotItems[i] != null) _slotItems[i].leaving = true;
                _slotItems[i] = slot.question != null ? SpawnQuestion(slot) : null;
            }

            if (screen.tagVersion == _tagVersion) return;
            _tagVersion = screen.tagVersion;

            // 새 목록에 없는 태그는 내보내고, 새로 생긴 것만 들인다. 두 목록에 다 있는 것은 그대로 떠 있다.
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (!item.isQuestion && !item.leaving && !screen.tags.Contains(item.path)) item.leaving = true;
            }

            foreach (string path in screen.tags)
            {
                if (_items.Exists(item => !item.isQuestion && !item.leaving && item.path == path)) continue;
                SpawnTag(path);
            }
        }

        Item SpawnQuestion(QuestionSlot slot)
        {
            var q = _config.questions;
            var item = CreateItem("Question", slot.question.questionPath, isQuestion: true);

            item.baseAlpha = 1f;
            ApplyGradient(item.image);
            item.imageSize = QuestionPlaceholder * q.questionScale;

            if (!string.IsNullOrEmpty(slot.titlePath))
            {
                var title = Instantiate(_spriteTemplate, item.rect);
                title.gameObject.SetActive(true);
                title.name = "Title";
                title.enabled = false;

                // 분류 문구는 PSD 색 단색이다. 투명도는 Apply 가 알파만 바꾸므로 색은 여기서 한 번만 정한다.
                title.ClearGradient();
                var titleColor = q.ResolveTitleColor();
                titleColor.a = 0f;
                title.color = titleColor;
                title.rectTransform.anchorMin = title.rectTransform.anchorMax = new Vector2(0.5f, 1f);
                title.rectTransform.pivot = new Vector2(0.5f, 0f);
                title.rectTransform.localScale = Vector3.one;

                item.title = title;
                item.titlePath = slot.titlePath;
                item.awaitingTitle = true;
                _sprites.Acquire(slot.titlePath);
            }

            UpdateLayout(item);
            PollTextures(item);
            Place(item, _questionMotions, q.minQuestionSpeed, q.maxQuestionSpeed);
            return item;
        }

        void SpawnTag(string path)
        {
            var item = CreateItem("Tag", path, isQuestion: false);

            item.baseAlpha = Random.Range(_config.minAlpha, _config.maxAlpha);
            item.tagHeight = Random.Range(_config.questions.minTagHeight, _config.questions.maxTagHeight);
            item.imageSize = new Vector2(item.tagHeight * 4f, item.tagHeight);

            ApplyGradient(item.image);

            UpdateLayout(item);
            PollTextures(item);
            Place(item, _tagMotions, _config.minSpeed, _config.maxSpeed);
        }

        /// <summary>
        /// 이 층의 그라데이션 중 하나를 입힌다(질문 · 해시태그). gradientPick 이 random 이면 항목마다 무작위, first 면 첫 번째.
        /// 목록이 없으면 흰색 그대로.
        /// </summary>
        void ApplyGradient(KeywordGradientImage image)
        {
            if (_gradients.Count == 0)
            {
                image.ClearGradient();
                return;
            }

            var gradient = _gradients[_config.PickGradientAtRandom ? Random.Range(0, _gradients.Count) : 0];
            image.SetGradient(gradient.from, gradient.to, _config.spriteGradientAngle);
        }

        /// <summary>그림 하나를 복제해 항목으로 만든다. 텍스처는 비동기로 오므로 올 때까지 그림을 꺼 둔다(켜 두면 흰 사각형).</summary>
        Item CreateItem(string kind, string path, bool isQuestion)
        {
            var image = Instantiate(_spriteTemplate, _itemRoot);
            image.gameObject.SetActive(true);
            image.name = $"{kind} {System.IO.Path.GetFileNameWithoutExtension(path)}";
            image.enabled = false;
            image.color = new Color(1f, 1f, 1f, 0f);

            // 자리는 매 프레임 코드가 계산한다(→ Apply). 그 계산은 "캔버스 가운데가 원점" 을 전제하므로
            // 앵커·피벗만은 프리팹 값을 따르지 않고 가운데로 고정한다.
            image.rectTransform.anchorMin = image.rectTransform.anchorMax = image.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            image.rectTransform.localScale = Vector3.one;

            var item = new Item
            {
                isQuestion = isQuestion,
                image = image,
                rect = image.rectTransform,
                path = path,
                awaitingTexture = true,
                fade = 0f,
            };

            _sprites.Acquire(path);
            _items.Add(item);
            return item;
        }

        void Remove(Item item)
        {
            _sprites.Release(item.path);
            if (!string.IsNullOrEmpty(item.titlePath)) _sprites.Release(item.titlePath);
            if (item.rect != null) Destroy(item.rect.gameObject);   // 분류 문구는 자식이라 함께 사라진다
        }

        void OnDestroy()
        {
            if (_sprites == null) return;
            foreach (var item in _items)
            {
                _sprites.Release(item.path);
                if (!string.IsNullOrEmpty(item.titlePath)) _sprites.Release(item.titlePath);
            }
            _items.Clear();
        }

        /// <summary>
        /// 텍스처가 도착했는지 본다. 왔으면 그림을 켜고 실제 비율로 크기를 다시 잡는다.
        /// 읽기에 실패한 파일은 빈 채로 둔다 — 매 프레임 재시도하지 않는다.
        /// </summary>
        void PollTextures(Item item)
        {
            bool changed = false;

            if (item.awaitingTexture)
            {
                if (_sprites.TryGet(item.path, out var texture, out bool failed))
                {
                    item.awaitingTexture = false;
                    item.image.texture = texture;
                    item.image.enabled = true;

                    float scale = item.isQuestion
                        ? _config.questions.questionScale
                        : item.tagHeight / Mathf.Max(1, texture.height);
                    item.imageSize = new Vector2(texture.width, texture.height) * scale;
                    changed = true;
                }
                else if (failed) item.awaitingTexture = false;
            }

            if (item.awaitingTitle)
            {
                if (_sprites.TryGet(item.titlePath, out var texture, out bool failed))
                {
                    item.awaitingTitle = false;
                    item.title.texture = texture;
                    item.title.enabled = true;
                    // PSD 에서 질문과 같은 배율로 뽑은 그림이라 질문 배율을 그대로 따라가야 비율이 유지된다.
                item.titleSize = new Vector2(texture.width, texture.height)
                               * (_config.questions.questionScale * _config.questions.titleScale);
                    changed = true;
                }
                else if (failed) item.awaitingTitle = false;
            }

            if (changed) UpdateLayout(item);
        }

        /// <summary>
        /// 그림 크기에서 합친 상자를 계산한다. 분류 문구는 질문 위 가운데에 붙고, 상자 가운데가 곧 item.position 이다.
        /// 그래서 질문 그림은 상자 가운데에서 (문구 높이 + 간격)/2 만큼 아래에 그린다.
        /// </summary>
        void UpdateLayout(Item item)
        {
            item.rect.sizeDelta = item.imageSize;

            Vector2 total = item.imageSize;
            item.drawOffsetY = 0f;

            if (item.title != null && item.titleSize.y > 0f)
            {
                float gap = _config.questions.titleGap * _config.questions.questionScale;
                item.title.rectTransform.sizeDelta = item.titleSize;
                item.title.rectTransform.anchoredPosition = new Vector2(0f, gap);

                total = new Vector2(Mathf.Max(item.imageSize.x, item.titleSize.x), item.imageSize.y + gap + item.titleSize.y);
                item.drawOffsetY = -(gap + item.titleSize.y) * 0.5f;
            }

            Vector2 canvasSize = _canvasRect.rect.size;
            item.halfSize = canvasSize.x > 0f && canvasSize.y > 0f
                ? new Vector2(total.x * 0.5f / canvasSize.x, total.y * 0.5f / canvasSize.y)
                : new Vector2(0.05f, 0.03f);

            // 화면보다 크면 경계 계산이 뒤집힌다. 여유를 남겨 둔다.
            item.halfSize = Vector2.Min(item.halfSize, new Vector2(0.45f, 0.45f));
            item.mass = Mathf.Max(1e-4f, item.halfSize.x * item.halfSize.y);
        }

        /// <summary>빈 자리를 골라 놓고 움직임을 배정한다.</summary>
        void Place(Item item, List<KeywordMotion> motions, float minSpeed, float maxSpeed)
        {
            item.motion = motions[Random.Range(0, motions.Count)];

            float speed = Random.Range(minSpeed, maxSpeed);
            float direction = Random.Range(0f, Mathf.PI * 2f);
            item.velocity = new Vector2(Mathf.Cos(direction), Mathf.Sin(direction)) * speed;

            item.position = FindOpenSpot(item);
            item.center = item.position;
            item.phase = Random.Range(0f, Mathf.PI * 2f);
            item.angle = Random.Range(0f, Mathf.PI * 2f);

            // 질문은 크고 읽혀야 하므로 흔들림 폭을 작게 잡는다.
            float sway = item.isQuestion ? 0.4f : 1f;

            switch (item.motion)
            {
                case KeywordMotion.Wave:
                    item.frequency = Random.Range(0.3f, 0.9f) * sway;
                    item.amplitude = Random.Range(0.04f, 0.12f) * sway;
                    // 물결 폭만큼 위아래 여유를 두고 중심을 잡는다.
                    item.center.y = Mathf.Clamp(item.center.y,
                        item.halfSize.y + item.amplitude, 1f - item.halfSize.y - item.amplitude);
                    break;

                case KeywordMotion.Orbit:
                    item.frequency = Random.Range(0.15f, 0.45f) * sway * (Random.value < 0.5f ? -1f : 1f);
                    item.radius = new Vector2(Random.Range(0.06f, 0.18f), Random.Range(0.04f, 0.12f)) * sway;
                    item.center = ClampCenter(item.center, item.halfSize + item.radius);
                    break;

                case KeywordMotion.Bob:
                    item.frequency = Random.Range(0.25f, 0.6f) * sway;
                    item.radius = new Vector2(Random.Range(0.008f, 0.025f), Random.Range(0.008f, 0.03f));
                    item.center = ClampCenter(item.center, item.halfSize + item.radius);
                    break;

                case KeywordMotion.Zigzag:
                    item.amplitude = Random.Range(1.2f, 3.0f);   // 방향을 꺾는 주기(초)
                    item.phase = Random.Range(0f, item.amplitude);
                    break;
            }
        }

        // ── 움직임 · 겹침 ──────────────────────────────────────────────────────

        /// <summary>
        /// 서로 너무 가까운 항목을 조금씩 밀어낸다.
        ///
        /// 상자를 타원으로 보고 겹친 정도만큼만 밀어서, 튕기지 않고 서서히 벌어지게 한다.
        /// 면적으로 나눠 밀어 큰 질문은 거의 그대로 있고 작은 해시태그가 비킨다. 나가는 항목은 밀지도 밀리지도 않는다.
        /// </summary>
        void Separate(float dt)
        {
            float strength = _config.separation;
            float padding = _config.separationPadding;

            for (int i = 0; strength > 0f && i < _items.Count; i++)
            {
                var a = _items[i];
                if (a.leaving) continue;

                for (int j = i + 1; j < _items.Count; j++)
                {
                    var b = _items[j];
                    if (b.leaving) continue;

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

                    float push = (1f - distance) * strength * dt;
                    float total = a.mass + b.mass;
                    Vector2 moveA = direction * push * (b.mass / total);
                    Vector2 moveB = direction * push * (a.mass / total);

                    a.position -= moveA;
                    b.position += moveB;

                    // 궤도를 도는 항목은 중심도 같이 옮겨야 다음 프레임에 제자리로 돌아가지 않는다.
                    a.center -= moveA;
                    b.center += moveB;
                }
            }

            for (int i = 0; i < _items.Count; i++)
                if (!_items[i].leaving) ClampInside(_items[i]);
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
                    // 거의 제자리. 아주 천천히 흔들린다.
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
        // 그림 크기 외에 설정값만큼 더 안쪽으로 들여 놓는다.
        Vector2 EdgeLimit(Item item)
        {
            float margin = _config.edgeMargin;
            return Vector2.Min(item.halfSize + new Vector2(margin, margin), new Vector2(0.45f, 0.45f));
        }

        // 화면 안에서만 움직이게 한다. 그림 크기를 반영해 잘리지 않는 선에서 튕긴다.
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
                (0.5f - item.position.y) * canvasSize.y + item.drawOffsetY);

            // 등장과 퇴장을 부드럽게. 갑자기 나타나면 배경이 아니라 알림처럼 보인다.
            float alpha = item.baseAlpha * Mathf.SmoothStep(0f, 1f, item.fade);

            var color = item.image.color;
            color.a = alpha;
            item.image.color = color;

            if (item.title != null)
            {
                var titleColor = item.title.color;
                titleColor.a = alpha;
                item.title.color = titleColor;
            }
        }

        /// <summary>
        /// 이미 떠 있는 것들과 가장 덜 붙는 자리를 고른다. 나가는 중인 것도 피한다 — 그 자리에서 사라지는 중이라
        /// 겹쳐 나타나면 두 글이 포개져 보인다.
        /// </summary>
        Vector2 FindOpenSpot(Item self)
        {
            Vector2 best = new Vector2(0.5f, 0.5f);
            float bestClearance = -1f;

            Vector2 limit = EdgeLimit(self);

            for (int attempt = 0; attempt < 16; attempt++)
            {
                var candidate = new Vector2(
                    Random.Range(limit.x, 1f - limit.x),
                    Random.Range(limit.y, 1f - limit.y));

                float clearance = float.MaxValue;

                for (int i = 0; i < _items.Count; i++)
                {
                    var other = _items[i];
                    if (other == self) continue;

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
    }
}
