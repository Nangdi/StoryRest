using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 직사각형이 아닌 임의의 사각형(사다리꼴 등)으로 텍스처를 그리는 UI 그래픽.
///
/// RawImage 는 RectTransform 모양 그대로만 그려서 원근을 표현할 수 없다.
/// 여기서는 면을 잘게 나눈 격자의 각 꼭짓점을 호모그래피로 직접 옮겨 놓고 그린다.
///
/// 삼각형 2장짜리 사각형을 쓰면 UV 가 선형 보간되어 대각선을 경계로 그림이 꺾여 보인다.
/// 격자로 잘게 나누면 각 칸 안의 오차가 눈에 안 보일 만큼 작아져
/// 전용 셰이더 없이 기본 UI 머티리얼로도 원근이 제대로 나온다.
/// </summary>
[DisallowMultipleComponent]
public class ArUcoWarpedImage : Graphic
{
    Texture _texture;
    bool _flipV;

    readonly List<Vector2> _nodes = new List<Vector2>();
    int _cols;
    int _rows;

    public override Texture mainTexture => _texture != null ? _texture : Texture2D.whiteTexture;

    /// <summary>
    /// 세로로 뒤집어 그린다. 영상 디코더가 내놓는 텍스처는 플랫폼에 따라 위아래가 반대라
    /// (AVPro 의 TextureProducer.RequiresVerticalFlip 참고) 그 경우에만 켠다.
    /// </summary>
    public bool FlipV
    {
        get => _flipV;
        set
        {
            if (_flipV == value) return;
            _flipV = value;
            SetVerticesDirty();
        }
    }

    /// <summary>Texture2D 든 RenderTexture 든 외부 디코더 텍스처든 받는다.</summary>
    public void SetTexture(Texture texture)
    {
        if (_texture == texture) return;
        _texture = texture;
        SetMaterialDirty();
    }

    /// <summary>
    /// 격자 꼭짓점을 넘긴다. 순서는 왼쪽아래부터 가로 우선, 개수는 (cols+1)*(rows+1).
    /// 좌표는 이 RectTransform 의 로컬 좌표(= 카메라 프레임 로컬)다.
    /// </summary>
    public void SetNodes(int cols, int rows, List<Vector2> nodes)
    {
        _cols = cols;
        _rows = rows;
        _nodes.Clear();
        _nodes.AddRange(nodes);
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        int nx = _cols + 1;
        int ny = _rows + 1;
        if (_cols < 1 || _rows < 1 || _nodes.Count < nx * ny) return;

        Color32 tint = color;

        for (int y = 0; y < ny; y++)
        {
            float v = (float)y / _rows;
            if (_flipV) v = 1f - v;

            for (int x = 0; x < nx; x++)
            {
                Vector2 p = _nodes[y * nx + x];
                vh.AddVert(new Vector3(p.x, p.y, 0f), tint, new Vector2((float)x / _cols, v));
            }
        }

        for (int y = 0; y < _rows; y++)
        {
            for (int x = 0; x < _cols; x++)
            {
                int i = y * nx + x;
                vh.AddTriangle(i, i + nx, i + nx + 1);
                vh.AddTriangle(i, i + nx + 1, i + 1);
            }
        }
    }
}
