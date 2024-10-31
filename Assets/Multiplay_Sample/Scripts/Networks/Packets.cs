using UnityEngine;
using ProtoBuf;
using System.IO;
using System.Buffers;
using System.Collections.Generic;
using System;

public static class Packets
{
    /// <summary>
    /// 송수신을 위한 ID, 서버에서도 같은 값으로 보내옴
    /// </summary>
    public enum HandlerIds : uint {
        Ping = 1,
        LocationUpdatePayload = 2,
        LocationUpdate = 3,
        Init = 4,
        
    }

    public static void Serialize<T>(IBufferWriter<byte> writer, T data)
    {
        Serializer.Serialize(writer, data);
    }

    public static T Deserialize<T>(byte[] data) {
        try {
            using (var stream = new MemoryStream(data)) {
                return ProtoBuf.Serializer.Deserialize<T>(stream);
            }
        } catch (Exception ex) {
            Debug.LogError($"Deserialize: Failed to deserialize data. Exception: {ex}");
            throw;
        }
    }
}

#region Request
[ProtoContract]
public class InitialPayload
{
    [ProtoMember(1, IsRequired = true)]
    public string deviceId { get; set; }

    [ProtoMember(2, IsRequired =true)]
    public string clientVersion { get; set; }

    [ProtoMember(3, IsRequired = true)]
    public uint playerId { get; set; }

    
}

[ProtoContract]
public class CommonPacket
{

    [ProtoMember(1)]
    public string userId { get; set; }

    [ProtoMember(2)]
    public uint sequence { get; set; }

    [ProtoMember(3)]
    public byte[] payload { get; set; }
}

[ProtoContract]
public class LocationUpdatePayload {
    [ProtoMember(1, IsRequired = true)]
    public float x { get; set; }
    [ProtoMember(2, IsRequired = true)]
    public float y { get; set; }
}

#endregion


#region Response

/// <summary>
/// 공통 응답 메시지 구조
/// </summary>
[ProtoContract]
public class Response {
    [ProtoMember(1)]
    public uint handlerId { get; set; }

    [ProtoMember(2)]
    public uint responseCode { get; set; }

    [ProtoMember(3)]
    public long timestamp { get; set; }

    [ProtoMember(4)]
    public byte[] data { get; set; }

    [ProtoMember(5)]
    public uint sequence { get; set; }
}


[System.Serializable]
public class Rep
{
    public string message { get; set; } 
}

/// <summary>
/// Init 요청에 대한 응답 구조
/// </summary>
[System.Serializable]
public class Initial : Rep
{
 public string userId { get; set; }
 public float x { get; set; }
 public float y { get; set; }
}


/// <summary>
/// Ping 구조체
/// </summary>
[System.Serializable]

public class Ping : Rep
{
    public long timestamp { get; set; }
}


[System.Serializable]
public class LocationUpdate : Rep
{
    public UserLocation[] users { get; set; }

    [System.Serializable]
    public class UserLocation
    {
        public string id { get; set; }

        public uint playerId { get; set; }

        public float x { get; set; }

        public float y { get; set; }
    }
}


#endregion