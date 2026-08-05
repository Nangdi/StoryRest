using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.Keyword
{
    /// <summary>
    /// 벽면에 과학 낱말이 떠다니는 화면. 프로젝터 한 대를 담당한다.
    ///
    /// 관람객 인터랙션이 없는 배경 화면이라 계속 돌아간다.
    /// 낱말마다 다른 움직임·크기·투명도를 주어 같은 패턴이 반복되어 보이지 않게 한다.
    ///
    /// 좌표는 화면을 0~1 로 보는 정규화 값이다. 해상도가 다른 프로젝터를 물려도 같은 그림이 나온다.
    /// </summary>
    [DisallowMultipleComponent]
    public class KeywordWall : MonoBehaviour
    {
        // ArUco 세트 카메라와 겹치지 않도록 반대 방향으로 떨어뜨린다.
        const float WallSeparation = -10000f;

        class Item
        {
            public TMP_Text label;
            public RectTransform rect;

            public KeywordMotion motion;
            public string word;

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

        Camera _camera;
        RectTransform _canvasRect;

        readonly List<Item> _items = new List<Item>();
        readonly HashSet<string> _onScreen = new HashSet<string>();

        int _wordCursor;

        public void Setup(KeywordWallConfig config, List<string> words, int displayIndex, int wallIndex)
        {
            _config = config;
            _words = words;
            _motions = config.ResolveMotions();
            _colors = config.ResolveColors();

            BuildScreen(displayIndex, wallIndex);

            if (_words == null || _words.Count == 0)
            {
                Debug.LogWarning("[Keyword] 낱말이 없어 빈 화면으로 둡니다. floor_<N>/keywords.txt 를 확인하세요.");
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
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            _canvasRect = canvasGo.GetComponent<RectTransform>();
        }

        Item CreateItem(int index)
        {
            var go = new GameObject($"Keyword{index}", typeof(RectTransform));
            go.transform.SetParent(_canvasRect, false);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.raycastTarget = false;
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;

            var rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);

            return new Item { label = label, rect = rect };
        }

        void Update()
        {
            if (_items.Count == 0 || _canvasRect == null) return;

            // 배경 화면이라 게임 시간 흐름(GameManager 의 timeScale)에 영향받지 않아야 한다.
            float dt = Time.unscaledDeltaTime;
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

                Move(item, dt);
            }

            // 움직임을 다 계산한 뒤에 겹친 것만 떼어 놓는다.
            // 낱말이 뭉쳐 읽을 수 없게 되는 것이 이 화면에서 가장 눈에 거슬리는 문제다.
            Separate(dt);

            for (int i = 0; i < _items.Count; i++) Apply(_items[i], canvasSize);
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
            if (strength <= 0f) return;

            float padding = _config.separationPadding;

            for (int i = 0; i < _items.Count; i++)
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

            for (int i = 0; i < _items.Count; i++) ClampInside(_items[i]);
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

            var color = item.label.color;
            color.a = alpha;
            item.label.color = color;
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
            if (!string.IsNullOrEmpty(item.word)) _onScreen.Remove(item.word);

            item.word = NextWord();
            item.motion = _motions[Random.Range(0, _motions.Count)];

            float fontSize = Random.Range(_config.minFontSize, _config.maxFontSize);
            item.label.text = item.word;
            item.label.fontSize = fontSize;

            var color = _colors[Random.Range(0, _colors.Count)];
            item.baseAlpha = Random.Range(_config.minAlpha, _config.maxAlpha);
            color.a = 0f;   // 첫 프레임부터 서서히 나타나게
            item.label.color = color;

            // 글자가 차지하는 크기를 알아야 화면 밖으로 나가는 것을 막을 수 있다.
            Vector2 preferred = item.label.GetPreferredValues();
            item.rect.sizeDelta = preferred;

            Vector2 canvasSize = _canvasRect.rect.size;
            item.halfSize = canvasSize.x > 0f && canvasSize.y > 0f
                ? new Vector2(preferred.x * 0.5f / canvasSize.x, preferred.y * 0.5f / canvasSize.y)
                : new Vector2(0.05f, 0.03f);

            // 글자가 화면보다 크면 경계 계산이 뒤집힌다. 여유를 남겨 둔다.
            item.halfSize = Vector2.Min(item.halfSize, new Vector2(0.45f, 0.45f));

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
        /// 목록을 순서대로 돌면서 낱말을 고른다. 매번 무작위로 뽑으면 어떤 낱말은 계속 안 나온다.
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
