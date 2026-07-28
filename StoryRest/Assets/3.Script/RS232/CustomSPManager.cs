using System.Collections.Generic;
using UnityEngine;

// 컨트롤러 명령 종류. 프로토콜의 (E, C) 자리에 대응


// 수신 한 줄을 파싱한 결과

public class CustomSPManager : SerialPortManager
{
    private const string Header = "80";
    private const int MinFrameLength = 4; // "80" + id(1) + cmd(1)
    private const int MinControllerId = 1;
    private const int MaxControllerId = 5;

    private Dictionary<int, string> nameByControllerId = new Dictionary<int, string>
    {
        { 1, "화력" },
        { 2, "수력" },
        { 3, "풍력" },
        { 4, "원자력" },
        { 5, "수소" }
    };

    protected override void Awake()
    {
        base.Awake();
    }

    protected override void Start()
    {
        base.Start();
    }
    void Update()
    {
        // 테스트용: 1번 컨트롤러에 E3C2 명령을 스페이스바 누를 때마다 송신
        if (Input.GetKeyDown(KeyCode.Q))
        {
            ReceivedData(1, "801E3C2");
            ReceivedData(2, "802E3C2");
            ReceivedData(3, "803E3C2");
            ReceivedData(4, "804E3C2");
            ReceivedData(5, "805E3C2");
        }
        if (Input.GetKeyDown(KeyCode.W))
        {
            SendToController(1, "테스트명령");
            SendToController(2, "테스트명령");
        }
    }
    // 부모 SerialPortManager가 줄바꿈으로 분리된 한 프레임을, 어느 포트에서 왔는지(controllerId)와 함께 넘겨줌
    protected override void ReceivedData(int controllerId, string data)
    {
        if (!TryParseMessage(controllerId, data))
        {
            Debug.LogWarning($"[Ctrl {controllerId}] 알 수 없는 프레임 형식: '{data}'");
            return;
        }

    }


    // ─────────────── 송신 헬퍼 ───────────────

    // 단일 컨트롤러로 송신: 해당 컨트롤러 전용 포트로 80 + id + cmd + payload 전송
    public void SendToController(int controllerId, string message)
    {
        if (controllerId < MinControllerId || controllerId > MaxControllerId)
        {
            Debug.LogWarning($"송신 거부 - 컨트롤러 번호 범위 밖: {controllerId}");
            return;
        }

        SendData(controllerId, message);
    }

    // 5개 모두에 동일 명령 일괄 송신
    public void BroadcastToAllControllers(string message)
    {
        for (int id = MinControllerId; id <= MaxControllerId; id++)
        {
            SendToController(id, message);
        }
    }

    // ─────────────── 파싱 ───────────────

    // 포트 라우팅으로 controllerId를 이미 알고 있으므로 프레임 내부 id는 검증 용도로만 비교한다.
    private bool TryParseMessage(int portControllerId, string data)
    {

        //데이터 형식 80(1~5)E(개수)C(개수)
        //data[0], data[1]은 '8', '0' 고정
        //data[2]는 프레임 id (1~5) - 포트 라우팅으로 이미 controllerId가 결정되어 있지만, 프레임 내부 id도 검증
        //data[3]는  E
        //data[4] E의 갯수
        //data[5]는 C
        //data[6] C의 갯수
        if (string.IsNullOrEmpty(data) || data.Length < MinFrameLength) return false;
        if (data[0] != Header[0] || data[1] != Header[1]) return false;

        char idCh = data[2];
        if (idCh < '0' + MinControllerId || idCh > '0' + MaxControllerId) return false;

        int frameId = idCh - '0';
        string messageToSend = CombineSendMessage(nameByControllerId[frameId], data[4]) + CombineSendMessage("탄소", data[6]);
        Debug.Log($"[Ctrl {portControllerId}] 수신 메시지 파싱 성공: '{data}' -> '{messageToSend}'");

        // 메인 서버로 TCP 전송
        if (TcpClientManager.Instance != null)
            TcpClientManager.Instance.Send(messageToSend);

        return true;
    }
    private string CombineSendMessage(string data, char cmd)
    {
        string messageToSend = data + ":" + cmd + ";";
        return messageToSend;
    }
}
