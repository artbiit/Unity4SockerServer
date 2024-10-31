using JetBrains.Annotations;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UI;

public class NetworkManager : MonoBehaviour
{
    public static NetworkManager instance;

    public InputField ipInputField;
    public InputField portInputField;
    public InputField deviceIdInputField;
    public GameObject uiNotice;
    private TcpClient tcpClient;
    private NetworkStream stream;
    
    WaitForSecondsRealtime wait;

    private byte[] receiveBuffer = new byte[4096];
    private List<byte> incompleteData = new List<byte>();
    private uint sequence = 0;

    void Awake() {        
        instance = this;
        SetDeviceIdDefaultValue();
        wait = new WaitForSecondsRealtime(5);
    }

    private void SetDeviceIdDefaultValue()
    {
        string deviceID = SystemInfo.deviceUniqueIdentifier.Substring(0, 5); // 기기 ID 일부만 사용

        
        string uniqueID = null;
#if UNITY_EDITOR
        uniqueID = $"{deviceID}_edtior";
#else
        // 인스턴스 카운트 증가 및 설정
        int instanceCount = PlayerPrefs.GetInt("InstanceCount", 0);
        uniqueID =  $"{deviceID}_{instanceCount}";
        // 인스턴스 카운트 증가 저장
        PlayerPrefs.SetInt("InstanceCount", instanceCount + 1);
        PlayerPrefs.Save();
#endif



        deviceIdInputField.text = uniqueID;
    }
    public void OnStartButtonClicked() {
        string ip = ipInputField.text;
        string port = portInputField.text;

        if (IsValidPort(port, out int portNumber) && IsValidIP(ip)) {

            if (deviceIdInputField.text != "") {
                GameManager.instance.deviceId = deviceIdInputField.text;
            } else {
                if (GameManager.instance.deviceId == "") {
                    GameManager.instance.deviceId = GenerateUniqueID();
                }
            }
  
            if (ConnectToServer(ip, portNumber)) {
                StartGame();
            } else {
                AudioManager.instance.PlaySfx(AudioManager.Sfx.LevelUp);
                StartCoroutine(NoticeRoutine(1));
            }
            
        } else {
            AudioManager.instance.PlaySfx(AudioManager.Sfx.LevelUp);
            StartCoroutine(NoticeRoutine(0));
        }
    }

    /// <summary>
    /// 포트없이 IPv4와 IPv6을 사용했는지 검사합니다.
    /// </summary>
    /// <param name="ip">ip 주소</param>
    /// <returns>정상 여부</returns>
    bool IsValidIP(string ip)
    {
        // IPv4 정규식: 0-255 범위의 4개 숫자 그룹
        string ipv4Pattern = @"^(\d{1,3}\.){3}\d{1,3}$";

        // IPv6 정규식: 콜론으로 구분된 8개의 16진수 그룹 (:: 생략 허용)
        string ipv6Pattern = @"^([0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}$|^(?:[0-9a-fA-F]{1,4}:){1,7}:$|^:(:[0-9a-fA-F]{1,4}){1,7}$";

        return Regex.IsMatch(ip, ipv4Pattern) || Regex.IsMatch(ip, ipv6Pattern);
    }


    bool IsValidPort(string port, out int result) =>  int.TryParse(port, out result) && result > 0 && result <= 65535;
    

     bool ConnectToServer(string ip, int port) {
        try {
            tcpClient = new TcpClient(ip, port);
            stream = tcpClient.GetStream();
            Debug.Log($"Connected to {ip}:{port}");

            return true;
        } catch (SocketException e) {
            Debug.LogError($"SocketException: {e}");
            return false;
        }
    }

    string GenerateUniqueID() {
        return System.Guid.NewGuid().ToString();
    }

    void StartGame()
    {
        // 게임 시작 코드 작성
        Debug.Log("Game Started");
        StartReceiving(); // Start receiving data
        SendInitialPacket();
    }

    IEnumerator NoticeRoutine(int index) {
        
        uiNotice.SetActive(true);
        uiNotice.transform.GetChild(index).gameObject.SetActive(true);

        yield return wait;

        uiNotice.SetActive(false);
        uiNotice.transform.GetChild(index).gameObject.SetActive(false);
    }

