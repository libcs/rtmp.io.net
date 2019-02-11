using Hina;
using Hina.IO;
using Konseki;
using Rtmp.IO.Amf3;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace Rtmp.IO
{
    public class AmfReader
    {
        readonly ByteReader reader;
        readonly SerializationContext context;

        readonly Base core;
        readonly Amf0 amf0;
        readonly Amf3 amf3;

        public int Length => reader.Length;
        public int Position => reader.Position;
        public int Remaining => reader.Length - reader.Position;

        public AmfReader(SerializationContext context)
            : this(EmptyCollection<byte>.Array, context) { }
        public AmfReader(byte[] data, SerializationContext context)
        {
            Check.NotNull(data, context);

            this.context = context;
            reader = new ByteReader(data);

            core = new Base(reader);
            amf3 = new Amf3(context, this, core);
            amf0 = new Amf0(context, core, amf3);
        }

        public bool HasLength(int count) =>
            core.HasLength(count);

        // efficiency: avoid re-allocating this object by re-binding it to a new buffer, effectively resetting this object.
        public void Rebind(Space<byte> span)
        {
            reader.Span = span;

            amf0.Reset();
            amf3.Reset();
        }

        public void Rebind(byte[] data) =>
            Rebind(new Space<byte>(data));
        public void Rebind(byte[] data, int index, int count) =>
            Rebind(new Space<byte>(data, index, count));

        public Space<byte> ReadSpan(int count) => core.ReadSpan(count);
        public byte ReadByte() => core.ReadByte();
        public byte[] ReadBytes(int count) => core.ReadBytes(count);
        public void ReadBytes(byte[] buffer, int index, int count) => core.ReadBytes(buffer, index, count);
        public ushort ReadUInt16() => core.ReadUInt16();
        public short ReadInt16() => core.ReadInt16();
        public bool ReadBoolean() => core.ReadBoolean();
        public int ReadInt32() => core.ReadInt32();
        public uint ReadUInt32() => core.ReadUInt32();
        public int ReadLittleEndianInt() => core.ReadLittleEndianInt();
        public uint ReadUInt24() => core.ReadUInt24();
        public double ReadDouble() => core.ReadDouble();
        public float ReadSingle() => core.ReadSingle();
        public string ReadUtf() => core.ReadUtf();
        public string ReadUtf(int length) => core.ReadUtf(length);
        public object ReadAmf0Object() => amf0.ReadItem();
        public object ReadAmf3Object() => amf3.ReadItem();
        public object ReadAmfObject(ObjectEncoding encoding)
        {
            if (encoding == ObjectEncoding.Amf0) return amf0.ReadItem();
            else if (encoding == ObjectEncoding.Amf3) return amf3.ReadItem();
            else throw new ArgumentOutOfRangeException("unsupported encoding");
        }

        class ReferenceList<T> : List<T>
        {
            public T Get(int index) => this[index];
        }

        class InvalidReference
        {
            public static readonly InvalidReference Instance = new InvalidReference();
        }

        #region Base

        class Base
        {
            // `cache` allows us to avoid allocations when reading items that are less than n bytes
            readonly byte[] temporary;
            readonly ByteReader reader;

            // constructor
            public Base(ByteReader reader)
            {
                this.reader = reader;
                temporary = new byte[8];
            }

            // helpers
            void Require(int count)
            {
                Kon.Assert(count <= temporary.Length);

                if (reader.ReadUnchecked(temporary, 0, count) != count)
                    throw new EndOfStreamException();
            }

            public bool HasLength(int count) => reader.HasLength(count);

            // readers
            public byte ReadByte() => reader.ReadByte();
            public byte[] ReadBytes(int count) => reader.ReadBytes(count);
            public void ReadBytes(byte[] buffer, int index, int count)
            {
                if (reader.Read(buffer, index, count) != count)
                    throw new ArgumentOutOfRangeException("tried to read past end of data stream");
            }
            public Space<byte> ReadSpan(int count) => reader.ReadSpan(count);
            public ushort ReadUInt16() { Require(2); return (ushort)(((temporary[0] & 0xFF) << 8) | (temporary[1] & 0xFF)); }
            public short ReadInt16() { Require(2); return (short)(((temporary[0] & 0xFF) << 8) | (temporary[1] & 0xFF)); }
            public bool ReadBoolean() => reader.ReadBoolean();
            public int ReadInt32() { Require(4); return temporary[0] << 24 | (temporary[1] << 16) | (temporary[2] << 8) | temporary[3]; }
            public uint ReadUInt32() { Require(4); return (uint)((temporary[0] << 24) | (temporary[1] << 16) | (temporary[2] << 8) | temporary[3]); }
            public int ReadLittleEndianInt() { Require(4); return (temporary[3] << 24) | (temporary[2] << 16) | (temporary[1] << 8) | temporary[0]; }
            public uint ReadUInt24() { Require(3); return (uint)(temporary[0] << 16 | temporary[1] << 8 | temporary[2]); }
            // 64-bit IEEE-754 double precision floating point
            public unsafe double ReadDouble()
            {
                Require(8);
                var lo = (uint)(temporary[7] | temporary[6] << 8 | temporary[5] << 16 | temporary[4] << 24);
                var hi = (uint)(temporary[3] | temporary[2] << 8 | temporary[1] << 16 | temporary[0] << 24);
                var value = (ulong)hi << 32 | lo;
                return *(double*)&value;
            }
            // single-precision floating point number
            public unsafe float ReadSingle() { Require(4); var value = (uint)(temporary[0] << 24 | temporary[2] << 8 | temporary[1] << 16 | temporary[3]); return *(float*)&value; }
            // utf8 string with length prefix
            public string ReadUtf() { var length = ReadUInt16(); return ReadUtf(length); }
            // utf8 string
            public string ReadUtf(int length)
            {
                if (length == 0)
                    return string.Empty;

                return Encoding.UTF8.GetString(
                    bytes: ReadBytes(length),
                    index: 0,
                    count: length);
            }
        }

        #endregion

        #region Amf0

        class Amf0
        {
            readonly SerializationContext context;
            readonly ReferenceList<object> refs;

            readonly Base b;
            readonly Amf3 amf3;

            public Amf0(SerializationContext context, Base b, Amf3 amf3)
            {
                this.b = b;
                this.amf3 = amf3;
                this.context = context;
                refs = new ReferenceList<object>();
            }


            // public helper methods
            public void Reset() => refs.Clear();
            public bool HasLength(int count) => b.HasLength(count);

            // read_* implementations
            public object ReadItem() { var marker = b.ReadByte(); return ReadItem(marker); }

            object ReadItem(int marker) => readers[marker](b, this, amf3);

            object ReadObjectRef() => refs.Get(b.ReadUInt16());

            // amf0 object
            object ReadObject()
            {
                var type = b.ReadUtf();

                if (context.HasConcreteType(type))
                {
                    var instance = context.CreateInstance(type);
                    var klass = context.GetClassInfo(instance);

                    refs.Add(instance);

                    foreach (var pair in ReadItems())
                        if (klass.TryGetMember(pair.key, out var member))
                            member.SetValue(instance, pair.value);

                    return instance;
                }
                else if (context.AsObjectFallback)
                {
                    // object reference added in this.ReadAmf0AsObject()
                    var obj = ReadAsObject();

                    obj.TypeName = type;
                    return obj;
                }
                else
                    throw new ArgumentException($"can't deserialize object: the type \"{type}\" isn't registered, and anonymous object fallback has been disabled");
            }

            AsObject ReadAsObject()
            {
                var asObject = new AsObject();

                refs.Add(asObject);
                asObject.Replace(ReadItems());

                return asObject;
            }

            string ReadLongString()
            {
                var length = b.ReadInt32();
                return b.ReadUtf(length);
            }

            Dictionary<string, object> ReadEcmaArray()
            {
                var length = b.ReadInt32();
                var dictionary = new Dictionary<string, object>(length);
                refs.Add(dictionary);

                foreach (var (key, value) in ReadItems())
                    dictionary[key] = value;

                return dictionary;
            }

            object[] ReadStrictArray()
            {
                var length = b.ReadInt32();
                var array = new object[length];

                refs.Add(array);

                for (var i = 0; i < length; i++)
                    array[i] = ReadItem();

                return array;
            }

            DateTime ReadDate()
            {
                var milliseconds = b.ReadDouble();
                var date = UnixDateTime.Epoch.AddMilliseconds(milliseconds);

                // http://download.macromedia.com/pub/labs/amf/amf0_spec_121207.pdf
                // """
                // While the design of this type reserves room for time zone offset information,
                // it should not be filled in, nor used, as it is unconventional to change time
                // zones when serializing dates on a network. It is suggested that the time zone
                // be queried independently as needed.
                //  -- AMF0 specification, 2.13 Date Type
                // """
                var offset = b.ReadUInt16();

                return date;
            }

            XDocument ReadXmlDocument()
            {
                var xml = ReadLongString();

                return string.IsNullOrEmpty(xml)
                    ? new XDocument()
                    : XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }


            // internal helper methods

            List<(string key, object value)> ReadItems()
            {
                var pairs = new List<(string key, object value)>();

                while (true)
                {
                    var key = b.ReadUtf();
                    var type = b.ReadByte();

                    if (type == (byte)0x09) // object-end marker
                        return pairs;

                    pairs.Add((key: key, value: ReadItem(type)));
                }
            }

            static readonly List<Func<Base, Amf0, Amf3, object>> readers = new List<Func<Base, Amf0, Amf3, object>>
            {
                (core, amf0, amf3) => core.ReadDouble(),                      // 0x00 - number
                (core, amf0, amf3) => core.ReadBoolean(),                     // 0x01 - boolean
                (core, amf0, amf3) => core.ReadUtf(),                         // 0x02 - string
                (core, amf0, amf3) => amf0.ReadAsObject(),                    // 0x03 - object
                (core, amf0, amf3) => { throw new NotSupportedException(); }, // 0x04 - movieclip
                (core, amf0, amf3) => null,                                   // 0x05 - null
                (core, amf0, amf3) => null,                                   // 0x06 - undefined
                (core, amf0, amf3) => amf0.ReadObjectRef(),                   // 0x07 - reference
                (core, amf0, amf3) => amf0.ReadEcmaArray(),                   // 0x08 - ECMA array
                (core, amf0, amf3) => { throw new NotSupportedException(); }, // 0x09 - 'object end marker' - we handle this in deserializer block; we shouldn't encounter it here
                (core, amf0, amf3) => amf0.ReadStrictArray(),                 // 0x0A - strict array
                (core, amf0, amf3) => amf0.ReadDate(),                        // 0x0B - date
                (core, amf0, amf3) => amf0.ReadLongString(),                  // 0x0C - long string
                (core, amf0, amf3) => { throw new NotSupportedException(); }, // 0x0D - unsupported marker
                (core, amf0, amf3) => { throw new NotSupportedException(); }, // 0x0E - recordset
                (core, amf0, amf3) => amf0.ReadXmlDocument(),                 // 0x0F - xml document
                (core, amf0, amf3) => amf0.ReadObject(),                      // 0x10 - typed object
                (core, amf0, amf3) => amf3.ReadItem()                         // 0x11 - avmplus object
            };
        }

        #endregion

        #region Amf3

        class Amf3
        {
            readonly SerializationContext context;
            readonly ReferenceList<object> refObjects;
            readonly ReferenceList<string> refStrings;
            readonly ReferenceList<ClassDescription> refClasses;

            readonly Base b;
            readonly AmfReader reader;

            public Amf3(SerializationContext context, AmfReader reader, Base b)
            {
                this.b = b;
                this.reader = reader;
                this.context = context;

                refObjects = new ReferenceList<object>();
                refStrings = new ReferenceList<string>();
                refClasses = new ReferenceList<ClassDescription>();
            }

            // public helper methods

            public void Reset()
            {
                refObjects.Clear();
                refStrings.Clear();
                refClasses.Clear();
            }

            // readers
            public object ReadItem()
            {
                var marker = b.ReadByte();
                return ReadItem(marker);
            }

            object ReadItem(byte marker)
            {
                return readers[marker](b, this);
            }

            // variable-length integer which uses the highest bit or each byte as a continuation flag.
            int ReadInt29()
            {
                // http://download.macromedia.com/pub/labs/amf/Amf3_spec_121207.pdf
                // """
                // AMF 3 makes use of a special compact format for writing integers to reduce the
                // number of bytes required for encoding. As with a normal 32-bit integer, up to
                // 4 bytes are required to hold the value however the high bit of the first 3
                // bytes are used as flags to determine whether the next byte is part of the
                // integer. With up to 3 bits of the 32 bits being used as flags, only 29
                // significant bits remain for encoding an integer. This means the largest
                // unsigned integer value that can be represented is 2^29 - 1.
                // -- AMF3 specification, 1.3.1 Variable Length Unsigned 29-bit Integer Encoding
                // """

                // first byte
                int total = b.ReadByte();
                if (total < 128)
                    return total;

                total = (total & 0x7f) << 7;

                // second byte
                int nextByte = b.ReadByte();
                if (nextByte < 128)
                {
                    total = total | nextByte;
                }
                else
                {
                    total = (total | nextByte & 0x7f) << 7;

                    // third byte
                    nextByte = b.ReadByte();
                    if (nextByte < 128)
                    {
                        total = total | nextByte;
                    }
                    else
                    {
                        total = (total | nextByte & 0x7f) << 8;

                        // fourth byte
                        nextByte = b.ReadByte();
                        total = total | nextByte;
                    }
                }

                // to sign extend a value from some number of bits to a greater number of bits just copy the sign bit
                // into all the additional bits in the new format. convert / sign extend the 29-bit two's complement number to 32 bit
                return -(total & (1 << 28)) | total;
            }

            DateTime ReadDate()
            {
                if (ReadReference(out var index))
                    return ObjectReferenceGet<DateTime>(index);

                var ms = b.ReadDouble();
                var date = UnixDateTime.Epoch.AddMilliseconds(ms);

                return ObjectReferenceAdd(date);
            }

            string ReadString()
            {
                // variant: object-ref if read-reference, else string-byte-length
                if (ReadReference(out var variant))
                    return StringReferenceGet(variant);

                if (variant == 0)
                    return string.Empty;

                var value = b.ReadUtf(variant);
                return StringReferenceAdd(value);
            }

            XDocument ReadXmlDocument()
            {
                // variant: object-ref if read-reference, else xml-byte-length
                var xml = ReadReference(out var variant)
                    ? ObjectReferenceGet<string>(variant)
                    : ObjectReferenceAdd(
                        b.ReadUtf(variant));

                return string.IsNullOrEmpty(xml)
                    ? new XDocument()
                    : XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }

            ByteArray ReadByteArray()
            {
                // variant: object-ref if read-reference, else data-length
                if (ReadReference(out var variant))
                    return ObjectReferenceGet<ByteArray>(variant);

                var data = b.ReadBytes(variant);
                var array = new ByteArray(data);

                return ObjectReferenceAdd(array);
            }

            object ReadArray()
            {
                // variant: object-ref if read-reference, else dense-element-count
                if (ReadReference(out var variant))
                    return ObjectReferenceGet<object>(variant);

                var me = this;
                var handle = ObjectReferenceReserve();
                var associative = GetAssociativeItems();
                var dense = GetDenseItems();

                if (associative.Count != 0)
                {
                    var dictionary = new Dictionary<string, object>(associative.Count + dense.Length);

                    foreach (var (key, value) in associative)
                        dictionary[key] = value;

                    for (var i = 0; i < dense.Length; i++)
                        dictionary[i.ToString(CultureInfo.InvariantCulture)] = dense[i];

                    return ObjectReferenceReplace(handle, dictionary);
                }
                else
                    return ObjectReferenceReplace(handle, dense);

                List<(string key, object value)> GetAssociativeItems()
                {
                    var key = me.ReadString();

                    if (key != "")
                    {
                        var items = new List<(string key, object value)>();

                        while (key != "")
                        {
                            var next = (key, me.ReadItem());

                            items.Add(next);
                            key = me.ReadString();
                        }

                        return items;
                    }

                    return EmptyCollection<(string key, object value)>.List;
                }

                object[] GetDenseItems()
                {
                    if (variant == 0)
                        return EmptyCollection<object>.Array;

                    var array = new object[variant];

                    for (var i = 0; i < variant; i++)
                        array[i] = me.ReadItem();

                    return array;
                }
            }

            object ReadVector<T>(bool hasTypeName, Func<Base, Amf3, T> read)
            {
                // variant: object-ref if read-reference, else item-count
                if (ReadReference(out var variant))
                    return ObjectReferenceGet<object>(variant);

                var handle = ObjectReferenceReserve();
                var isFixedLength = b.ReadByte() == 0x01;
                var list = new List<T>(variant);
                var typeName = hasTypeName ? ReadString() : "";

                for (var i = 0; i < variant; i++)
                {
                    var element = read(b, this);
                    list.Add(element);
                }

                return ObjectReferenceReplace(
                    handle,
                    value: isFixedLength ? list.ToArray() : (object)list);
            }

            object ReadDictionary()
            {
                // variant: object-ref if read-reference, else item-count
                if (ReadReference(out var variant))
                    return ObjectReferenceGet<object>(variant);

                var isWeak = b.ReadByte() == 0x01;
                var dictionary = new Dictionary<object, object>(variant);

                ObjectReferenceAdd(dictionary);

                // we store weak references as strong references, always
                //     var wrap = new Func<object, object>(obj => isWeak ? new WeakReference(obj) : obj);
                for (int i = 0; i < variant; i++)
                {
                    var key = ReadItem();
                    var value = ReadItem();

                    dictionary[key] = value;
                }

                return dictionary;
            }

            object ReadTyedObject()
            {
                // variant: object-ref if read-reference, else class-descriptor-flags
                if (ReadReference(out var variant))
                    return ObjectReferenceGet<object>(variant);

                var me = this;
                var description = ReadClassDefinition(variant);
                var haveConcrete = context.HasConcreteType(description.TypeName);
                var instance = CreateInstance(description);

                ObjectReferenceAdd(instance);

                if (description.IsExternalizable)
                {
                    if (!(instance is IExternalizable externalizable))
                        throw new ArgumentException($"{description.TypeName} does not implement IExternalizable");

                    externalizable.ReadExternal(new DataInput(reader));
                }
                else
                {
                    var klass = context.GetClassInfo(instance);

                    foreach (var name in description.MemberNames)
                    {
                        var value = ReadItem();

                        if (klass.TryGetMember(name, out var member))
                            member.SetValue(instance, value);
                    }

                    if (description.IsDynamic)
                    {
                        string key;

                        while (true)
                        {
                            if ((key = ReadString()) == string.Empty)
                                break;

                            var value = ReadItem();

                            if (klass.TryGetMember(key, out var wrapper))
                                wrapper.SetValue(instance, value);
                        }
                    }
                }

                return instance;


                ClassDescription ReadClassDefinition(int flags)
                {
                    // if last bit of flags is 0, then this is a reference to some previous class definition
                    if ((flags & 1) == 0)
                        return me.ClassReferenceGet(flags >> 1);

                    var typeName = me.ReadString();
                    var isExternalizable = ((flags >> 1) & 1) != 0;
                    var isDynamic = ((flags >> 2) & 1) != 0;
                    var members = flags >> 3;
                    var memberNames = EnumerableEx.Range(members, () => me.ReadString());

                    return me.ClassReferenceAdd(new ClassDescription()
                    {
                        TypeName = typeName,
                        MemberNames = memberNames,
                        IsExternalizable = isExternalizable,
                        IsDynamic = isDynamic
                    });
                }

                object CreateInstance(ClassDescription d)
                {
                    if (!d.IsTyped)
                        return new AsObject();

                    switch (context.CreateOrNull(d.TypeName))
                    {
                        case object o:
                            return o;

                        default:
                            if (context.AsObjectFallback)
                                return new AsObject(d.TypeName);

                            throw new ArgumentException($"can't deserialize object: the type \"{d.TypeName}\" isn't registered, and anonymous object fallback has been disabled");
                    }
                }
            }


            // helper

            bool ReadReference(out int index)
            {
                var x = ReadInt29();

                // for the last bit:
                //     0: reference to previously seen object
                //     1: inline object
                index = x >> 1;
                return (x & 1) == 0;
            }


            // read + write ref helpers

            T ObjectReferenceGet<T>(int index)
            {
                var value = refObjects.Get(index);

                if (value == InvalidReference.Instance)
                    throw new InvalidOperationException("attempted to retrieve reference to an invalid object. recursive data structures are not supported.");

                return (T)value;
            }

            T ObjectReferenceAdd<T>(T value)
            {
                refObjects.Add(value);
                return value;
            }

            int ObjectReferenceReserve()
            {
                refObjects.Add(InvalidReference.Instance);
                return refObjects.Count - 1;
            }

            T ObjectReferenceReplace<T>(int handle, T value)
            {
                refObjects[handle] = value;
                return value;
            }

            string StringReferenceGet(int index)
            {
                return refStrings.Get(index);
            }

            string StringReferenceAdd(string value)
            {
                refStrings.Add(value);
                return value;
            }

            ClassDescription ClassReferenceGet(int index)
            {
                return refClasses.Get(index);
            }

            ClassDescription ClassReferenceAdd(ClassDescription value)
            {
                refClasses.Add(value);
                return value;
            }

            // definitions

            struct ClassDescription
            {
                public string TypeName;
                public string[] MemberNames;
                public bool IsExternalizable;
                public bool IsDynamic;
                public bool IsTyped => !string.IsNullOrEmpty(TypeName);
            }

            // readers

            static readonly List<Func<Base, Amf3, object>> readers = new List<Func<Base, Amf3, object>>
            {
                (core, amf3) => null,                                             // 0x00 - undefined
                (core, amf3) => null,                                             // 0x01 - null
                (core, amf3) => false,                                            // 0x02 - false
                (core, amf3) => true,                                             // 0x03 - true
                (core, amf3) => amf3.ReadInt29(),                                 // 0x04 - integer
                (core, amf3) => core.ReadDouble(),                                // 0x05 - double
                (core, amf3) => amf3.ReadString(),                                // 0x06 - string
                (core, amf3) => amf3.ReadXmlDocument(),                           // 0x07 - xml document
                (core, amf3) => amf3.ReadDate(),                                  // 0x08 - date
                (core, amf3) => amf3.ReadArray(),                                 // 0x09 - array
                (core, amf3) => amf3.ReadTyedObject(),                            // 0x0A - object
                (core, amf3) => amf3.ReadXmlDocument(),                           // 0x0B - xml
                (core, amf3) => amf3.ReadByteArray(),                             // 0x0C - byte array
                (core, amf3) => amf3.ReadVector(false, (a, b) => a.ReadInt32()),  // 0x0D - int vector
                (core, amf3) => amf3.ReadVector(false, (a, b) => a.ReadUInt32()), // 0x0E - uint vector
                (core, amf3) => amf3.ReadVector(false, (a, b) => a.ReadDouble()), // 0x0F - double vector
                (core, amf3) => amf3.ReadVector(true,  (a, b) => b.ReadItem()),   // 0x10 - object vector
                (core, amf3) => amf3.ReadDictionary(),                            // 0x11 - dictionary
            };
        }

        #endregion
    }
}
