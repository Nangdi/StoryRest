using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// 카메라 픽셀 좌표를 프로젝터 좌표로 옮긴다(H_camproj).
    ///
    /// 카메라와 프로젝터는 위치도 렌즈도 다른 별개의 장치라, 카메라가 마커를 본 자리를
    /// 그대로 쏘면 실물과 어긋난다. 투사면이 평면이므로 대응점 4쌍이면 변환이 결정된다.
    /// 배경은 docs/ARCHITECTURE.md §1.
    ///
    /// 두 단계로 옮긴다. 어디까지 쓸지는 calibration.model 이 정한다(기본 "square").
    ///  1. 전체 맞춤 — 대응점 전부에 가장 잘 맞는 변환(최소제곱). 점이 많을수록 측정 오차가 상쇄된다.
    ///     "square" 는 이동 · 회전 · 균일 크기만(정사각형이 정사각형으로 남는다), "plane" · "warp" 는 호모그래피.
    ///  2. 왜곡 보간("warp" 만) — 카메라 렌즈의 휨(가장자리로 갈수록 커지는 술통 왜곡)은 평면 변환으로 설명되지 않는다.
    ///     보정 점마다 1 이 남긴 어긋남을 재 두고, 그 사이를 thin-plate spline 으로 매끄럽게 이어 더한다.
    ///     보정 점 위에서는 측정값과 정확히 일치하고, 점 사이는 가장 덜 휘는 면으로 채워진다.
    ///     점이 네 개뿐이면 1 이 이미 정확히 지나가므로 2 는 아무 일도 하지 않는다(예전 4점 보정과 같다).
    ///
    /// 좌표계
    ///  - 입력: 카메라 영상 픽셀. 좌상단이 원점이고 y 는 아래로 증가한다(OpenCV Mat 기준).
    ///  - 출력: 프로젝터 정규화 좌표 0~1. 방향은 입력과 같게 잡아(좌상단 원점, y 아래로)
    ///          두 좌표계를 같은 방향으로 유지한다. UI 로 올릴 때만 y 를 뒤집는다.
    /// </summary>
    public struct ArUcoProjection
    {
        ArUcoHomography _camToProjector;
        bool _calibrated;

        // 미보정 상태에서 쓸 단순 정규화 계수.
        float _invCameraWidth;
        float _invCameraHeight;

        // ── 왜곡 보간(thin-plate spline). _warpNodes 가 null 이면 평면 변환만 쓴다. ──
        // 좌표는 모두 "x 에 화면비를 곱한" 프로젝터 좌표다 — 가로세로 거리를 화면 픽셀 비율대로 재기 위해서다.
        Vector2[] _warpNodes;
        Vector2[] _warpWeights;
        Vector2 _warpA0, _warpAx, _warpAy;   // 아핀 항: a0 + ax·x + ay·y
        Vector2 _warpMin, _warpMax;          // 보간 범위. 밖은 경계 값으로 붙잡아 멀리서 튀지 않게 한다
        float _aspect;

        // 보정 점마다 평면 변환만으로 남는 어긋남(정규화 좌표). 품질 보고용.
        Vector2[] _planeResiduals;

        public bool IsCalibrated => _calibrated;

        /// <summary>왜곡 보간이 켜져 있는지(보정 점이 5개 이상일 때).</summary>
        public bool HasWarp => _warpNodes != null;

        // ── 세부 조정(calibration.fine*). 보정 결과 전체를 통째로 옮기고 돌리고 키운다. ──
        // 화면비를 곱한 공간(가로세로 같은 단위)에서 기준점을 중심으로 회전 · 확대한 뒤 이동한다.
        bool _fine;
        Vector2 _finePivot;
        float _fineCos, _fineSin;   // 배율이 곱해진 회전
        Vector2 _fineOffset;
        Vector2 _fineTilt;
        float _fineAspect;

        /// <summary>
        /// 캘리브레이션 값으로 변환을 세운다. 카메라 해상도는 검출기가 열린 뒤에야 알 수 있으므로
        /// 첫 프레임이 도착한 다음에 한 번 호출한다.
        ///
        /// 보정값이 없으면 카메라 화각이 곧 투사 화각이라고 가정한 단순 정규화로 동작한다.
        /// 정합은 맞지 않지만 화면은 뜨므로, 캘리브레이션 전에도 작업을 이어갈 수 있다.
        /// </summary>
        public static ArUcoProjection Build(CalibrationConfig calibration, int cameraWidth, int cameraHeight, string setName)
        {
            var projection = new ArUcoProjection
            {
                _invCameraWidth = cameraWidth > 0 ? 1f / cameraWidth : 0f,
                _invCameraHeight = cameraHeight > 0 ? 1f / cameraHeight : 0f,
                _calibrated = false,
            };

            if (calibration == null || !calibration.IsUsable) return projection;

            if (calibration.HasPairs)
            {
                bool fitted = calibration.model == CalibrationConfig.ModelSquare
                    ? TryFitSquare(calibration.pairCamera, calibration.pairProjector, calibration.projectorAspect, out projection._camToProjector)
                    : ArUcoHomography.TryFit(calibration.pairCamera, calibration.pairProjector, out projection._camToProjector);

                if (!fitted)
                {
                    Debug.LogError($"[ArUco] 세트 '{setName}' 캘리브레이션 점들이 한 직선에 몰려 있습니다. 보정을 다시 해야 합니다.");
                    return projection;
                }

                projection._calibrated = true;
                projection.BuildWarp(calibration, setName);
                projection.SetupFineAdjust(calibration);
                return projection;
            }

            // 예전 4점 보정 — 카메라 사각형 → 단위 정사각형 → 프로젝터 사각형 순으로 거친다.
            var cameraPoints = calibration.cameraPoints;
            var projectorPoints = calibration.projectorPoints;

            var toCamera = ArUcoHomography.FromUnitSquare(
                cameraPoints[0], cameraPoints[1], cameraPoints[2], cameraPoints[3]);

            if (!toCamera.IsValid || !toCamera.TryGetInverse(out var fromCamera))
            {
                Debug.LogError($"[ArUco] 세트 '{setName}' 캘리브레이션의 카메라 점 4개가 한 직선에 가깝습니다. " +
                               $"보정을 다시 해야 합니다.");
                return projection;
            }

            var toProjector = ArUcoHomography.FromUnitSquare(
                projectorPoints[0], projectorPoints[1], projectorPoints[2], projectorPoints[3]);

            if (!toProjector.IsValid)
            {
                Debug.LogError($"[ArUco] 세트 '{setName}' 캘리브레이션의 프로젝터 점 4개가 한 직선에 가깝습니다.");
                return projection;
            }

            // 점마다 두 번 매핑하지 않도록 미리 하나로 합친다(마커 하나에 격자 정점이 수백 개다).
            if (!ArUcoHomography.TryConcat(toProjector, fromCamera, out projection._camToProjector))
            {
                Debug.LogError($"[ArUco] 세트 '{setName}' 캘리브레이션 변환을 세우지 못했습니다.");
                return projection;
            }

            projection._calibrated = true;
            projection.SetupFineAdjust(calibration);
            return projection;
        }

        void SetupFineAdjust(CalibrationConfig calibration)
        {
            _fine = calibration.HasFineAdjust;
            if (!_fine) return;

            _fineAspect = calibration.projectorAspect > 0f ? calibration.projectorAspect : 16f / 9f;

            Vector2 pivot = Vector2.zero;
            var quad = calibration.projectorPoints;
            for (int i = 0; i < quad.Length; i++) pivot += quad[i];
            pivot /= Mathf.Max(1, quad.Length);
            _finePivot = new Vector2(pivot.x * _fineAspect, pivot.y);

            // 좌표가 y 아래로 커지므로 이 회전은 화면에서 시계 방향이다.
            float rad = calibration.fineRotation * Mathf.Deg2Rad;
            _fineCos = Mathf.Cos(rad) * calibration.fineScale;
            _fineSin = Mathf.Sin(rad) * calibration.fineScale;
            _fineOffset = calibration.fineOffset;
            _fineTilt = calibration.fineTilt;
        }

        Vector2 ApplyFineAdjust(Vector2 projector)
        {
            Vector2 d = new Vector2(projector.x * _fineAspect, projector.y) - _finePivot;

            // 원근 기울기 — 기준점에서 멀어질수록 한쪽은 커지고 반대쪽은 작아진다(기준점 자체는 그대로).
            // y 는 아래로 커지므로 "위쪽이 넓어진다" 는 d.y 가 음수인 쪽의 분모가 작아지는 것이다.
            float w = 1f - _fineTilt.x * d.x + _fineTilt.y * d.y;
            w = Mathf.Max(0.2f, w);   // 기울기를 과하게 줘도 반대편이 뒤집히지 않게
            d /= w;

            Vector2 p = _finePivot + new Vector2(d.x * _fineCos - d.y * _fineSin, d.x * _fineSin + d.y * _fineCos) + _fineOffset;
            return new Vector2(p.x / _fineAspect, p.y);
        }

        /// <summary>
        /// 보정 점마다 평면 변환이 남긴 어긋남을 재고, 그것을 매끄럽게 잇는 면을 푼다.
        /// 자동 보정은 마커 꼭짓점 넷이 한 점을 이루므로 넷의 평균을 그 점의 값으로 쓴다.
        /// </summary>
        void BuildWarp(CalibrationConfig calibration, string setName)
        {
            int per = calibration.pairsPerPoint;
            int count = calibration.pairCamera.Length / per;

            _aspect = calibration.projectorAspect;

            var nodes = new Vector2[count];
            var residuals = new Vector2[count];

            for (int k = 0; k < count; k++)
            {
                Vector2 node = Vector2.zero;
                Vector2 residual = Vector2.zero;

                for (int m = 0; m < per; m++)
                {
                    int i = k * per + m;
                    Vector2 target = calibration.pairProjector[i];

                    // 보간 면은 "평면 변환이 보낸 자리" 에서 읽힌다(→ TryMap). 그러니 점도 그 자리에 세워야
                    // 보정 점 위에서 정확히 목표로 돌아온다. 목표 자리에 세우면 어긋남만큼 비켜 읽혀 오차가 남는다.
                    if (!_camToProjector.TryMap(calibration.pairCamera[i], out Vector2 mapped)) mapped = target;

                    node += mapped;
                    residual += target - mapped;
                }

                nodes[k] = node / per;
                residuals[k] = residual / per;
            }

            _planeResiduals = residuals;

            // 휨 보간은 "warp" 모델에서만 한다. 나머지는 격자 전체를 한 모양으로 맞추는 것이 목적이라
            // 점마다 따로 당기면 그 목적이 깨진다.
            if (calibration.model != CalibrationConfig.ModelWarp) return;

            // 수동 4점이면 평면 변환이 이미 정확히 지나가 어긋남이 0 이라 보간해도 달라지지 않는다.
            // 자동 4점(꼭짓점 16쌍)은 최소제곱이라 점마다 조금씩 남으므로 보간이 의미가 있다.

            for (int k = 0; k < count; k++) nodes[k].x *= _aspect;

            if (!TrySolveSpline(nodes, residuals, out var weights, out var a0, out var ax, out var ay))
            {
                Debug.LogWarning($"[ArUco] 세트 '{setName}' 왜곡 보간을 풀지 못해 평면 변환만 씁니다. " +
                                 "보정 점이 한 직선에 몰렸거나 같은 자리에 겹쳤을 수 있습니다.");
                return;
            }

            Vector2 min = nodes[0], max = nodes[0];
            for (int k = 1; k < count; k++)
            {
                min = Vector2.Min(min, nodes[k]);
                max = Vector2.Max(max, nodes[k]);
            }

            // 점들이 둘러싼 범위보다 조금 밖까지는 면을 그대로 따르고, 그 너머는 경계 값을 유지한다.
            // thin-plate spline 은 멀리 갈수록 아핀으로 뻗어 나가 엉뚱한 보정이 커질 수 있다.
            Vector2 margin = (max - min) * 0.15f;
            _warpMin = min - margin;
            _warpMax = max + margin;

            _warpNodes = nodes;
            _warpWeights = weights;
            _warpA0 = a0;
            _warpAx = ax;
            _warpAy = ay;
        }

        /// <summary>
        /// 이동 · 회전 · 균일 크기만으로 대응점 전부에 가장 잘 맞는 변환을 구한다(최소제곱).
        /// 정사각형이 어디서나 같은 크기의 정사각형으로 남는다 — 점 하나하나를 지나가지 않는 대신
        /// 짚을 때 생긴 오차가 평균으로 흡수된다.
        ///
        /// 프로젝터 정규화 좌표는 가로세로 단위가 달라(0~1 이 1920px 와 1080px) 그대로 맞추면 정사각형이 눌린다.
        /// 가로에 화면비를 곱한 공간에서 맞춘 뒤 되돌린다. 카메라 픽셀은 원래 정사각 픽셀이다.
        /// 카메라가 거울상으로 보는 설치도 있을 수 있어 뒤집힌 해도 함께 구해 오차가 작은 쪽을 쓴다.
        /// </summary>
        static bool TryFitSquare(Vector2[] camera, Vector2[] projector, float aspect, out ArUcoHomography result)
        {
            result = default;
            int n = camera.Length;
            if (n < 2 || projector.Length != n || aspect <= 0f) return false;

            double pcx = 0, pcy = 0, qcx = 0, qcy = 0;
            for (int i = 0; i < n; i++)
            {
                pcx += camera[i].x; pcy += camera[i].y;
                qcx += projector[i].x * aspect; qcy += projector[i].y;
            }
            pcx /= n; pcy /= n; qcx /= n; qcy /= n;

            double s = 0, proper1 = 0, proper2 = 0, mirror1 = 0, mirror2 = 0;
            for (int i = 0; i < n; i++)
            {
                double px = camera[i].x - pcx, py = camera[i].y - pcy;
                double qx = projector[i].x * aspect - qcx, qy = projector[i].y - qcy;

                s += px * px + py * py;
                proper1 += px * qx + py * qy;   // [a -b; b a]
                proper2 += px * qy - py * qx;
                mirror1 += px * qx - py * qy;   // [a b; b -a]
                mirror2 += py * qx + px * qy;
            }

            if (s < 1e-9) return false;   // 점이 전부 한 자리에 겹쳤다

            double pa = proper1 / s, pb = proper2 / s;
            double ma = mirror1 / s, mb = mirror2 / s;

            // 오차 합 = Σ|q|² - (a² + b²)·s 이므로 계수 크기가 큰 쪽이 더 잘 맞는다.
            bool mirrored = ma * ma + mb * mb > pa * pa + pb * pb;
            double a = mirrored ? ma : pa, b = mirrored ? mb : pb;
            if (a * a + b * b < 1e-18) return false;

            // 닫힌 형태를 직접 넣는 대신, 이 변환으로 옮긴 네 점을 4점 풀이에 넘겨 같은 호모그래피 형태로 만든다.
            var from = new Vector2[4];
            var to = new Vector2[4];
            for (int k = 0; k < 4; k++)
            {
                double dx = (k == 1 || k == 2) ? 100 : -100;
                double dy = k >= 2 ? 100 : -100;

                double qx = mirrored ? a * dx + b * dy : a * dx - b * dy;
                double qy = mirrored ? b * dx - a * dy : b * dx + a * dy;

                from[k] = new Vector2((float)(pcx + dx), (float)(pcy + dy));
                to[k] = new Vector2((float)((qcx + qx) / aspect), (float)(qcy + qy));
            }

            return ArUcoHomography.TryFit(from, to, out result);
        }

        /// <summary>
        /// thin-plate spline: f(p) = a0 + ax·x + ay·y + Σ wᵢ·φ(|p - nodeᵢ|), φ(r) = r² ln r.
        /// [K P; Pᵀ 0][w; a] = [v; 0] 를 x · y 성분 따로 푼다.
        /// </summary>
        static bool TrySolveSpline(Vector2[] nodes, Vector2[] values,
            out Vector2[] weights, out Vector2 a0, out Vector2 ax, out Vector2 ay)
        {
            int n = nodes.Length;
            int size = n + 3;

            weights = null;
            a0 = ax = ay = Vector2.zero;

            var solution = new double[2][];

            for (int axis = 0; axis < 2; axis++)
            {
                var a = new double[size, size];
                var b = new double[size];

                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++) a[i, j] = Kernel(nodes[i], nodes[j]);

                    a[i, n] = 1; a[i, n + 1] = nodes[i].x; a[i, n + 2] = nodes[i].y;
                    a[n, i] = 1; a[n + 1, i] = nodes[i].x; a[n + 2, i] = nodes[i].y;

                    b[i] = axis == 0 ? values[i].x : values[i].y;
                }

                if (!ArUcoHomography.TrySolve(a, b, size)) return false;
                solution[axis] = b;
            }

            weights = new Vector2[n];
            for (int i = 0; i < n; i++) weights[i] = new Vector2((float)solution[0][i], (float)solution[1][i]);

            a0 = new Vector2((float)solution[0][n], (float)solution[1][n]);
            ax = new Vector2((float)solution[0][n + 1], (float)solution[1][n + 1]);
            ay = new Vector2((float)solution[0][n + 2], (float)solution[1][n + 2]);
            return true;
        }

        static float Kernel(Vector2 p, Vector2 q)
        {
            float r2 = (p - q).sqrMagnitude;
            return r2 < 1e-12f ? 0f : 0.5f * r2 * Mathf.Log(r2);   // r² ln r = ½ r² ln r²
        }

        Vector2 WarpAt(Vector2 projector)
        {
            var p = new Vector2(projector.x * _aspect, projector.y);
            p = Vector2.Max(_warpMin, Vector2.Min(_warpMax, p));

            Vector2 sum = _warpA0 + _warpAx * p.x + _warpAy * p.y;
            for (int i = 0; i < _warpNodes.Length; i++) sum += _warpWeights[i] * Kernel(p, _warpNodes[i]);
            return sum;
        }

        /// <summary>
        /// 카메라 픽셀 → 프로젝터 정규화 좌표.
        /// 마커 면이 카메라와 거의 평행해 좌표가 발산하면 false 를 돌린다.
        /// </summary>
        public bool TryMap(Vector2 cameraPixel, out Vector2 projectorNormalized)
        {
            if (_calibrated)
            {
                if (!_camToProjector.TryMap(cameraPixel, out projectorNormalized)) return false;
                if (_warpNodes != null) projectorNormalized += WarpAt(projectorNormalized);
                if (_fine) projectorNormalized = ApplyFineAdjust(projectorNormalized);
                return true;
            }

            projectorNormalized = new Vector2(
                cameraPixel.x * _invCameraWidth,
                cameraPixel.y * _invCameraHeight);
            return true;
        }

        /// <summary>
        /// 평면 변환 하나로 설명되지 않고 남은 어긋남을 화면 픽셀로 보고한다(왜곡 보간이 흡수하는 양).
        /// 한 점만 유독 크면 그 점을 잘못 짚었을 가능성이 높다 — 그 점의 순번을 함께 준다.
        /// </summary>
        /// <returns>새 방식 보정이 아니라 잴 것이 없으면 false.</returns>
        public bool TryGetPlaneError(Vector2 canvasSize, out float meanPx, out float maxPx, out int worstPoint)
        {
            meanPx = maxPx = 0f;
            worstPoint = -1;
            if (_planeResiduals == null || _planeResiduals.Length == 0) return false;

            for (int k = 0; k < _planeResiduals.Length; k++)
            {
                float px = new Vector2(_planeResiduals[k].x * canvasSize.x, _planeResiduals[k].y * canvasSize.y).magnitude;
                meanPx += px;

                if (px <= maxPx) continue;
                maxPx = px;
                worstPoint = k;
            }

            meanPx /= _planeResiduals.Length;
            return true;
        }
    }
}
