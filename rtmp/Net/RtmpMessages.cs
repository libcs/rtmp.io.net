// field is never assigned to, and will always have its default value null
#pragma warning disable CS0649

namespace Rtmp.Net.RtmpMessages
{
    #region RtmpMessage

    abstract class RtmpMessage
    {
        public readonly uint StreamId;
        public readonly PacketContentType ContentType;

        protected RtmpMessage(uint streamId, PacketContentType contentType) { StreamId = streamId; ContentType = contentType; }
    }

    #endregion

    #region Abort

    class Abort : RtmpMessage
    {
        public readonly int ChunkStreamId;

        public Abort(uint streamId, int chunkStreamId) : base(streamId, PacketContentType.AbortMessage) =>
            ChunkStreamId = chunkStreamId;
    }

    #endregion

    #region Acknowledgement

    class Acknowledgement : RtmpMessage
    {
        public readonly uint TotalRead;

        public Acknowledgement(uint read) : base(0U, PacketContentType.Acknowledgement) =>
            TotalRead = read;
    }

    #endregion

    #region AudioVideoData

    abstract class ByteData : RtmpMessage
    {
        public readonly byte[] Data;

        protected ByteData(uint streamId, byte[] data, PacketContentType type) : base(streamId, type) =>
            Data = data;
    }

    class AudioData : ByteData
    {
        public AudioData(uint streamId, byte[] data) : base(streamId, data, PacketContentType.Audio) { }
    }

    class VideoData : ByteData
    {
        public VideoData(uint streamId, byte[] data) : base(streamId, data, PacketContentType.Video) { }
    }

    #endregion

    #region ChunkLength

    class ChunkLength : RtmpMessage
    {
        public readonly int Length;

        public ChunkLength(int length) : base(0U, PacketContentType.SetChunkSize) =>
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

        internal Invoke(uint streamId, PacketContentType type) : base(streamId, type) { }
    }

    class InvokeAmf0 : Invoke
    {
        public InvokeAmf0(uint streamId) : base(streamId, PacketContentType.CommandAmf0) { }
    }

    class InvokeAmf3 : Invoke
    {
        public InvokeAmf3(uint streamId) : base(streamId, PacketContentType.CommandAmf3) { }
    }

    #endregion

    #region Notify

    class Notify : RtmpMessage
    {
        public object Data;

        internal Notify(uint streamId, PacketContentType type) : base(streamId, type) { }
    }

    class NotifyAmf0 : Notify
    {
        public NotifyAmf0(uint streamId) : base(streamId, PacketContentType.DataAmf0) { }
    }

    class NotifyAmf3 : Notify
    {
        public NotifyAmf3(uint streamId) : base(streamId, PacketContentType.DataAmf3) { }
    }

    #endregion

    #region SharedObject

    class SharedObject : RtmpMessage
    {
        public object Data;

        internal SharedObject(uint streamId, PacketContentType type) : base(streamId, type) { }
    }

    class SharedObjectAmf0 : SharedObject
    {
        public SharedObjectAmf0(uint streamId) : base(streamId, PacketContentType.SharedObjectAmf0) { }
    }

    class SharedObjectAmf3 : SharedObject
    {
        public SharedObjectAmf3(uint streamId) : base(streamId, PacketContentType.SharedObjectAmf3) { }
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
        public readonly int AckWindowSize;
        public readonly PeerBandwidthLimitType LimitType;

        public PeerBandwidth(int windowSize, PeerBandwidthLimitType type) : base(0U, PacketContentType.SetPeerBandwith)
        {
            AckWindowSize = windowSize;
            LimitType = type;
        }

        public PeerBandwidth(int acknowledgementWindowSize, byte type) : base(0U, PacketContentType.SetPeerBandwith)
        {
            AckWindowSize = acknowledgementWindowSize;
            LimitType = (PeerBandwidthLimitType)type;
        }
    }

    #endregion

    #region UserControlMessage

    class UserControlMessage : RtmpMessage
    {
        public readonly Type EventType;
        public readonly uint[] Values;

        public UserControlMessage(Type type, uint[] values) : base(0U, PacketContentType.UserControlMessage)
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
        public readonly int Count;

        public WindowAcknowledgementSize(int count) : base(0U, PacketContentType.WindowAcknowledgementSize) =>
            Count = count;
    }

    #endregion
}
