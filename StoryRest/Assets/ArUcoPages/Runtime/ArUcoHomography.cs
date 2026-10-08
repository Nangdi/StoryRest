using UnityEngine;

/// <summary>
/// 단위 정사각형 (0,0)-(1,0)-(1,1)-(0,1) 을 임의의 사각형으로 보내는 사영변환(호모그래피).
///
/// 마커가 영상에 찍힌 네 꼭짓점만 있으면 "마커가 놓인 평면" 전체가 결정된다.
/// 그 평면 위의 좌표를 그대로 옮겨 그리면 책을 기울이거나 넘길 때
/// 이미지도 페이지 면과 같이 눕는다. 카메라 캘리브레이션(내부 파라미터)이 필요 없다.
///
/// 4점 대응은 닫힌 해가 있어서 반복 풀이(findHomography)나 Mat 할당 없이 계산된다.
/// Heckbert, "Projective Mappings for Image Warping" 의 유도를 그대로 쓴다.
/// </summary>
public struct ArUcoHomography
{
    float _a, _b, _c;
    float _d, _e, _f;
    float _g, _h;

    public bool IsValid { get; private set; }

    /// <param name="p0">(0,0) 이 갈 자리 — 마커 좌상단</param>
    /// <param name="p1">(1,0) 이 갈 자리 — 마커 우상단</param>
    /// <param name="p2">(1,1) 이 갈 자리 — 마커 우하단</param>
    /// <param name="p3">(0,1) 이 갈 자리 — 마커 좌하단</param>
    public static ArUcoHomography FromUnitSquare(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        var m = new ArUcoHomography();

        float dx1 = p1.x - p2.x, dx2 = p3.x - p2.x, sx = p0.x - p1.x + p2.x - p3.x;
        float dy1 = p1.y - p2.y, dy2 = p3.y - p2.y, sy = p0.y - p1.y + p2.y - p3.y;

        float den = dx1 * dy2 - dx2 * dy1;
        if (Mathf.Abs(den) < 1e-6f)
        {
            // 네 점이 거의 한 직선 위에 놓였다 — 평면이 결정되지 않는다.
            m.IsValid = false;
            return m;
        }

        if (Mathf.Abs(sx) < 1e-6f && Mathf.Abs(sy) < 1e-6f)
        {
            // 평행사변형이면 원근 성분이 없다. 아핀으로 처리해 0 나눗셈을 피한다.
            m._g = 0f;
            m._h = 0f;
        }
        else
        {
            m._g = (sx * dy2 - dx2 * sy) / den;
            m._h = (dx1 * sy - sx * dy1) / den;
        }

        m._a = p1.x - p0.x + m._g * p1.x;
        m._b = p3.x - p0.x + m._h * p3.x;
        m._c = p0.x;
        m._d = p1.y - p0.y + m._g * p1.y;
        m._e = p3.y - p0.y + m._h * p3.y;
        m._f = p0.y;

        m.IsValid = true;
        return m;
    }

    /// <summary>
    /// 대응점 N쌍(N ≥ 4)에 가장 잘 맞는 변환을 최소제곱으로 구한다. <paramref name="from"/>[i] 가 <paramref name="to"/>[i] 로 간다.
    ///
    /// 4쌍이면 FromUnitSquare 를 두 번 거친 것과 같은 답이 나오고, 그보다 많으면 점마다의 측정 오차가
    /// 서로 상쇄된다. 좌표 크기가 다른 두 공간(카메라 픽셀 ↔ 0~1 정규화)을 그대로 풀면 수치가 나빠지므로
    /// 양쪽을 원점 중심 · 평균거리 √2 로 맞춘 뒤 푼다(Hartley 정규화). 계산은 double 로 한다.
    /// </summary>
    /// <returns>점이 모자라거나 한 직선에 몰려 변환이 정해지지 않으면 false.</returns>
    public static bool TryFit(Vector2[] from, Vector2[] to, out ArUcoHomography result)
    {
        result = new ArUcoHomography();

        int n = from != null ? from.Length : 0;
        if (n < 4 || to == null || to.Length != n) return false;

        if (!TryNormalize(from, out double fx, out double fy, out double fs)) return false;
        if (!TryNormalize(to, out double tx, out double ty, out double ts)) return false;

        // u = (a x + b y + c) / (g x + h y + 1) 를 a x + b y + c - g x u - h y u = u 로 펴서 정규방정식을 쌓는다.
        var ata = new double[8, 8];
        var atb = new double[8];
        var row = new double[8];

        for (int i = 0; i < n; i++)
        {
            double x = (from[i].x - fx) * fs, y = (from[i].y - fy) * fs;
            double u = (to[i].x - tx) * ts, v = (to[i].y - ty) * ts;

            row[0] = x; row[1] = y; row[2] = 1; row[3] = 0; row[4] = 0; row[5] = 0; row[6] = -x * u; row[7] = -y * u;
            Accumulate(ata, atb, row, u);

            row[0] = 0; row[1] = 0; row[2] = 0; row[3] = x; row[4] = y; row[5] = 1; row[6] = -x * v; row[7] = -y * v;
            Accumulate(ata, atb, row, v);
        }

        if (!TrySolve(ata, atb, 8)) return false;

        // 정규화 공간의 해를 원래 좌표로 되돌린다: H = T_to⁻¹ · Hn · T_from
        double[] hn = { atb[0], atb[1], atb[2], atb[3], atb[4], atb[5], atb[6], atb[7], 1 };
        double[] tFrom = { fs, 0, -fs * fx, 0, fs, -fs * fy, 0, 0, 1 };
        double[] tToInv = { 1 / ts, 0, tx, 0, 1 / ts, ty, 0, 0, 1 };
        double[] h = Mul3(tToInv, Mul3(hn, tFrom));

        if (System.Math.Abs(h[8]) < 1e-12) return false;

        result._a = (float)(h[0] / h[8]); result._b = (float)(h[1] / h[8]); result._c = (float)(h[2] / h[8]);
        result._d = (float)(h[3] / h[8]); result._e = (float)(h[4] / h[8]); result._f = (float)(h[5] / h[8]);
        result._g = (float)(h[6] / h[8]); result._h = (float)(h[7] / h[8]);
        result.IsValid = true;
        return true;
    }

