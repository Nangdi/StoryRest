using UnityEngine;
using UnityEngine.UI;

namespace StoryRest.Keyword
{
    /// <summary>
    /// 두 색을 꼭짓점 색으로 곱해 그리는 RawImage. 흰 글씨 스프라이트에 그라데이션을 입히는 용도다(→ SPEC §3.1).
    ///
    /// 스프라이트는 사각형 4꼭짓점이라 한쪽 끝을 from, 반대쪽을 to 로 주면 GPU 가 그 사이를 선형 보간한다 —
    /// "보라 → 회색" 같은 두 색 직선 그라데이션이 셰이더 없이 나온다. 셋 이상의 색이나 곡선은 안 된다(이미지에 굽는다).
    /// 이미지에 이미 색이 있으면 곱해져 탁해지므로 흰색(밝은) 글씨여야 한다.
    ///
    /// Graphic.color(투명도 페이드에 쓴다)는 base 가 이미 꼭짓점에 넣어 두므로 여기서는 그 위에 곱하기만 한다.
    /// </summary>
    public class KeywordGradientImage : RawImage
    {
        Color _from = Color.white;
        Color _to = Color.white;
        Vector2 _direction = Vector2.right;   // from → to 방향(단위 벡터)
        bool _tinted;

        /// <summary>그라데이션을 정한다. angleDegrees 0 = 왼쪽→오른쪽, 90 = 위→아래.</summary>
        public void SetGradient(Color from, Color to, float angleDegrees)
        {
            _from = from;
            _to = to;
            _tinted = true;

            float rad = angleDegrees * Mathf.Deg2Rad;
            // UI 는 y 가 위로 커지므로 "90 = 위→아래" 가 되게 y 를 뒤집는다.
            _direction = new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad));

            SetVerticesDirty();
        }

        /// <summary>원본 색 그대로 그린다.</summary>
        public void ClearGradient()
        {
            if (!_tinted) return;
            _tinted = false;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            base.OnPopulateMesh(vh);
            if (!_tinted) return;

            // 꼭짓점 위치를 방향에 투영해 0~1 로 편다. 사각형이라 최소·최대만 있으면 된다.
            Rect r = GetPixelAdjustedRect();
            var corners = new[]
            {
                new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin),
                new Vector2(r.xMin, r.yMax), new Vector2(r.xMax, r.yMax),
            };

            float min = float.MaxValue, max = float.MinValue;
            foreach (var c in corners)
            {
                float d = Vector2.Dot(c, _direction);
                if (d < min) min = d;
                if (d > max) max = d;
            }
            float span = Mathf.Max(max - min, 1e-4f);

            var vertex = new UIVertex();
            for (int i = 0; i < vh.currentVertCount; i++)
            {
                vh.PopulateUIVertex(ref vertex, i);

                float t = (Vector2.Dot((Vector2)vertex.position, _direction) - min) / span;
                Color tinted = vertex.color;
                tinted *= Color.Lerp(_from, _to, t);
                vertex.color = tinted;

                vh.SetUIVertex(vertex, i);
            }
        }
    }
}
