using Hina;
using Hina.IO.Zlib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Rtmp.IO.Amf3
{
    public interface IExternalizable
    {
        void ReadExternal(IDataInput input);
        void WriteExternal(IDataOutput output);
    }

    #region ArrayCollection

    [Rtmp("flex.messaging.io.ArrayCollection"), TypeConverter(typeof(ArrayCollectionConverter))]
    public class ArrayCollection : List<object>, IExternalizable
    {
        public void ReadExternal(IDataInput input)
        {
            if (input.ReadObject() is object[] obj)
                AddRange(obj);
        }

        public void WriteExternal(IDataOutput output) =>
            output.WriteObject(ToArray());
    }

    public class ArrayCollectionConverter : TypeConverter
    {
        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType) =>
            NanoTypeConverter.ConvertTo(value, destinationType);

        public override bool CanConvertTo(ITypeDescriptorContext context, Type type) =>
            type.IsArray
            || type == typeof(ArrayCollection)
            || type == typeof(IList)
            || (type.GetTypeInfo().IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>));
    }

    #endregion

    #region ByteArray

    [Rtmp("flex.messaging.io.ByteArray"), TypeConverter(typeof(ByteArrayConverter))]
    public class ByteArray
    {
        public ArraySegment<byte> Buffer;

        public ByteArray() { }
        public ByteArray(byte[] buffer) => Buffer = new ArraySegment<byte>(buffer);
        public ByteArray(ArraySegment<byte> buffer) => Buffer = buffer;

        // returns a copy of the underlying buffer
        public byte[] ToArray() => Buffer.ToArray();

        public void Deflate() => Compress(Compression.Deflate);
        public void Inflate() => Uncompress(Compression.Deflate);

        public void Compress() => Compress(Compression.Zlib);
        public void Uncompress() => Uncompress(Compression.Zlib);

        public void Compress(Compression algorithm)
        {
            using (var memory = new MemoryStream())
            using (var stream = algorithm == Compression.Zlib ? new ZlibStream(memory, CompressionMode.Compress, true) : new DeflateStream(memory, CompressionMode.Compress, true))
            {
                stream.Write(Buffer.Array, Buffer.Offset, Buffer.Count);
                Buffer = new ArraySegment<byte>(memory.GetBuffer(), offset: 0, count: (int)memory.Length);
            }
        }

        public void Uncompress(Compression algorithm)
        {
            using (var source = new MemoryStream(Buffer.Array, Buffer.Offset, Buffer.Count, false))
            using (var intermediate = algorithm == Compression.Zlib ? new ZlibStream(source, CompressionMode.Decompress, false) : new DeflateStream(source, CompressionMode.Decompress, false))
            using (var memory = new MemoryStream())
            {
                intermediate.CopyTo(memory);
                Buffer = new ArraySegment<byte>(memory.GetBuffer(), offset: 0, count: (int)memory.Length);
            }
        }

        public enum Compression { Deflate, Zlib }
    }

    public class ByteArrayConverter : TypeConverter
    {
        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
        {
            if (destinationType == typeof(byte[]))
                return true;

            return base.CanConvertTo(context, destinationType);
        }

        public override object ConvertTo(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object source, Type destinationType)
        {
            if (destinationType == typeof(byte[]))
            {
                Check.NotNull(source);
                return (source as ByteArray).ToArray();
            }

            return base.ConvertTo(context, culture, source, destinationType);
        }
    }

    #endregion

    #region ObjectProxy

    [Rtmp("flex.messaging.io.ObjectProxy")]
    class ObjectProxy : Dictionary<string, object>, IExternalizable
    {
        public void ReadExternal(IDataInput input)
        {
            if (input.ReadObject() is IDictionary<string, object> values)
            {
                foreach (var (key, value) in values)
                    this[key] = value;
            }
        }

        public void WriteExternal(IDataOutput output) =>
            output.WriteObject(new AsObject(this, owned: true) { TypeName = "flex.messaging.io.ObjectProxy" });
    }

    #endregion

    #region DataInput

    public interface IDataInput
    {
        object ReadObject();
        bool ReadBoolean();
        byte ReadByte();
        byte[] ReadBytes(int count);
        void ReadBytes(byte[] buffer, int index, int count);
        Space<byte> ReadSpan(int count);
        double ReadDouble();
        float ReadSingle();
        short ReadInt16();
        ushort ReadUInt16();
        uint ReadUInt24();
        int ReadInt32();
        uint ReadUInt32();
        string ReadUtf();
        string ReadUtf(int length);
    }

    class DataInput : IDataInput
    {
        readonly AmfReader reader;

        public DataInput(AmfReader reader)
        {
            this.reader = reader;
            ObjectEncoding = ObjectEncoding.Amf3;
        }

        public ObjectEncoding ObjectEncoding { get; set; }

        public object ReadObject()
        {
            switch (ObjectEncoding)
            {
                case ObjectEncoding.Amf0: return reader.ReadAmf0Object();
                case ObjectEncoding.Amf3: return reader.ReadAmf3Object();
                default: throw new ArgumentOutOfRangeException();
            }
        }

        public bool ReadBoolean() => reader.ReadBoolean();
        public byte ReadByte() => reader.ReadByte();
        public byte[] ReadBytes(int count) => reader.ReadBytes(count);
        public double ReadDouble() => reader.ReadDouble();
        public float ReadSingle() => reader.ReadSingle();
        public short ReadInt16() => reader.ReadInt16();
        public ushort ReadUInt16() => reader.ReadUInt16();
        public uint ReadUInt24() => reader.ReadUInt24();
        public int ReadInt32() => reader.ReadInt32();
        public uint ReadUInt32() => reader.ReadUInt32();
        public string ReadUtf() => reader.ReadUtf();
        public string ReadUtf(int length) => reader.ReadUtf(length);

        public Space<byte> ReadSpan(int count) => reader.ReadSpan(count);
        public void ReadBytes(byte[] buffer, int index, int count) => reader.ReadBytes(buffer, index, count);
    }

    #endregion

    #region DataOutput

    public interface IDataOutput
    {
        void WriteObject(object value);
        void WriteBoolean(bool value);
        void WriteByte(byte value);
        void WriteBytes(byte[] buffer);
        void WriteBytes(byte[] buffer, int index, int count);
        void WriteBytes(Space<byte> span);
        void WriteDouble(double value);
        void WriteSingle(float value);
        void WriteInt16(short value);
        void WriteUInt16(ushort value);
        void WriteUInt24(uint value);
        void WriteInt32(int value);
        void WriteUInt32(uint value);
        void WriteUtf(string value);
        // writes string without 16-bit length prefix
        void WriteUtfBytes(string value);
    }

    class DataOutput : IDataOutput
    {
        readonly AmfWriter writer;

        public DataOutput(AmfWriter writer)
        {
            this.writer = writer;
            ObjectEncoding = ObjectEncoding.Amf3;
        }

        public ObjectEncoding ObjectEncoding { get; set; }

        public void WriteObject(object value)
        {
            switch (ObjectEncoding)
            {
                case ObjectEncoding.Amf0: writer.WriteAmf0Object(value); break;
                case ObjectEncoding.Amf3: writer.WriteAmf3Object(value); break;
                default: throw new ArgumentOutOfRangeException();
            }
        }

        public void WriteBoolean(bool value) => writer.WriteBoolean(value);
        public void WriteUInt32(uint value) => writer.WriteUInt32(value);
        public void WriteByte(byte value) => writer.WriteByte(value);
        public void WriteBytes(byte[] buffer) => writer.WriteBytes(buffer);
        public void WriteBytes(Space<byte> span) => writer.WriteBytes(span);
        public void WriteDouble(double value) => writer.WriteDouble(value);
        public void WriteSingle(float value) => writer.WriteSingle(value);
        public void WriteInt16(short value) => writer.WriteInt16(value);
        public void WriteInt32(int value) => writer.WriteInt32(value);
        public void WriteUInt16(ushort value) => writer.WriteUInt16(value);
        public void WriteUInt24(uint value) => writer.WriteUInt24(value);
        public void WriteUtf(string value) => writer.WriteUtfPrefixed(value);
        public void WriteUtfBytes(string value) => writer.WriteBytes(Encoding.UTF8.GetBytes(value));

        public void WriteBytes(byte[] buffer, int index, int count) => writer.WriteBytes(buffer, index, count);
    }

    #endregion
}
