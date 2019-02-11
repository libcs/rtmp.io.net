using Hina;
using Rtmp.FlexMessages;
using Rtmp.IO.Amf3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Rtmp
{
    public class SerializationContext
    {
        // if true, then we fall back to deserializing unregistered objects into asobjects. otherwise, trying to
        // deserialize an object with unknown types will throw an exception.
        public bool AsObjectFallback = true;

        // for rtmp connections, specifies the largest allocation allowed when reading from a remote peer. this will be
        // the largest buffer size we allocate in order to read and deserialize packets and object trees. if a remote
        // peer attempts to send us a packet or object that is larger than this value, an exception is thrown and the
        // connection is closed.
        public int MaximumReadAllocation = 4192;

        readonly TypeRegistry registry;
        readonly ObjectInfo infos;

        public SerializationContext(params Type[] types)
        {
            infos = new ObjectInfo(this);
            registry = new TypeRegistry();

            foreach (var type in types)
                registry.RegisterType(type);
        }

        public object CreateInstance(string name) => CreateOrNull(name) ?? throw new InvalidOperationException($"the type \"{name}\" hasn't been registered with this context");

        internal object CreateOrNull(string name) => registry.CreateOrNull(name);
        internal bool HasConcreteType(string name) => registry.Exists(name);
        internal string GetCanonicalName(string name) => registry.CanonicalName(name);
        internal ClassInfo GetClassInfo(object obj) => infos.GetClassInfo(obj) ?? throw new InvalidOperationException("couldn't get class description for that object");

        internal void RequestReadAllocation(int requested)
        {
            if (requested > MaximumReadAllocation)
                throw new InvalidOperationException("attempted to allocate more than the maximum read allocation amount");
        }

        #region TypeRegistry

        class TypeRegistry
        {
            readonly Dictionary<Type, MethodFactory.ConstructorCall> constructors = new Dictionary<Type, MethodFactory.ConstructorCall>();
            readonly Dictionary<string, Type> localTypeLookup = new Dictionary<string, Type>();
            readonly Dictionary<string, string> remoteNameLookup = new Dictionary<string, string>();

            public TypeRegistry()
            {
                foreach (var type in DefaultTypes)
                    RegisterType(type);
            }

            public string CanonicalName(string name) => remoteNameLookup.TryGetValue(name, out var canonical) ? canonical : name;
            public bool Exists(string name) => localTypeLookup.TryGetValue(name, out var type) && constructors.ContainsKey(type);
            public object CreateOrNull(string name) => localTypeLookup.TryGetValue(name, out var type) ? constructors[type](EmptyCollection<object>.Array) : null;

            // registry this type with the registry
            public void RegisterType(Type type)
            {
                var info = type.GetTypeInfo();
                if (info.IsEnum || constructors.ContainsKey(type))
                    return;

                var constructor = type.GetConstructors().FirstOrDefault(x => x.GetParameters().Length == 0);
                if (constructor == null)
                    throw new ArgumentException($"{type.FullName} does not have any accessible parameterless constructors.", nameof(type));

                var attribute = info.GetCustomAttribute<RtmpAttribute>(false) ?? RtmpAttribute.Empty;
                var canonicalName = attribute.CanonicalName ?? type.FullName;
                var names = attribute.Names ?? EmptyCollection<string>.Array;

                foreach (var name in names)
                    localTypeLookup[name] = type;

                constructors[type] = MethodFactory.CompileConstructor(constructor);
                remoteNameLookup[type.FullName] = canonicalName;

                if (canonicalName != "")
                    localTypeLookup[canonicalName] = type;
            }

            static readonly Type[] DefaultTypes =
            {
            typeof(AcknowledgeMessage),
            typeof(ArrayCollection),
            typeof(AsyncMessage),
            typeof(ByteArray),
            typeof(CommandMessage),
            typeof(ErrorMessage),
            typeof(FlexMessage),
            typeof(ObjectProxy),
            typeof(RemotingMessage)
        };
        }

        #endregion
    }
}