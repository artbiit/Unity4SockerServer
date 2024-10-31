using JetBrains.Annotations;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Windows;

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
    public float RTT { get; private set; } = 0.0f;
    public float Latency { get; private set; } = 0.0f;
    private Dictionary<Packets.HandlerIds, Action<Response>> handlerMapper = new Dictionary<Packets.HandlerIds, Action<Response>> ();
    

    void Awake() {
        if (instance)
        {
            Debug.LogWarning("Already instantiated NetworkManager");
            Destroy(this);
            return;
         }

            
        instance = this;
        SetDeviceIdDefaultValue();
        handlerMapper.Add(Packets.HandlerIds.Init, HandleInitPacket);
        handlerMapper.Add(Packets.HandlerIds.LocationUpdate, HandleLocationPacket);
        handlerMapper.Add(Packets.HandlerIds.Ping, HandlePingPacket);
        wait = new WaitForSecondsRealtime(5);

        Application.wantsToQuit += OnWantsToQuit;
    }


    private bool OnWantsToQuit()
    {
        Disconnect();
        return true;
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


    bool IsValidIP(string ip)
    {
        // IPv4 regular expression: matches 4 groups of numbers ranging from 0-255
        string ipv4Pattern = @"^(\d{1,3}\.){3}\d{1,3}$";

        // IPv6 regular expression: matches 8 groups of hexadecimal numbers separated by colons (allows :: abbreviation)
        string ipv6Pattern = @"^([0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}$|^(?:[0-9a-fA-F]{1,4}:){1,7}:$|^:(:[0-9a-fA-F]{1,4}){1,7}$";

        // Check if the input matches IPv4 or IPv6 patterns
        if (Regex.IsMatch(ip, ipv4Pattern) || Regex.IsMatch(ip, ipv6Pattern))
        {
            return true;
        }

        // Regular expression to validate domain names
        var domainPattern = @"^(?!-)[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*(\.[A-Za-z]{2,})$";
        var domainRegex = new Regex(domainPattern);

        return domainRegex.IsMatch(ip);
    }


    bool IsValidPort(string port, out int result) =>  int.TryParse(port, out result) && result > 0 && result <= 65535;
    

     bool ConnectToServer(string ip, int port) {
        try {
            if (tcpClient?.Connected == true)
            {
                return true;
            }
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

        //Debug.Log($"Write[{handlerId}] : {header.Length}/{data.Length}/{packet.Length}");
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

        SendPacket(locationUpdatePayload, Packets.HandlerIds.LocationUpdatePayload);
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
        while (incompleteData.Count >= 4)
        {
            
            // 패킷 길이와 타입 읽기
            byte[] lengthBytes = incompleteData.GetRange(0, 4).ToArray();
            int packetLength = BitConverter.ToInt32(ToBigEndian(lengthBytes), 0);
          
            if (incompleteData.Count < packetLength)
            {
                // 데이터가 충분하지 않으면 반환
                return;
            }

            // 패킷 데이터 추출
            byte[] packetData = incompleteData.GetRange(4, packetLength - 4).ToArray();
            incompleteData.RemoveRange(0, packetLength);

            // Debug.Log($"Received packet: Length = {packetData.Length}");
            HandleResponse( packetData);
        }
    }

    void HandleResponse( byte[] packetData)
    {
        try { 
        var response = Packets.Deserialize<Response>(packetData);

            Packets.HandlerIds handlerId = (Packets.HandlerIds)response.handlerId;
            if (response.responseCode != 0 && !uiNotice.activeSelf)
            {
                AudioManager.instance.PlaySfx(AudioManager.Sfx.LevelUp);
                StartCoroutine(NoticeRoutine(2));
                return;
            }

            if(handlerMapper.TryGetValue(handlerId,out var handler))
            {
                handler(response);
            }
            
        }
        catch (Exception e)
        {
            Debug.LogError($"HandleResponseError : {e}");
        }
    }


    void HandlePingPacket(Response response)
    {
        Pong ping = Packets.FromJson<Pong>(response.data);
        RTT =  (float)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ping.timestamp);
        Latency = RTT * 0.5f;
        SendPacket(new PingPayload { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, Packets.HandlerIds.Ping);
    }

    void HandleInitPacket(Response response)
    {
        Initial initial = null;
        var data = response.data;
        if (data.Length > 0)
        {
            initial = Packets.FromJson<Initial>(data);
        }
        else
        {
            initial = new Initial();
            initial.allLocation =  new LocationUpdate.UserLocation[0];
        }

        GameManager.instance.userId = initial.userId;
        GameManager.instance.GameStart();
        GameManager.instance.player.transform.position = new Vector2(initial.x, initial.y);

        var groundRoot = GameObject.FindWithTag("GroundRoot");
        if (groundRoot)
        {
            groundRoot.transform.position = new Vector2(initial.x, initial.y);
        }
        Spawner.instance.Spawn(initial.allLocation);
    }

    void HandleLocationPacket(Response response) {

            LocationUpdate locationUpdate;

            var data = response.data;

            if (data.Length > 0) {
                // 패킷 데이터 처리
                locationUpdate = Packets.FromJson<LocationUpdate>(data);
                Spawner.instance?.Spawn(locationUpdate.users);
           } else {
                // data가 비어있을 경우 빈 배열을 전달
            //    locationUpdate = new LocationUpdate { users =   new  LocationUpdate.UserLocation[0] };
            }

        
    }

    
    private void OnDestroy()
    {
        Disconnect();
    }

    private void Disconnect()
    {
        if (tcpClient != null)
        {
            tcpClient.Close();
            tcpClient.Dispose();
            tcpClient = null;
        }
    }
}
