using Hina;
using Hina.Collections;
using Hina.IO;
using Hina.Reflection;
using Konseki;
using Rtmp.IO.Amf3;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Rtmp.IO
{
    public class AmfWriter
    {
        readonly ByteWriter writer;
        readonly SerializationContext context;

        readonly Base core;
        readonly Amf0 amf0;
        readonly Amf3 amf3;

        public int Length => writer.Length;
        public int Position => writer.Position;

        public AmfWriter(SerializationContext context)
            : this(new ByteWriter(), context, 0) { }
        public AmfWriter(byte[] data, SerializationContext context)
            : this(new ByteWriter(data), context, 0) { }
        public AmfWriter(int initialBufferLengthHint, SerializationContext context)
            : this(new ByteWriter(initialBufferLengthHint), context, 0) { }
        AmfWriter(ByteWriter writer, SerializationContext context, byte _)
        {
            this.context = Check.NotNull(context);
            this.writer = writer;

            core = new Base(writer);
            amf3 = new Amf3(context, this, core);
            amf0 = new Amf0(context, core, amf3);
        }

        public Space<byte> Span => writer.Span;
        public void Return() => writer.Return();
        public byte[] ToArray() => writer.ToArray();
        public byte[] ToArrayAndReturn() => writer.ToArrayAndReturn();

        // efficiency: avoid re-allocating this object by re-binding it to a new buffer, effectively resetting this object.

        public void Reset()
        {
            amf0.Reset();
            amf3.Reset();
            writer.Reset();
        }

        public void Rebind(Space<byte> data)
        {
            amf0.Reset();
            amf3.Reset();
            writer.Buffer = data;
        }
        public void Rebind(byte[] data) =>
            Rebind(new Space<byte>(data));

        public void WriteByte(byte value) => core.WriteByte(value);
        public void WriteBytes(Space<byte> span) => core.WriteBytes(span);
        public void WriteBytes(byte[] buffer) => core.WriteBytes(buffer);
        public void WriteBytes(byte[] buffer, int index, int count) => core.WriteBytes(buffer, index, count);
        public void WriteInt16(short value) => core.WriteInt16(value);
        public void WriteUInt16(ushort value) => core.WriteUInt16(value);
        public void WriteInt32(int value) => core.WriteInt32(value);
        public void WriteUInt32(uint value) => core.WriteUInt32(value);
        public void WriteLittleEndianInt(uint value) => core.WriteLittleEndianInt(value);
        public void WriteUInt24(uint value) => core.WriteUInt24(value);
        public void WriteBoolean(bool value) => core.WriteBoolean(value);
        public void WriteDouble(double value) => core.WriteDouble(value);
        public void WriteSingle(float value) => core.WriteSingle(value);
        public void WriteUtfPrefixed(string value) => core.WriteUtfPrefixed(value);
        public void WriteUtfPrefixed(byte[] utf8) => core.WriteUtfPrefixed(utf8);
        public void WriteAmf0Object(object value) => amf0.WriteItem(value);
        public void WriteAmf3Object(object value) => amf3.WriteItem(value);
        public void WriteBoxedAmf0Object(ObjectEncoding encoding, object value) => amf0.WriteBoxedItem(encoding, value);
        public void WriteAmfObject(ObjectEncoding encoding, object value)
        {
            if (encoding == ObjectEncoding.Amf0) amf0.WriteItem(value);
            else if (encoding == ObjectEncoding.Amf3) amf3.WriteItem(value);
            else throw new ArgumentOutOfRangeException("unsupported encoding");
        }

        class ReferenceList<T> : Dictionary<T, ushort>
        {
            // returns true if there was already a reference to this object, false otherwise. `index` is always set to
            // the index for object `obj` regardless of return value.
            public bool Add(T obj, out ushort index)
            {
                const int MaximumReferences = 65535;

                if (TryGetValue(obj, out index))
                    return true;

                var count = Count;
                if (count >= MaximumReferences)
                {
                    index = 0;
                    return false;
                }

                Add(obj, index = (ushort)count);
                return false;
            }

            public int GetIndex(T obj) => this[obj];
        }

        #region Base

        struct Base
        {
            // `cache` allows us to avoid allocations when reading items that are less than n bytes
            readonly byte[] temporary;
            readonly ByteWriter writer;

            // constructor
            public Base(ByteWriter writer)
            {
                this.writer = writer;
                temporary = new byte[8];
            }

            // helper
            void CopyTemporary(int length)
                => WriteBytes(temporary, 0, length);

            // writers
            public void WriteByte(byte value) => writer.Write(value);
            public void WriteBytes(byte[] buffer) => writer.Write(buffer);
            public void WriteBytes(byte[] buffer, int index, int count) => writer.Write(buffer, index, count);
            public void WriteBytes(Space<byte> span) => writer.Write(span);
            public void WriteInt16(short value)
            {
                temporary[0] = (byte)(value >> 8);
                temporary[1] = (byte)value;
                CopyTemporary(2);
            }
            public void WriteUInt16(ushort value)
            {
                temporary[0] = (byte)(value >> 8);
                temporary[1] = (byte)value;
                CopyTemporary(2);
            }
            public void WriteInt32(int value)
            {
                temporary[0] = (byte)(value >> 24);
                temporary[1] = (byte)(value >> 16);
                temporary[2] = (byte)(value >> 8);
                temporary[3] = (byte)value;
                CopyTemporary(4);
            }
            public void WriteUInt32(uint value)
            {
                temporary[0] = (byte)(value >> 24);
                temporary[1] = (byte)(value >> 16);
                temporary[2] = (byte)(value >> 8);
                temporary[3] = (byte)value;
                CopyTemporary(4);
            }
            // writes a little endian 32-bit integer
            public void WriteLittleEndianInt(uint value)
            {
                temporary[0] = (byte)value;
                temporary[1] = (byte)(value >> 8);
                temporary[2] = (byte)(value >> 16);
                temporary[3] = (byte)(value >> 24);
                CopyTemporary(4);
            }
            public void WriteUInt24(uint value)
            {
                temporary[0] = (byte)(value >> 16);
                temporary[1] = (byte)(value >> 8);
                temporary[2] = (byte)(value >> 0);
                CopyTemporary(3);
            }
            public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);
            public unsafe void WriteDouble(double value)
            {
                var temp = *(ulong*)&value;
                temporary[0] = (byte)(temp >> 56);
                temporary[1] = (byte)(temp >> 48);
                temporary[2] = (byte)(temp >> 40);
                temporary[3] = (byte)(temp >> 32);
                temporary[4] = (byte)(temp >> 24);
                temporary[5] = (byte)(temp >> 16);
                temporary[6] = (byte)(temp >> 8);
                temporary[7] = (byte)temp;
                CopyTemporary(8);
            }
            public unsafe void WriteSingle(float value)
            {
                var temp = *(uint*)&value;
                temporary[0] = (byte)(temp >> 24);
                temporary[1] = (byte)(temp >> 16);
                temporary[2] = (byte)(temp >> 8);
                temporary[3] = (byte)temp;
                CopyTemporary(4);
            }
            // string with 16-bit length prefix
            public void WriteUtfPrefixed(string value)
            {
                Check.NotNull(value);
                var utf8 = Encoding.UTF8.GetBytes(value);
                WriteUtfPrefixed(utf8);
            }
            public void WriteUtfPrefixed(byte[] utf8)
            {
                Check.NotNull(utf8);
                WriteUInt16((ushort)utf8.Length);
                WriteBytes(utf8);
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

            // helper methods
            public void Reset() => refs.Clear();

            void ReferenceAdd(object value) => refs.Add(value, out var _);
            bool ReferenceAdd(object value, out ushort index) => refs.Add(value, out index);
            bool ReferenceGet(object value, out ushort index) => refs.TryGetValue(value, out index);

            // writers
            public void WriteItem(object value)
            {
                if (value == null)
                    WriteMarker(Marker.Null);
                else if (ReferenceGet(value, out var index))
                {
                    WriteMarker(Marker.Reference);
                    b.WriteUInt16(index);
                }
                else
                    WriteItemInternal(value);
            }

            // writes an object, with the specified encoding. if amf3 encoding is specified, then it is wrapped in an
            // amf0 envelope that says to upgrade the encoding to amf3
            public void WriteBoxedItem(ObjectEncoding encoding, object value)
            {
                if (value == null)
                    WriteMarker(Marker.Null);
                else if (ReferenceGet(value, out var index))
                {
                    WriteMarker(Marker.Reference);
                    b.WriteUInt16(index);
                }
                else
                    switch (encoding)
                    {
                        case ObjectEncoding.Amf0:
                            WriteItemInternal(value);
                            break;

                        case ObjectEncoding.Amf3:
                            WriteMarker(Marker.Amf3Object);
                            amf3.WriteItem(value);
                            break;

                        default:
                            throw new ArgumentOutOfRangeException(nameof(encoding));
                    }
            }

            void WriteItemInternal(object value)
            {
                var type = value.GetType();

                if (NumberTypes.Contains(type)) WriteNumber(Convert.ToDouble(value));
                else if (Writers.TryGetValue(type, out var write)) write(this, value);
                else DispatchGenericWrite(value);
            }

            // writes a string, either as a short or long strong depending on length.
            void WriteVariantString(string value)
            {
                CheckDebug.NotNull(value);

                var utf8 = Encoding.UTF8.GetBytes(value);
                var length = utf8.Length;

                if (length < ushort.MaxValue)
                {
                    // unsigned 16-bit length
                    WriteMarker(Marker.String);
                    b.WriteUInt16((ushort)utf8.Length);
                    b.WriteBytes(utf8);
                }
                else
                {
                    // unsigned 32-bit length
                    WriteMarker(Marker.LongString);
                    b.WriteUInt32((uint)utf8.Length);
                    b.WriteBytes(utf8);
                }
            }

            void WriteAsObject(AsObject value)
            {
                CheckDebug.NotNull(value);
                ReferenceAdd(value);

                if (string.IsNullOrEmpty(value.TypeName))
                    WriteMarker(Marker.Object);
                else
                {
                    WriteMarker(Marker.TypedObject);
                    b.WriteUtfPrefixed(value.TypeName);
                }

                foreach (var property in value)
                {
                    b.WriteUtfPrefixed(property.Key);
                    WriteItem(property.Value);
                }

                // object end is marked with a zero-length field name, and an end of object marker.
                b.WriteUInt16(0);
                WriteMarker(Marker.ObjectEnd);
            }

            void WriteTypedObject(object value)
            {
                CheckDebug.NotNull(value);
                ReferenceAdd(value);

                var klass = context.GetClassInfo(value);

                WriteMarker(Marker.TypedObject);
                b.WriteUtfPrefixed(klass.Name);

                foreach (var member in klass.Members)
                {
                    b.WriteUtfPrefixed(member.Name);
                    WriteItem(member.GetValue(value));
                }

                // object end is marked with a zero-length field name, and an end of object marker.
                b.WriteUInt16(0);
                WriteMarker(Marker.ObjectEnd);
            }

            void WriteDateTime(DateTime value)
            {
                // http://download.macromedia.com/pub/labs/amf/amf0_spec_121207.pdf
                // """
                // While the design of this type reserves room for time zone offset information,
                // it should not be filled in, nor used, as it is unconventional to change time
                // zones when serializing dates on a network. It is suggested that the time zone
                // be queried independently as needed.
                //  -- AMF0 specification, 2.13 Date Type
                // """

                var duration = value.ToUniversalTime() - UnixDateTime.Epoch;
                WriteMarker(Marker.Date);
                b.WriteDouble(duration.TotalMilliseconds);
                b.WriteUInt16(0); // time zone offset
            }

            void WriteXDocument(XDocument value)
            {
                CheckDebug.NotNull(value);
                ReferenceAdd(value);

                UnmarkedWriteLongString(
                    value.ToString(SaveOptions.DisableFormatting));
            }

            void WriteXElement(XElement value)
            {
                CheckDebug.NotNull(value);
                ReferenceAdd(value);

                UnmarkedWriteLongString(
                    value.ToString(SaveOptions.DisableFormatting));
            }

            void WriteArray(IEnumerable enumerable, int length)
            {
                CheckDebug.NotNull(enumerable);
                ReferenceAdd(enumerable);

                b.WriteInt32(length);

                foreach (var element in enumerable)
                    WriteItem(element);
            }

            void WriteAssociativeArray(IDictionary<string, object> dictionary)
            {
                CheckDebug.NotNull(dictionary);
                ReferenceAdd(dictionary);

                WriteMarker(Marker.EcmaArray);
                b.WriteInt32(dictionary.Count);

                foreach (var (key, value) in dictionary)
                {
                    b.WriteUtfPrefixed(key);
                    WriteItem(value);
                }

                // object end is marked with a zero-length field name, and an end of object marker.
                b.WriteUInt16(0);
                WriteMarker(Marker.ObjectEnd);
            }

            void WriteBoolean(bool value)
            {
                WriteMarker(Marker.Boolean);
                b.WriteBoolean(value);
            }

            void WriteNumber(double value)
            {
                WriteMarker(Marker.Number);
                b.WriteDouble(value);
            }

            void DispatchGenericWrite(object value)
            {
                switch (value)
                {
                    case Enum e:
                        WriteNumber(Convert.ToDouble(e));
                        break;

                    case IDictionary<string, object> dictionary:
                        WriteAssociativeArray(dictionary);
                        break;

                    case IList list:
                        WriteArray(list, list.Count);
                        break;

                    case ICollection collection:
                        WriteArray(collection, collection.Count);
                        break;

                    case IEnumerable enumerable:
                        var type = value.GetType();

                        if (type.ImplementsGenericInterface(typeof(ICollection<>)) || type.ImplementsGenericInterface(typeof(IList<>)))
                        {
                            dynamic d = value;
                            int count = d.Count;

                            WriteArray(enumerable, count);
                        }
                        else
                        {
                            var values = enumerable.Cast<object>().ToArray();
                            WriteArray(values, values.Length);
                        }

                        break;

                    default:
                        WriteTypedObject(value);
                        break;
                }
            }

            void UnmarkedWriteLongString(string value)
            {
                CheckDebug.NotNull(value);

                var utf8 = Encoding.UTF8.GetBytes(value);

                WriteMarker(Marker.LongString);
                b.WriteUInt32((uint)utf8.Length);
                b.WriteBytes(utf8);
            }

            void WriteMarker(Marker marker) =>
                b.WriteByte((byte)marker);

            static readonly Type[] NumberTypes =
            {
                typeof(Byte),
                typeof(Int16),
                typeof(Int32),
                typeof(Int64),

                typeof(SByte),
                typeof(UInt16),
                typeof(UInt32),
                typeof(UInt64),

                typeof(Single),
                typeof(Double),
                typeof(Decimal)
            };

            // ordering is important, entries here are checked sequentially
            static readonly IDictionary<Type, Action<Amf0, object>> Writers = new KeyDictionary<Type, Action<Amf0, object>>()
            {
                { typeof(bool),      (x, v) => x.WriteBoolean((bool)v)            },
                { typeof(char),      (x, v) => x.WriteVariantString(v.ToString()) },
                { typeof(string),    (x, v) => x.WriteVariantString((string)v)    },
                { typeof(DateTime),  (x, v) => x.WriteDateTime((DateTime)v)       },
                { typeof(AsObject),  (x, v) => x.WriteAsObject((AsObject)v)       },
                { typeof(Guid),      (x, v) => x.WriteVariantString(v.ToString()) },
                { typeof(XDocument), (x, v) => x.WriteXDocument((XDocument)v)     },
                { typeof(XElement),  (x, v) => x.WriteXElement((XElement)v)       },
            };

            enum Marker : byte
            {
                Number = 0x00, // 0x00 | 0
                Boolean = 0x01, // 0x01 | 1
                String = 0x02, // 0x02 | 2
                Object = 0x03, // 0x03 | 3
                Movieclip = 0x04, // 0x04 | 4
                Null = 0x05, // 0x05 | 5
                Undefined = 0x06, // 0x06 | 6
                Reference = 0x07, // 0x07 | 7
                EcmaArray = 0x08, // 0x08 | 8
                ObjectEnd = 0x09, // 0x09 | 9
                StrictArray = 0x0A, // 0x0A | 10
                Date = 0x0B, // 0x0B | 11
                LongString = 0x0C, // 0x0C | 12
                Unsupported = 0x0D, // 0x0D | 13
                Recordset = 0x0E, // 0x0E | 14
                Xml = 0x0F, // 0x0F | 15
                TypedObject = 0x10, // 0x10 | 16
                Amf3Object = 0x11, // 0x11 | 17
            };
        }

        #endregion

        #region Amf3

        class Amf3
        {
            readonly SerializationContext context;
            readonly ReferenceList<object> refObjects;
            readonly ReferenceList<string> refStrings;
            readonly ReferenceList<ClassInfo> refClasses;

            readonly Base b;
            readonly AmfWriter writer;

            public Amf3(SerializationContext context, AmfWriter writer, Base b)
            {
                this.b = b;
                this.writer = writer;
                this.context = context;

                refObjects = new ReferenceList<object>();
                refStrings = new ReferenceList<string>();
                refClasses = new ReferenceList<ClassInfo>();
            }

            // public helper methods

            public void Reset()
            {
                refObjects.Clear();
                refStrings.Clear();
                refClasses.Clear();
            }

            // writers

            public void WriteItem(object value)
            {
                if (value == null)
                    WriteMarker(Marker.Null);
                else
                {
                    var type = value.GetType();

                    if (Int29Types.Contains(type)) WriteInt29(Convert.ToInt32(value));
                    else if (NumberTypes.Contains(type)) WriteDouble(Convert.ToDouble(value));
                    else if (Writers.TryGetValue(type, out var writer)) writer(this, value);
                    else DispatchGenericWrite(value);
                }
            }

            void WriteBoolean(bool value) =>
                WriteMarker(value ? Marker.True : Marker.False);

            void WriteArray(IEnumerable enumerable, int length)
            {
                CheckDebug.NotNull(enumerable);

                WriteMarker(Marker.Array);
                if (ObjectReferenceAddOrWrite(enumerable))
                    return;

                WriteInlineHeaderValue(length);

                // empty key signifies end of associative section
                UnmarkedWriteString("", isString: true);

                foreach (var element in enumerable)
                    WriteItem(element);
            }

            void WriteAssociativeArray(IDictionary<string, object> dictionary)
            {
                CheckDebug.NotNull(dictionary);

                WriteMarker(Marker.Array);
                if (ObjectReferenceAddOrWrite(dictionary))
                    return;

                // inline-header-value: number of dense items - zero for an associative array
                WriteInlineHeaderValue(0);

                foreach (var (key, value) in dictionary)
                {
                    UnmarkedWriteString(key, isString: true);
                    WriteItem(value);
                }

                // empty key signifies end of associative section
                UnmarkedWriteString("", isString: true);
            }

            void WriteByteArray(ArraySegment<byte> value)
            {
                CheckDebug.NotNull(value);

                WriteMarker(Marker.ByteArray);
                if (ObjectReferenceAddOrWrite(value))
                    return;

                // inline-header-value: array length
                WriteInlineHeaderValue(value.Count);
                b.WriteBytes(value.Array, value.Offset, value.Count);
            }

            void WriteByteArray(byte[] value)
            {
                var segment = new ArraySegment<byte>(value);
                WriteByteArray(segment);
            }

            void WriteDateTime(DateTime value)
            {
                WriteMarker(Marker.Date);
                if (ObjectReferenceAddOrWrite(value))
                    return;

                var duration = value.ToUniversalTime() - UnixDateTime.Epoch;
                // not used except to denote inline object
                WriteInlineHeaderValue(0);
                b.WriteDouble(duration.TotalMilliseconds);
            }

            void WriteXDocument(XDocument value)
            {
                CheckDebug.NotNull(value);

                WriteMarker(Marker.Xml);
                if (ObjectReferenceAddOrWrite(value))
                    return;

                UnmarkedWriteString(
                    value.ToString(SaveOptions.DisableFormatting) ?? "",
                    isString: false);
            }

            void WriteXElement(XElement value)
            {
                WriteMarker(Marker.Xml);
                if (ObjectReferenceAddOrWrite(value))
                    return;

                UnmarkedWriteString(
                    value.ToString(SaveOptions.DisableFormatting) ?? "",
                    isString: false);
            }

            void WriteVector<T>(
                Marker marker,
                bool isObjectVector,
                bool isFixedLength,
                IList items,
                Action<T> write)
            {
                CheckDebug.NotNull(items);

                WriteMarker(marker);
                if (ObjectReferenceAddOrWrite(items))
                    return;

                WriteInlineHeaderValue(items.Count);

                b.WriteBoolean(isFixedLength);
                UnmarkedWriteString(isObjectVector ? "*" : "", isString: true);

                foreach (var item in items)
                    write((T)item);
            }

            void WriteDictionary(IDictionary value)
            {
                CheckDebug.NotNull(value);

                WriteMarker(Marker.Dictionary);
                if (ObjectReferenceAddOrWrite(value))
                    return;

                WriteInlineHeaderValue(value.Count);

                // true:  weakly referenced entries
                // false: strongly referenced entries
                b.WriteBoolean(false);

                foreach (DictionaryEntry entry in value)
                {
                    WriteItem(entry.Key);
                    WriteItem(entry.Value);
                }
            }

            void WriteObject(object obj)
            {
                CheckDebug.NotNull(obj);

                WriteMarker(Marker.Object);
                if (ObjectReferenceAddOrWrite(obj))
                    return;

                var info = context.GetClassInfo(obj);
                if (refClasses.Add(info, out var index))
                {
                    // http://download.macromedia.com/pub/labs/amf/amf3_spec_121207.pdf
                    // """
                    // The first (low) bit is a flag with value 1. The second bit is a flag
                    // (representing whether a trait reference follows) with value 0 to imply that
                    // this objects traits are being sent by reference. The remaining 1 to 27
                    // significant bits are used to encode a trait reference index (an integer).
                    // -- AMF3 specification, 3.12 Object type
                    // """

                    // <u27=trait-reference-index> <0=trait-reference> <1=object-inline>
                    WriteInlineHeaderValue(index << 1);
                }
                else
                {
                    // write the class definition
                    // we can use the same format to serialize normal and extern classes, for simplicity's sake.
                    //     normal:         <u25=member-count>  <u1=dynamic>       <0=externalizable> <1=trait-inline> <1=object-inline>
                    //     externalizable: <u25=insignificant> <u1=insignificant> <1=externalizable> <1=trait-inline> <1=object-inline>
                    var header = info.Members.Length;
                    header = (header << 1) | (info.IsDynamic ? 1 : 0);
                    header = (header << 1) | (info.IsExternalizable ? 1 : 0);
                    header = (header << 1) | 1;

                    // the final shift is done here.
                    WriteInlineHeaderValue(header);

                    // write the type name
                    UnmarkedWriteString(info.Name, isString: true);

                    // then, write the actual object value
                    if (info.IsExternalizable)
                    {
                        if (!(obj is IExternalizable externalizable))
                            throw new ArgumentException($"{obj.GetType().FullName} ({info.Name}) is marked as externalizable but does not implement IExternalizable");

                        externalizable.WriteExternal(new DataOutput(writer));
                    }
                    else
                    {
                        foreach (var member in info.Members)
                            UnmarkedWriteString(member.Name, isString: true);

                        foreach (var member in info.Members)
                            WriteItem(member.GetValue(obj));

                        if (info.IsDynamic)
                        {
                            if (!(obj is IDictionary<string, object> dictionary))
                                throw new ArgumentException($"{obj.GetType()} is marked as dynamic but does not implement IDictionary");

                            foreach (var (key, value) in dictionary)
                            {
                                UnmarkedWriteString(key, isString: true);
                                WriteItem(value);
                            }

                            UnmarkedWriteString(string.Empty, isString: true);
                        }
                    }
                }
            }

            void WriteVariantNumber(int value)
            {
                if (value >= -268435456 && value <= 268435455)
                    WriteInt29(value);
                else
                    WriteDouble(value);
            }

            void WriteInt29(int value)
            {
                WriteMarker(Marker.Integer);
                UnmarkedWriteInt29(value);
            }

            void WriteDouble(double value)
            {
                WriteMarker(Marker.Double);
                b.WriteDouble(value);
            }

            void WriteString(string value)
            {
                WriteMarker(Marker.String);
                UnmarkedWriteString(value, isString: true);
            }

            // internal helper methods

            void DispatchGenericWrite(object value)
            {
                switch (value)
                {
                    case IExternalizable externalizable:
                        WriteObject(externalizable);
                        break;

                    case IDictionary<string, object> dictionary:
                        WriteAssociativeArray(dictionary);
                        break;

                    case IList list:
                        WriteArray(list, list.Count);
                        break;

                    case ICollection collection:
                        WriteArray(collection, collection.Count);
                        break;

                    case IEnumerable enumerable:
                        var type = value.GetType();

                        if (type.ImplementsGenericInterface(typeof(ICollection<>)) || type.ImplementsGenericInterface(typeof(IList<>)))
                        {
                            dynamic d = value;
                            int count = d.Count;

                            WriteArray(enumerable, count);
                        }
                        else
                        {
                            var values = enumerable.Cast<object>().ToArray();
                            WriteArray(values, values.Length);
                        }

                        break;

                    default:
                        WriteObject(value);
                        break;
                }
            }

            void UnmarkedWriteString(string value, bool isString)
            {
                CheckDebug.NotNull(value);

                if (value == "")
                {
                    // spec: empty strings are never sent by reference
                    WriteInlineHeaderValue(0);
                    return;
                }

                if (isString ? ReferenceListAddOrWriteInternal(refStrings, value) : ReferenceListAddOrWriteInternal(refObjects, value))
                    return;

                var bytes = Encoding.UTF8.GetBytes(value);
                WriteInlineHeaderValue(bytes.Length);
                b.WriteBytes(bytes);
            }

            // writes a variable length 29-bit signed integer. sign does not matter, may take an unsigned int.
            void UnmarkedWriteInt29(int value)
            {
                Kon.Assert(value >= -268435456 && value <= 268435455, "value isn't in the range of encodable 29-bit numbers");

                // sign contraction - the high order bit of the resulting value must match every bit removed from the number
                // clear 3 bits
                value = value & 0x1fffffff;

                if (value < 0x80)
                    b.WriteByte((byte)value);
                else if (value < 0x4000)
                {
                    b.WriteByte((byte)(value >> 7 & 0x7f | 0x80));
                    b.WriteByte((byte)(value & 0x7f));
                }
                else if (value < 0x200000)
                {
                    b.WriteByte((byte)(value >> 14 & 0x7f | 0x80));
                    b.WriteByte((byte)(value >> 7 & 0x7f | 0x80));
                    b.WriteByte((byte)(value & 0x7f));
                }
                else
                {
                    b.WriteByte((byte)(value >> 22 & 0x7f | 0x80));
                    b.WriteByte((byte)(value >> 15 & 0x7f | 0x80));
                    b.WriteByte((byte)(value >> 8 & 0x7f | 0x80));
                    b.WriteByte((byte)(value & 0xff));
                }
            }

            void WriteMarker(Marker marker) =>
                b.WriteByte((byte)marker);

            void WriteInlineHeaderValue(int value)
            {
                // 0: object reference
                // 1: inline object (not an object reference)
                UnmarkedWriteInt29((value << 1) | 1);
            }

            // returns true after writing a reference marker if an existing reference existed, otherwise returning false.
            bool ReferenceListAddOrWriteInternal<T>(ReferenceList<T> refs, T value)
            {
                if (refs.Add(value, out var index))
                {
                    // 0: object reference (not inline)
                    // 1: object inline
                    UnmarkedWriteInt29(index << 1);
                    return true;
                }

                return false;
            }

            bool ObjectReferenceAddOrWrite(object value)
            {
                return ReferenceListAddOrWriteInternal(refObjects, value);
            }

            static readonly Type[] Int29Types = { typeof(SByte), typeof(Byte), typeof(Int16), typeof(UInt16), typeof(Int32), typeof(UInt32) };
            static readonly Type[] NumberTypes = { typeof(Int64), typeof(UInt64), typeof(Single), typeof(Double), typeof(Decimal) };

            // ordering is important, entries here are checked sequentially
            static readonly IDictionary<Type, Action<Amf3, object>> Writers = new KeyDictionary<Type, Action<Amf3, object>>()
            {
                { typeof(bool),      (x, v) => x.WriteBoolean((bool)v)                 },
                { typeof(char),      (x, v) => x.WriteString(v.ToString())             },
                { typeof(string),    (x, v) => x.WriteString((string)v)                },
                { typeof(byte[]),    (x, v) => x.WriteByteArray((byte[])v)             },
                { typeof(DateTime),  (x, v) => x.WriteDateTime((DateTime)v)            },
                { typeof(AsObject),  (x, v) => x.WriteObject((AsObject)v)              }, // required, or asobject will be detected as an IDictionary<string, object> and thus written as an associative array
                { typeof(ByteArray), (x, v) => x.WriteByteArray(((ByteArray)v).Buffer) },
                { typeof(Guid),      (x, v) => x.WriteString(v.ToString())             },
                { typeof(XDocument), (x, v) => x.WriteXDocument((XDocument)v)          },
                { typeof(XElement),  (x, v) => x.WriteXElement((XElement)v)            },
            };

            enum Marker : byte
            {
                Undefined = 0x00, // 0x00 | 0
                Null = 0x01, // 0x01 | 1
                False = 0x02, // 0x02 | 2
                True = 0x03, // 0x03 | 3
                Integer = 0x04, // 0x04 | 4
                Double = 0x05, // 0x05 | 5
                String = 0x06, // 0x06 | 6
                LegacyXml = 0x07, // 0x07 | 7
                Date = 0x08, // 0x08 | 8
                Array = 0x09, // 0x09 | 9
                Object = 0x0A, // 0x0A | 10
                Xml = 0x0B, // 0x0B | 11
                ByteArray = 0x0C, // 0x0C | 12
                VectorInt = 0x0D, // 0x0D | 13
                VectorUInt = 0x0E, // 0x0E | 14
                VectorDouble = 0x0F, // 0x0F | 15
                VectorObject = 0x10, // 0x10 | 16
                Dictionary = 0x11  // 0x11 | 17
            };
        }

        #endregion
    }
}