    static bool TryNormalize(Vector2[] points, out double cx, out double cy, out double scale)
    {
        cx = 0; cy = 0; scale = 0;
        for (int i = 0; i < points.Length; i++) { cx += points[i].x; cy += points[i].y; }
        cx /= points.Length;
        cy /= points.Length;

        double mean = 0;
        for (int i = 0; i < points.Length; i++)
        {
            double dx = points[i].x - cx, dy = points[i].y - cy;
            mean += System.Math.Sqrt(dx * dx + dy * dy);
        }
        mean /= points.Length;

        if (mean < 1e-12) return false;   // 전부 한 점에 겹쳤다
        scale = System.Math.Sqrt(2) / mean;
        return true;
    }

    static void Accumulate(double[,] ata, double[] atb, double[] row, double rhs)
    {
        for (int r = 0; r < 8; r++)
        {
            if (row[r] == 0) continue;
            for (int c = 0; c < 8; c++) ata[r, c] += row[r] * row[c];
            atb[r] += row[r] * rhs;
        }
    }

    static double[] Mul3(double[] a, double[] b)
    {
        var m = new double[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        return m;
    }

    /// <summary>
    /// 정사각 연립방정식 A x = b 를 부분 피벗 가우스 소거로 푼다. 답은 b 자리에 남는다.
    /// 보정 계산(이 파일과 ArUcoProjection 의 왜곡 보간)이 같이 쓴다.
    /// </summary>
    /// <returns>A 가 특이(점이 한 직선에 몰린 경우 등)하면 false.</returns>
    public static bool TrySolve(double[,] a, double[] b, int size)
    {
        for (int col = 0; col < size; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < size; r++)
                if (System.Math.Abs(a[r, col]) > System.Math.Abs(a[pivot, col])) pivot = r;

            if (System.Math.Abs(a[pivot, col]) < 1e-12) return false;

            if (pivot != col)
            {
                for (int c = 0; c < size; c++) { double t = a[col, c]; a[col, c] = a[pivot, c]; a[pivot, c] = t; }
                double tb = b[col]; b[col] = b[pivot]; b[pivot] = tb;
            }

            for (int r = col + 1; r < size; r++)
            {
                double k = a[r, col] / a[col, col];
                if (k == 0) continue;
                for (int c = col; c < size; c++) a[r, c] -= k * a[col, c];
                b[r] -= k * b[col];
            }
        }

        for (int r = size - 1; r >= 0; r--)
        {
            double sum = b[r];
            for (int c = r + 1; c < size; c++) sum -= a[r, c] * b[c];
            b[r] = sum / a[r, r];
        }
        return true;
    }

    /// <summary>항등 변환. 아직 캘리브레이션하지 않은 구간에서 "아무것도 바꾸지 않음" 으로 쓴다.</summary>
    public static ArUcoHomography Identity()
    {
        var m = new ArUcoHomography();
        m._a = 1f; m._b = 0f; m._c = 0f;
        m._d = 0f; m._e = 1f; m._f = 0f;
        m._g = 0f; m._h = 0f;
        m.IsValid = true;
        return m;
    }