    public static byte[] ToBigEndian(byte[] bytes) {
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }
        return bytes;
    }

    byte[] CreatePacketHeader(int dataLength, Packets.HandlerIds handlerId) {
        int packetLength = 4 + 1 + dataLength; // 전체 패킷 길이 (헤더 포함)
        byte[] header = new byte[5]; // 4바이트 길이 + 1바이트 타입

        // 첫 4바이트: 패킷 전체 길이
        byte[] lengthBytes = BitConverter.GetBytes(packetLength);
        lengthBytes = ToBigEndian(lengthBytes);
        Array.Copy(lengthBytes, 0, header, 0, 4);

        // 다음 1바이트: 핸들러 종류
        header[4] = (byte)handlerId;

        return header;
    }

    // 공통 패킷 생성 함수
    async void SendPacket<T>(T payload, Packets.HandlerIds handlerId)
    {
        // ArrayBufferWriter<byte>를 사용하여 직렬화
        var payloadWriter = new ArrayBufferWriter<byte>();

        Packets.Serialize(payloadWriter, payload);
        byte[] payloadData = payloadWriter.WrittenSpan.ToArray();

        CommonPacket commonPacket = new CommonPacket
        {
            userId = GameManager.instance.deviceId,
            sequence = sequence,
            payload = payloadData,
        };

        // ArrayBufferWriter<byte>를 사용하여 직렬화
        var commonPacketWriter = new ArrayBufferWriter<byte>();
        Packets.Serialize(commonPacketWriter, commonPacket);
        byte[] data = commonPacketWriter.WrittenSpan.ToArray();

        // 헤더 생성
        byte[] header = CreatePacketHeader(data.Length, handlerId);

        // 패킷 생성
        byte[] packet = new byte[header.Length + data.Length];
        Array.Copy(header, 0, packet, 0, header.Length);
        Array.Copy(data, 0, packet, header.Length, data.Length);

        await Task.Delay(GameManager.instance.latency);
        
        // 패킷 전송
        stream.Write(packet, 0, packet.Length);
    }

    void SendInitialPacket() {
        InitialPayload initialPayload = new InitialPayload
        {
            deviceId = GameManager.instance.deviceId,
            clientVersion = Application.version,
            playerId = GameManager.instance.playerId
        };

        // handlerId는 0으로 가정
        SendPacket(initialPayload, Packets.HandlerIds.Init);
    }

    public void SendLocationUpdatePacket(float x, float y) {
        LocationUpdatePayload locationUpdatePayload = new LocationUpdatePayload
        {
            x = x,
            y = y,
        };

        SendPacket(locationUpdatePayload, Packets.HandlerIds.LocationUpdate);
    }


    void StartReceiving() {
        _ = ReceivePacketsAsync();
    }

    async System.Threading.Tasks.Task ReceivePacketsAsync() {
        while (tcpClient.Connected) {
            try {
                int bytesRead = await stream.ReadAsync(receiveBuffer, 0, receiveBuffer.Length);
                if (bytesRead > 0) {
                    ProcessReceivedData(receiveBuffer, bytesRead);
                }
            } catch (Exception e) {
                Debug.LogError($"Receive error: {e.Message}");
                break;
            }
        }
    }

    void ProcessReceivedData(byte[] data, int length) {
         incompleteData.AddRange(data.AsSpan(0, length).ToArray());

        while (incompleteData.Count >= 5)
        {
            // 패킷 길이와 타입 읽기
            byte[] lengthBytes = incompleteData.GetRange(0, 4).ToArray();
            int packetLength = BitConverter.ToInt32(ToBigEndian(lengthBytes), 0);
            Packets.HandlerIds handlerId = (Packets.HandlerIds)incompleteData[4];

            if (incompleteData.Count < packetLength)
            {
                // 데이터가 충분하지 않으면 반환
                return;
            }

            // 패킷 데이터 추출
            byte[] packetData = incompleteData.GetRange(5, packetLength - 5).ToArray();
            incompleteData.RemoveRange(0, packetLength);

            // Debug.Log($"Received packet: Length = {packetLength}, Type = {packetType}");

            switch (handlerId)
            {
/*                case Packets.PacketType.Normal:
                    HandleNormalPacket(packetData);
                    break;
                case Packets.PacketType.Location:
                    HandleLocationPacket(packetData);
                    break;*/
            }
        }
    }

    void HandleNormalPacket(byte[] packetData) {
        // 패킷 데이터 처리
        var response = Packets.Deserialize<Response>(packetData);
        // Debug.Log($"HandlerId: {response.handlerId}, responseCode: {response.responseCode}, timestamp: {response.timestamp}");
        
        if (response.responseCode != 0 && !uiNotice.activeSelf) {
            AudioManager.instance.PlaySfx(AudioManager.Sfx.LevelUp);
            StartCoroutine(NoticeRoutine(2));
            return;
        }

        if (response.data != null && response.data.Length > 0) {
            if (response.handlerId == 0) {
                GameManager.instance.GameStart();
            }
            ProcessResponseData(response.data);
        }
    }

    void ProcessResponseData(byte[] data) {
        try {
            // var specificData = Packets.Deserialize<SpecificDataType>(data);
            string jsonString = Encoding.UTF8.GetString(data);
            Debug.Log($"Processed SpecificDataType: {jsonString}");
        } catch (Exception e) {
            Debug.LogError($"Error processing response data: {e.Message}");
        }
    }

    void HandleLocationPacket(byte[] data) {
        try {
            LocationUpdate response;

            if (data.Length > 0) {
                // 패킷 데이터 처리
                response = Packets.Deserialize<LocationUpdate>(data);
            } else {
                // data가 비어있을 경우 빈 배열을 전달
                response = new LocationUpdate { users = new List<LocationUpdate.UserLocation>() };
            }

            Spawner.instance.Spawn(response);
        } catch (Exception e) {
            Debug.LogError($"Error HandleLocationPacket: {e.Message}");
        }
    }
}
