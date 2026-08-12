using System.Collections.Generic;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.UnityIntegration;
using UnityEngine;

namespace StoryRest.ArUco
{
    /// <summary>
    /// ArUco 마커를 런타임에 텍스처로 만든다.
    ///
    /// 캘리브레이션에서 프로젝터가 마커를 직접 투사하고 카메라가 그 빛을 읽는다(→ ARCHITECTURE §1).
    /// 인쇄물이 아니라 화면에 띄우는 마커라 런타임 생성이 필요하다.
    /// </summary>
    public static class ArUcoMarkerTexture
    {
        // (딕셔너리, ID, 크기) 조합마다 한 번만 만든다. 매 프레임 만들 물건이 아니다.
        static readonly Dictionary<long, Texture2D> _cache = new Dictionary<long, Texture2D>();

        /// <param name="sizePx">여백을 뺀 마커 자체의 한 변 픽셀 수</param>
        /// <returns>만들지 못하면 null. 딕셔너리에 없는 ID 가 대표적인 원인이다.</returns>
        ///
        /// <remarks>
        /// 마커는 흑백 그대로 둔다. 프로젝터는 빛을 더하기만 하므로, 어두운 칸에 아무것도 쏘지 않는
        /// 검은색이 가장 어둡다. 다른 색을 쓰면 그만큼 밝아져 오히려 대비가 줄어든다.
        /// (흰 종이 기준 — 검정 칸 대비 55, 파란 칸 대비 31)
        /// 밝은 투사면에서 인식이 안 되는 것은 색이 아니라 투사면 밝기 탓이다. → docs/ARCHITECTURE.md §1
        /// </remarks>
        public static Texture2D Get(int dictionaryId, int markerId, int sizePx = 256)
        {
            long key = ((long)dictionaryId << 40) ^ ((long)markerId << 16) ^ sizePx;
            if (_cache.TryGetValue(key, out var cached) && cached != null) return cached;

            var texture = Create(dictionaryId, markerId, sizePx);

            // 실패를 캐시하면 원인을 고친 뒤에도 계속 실패한다.
            if (texture != null) _cache[key] = texture;

            return texture;
        }

        /// <summary>
        /// 그 딕셔너리가 가진 마커 개수(= 쓸 수 있는 ID 는 0 ~ 개수-1).
        /// 예: DICT_4X4_100 은 100개뿐이라 240 번 같은 ID 는 존재하지 않는다.
        /// </summary>
        public static int GetDictionarySize(int dictionaryId)
        {
            Dictionary dictionary = null;
            Mat bytes = null;

            try
            {
                dictionary = Objdetect.getPredefinedDictionary(dictionaryId);
                bytes = dictionary.get_bytesList();
                return bytes != null ? bytes.rows() : 0;
            }
            catch
            {
                return 0;
            }
            finally
            {
                bytes?.Dispose();
                dictionary?.Dispose();
            }
        }

        static Texture2D Create(int dictionaryId, int markerId, int sizePx)
        {
            sizePx = Mathf.Clamp(sizePx, 32, 1024);

            // 마커 주변의 흰 여백(quiet zone)은 장식이 아니라 검출의 일부다. 없으면 인식되지 않는다.
            int quiet = Mathf.Max(4, sizePx / 8);

            Dictionary dictionary = null;
            Mat markerMat = null;
            Mat padded = null;
            Mat rgba = null;

            try
            {
                dictionary = Objdetect.getPredefinedDictionary(dictionaryId);
                markerMat = new Mat();
                Objdetect.generateImageMarker(dictionary, markerId, sizePx, markerMat, 1);

                // 딕셔너리에 없는 ID 를 넘기면 예외 없이 빈 Mat 이 돌아온다.
                // 그대로 두면 "여백만 있는 흰 사각형" 이 만들어져 원인을 찾기 어려워진다.
                if (markerMat.empty() || markerMat.width() == 0)
                {
                    int size = GetDictionarySize(dictionaryId);
                    Debug.LogError($"[ArUco] {markerId}번 마커를 만들지 못했습니다. " +
                                   $"딕셔너리 {dictionaryId} 이 가진 ID 는 0~{Mathf.Max(0, size - 1)} 입니다.");
                    return null;
                }

                padded = new Mat();
                Core.copyMakeBorder(markerMat, padded, quiet, quiet, quiet, quiet,
                    Core.BORDER_CONSTANT, new Scalar(255));

                rgba = new Mat();
                Imgproc.cvtColor(padded, rgba, Imgproc.COLOR_GRAY2RGBA);

                var texture = new Texture2D(rgba.width(), rgba.height(), TextureFormat.RGBA32, false);

                // 보간되면 마커 경계가 흐려져 검출률이 떨어진다. 확대해도 각을 유지해야 한다.
                texture.filterMode = FilterMode.Point;
                texture.wrapMode = TextureWrapMode.Clamp;

                OpenCVMatUtils.MatToTexture2D(rgba, texture);
                texture.name = $"ArUco_{dictionaryId}_{markerId}";
                return texture;
            }
            finally
            {
                dictionary?.Dispose();
                markerMat?.Dispose();
                padded?.Dispose();
                rgba?.Dispose();
            }
        }

        /// <summary>
        /// 만들어진 텍스처를 다시 검출해 본다. 카메라 없이도 마커가 올바른지(좌우 반전 등) 확인할 수 있다.
        /// 검출된 ID 를 돌려주며, 실패하면 -1.
        /// </summary>
        public static int SelfTest(Texture2D texture, int dictionaryId)
        {
            if (texture == null) return -1;

            Mat rgba = null;
            Mat gray = null;
            Dictionary dictionary = null;
            ArucoDetector detector = null;
            Mat ids = null;
            var corners = new List<Mat>();
            var rejected = new List<Mat>();

            try
            {
                rgba = new Mat(texture.height, texture.width, CvType.CV_8UC4);
                OpenCVMatUtils.Texture2DToMat(texture, rgba);

                gray = new Mat();
                Imgproc.cvtColor(rgba, gray, Imgproc.COLOR_RGBA2GRAY);

                dictionary = Objdetect.getPredefinedDictionary(dictionaryId);
                detector = new ArucoDetector(dictionary, new DetectorParameters());
                ids = new Mat();
                detector.detectMarkers(gray, corners, ids, rejected);

                if (ids.total() == 0) return -1;

                var buffer = new int[1];
                ids.get(0, 0, buffer);
                return buffer[0];
            }
            finally
            {
                foreach (var m in corners) m.Dispose();
                foreach (var m in rejected) m.Dispose();
                ids?.Dispose();
                detector?.Dispose();
                dictionary?.Dispose();
                gray?.Dispose();
                rgba?.Dispose();
            }
        }
    }
}
