using System.IO;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.UnityIntegration;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 책에 붙일 ArUco 마커를 png 로 뽑는 에디터 창. (메뉴: Tools / ArUco 마커 생성)
///
/// 인쇄한 마커의 딕셔너리와 aruco.json 의 dictionaryId 가 반드시 같아야 인식된다.
/// 마커 둘레의 흰 여백(quiet zone)이 없으면 검출률이 크게 떨어지므로 기본값을 유지할 것.
/// </summary>
public class ArUcoMarkerGeneratorWindow : EditorWindow
{
    static readonly string[] DictionaryNames =
    {
        "DICT_4X4_50", "DICT_4X4_100", "DICT_4X4_250", "DICT_4X4_1000",
        "DICT_5X5_50", "DICT_5X5_100", "DICT_5X5_250", "DICT_5X5_1000",
        "DICT_6X6_50", "DICT_6X6_100", "DICT_6X6_250", "DICT_6X6_1000",
        "DICT_7X7_50", "DICT_7X7_100", "DICT_7X7_250", "DICT_7X7_1000",
        "DICT_ARUCO_ORIGINAL",
    };

    static readonly int[] DictionaryIds =
    {
        Objdetect.DICT_4X4_50, Objdetect.DICT_4X4_100, Objdetect.DICT_4X4_250, Objdetect.DICT_4X4_1000,
        Objdetect.DICT_5X5_50, Objdetect.DICT_5X5_100, Objdetect.DICT_5X5_250, Objdetect.DICT_5X5_1000,
        Objdetect.DICT_6X6_50, Objdetect.DICT_6X6_100, Objdetect.DICT_6X6_250, Objdetect.DICT_6X6_1000,
        Objdetect.DICT_7X7_50, Objdetect.DICT_7X7_100, Objdetect.DICT_7X7_250, Objdetect.DICT_7X7_1000,
        Objdetect.DICT_ARUCO_ORIGINAL,
    };

    int _dictionaryIndex;
    int _firstId;
    int _lastId = 9;
    int _markerPixels = 600;
    float _quietZoneRatio = 0.25f;
    bool _drawLabel = true;
    string _outputFolder = "";

    [MenuItem("Tools/ArUco 마커 생성")]
    static void Open()
    {
        GetWindow<ArUcoMarkerGeneratorWindow>(true, "ArUco 마커 생성");
    }

    void OnEnable()
    {
        if (string.IsNullOrEmpty(_outputFolder))
            _outputFolder = Path.Combine(Directory.GetCurrentDirectory(), "ArUcoMarkers");
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "책 페이지에 붙일 마커를 png 로 만듭니다.\n" +
            "여기서 고른 딕셔너리와 StreamingAssets/aruco.json 의 dictionaryId 가 같아야 인식됩니다.",
            MessageType.Info);

        EditorGUILayout.Space();

        _dictionaryIndex = EditorGUILayout.Popup("딕셔너리", _dictionaryIndex, DictionaryNames);
        EditorGUILayout.LabelField(" ", $"aruco.json 의 dictionaryId = {DictionaryIds[_dictionaryIndex]}");

        EditorGUILayout.Space();

        _firstId = EditorGUILayout.IntField("시작 ID", Mathf.Max(0, _firstId));
        _lastId = EditorGUILayout.IntField("끝 ID", Mathf.Max(_firstId, _lastId));
        _markerPixels = EditorGUILayout.IntField("마커 한 변 픽셀", Mathf.Max(50, _markerPixels));
        _quietZoneRatio = EditorGUILayout.Slider("흰 여백 (마커 대비)", _quietZoneRatio, 0f, 0.5f);
        _drawLabel = EditorGUILayout.Toggle("아래에 ID 숫자 인쇄", _drawLabel);

        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("저장 폴더", GUILayout.Width(70));
            EditorGUILayout.SelectableLabel(_outputFolder, EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button("변경", GUILayout.Width(50)))
            {
                string picked = EditorUtility.SaveFolderPanel("마커를 저장할 폴더", _outputFolder, "");
                if (!string.IsNullOrEmpty(picked)) _outputFolder = picked;
            }
        }

        EditorGUILayout.Space();

        int count = _lastId - _firstId + 1;
        using (new EditorGUI.DisabledScope(count <= 0))
        {
            if (GUILayout.Button($"마커 {Mathf.Max(0, count)}개 생성", GUILayout.Height(32)))
                Generate();
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "인쇄 팁\n" +
            "· 무광 용지에 인쇄하세요. 코팅지는 조명 반사로 인식이 끊깁니다.\n" +
            "· 마커 실물이 클수록 멀리서도 잡힙니다. 책 페이지라면 3~5cm 를 권장합니다.\n" +
            "· 마커 둘레의 흰 여백을 잘라내지 마세요. 검출에 필요합니다.",
            MessageType.None);
    }

    void Generate()
    {
        if (!Directory.Exists(_outputFolder)) Directory.CreateDirectory(_outputFolder);

        int dictionaryId = DictionaryIds[_dictionaryIndex];
        string dictionaryName = DictionaryNames[_dictionaryIndex];
        int border = Mathf.RoundToInt(_markerPixels * _quietZoneRatio);
        int labelHeight = _drawLabel ? Mathf.RoundToInt(_markerPixels * 0.18f) : 0;

        using (var dictionary = Objdetect.getPredefinedDictionary(dictionaryId))
        {
            for (int id = _firstId; id <= _lastId; id++)
            {
                using (var marker = new Mat(_markerPixels, _markerPixels, CvType.CV_8UC1))
                using (var padded = new Mat())
                using (var rgba = new Mat())
                {
                    Objdetect.generateImageMarker(dictionary, id, _markerPixels, marker, 1);

                    Core.copyMakeBorder(marker, padded, border, border + labelHeight, border, border,
                        Core.BORDER_CONSTANT, new Scalar(255));

                    Imgproc.cvtColor(padded, rgba, Imgproc.COLOR_GRAY2RGBA);

                    if (_drawLabel)
                    {
                        string label = $"{id}  ({dictionaryName})";
                        double fontScale = _markerPixels / 600.0 * 1.1;
                        int thickness = Mathf.Max(1, Mathf.RoundToInt(_markerPixels / 400f));
                        var size = Imgproc.getTextSize(label, Imgproc.FONT_HERSHEY_SIMPLEX, fontScale, thickness, new int[1]);

                        var origin = new Point(
                            (rgba.cols() - size.width) * 0.5,
                            rgba.rows() - border * 0.5);

                        Imgproc.putText(rgba, label, origin, Imgproc.FONT_HERSHEY_SIMPLEX,
                            fontScale, new Scalar(0, 0, 0, 255), thickness, Imgproc.LINE_AA, false);
                    }

                    var texture = new Texture2D(rgba.cols(), rgba.rows(), TextureFormat.RGBA32, false);
                    OpenCVMatUtils.MatToTexture2D(rgba, texture);

                    string path = Path.Combine(_outputFolder, $"{dictionaryName}_{id}.png");
                    File.WriteAllBytes(path, texture.EncodeToPNG());
                    DestroyImmediate(texture);
                }
            }
        }

        Debug.Log($"[ArUco] 마커 {_lastId - _firstId + 1}개를 저장했습니다: {_outputFolder}");
        EditorUtility.RevealInFinder(_outputFolder);
    }
}
