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

        public bool IsCalibrated => _calibrated;

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

            var cameraPoints = calibration.cameraPoints;
            var projectorPoints = calibration.projectorPoints;

            // 카메라 사각형 → 단위 정사각형 → 프로젝터 사각형 순으로 거친다.
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
            return projection;
        }

        /// <summary>
        /// 카메라 픽셀 → 프로젝터 정규화 좌표.
        /// 마커 면이 카메라와 거의 평행해 좌표가 발산하면 false 를 돌린다.
        /// </summary>
        public bool TryMap(Vector2 cameraPixel, out Vector2 projectorNormalized)
        {
            if (_calibrated)
                return _camToProjector.TryMap(cameraPixel, out projectorNormalized);

            projectorNormalized = new Vector2(
                cameraPixel.x * _invCameraWidth,
                cameraPixel.y * _invCameraHeight);
            return true;
        }
    }
}