    /// <summary>
    /// 반대 방향 변환. FromUnitSquare 가 만든 것을 뒤집으면 "임의의 사각형 → 단위 정사각형" 이 된다.
    ///
    /// 카메라 픽셀을 프로젝터 좌표로 옮기려면 (카메라 사각형 → 단위 정사각형 → 프로젝터 사각형)
    /// 순으로 거쳐야 하는데, 그 첫 구간이 이것이다.
    /// </summary>
    public bool TryGetInverse(out ArUcoHomography inverse)
    {
        inverse = new ArUcoHomography();
        if (!IsValid) return false;

        // 행렬 [[a,b,c],[d,e,f],[g,h,1]] 의 여인수. 역행렬은 여인수 행렬의 전치를 det 로 나눈 것이다.
        float k0 = _e - _f * _h;
        float k1 = _f * _g - _d;
        float k2 = _d * _h - _e * _g;

        float det = _a * k0 + _b * k1 + _c * k2;
        if (Mathf.Abs(det) < 1e-12f) return false;

        float i00 = k0;
        float i01 = _c * _h - _b;
        float i02 = _b * _f - _c * _e;
        float i10 = k1;
        float i11 = _a - _c * _g;
        float i12 = _c * _d - _a * _f;
        float i20 = k2;
        float i21 = _b * _g - _a * _h;
        float i22 = _a * _e - _b * _d;

        // 사영변환은 스케일이 자유롭다. 마지막 성분을 1 로 맞춰 같은 8-파라미터 형태로 되돌린다.
        // (det 로 나눌 필요가 없어지는 것도 이 정규화 덕분이다.)
        if (Mathf.Abs(i22) < 1e-12f) return false;

        inverse._a = i00 / i22; inverse._b = i01 / i22; inverse._c = i02 / i22;
        inverse._d = i10 / i22; inverse._e = i11 / i22; inverse._f = i12 / i22;
        inverse._g = i20 / i22; inverse._h = i21 / i22;
        inverse.IsValid = true;
        return true;
    }

    /// <summary>
    /// 두 변환을 하나로 합친다. 먼저 <paramref name="first"/> 를 거친 뒤 <paramref name="second"/> 를 거치는 것과 같다.
    ///
    /// 마커 하나에 격자 정점이 수백 개씩 생기므로, 점마다 두 번 매핑하는 대신 미리 합쳐 둔다.
    /// </summary>
    public static bool TryConcat(ArUcoHomography second, ArUcoHomography first, out ArUcoHomography result)
    {
        result = new ArUcoHomography();
        if (!second.IsValid || !first.IsValid) return false;

        // second * first (행 우선 3x3, 마지막 원소는 1 로 정규화되어 있다)
        float m00 = second._a * first._a + second._b * first._d + second._c * first._g;
        float m01 = second._a * first._b + second._b * first._e + second._c * first._h;
        float m02 = second._a * first._c + second._b * first._f + second._c;

        float m10 = second._d * first._a + second._e * first._d + second._f * first._g;
        float m11 = second._d * first._b + second._e * first._e + second._f * first._h;
        float m12 = second._d * first._c + second._e * first._f + second._f;

        float m20 = second._g * first._a + second._h * first._d + first._g;
        float m21 = second._g * first._b + second._h * first._e + first._h;
        float m22 = second._g * first._c + second._h * first._f + 1f;

        if (Mathf.Abs(m22) < 1e-12f) return false;

        result._a = m00 / m22; result._b = m01 / m22; result._c = m02 / m22;
        result._d = m10 / m22; result._e = m11 / m22; result._f = m12 / m22;
        result._g = m20 / m22; result._h = m21 / m22;
        result.IsValid = true;
        return true;
    }

    /// <summary>
    /// 마커 평면 좌표(단위 정사각형 기준)를 영상 픽셀 좌표로 옮긴다.
    ///
    /// 마커 면이 카메라와 거의 평행해지면(=거의 옆에서 볼 때) 분모가 0 을 지나며
    /// 점이 무한대로 튄다. 그런 프레임은 false 를 돌려 표시를 건너뛰게 한다.
    /// </summary>
    public bool TryMap(Vector2 uv, out Vector2 result)
    {
        float w = _g * uv.x + _h * uv.y + 1f;
        if (w < 1e-3f)
        {
            result = Vector2.zero;
            return false;
        }

        result = new Vector2(
            (_a * uv.x + _b * uv.y + _c) / w,
            (_d * uv.x + _e * uv.y + _f) / w);
        return true;
    }
}
