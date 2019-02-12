using Hina;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Rtmp
{
    internal class rtmp
    {
        #region assert / logging

        public const int LOG_LEVEL_NONE = 0;
        public const int LOG_LEVEL_ERROR = 1;
        public const int LOG_LEVEL_INFO = 2;
        public const int LOG_LEVEL_DEBUG = 3;

        [DebuggerStepThrough, Conditional("DEBUG")]
        public static void assert(bool condition)
        {
            if (!condition)
            {
                var stackFrame = new StackTrace().GetFrame(1);
                assert_function?.Invoke(null, stackFrame.GetMethod().Name, stackFrame.GetFileName(), stackFrame.GetFileLineNumber());
                Environment.Exit(1);
            }
        }

        static void default_assert_handler(string condition, string function, string file, int line)
        {
            Console.Write($"assert failed: ( {condition} ), function {function}, file {file}, line {line}\n");
            Debugger.Break();
            Environment.Exit(1);
        }

        static int log_level_ = 0;

        static Action<string> printf_function =
            x => Console.Write(x);

        public static Action<string, string, string, int> assert_function = default_assert_handler;

        public static void log_level(int level) =>
            log_level_ = level;

        public static void set_printf_function(Action<string> function)
        {
            assert(function != null);
            printf_function = function;
        }

        public static void set_assert_function(Action<string, string, string, int> function) =>
            assert_function = function;

#if !RTMP_ENABLE_LOGGING
        internal static void printf(int level, string format)
        {
            if (level > log_level_) return;
            printf_function(format);
        }
#else
        static void printf(int level, string format) { }
#endif

        #endregion
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class RtmpIgnoreAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
    public sealed class RtmpAttribute : Attribute
    {
        public static readonly RtmpAttribute Empty = new RtmpAttribute();

        public string CanonicalName;
        public string[] Names;

        public RtmpAttribute(params string[] names)
        {
            Names = names;
            CanonicalName = names.FirstOrDefault();
        }
    }

    public enum ObjectEncoding
    {
        Amf0 = 0,
        Amf3 = 3
    }

    public static class TypeSerializer
    {
        public static void RegisterTypeConverters() =>
            TypeDescriptor.AddAttributes(
                typeof(string),
                new TypeConverterAttribute(typeof(StringToCharConverter)));

        #region TypeConverters

        // converts a single-character `string` into a `char`
        class StringToCharConverter : StringConverter
        {
            public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
                => sourceType == typeof(char)
                    || base.CanConvertFrom(context, sourceType);

            public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
                => value is char
                    ? value.ToString()
                    : base.ConvertFrom(context, culture, value);

            public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
                => destinationType == typeof(string)
                    || base.CanConvertTo(context, destinationType);

            public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
            {
                if (value is string str)
                {
                    switch (str.Length)
                    {
                        case 0: return null;
                        case 1: return str[0];
                        default: throw new ArgumentException("cannot convert a multi-character string into a single character");
                    }
                }

                return base.ConvertTo(context, culture, value, destinationType);
            }
        }

        #endregion
    }

    #region AsObject

    [TypeConverter(typeof(AsObjectConverter))]
    public class AsObject : DynamicObject, IDictionary<string, object>
    {
        string typeName;
        Dictionary<string, object> values;

        public bool IsTyped => !string.IsNullOrEmpty(typeName);

        public string TypeName
        {
            get => typeName ?? string.Empty;
            set => typeName = value;
        }

        // constructor
        public AsObject() =>
            values = new Dictionary<string, object>(0);
        // if owned, this asobject will assume ownership of `dictionary` and use it as its own
        public AsObject(Dictionary<string, object> dictionary, bool owned = false) =>
            values = owned ? dictionary : new Dictionary<string, object>(dictionary);
        public AsObject(string typeName)
        {
            this.typeName = typeName ?? string.Empty;
            values = new Dictionary<string, object>(0);
        }

        // deserialization helpers

        internal void Replace(IEnumerable<(string key, object value)> items) =>
            values = items.ToDictionary(x => x.key, x => x.value);

        // DynamicObject members

        public override IEnumerable<string> GetDynamicMemberNames() => values.Keys;
        public override bool TryGetMember(GetMemberBinder binder, out object result) => values.TryGetValue(binder.Name, out result);
        public override bool TryDeleteMember(DeleteMemberBinder binder) => values.Remove(binder.Name);
        public override bool TrySetMember(SetMemberBinder binder, object value) { values[binder.Name] = value; return true; }

        // IDictionary<> members

        IDictionary<string, object> IDictionary => values;

        public int Count => values.Count;
        public bool IsReadOnly => IDictionary.IsReadOnly;
        public ICollection<string> Keys => values.Keys;
        public ICollection<object> Values => values.Values;

        public void Add(KeyValuePair<string, object> item) => IDictionary.Add(item);
        public void Clear() => values.Clear();
        public bool Contains(KeyValuePair<string, object> item) => values.Contains(item);
        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex) => IDictionary.CopyTo(array, arrayIndex);
        public bool Remove(KeyValuePair<string, object> item) => IDictionary.Remove(item);
        public void Add(string key, object value) => values.Add(key, value);
        public bool ContainsKey(string key) => values.ContainsKey(key);
        public bool Remove(string key) => values.Remove(key);
        public bool TryGetValue(string key, out object value) => values.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public object this[string key]
        {
            get => values[key];
            set => values[key] = value;
        }
    }

    public class AsObjectConverter : TypeConverter
    {
        readonly SerializationContext context;

        public AsObjectConverter() { }
        public AsObjectConverter(SerializationContext context) => this.context = context;

        public override bool CanConvertTo(ITypeDescriptorContext context, Type destination)
        {
            var info = destination.GetTypeInfo();
            var unsupported = info.IsValueType || info.IsEnum || info.IsArray || info.IsAbstract || info.IsInterface;

            return !unsupported;
        }

        public override object ConvertTo(ITypeDescriptorContext descriptorContext, CultureInfo culture, object source, Type destinationType)
        {
            var instance = MethodFactory.CreateInstance(destinationType);
            var klass = context.GetClassInfo(instance);

            if (source is IDictionary<string, object> dictionary)
            {
                foreach (var (key, value) in dictionary)
                {
                    if (klass.TryGetMember(key, out var member))
                        member.SetValue(instance, value);
                }

                return instance;
            }

            return base.ConvertTo(descriptorContext, culture, source, destinationType);
        }
    }

    #endregion
}
