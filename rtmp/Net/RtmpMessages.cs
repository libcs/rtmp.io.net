// field is never assigned to, and will always have its default value null
#pragma warning disable CS0649

namespace Rtmp.Net.RtmpMessages
{
    #region RtmpMessage

    abstract class RtmpMessage
    {
        public PacketContentType ContentType;

        protected RtmpMessage(PacketContentType contentType) => ContentType = contentType;
    }

    #endregion

    #region Abort

    class Abort : RtmpMessage
    {
        public int ChunkStreamId;

        public Abort(int chunkStreamId) : base(PacketContentType.AbortMessage) =>
            ChunkStreamId = chunkStreamId;
    }

    #endregion

    #region Acknowledgement

    class Acknowledgement : RtmpMessage
    {
        public uint TotalRead;

        public Acknowledgement(uint read) : base(PacketContentType.Acknowledgement) =>
            TotalRead = read;
    }

    #endregion

    #region AudioVideoData

    abstract class ByteData : RtmpMessage
    {
        public byte[] Data;

        protected ByteData(byte[] data, PacketContentType type) : base(type) =>
            Data = data;
    }

    class AudioData : ByteData
    {
        public AudioData(byte[] data) : base(data, PacketContentType.Audio) { }
    }

    class VideoData : ByteData
    {
        public VideoData(byte[] data) : base(data, PacketContentType.Video) { }
    }

    #endregion

    #region ChunkLength

    class ChunkLength : RtmpMessage
    {
        public int Length;

        public ChunkLength(int length) : base(PacketContentType.SetChunkSize) =>
            Length = length > 0xFFFFFF ? 0xFFFFFF : length;
    }

    #endregion

    #region Invoke

    class Invoke : RtmpMessage
    {
        public string MethodName;
        public object[] Arguments;
        public uint InvokeId;
        public object Headers;

        public Invoke(PacketContentType type) : base(type) { }
    }

    class InvokeAmf0 : Invoke
    {
        public InvokeAmf0() : base(PacketContentType.CommandAmf0) { }
    }

    class InvokeAmf3 : Invoke
    {
        public InvokeAmf3() : base(PacketContentType.CommandAmf3) { }
    }

    #endregion

    #region Notify

    class Notify : RtmpMessage
    {
        public object Data;

        protected Notify(PacketContentType type) : base(type) { }
    }

    class NotifyAmf0 : Notify
    {
        public NotifyAmf0() : base(PacketContentType.DataAmf0) { }
    }

    class NotifyAmf3 : Notify
    {
        public NotifyAmf3() : base(PacketContentType.DataAmf3) { }
    }

    #endregion

    #region PeerBandwidth

    public enum PeerBandwidthLimitType : byte
    {
        Hard = 0,
        Soft = 1,
        Dynamic = 2
    }

    class PeerBandwidth : RtmpMessage
    {
        public int AckWindowSize;
        public PeerBandwidthLimitType LimitType;

        public PeerBandwidth(int windowSize, PeerBandwidthLimitType type) : base(PacketContentType.SetPeerBandwith)
        {
            AckWindowSize = windowSize;
            LimitType = type;
        }

        public PeerBandwidth(int acknowledgementWindowSize, byte type) : base(PacketContentType.SetPeerBandwith)
        {
            AckWindowSize = acknowledgementWindowSize;
            LimitType = (PeerBandwidthLimitType)type;
        }
    }

    #endregion

    #region UserControlMessage

    class UserControlMessage : RtmpMessage
    {
        public Type EventType;
        public uint[] Values;

        public UserControlMessage(Type type, uint[] values) : base(PacketContentType.UserControlMessage)
        {
            EventType = type;
            Values = values;
        }

        public enum Type : ushort
        {
            StreamBegin = 0,
            StreamEof = 1,
            StreamDry = 2,
            SetBufferLength = 3,
            StreamIsRecorded = 4,
            PingRequest = 6,
            PingResponse = 7
        }
    }

    #endregion

    #region WindowAcknowledgementSize

    class WindowAcknowledgementSize : RtmpMessage
    {
        // """
        // The receiving peer MUST send an Acknowledgement (Section 5.4.3) after
        // receiving the indicated number of bytes since the last Acknowledgement was
        // sent, or from the beginning of the session if no Acknowledgement has yet been
        // sent
        // """
        public int Count;

        public WindowAcknowledgementSize(int count) : base(PacketContentType.WindowAcknowledgementSize) =>
            Count = count;
    }

    #endregion
}
