using System;
using System.Collections.Generic;

// StreamingAssets/aruco.json 의 데이터 구조.
// 현장에서 메모장으로 직접 고치거나, 편집모드(F1)에서 키보드로 조정한 값이 실시간으로 덮어써진다.
// 읽고 쓰는 일은 ArUcoConfigStore 가 맡는다.

[Serializable]
public class ArUcoCameraConfig
{
    // OpenCV VideoCapture 디바이스 인덱스. 0 = 첫 번째 카메라.
    public int deviceId = 0;
    // 0 = CAP_DSHOW(DirectShow, 권장) / 1 = CAP_ANY / 2 = CAP_MSMF
    // ArUcoDialInput 에서 검증된 대로 DirectShow 가 비전카메라 호환성이 가장 높다.
    public int backend = 0;
    // 0 이면 카메라 기본 해상도를 쓴다. 지정할 경우 카메라가 실제로 지원하는 값이어야 한다.
    public int width = 0;
    public int height = 0;
    public int fps = 0;
    // 주의: 이 값은 "거울처럼 보이게" 하는 용도가 아니다.
    // ArUco 는 좌우가 뒤집힌 마커를 인식하지 못하므로, 화면만 뒤집는 것은 불가능하다.
    // 카메라가 애초에 거울상으로 영상을 내보낼 때(전면 카메라 등) 그것을 바로잡는 용도로만 쓴다.
    public bool flipHorizontal = false;
    public bool flipVertical = false;
}

[Serializable]
public class ArUcoMarkerConfig
{
    public int id;
    // StreamingAssets/<imageFolder>/ 안의 파일명.
    // 비워두면 "<imagePrefix><id>" 규칙으로 찾는다. (기본: image_1.png)
    public string image = "";
    public bool enabled = true;

    // 이미지 가로폭 = 화면에 잡힌 마커 한 변 길이 x globalScale x scale
    public float scale = 1f;
    // 위치 오프셋. 마커 한 변 길이를 1 로 보는 상대값이라 카메라 거리가 바뀌어도 유지된다.
    // 마커와 함께 회전하므로 책을 돌려도 이미지는 페이지의 같은 자리에 머문다.
    public float offsetX = 0f;
    public float offsetY = 0f;
    // 마커 각도에 더해줄 각도(도). 마커를 비뚤게 붙였을 때 보정용.
    public float rotationOffset = 0f;
}

[Serializable]
public class ArUcoJson
{
    public ArUcoCameraConfig camera = new ArUcoCameraConfig();

    // OpenCV predefined dictionary 번호. 0 = DICT_4X4_50.
    // 마커를 다시 인쇄해야 하므로 초기에 정해둘 것. 값을 모르면 autoScanDictionary 를 켠다.
    public int dictionaryId = 0;

    // 인쇄한 마커의 딕셔너리를 모를 때, 마커 후보만 잡히고 인식이 안 되면
    // 전체 딕셔너리를 훑어 어느 것인지 콘솔에 알려준다. (0 = 사용 안 함)
    public int autoScanIntervalFrames = 60;

    // StreamingAssets 아래의 이미지 폴더와 파일 이름 규칙.
    // 기본값 기준 경로는 StreamingAssets/Images/image_1.png (1번 마커) 이다.
    public string imageFolder = "Images";
    public string imagePrefix = "image_";

    // 시작할 때 카메라 영상을 화면에 띄울지. false 면 검은 배경 위에 이미지만 보인다.
    // 어느 쪽이든 C 키로 켜고 끌 수 있다.
    public bool showCameraOnStart = true;

    // true 면 마커 네 꼭짓점으로 페이지 평면을 복원해 이미지를 그 위에 눕힌다.
    // 책을 기울이거나 넘길 때 이미지도 같이 기울어지며 원근이 생긴다.
    // false 면 이전처럼 화면상 회전과 크기만 맞추고 이미지는 항상 정면을 본다.
    // 마커가 작거나 화질이 나빠 원근이 심하게 떨리면 false 로 돌려 쓸 수 있다. (편집모드 P)
    public bool perspectiveMapping = true;

    // 원근 매핑에서 면을 몇 칸으로 나눠 그릴지. 값이 클수록 정확하지만 정점이 늘어난다.
    // 삼각형 2장(=1)만 쓰면 대각선을 경계로 그림이 꺾여 보이므로 최소 6 이상을 권한다.
    public int warpSubdivisions = 10;

    // 모든 이미지에 공통으로 곱해지는 배율. 현장에서 전체 크기를 한 번에 맞출 때 쓴다.
    public float globalScale = 1f;

    // 0 = 즉시 반응(떨림 많음), 1 에 가까울수록 부드럽지만 늦게 따라온다.
    public float smoothing = 0.4f;

    // 마커를 놓쳤을 때 이미지를 몇 초 더 붙잡아 둘지.
    // ArUcoDialMaterialBridge 의 briefMarkerLossSeconds 와 같은 취지로, 손이 스칠 때 깜빡임을 막는다.
    public float holdSeconds = 0.3f;

    // 동시에 표시할 최대 개수. 0 이면 제한 없음. 1 로 두면 가장 크게 잡힌 마커만 나온다.
    public int maxSimultaneous = 0;

    // 편집모드에서 키를 한 번 누를 때 움직이는 양. Shift 를 같이 누르면 1/5 로 줄어든다.
    public float adjustPositionStep = 0.02f;
    public float adjustScaleStep = 0.02f;
    public float adjustRotationStep = 1f;

    public List<ArUcoMarkerConfig> markers = new List<ArUcoMarkerConfig>();

    public ArUcoMarkerConfig Find(int id)
    {
        if (markers == null) return null;
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i] != null && markers[i].id == id)
                return markers[i];
        }
        return null;
    }

    // 설정에 없는 마커가 카메라에 잡히면 기본값 항목을 만들어 둔다.
    // 새 페이지를 추가할 때 json 을 미리 손보지 않아도 편집모드에서 바로 잡을 수 있다.
    public ArUcoMarkerConfig GetOrCreate(int id)
    {
        var found = Find(id);
        if (found != null) return found;

        if (markers == null) markers = new List<ArUcoMarkerConfig>();
        var created = new ArUcoMarkerConfig { id = id };
        markers.Add(created);
        return created;
    }
}
