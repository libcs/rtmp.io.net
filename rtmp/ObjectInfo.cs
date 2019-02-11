using Hina;
using Hina.Linq;
using Rtmp.IO.Amf3;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Rtmp
{
    interface IObjectInfo
    {
        bool IsExternalizable(object instance);
        bool IsDynamic(object instance);
        ClassInfo GetClassInfo(object instance);
    }

    class ClassInfo
    {
        public string Name { get; }
        public IMemberInfo[] Members { get; }
        public bool IsExternalizable { get; }
        public bool IsDynamic { get; }
        public bool IsTyped => !string.IsNullOrEmpty(Name);

        public virtual bool TryGetMember(string name, out IMemberInfo member) =>
            throw new NotImplementedException();

        public ClassInfo(string name, IMemberInfo[] members, bool externalizable, bool dynamic)
        {
            Name = name;
            Members = members;
            IsExternalizable = externalizable;
            IsDynamic = dynamic;
        }
    }

    interface IMemberInfo
    {
        string Name { get; }
        string LocalName { get; }
        object GetValue(object instance);
        void SetValue(object instance, object value);
    }

    // provides information about object types for some rtmp serialization context. provides types, names, and
    // facilities to get and set values of instances of those objects.
    class ObjectInfo
    {
        readonly SerializationContext context;

        readonly IObjectInfo basic;
        readonly IObjectInfo externalizable;
        readonly IObjectInfo asObject;
        readonly IObjectInfo exception;

        public ObjectInfo(SerializationContext context)
        {
            this.context = context;

            basic = new BasicObjectInfo(context);
            asObject = new AsObjectInfo(context);
            externalizable = new ExternalizableObjectInfo(context);
            exception = new ExceptionObjectInfo(context);
        }

        public IObjectInfo GetInstance(object obj)
        {
            if (obj is IExternalizable) return basic;
            if (obj is AsObject) return asObject;
            if (obj is Exception) return exception;

            return basic;
        }

        public ClassInfo GetClassInfo(object obj) => GetInstance(obj).GetClassInfo(obj);

        #region BasicObjectInfo

        class BasicObjectInfo : IObjectInfo
        {
            readonly ConcurrentDictionary<Type, ClassInfo> cache = new ConcurrentDictionary<Type, ClassInfo>();

            readonly SerializationContext context;

            public BasicObjectInfo(SerializationContext context) => this.context = context;
            public bool IsExternalizable(object instance) => instance is IExternalizable;
            public bool IsDynamic(object instance) => instance is AsObject;

            public virtual ClassInfo GetClassInfo(object instance)
            {
                Check.NotNull(instance);

                return cache.GetOrAdd(instance.GetType(), type =>
                {
                    var (fields, properties) = Helper.GetSerializableFields(type);
                    var members = new List<IMemberInfo>();

                    members.AddRange(
                        fields.Select(x => new BasicObjectMemberInfo(x)));

                    foreach (var property in properties)
                    {
                        // there is no reflection api that allows us to check whether a variable hides another variable (in c#,
                        // that would be with the `new` keyword). to do this, we have to manually attempt to access a property
                        // by name detect ambiguous matches.
                        try
                        {
                            type.GetProperty(property.Name);
                        }
                        catch (AmbiguousMatchException)
                        {
                            if (type.DeclaringType != type)
                                continue;
                        }

                        members.Add(new BasicObjectMemberInfo(property));
                    }

                    return new BasicObjectClassInfo(
                        name: context.GetCanonicalName(type.FullName),
                        members: members.ToArray(),
                        externalizable: IsExternalizable(instance),
                        dynamic: IsDynamic(instance));
                });
            }

            class BasicObjectClassInfo : ClassInfo
            {
                IDictionary<string, IMemberInfo> lookup;

                public BasicObjectClassInfo(string name, IMemberInfo[] members, bool externalizable, bool dynamic) : base(name, members, externalizable, dynamic) =>
                    lookup = members.ToQuickDictionary(
                        x => string.IsNullOrEmpty(x.Name) ? x.LocalName : x.Name,
                        x => x);

                public override bool TryGetMember(string name, out IMemberInfo member) =>
                    lookup.TryGetValue(name, out member);
            }

            class BasicObjectMemberInfo : IMemberInfo
            {
                readonly Func<object, object> getValue;
                readonly Action<object, object> setValue;
                readonly Type valueType;

                public string LocalName { get; }
                public string Name { get; }

                public BasicObjectMemberInfo(PropertyInfo property)
                {
                    LocalName = property.Name;
                    Name = property.GetCustomAttribute<RtmpAttribute>(true)?.CanonicalName ?? LocalName;

                    getValue = Helper.AccessProperty(property);
                    setValue = Helper.AssignProperty(property);
                    valueType = property.PropertyType;
                }

                public BasicObjectMemberInfo(FieldInfo field)
                {
                    LocalName = field.Name;
                    Name = field.GetCustomAttribute<RtmpAttribute>(true)?.CanonicalName ?? LocalName;

                    getValue = Helper.AccessField(field);
                    setValue = Helper.AssignField(field);
                    valueType = field.FieldType;
                }

                public object GetValue(object instance) => getValue(instance);
                public void SetValue(object instance, object value) => setValue(instance, NanoTypeConverter.ConvertTo(value, valueType));
            }

            static class Helper
            {
                public static (FieldInfo[] fields, PropertyInfo[] properties) GetSerializableFields(object obj) =>
                    GetSerializableFields(obj.GetType());

                public static (FieldInfo[] fields, PropertyInfo[] properties) GetSerializableFields(Type type)
                {
                    var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(x => x.GetCustomAttributes(typeof(RtmpIgnoreAttribute), true).None())
                        .Where(x => x.GetGetMethod()?.GetParameters().Length == 0) // skip if not a "pure" get property, aka has parameters (eg `class[int index]`)
                        .ToArray();

                    var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Where(x => x.GetCustomAttributes(typeof(RtmpIgnoreAttribute), true).None())
                        .ToArray();

                    return (fields, properties);
                }

                public static Action<object, object> AssignField(FieldInfo field)
                {
                    var instance = Expression.Parameter(typeof(object));
                    var value = Expression.Parameter(typeof(object));
                    var assign = Expression.Assign(
                        Expression.Field(
                            Expression.Convert(instance, field.DeclaringType),
                            field),
                        Expression.Convert(value, field.FieldType));

                    return Expression.Lambda<Action<object, object>>(assign, instance, value).Compile();
                }

                public static Func<object, object> AccessField(FieldInfo field)
                {
                    var instance = Expression.Parameter(typeof(object));
                    var access = Expression.Convert(
                        Expression.Field(
                            Expression.Convert(instance, field.DeclaringType),
                            field),
                        typeof(object));

                    return Expression.Lambda<Func<object, object>>(access, instance).Compile();
                }

                public static Action<object, object> AssignProperty(PropertyInfo property)
                {
                    var instance = Expression.Parameter(typeof(object));
                    var value = Expression.Parameter(typeof(object));
                    var assign = Expression.Assign(
                        Expression.Property(
                            Expression.Convert(instance, property.DeclaringType),
                            property),
                        Expression.Convert(value, property.PropertyType));

                    return Expression.Lambda<Action<object, object>>(assign, instance, value).Compile();
                }

                public static Func<object, object> AccessProperty(PropertyInfo property)
                {
                    var instance = Expression.Parameter(typeof(object));
                    var access = Expression.Convert(
                        Expression.Property(
                            Expression.Convert(instance, property.DeclaringType),
                            property),
                        typeof(object));

                    return Expression.Lambda<Func<object, object>>(access, instance).Compile();
                }
            }
        }

        #endregion

        #region AsObjectInfo

        class AsObjectInfo : IObjectInfo
        {
            readonly SerializationContext context;

            public AsObjectInfo(SerializationContext context) => this.context = context;
            public bool IsDynamic(object instance) => ((AsObject)instance).IsTyped;
            public bool IsExternalizable(object instance) => false;

            public ClassInfo GetClassInfo(object instance)
            {
                var obj = (AsObject)instance;

                return obj.IsTyped
                    ? new AsObjectClassInfo(
                        name: context.GetCanonicalName(obj.TypeName),
                        members: obj.MapArray(x => new AsObjectMemberInfo(x.Key)),
                        externalizable: false,
                        dynamic: false)
                    : AsObjectClassInfo.Empty;
            }

            class AsObjectClassInfo : ClassInfo
            {
                public static readonly ClassInfo Empty = new AsObjectClassInfo(
                    name: string.Empty,
                    members: new IMemberInfo[0],
                    externalizable: false,
                    dynamic: true);

                public AsObjectClassInfo(string name, IMemberInfo[] members, bool externalizable, bool dynamic)
                    : base(name, members, externalizable, dynamic) { }

                public override bool TryGetMember(string name, out IMemberInfo member)
                {
                    member = new AsObjectMemberInfo(name);
                    return true;
                }
            }

            class AsObjectMemberInfo : IMemberInfo
            {
                public string Name => LocalName;
                public string LocalName { get; }

                public AsObjectMemberInfo(string name) => LocalName = name;

                public object GetValue(object instance) => ((AsObject)instance)[LocalName];
                public void SetValue(object instance, object value) => ((AsObject)instance)[LocalName] = value;
            }
        }

        #endregion

        #region ExceptionObjectInfo

        class ExceptionObjectInfo : BasicObjectInfo
        {
            static readonly string[] ExcludedMembers = { "HelpLink", "HResult", "Source", "StackTrace", "TargetSite" };

            public ExceptionObjectInfo(SerializationContext context)
                : base(context) { }

            public override ClassInfo GetClassInfo(object instance)
            {
                var klass = base.GetClassInfo(instance);

                return new ClassInfo(
                    name: klass.Name,
                    members: klass.Members.FilterArray(x => !ExcludedMembers.Contains(x.LocalName)),
                    externalizable: klass.IsExternalizable,
                    dynamic: klass.IsDynamic);
            }
        }

        #endregion

        #region ExternalizableObjectInfo

        class ExternalizableObjectInfo : IObjectInfo
        {
            readonly SerializationContext context;

            public ExternalizableObjectInfo(SerializationContext context) => this.context = context;
            public bool IsDynamic(object instance) => false;
            public bool IsExternalizable(object instance) => false;

            public ClassInfo GetClassInfo(object instance)
            {
                var type = instance.GetType();

                return new ExternalizableClassInfo(
                    name: context.GetCanonicalName(type.FullName),
                    members: EmptyCollection<IMemberInfo>.Array,
                    externalizable: true,
                    dynamic: false);
            }

            class ExternalizableClassInfo : ClassInfo
            {
                public ExternalizableClassInfo(string name, IMemberInfo[] members, bool externalizable, bool dynamic)
                    : base(name, members, externalizable, dynamic) { }

                public override bool TryGetMember(string name, out IMemberInfo member) =>
                    throw new InvalidOperationException("attempting to access member info for externalizable object");
            }
        }

        #endregion
    }
}