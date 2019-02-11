using Hina.Collections;
using System;
using System.Collections.Generic;

// field is never assigned to, and will always have its default value null
#pragma warning disable CS0649

namespace Rtmp.FlexMessages
{
    #region MessageReceivedEventArgs

    public class MessageReceivedEventArgs
    {
        public readonly object Body;
        public readonly string ClientId;
        public readonly string Subtopic;

        internal MessageReceivedEventArgs(string clientId, string subtopic, object body)
        {
            ClientId = clientId;
            Subtopic = subtopic;
            Body = body;
        }
    }

    #endregion

    #region InvocationException

    public class InvocationException : Exception
    {
        public string FaultCode;
        public string FaultString;
        public string FaultDetail;
        public object RootCause;
        public object ExtendedData;
        public object SourceException;

        public override string Message => FaultString;
        public override string StackTrace => FaultDetail;

        internal InvocationException(object source, string faultCode, string faultString, string faultDetail, object rootCause, object extendedData)
        {
            FaultCode = faultCode;
            FaultString = faultString;
            FaultDetail = faultDetail;
            RootCause = rootCause;
            ExtendedData = extendedData;

            SourceException = source;
        }

        public InvocationException() { }
    }

    #endregion

    #region AcknowledgeMessage

    [Rtmp("flex.messaging.messages.AcknowledgeMessage", "DSK")]
    class AcknowledgeMessage : FlexMessage
    {
        public AcknowledgeMessage()
            => Timestamp = Environment.TickCount;
    }

    #endregion

    #region AsyncMessage

    [Rtmp("flex.messaging.messages.AsyncMessage", "DSA")]
    class AsyncMessage : FlexMessage
    {
        [Rtmp("correlationId")]
        public string CorrelationId;
    }

    static class AsyncMessageHeaders
    {
        public const string Subtopic = "DSSubtopic";
    }

    #endregion

    #region CommandMessage

    [Rtmp("flex.messaging.messages.CommandMessage", "DSC")]
    class CommandMessage : AsyncMessage
    {
        [Rtmp("messageRefType")]
        public string Type;

        [Rtmp("operation")]
        public Operations Operation;

        public enum Operations : int
        {
            Subscribe = 0,
            Unsubscribe = 1,
            Poll = 2, // poll for undelivered messages
            DataUpdateAttributes = 3,
            ClientSync = 4, // sent by remote to sync missed or cached messages to a client as a result of a client issued poll command
            ClientPing = 5, // connectivity test
            DataUpdate = 7,
            ClusterRequest = 7, // request a list of failover endpoint URIs for the remote destination based on cluster membership
            Login = 8,
            Logout = 9,
            InvalidateSubscription = 10, // indicates that client subscription has been invalidated (eg timed out)
            ChannelDisconnected = 12, // indicates that a channel has disconnected
            Unknown = 10000
        }
    }

    #endregion

    #region ErrorMessage

    [Rtmp("flex.messaging.messages.ErrorMessage")]
    class ErrorMessage : FlexMessage
    {
        [Rtmp("faultCode")]
        public string FaultCode;

        [Rtmp("faultString")]
        public string FaultString;

        [Rtmp("faultDetail")]
        public string FaultDetail;

        [Rtmp("rootCause")]
        public object RootCause;

        [Rtmp("extendedData")]
        public object ExtendedData;
    }

    #endregion

    #region FlexMessage

    class FlexMessage
    {
        IDictionary<string, object> headers;

        [Rtmp("clientId")]
        public string ClientId;

        [Rtmp("destination")]
        public string Destination;

        [Rtmp("messageId")]
        public string MessageId;

        [Rtmp("timestamp")]
        public long Timestamp;

        // ttl, in milliseconds, after `timestamp` that this message remains valid
        [Rtmp("timeToLive")]
        public long TimeToLive;

        [Rtmp("body")]
        public object Body;

        [Rtmp("headers")]
        public IDictionary<string, object> Headers
        {
            get => headers ?? (headers = new KeyDictionary<string, object>());
            set => headers = value;
        }

        public FlexMessage() => MessageId = Guid.NewGuid().ToString("D");
    }

    static class FlexMessageHeaders
    {
        // messages pushed from the server may arrive in a batch, with messages in the batch potentially targeted to
        // different consumer instances.
        //
        // each message will contain this header identifying the consumer instance that will receive the message.
        public const string DestinationClientId = "DSDstClientId";

        // messages are tagged with the endpoint id for the channel they are sent over. channels set this value
        // automatically when they send a message.
        public const string Endpoint = "DSEndpoint";

        // messages that need to set remote credentials for a destination carry the base64 encoded credentials in this
        // header.
        public const string RemoteCredentials = "DSRemoteCredentials";

        // messages sent with a defined request timeout use this header.
        //
        // the request timeout value is set on outbound messages by services or channels and the value
        // controls how long the corresponding MessageResponder will wait for an acknowledgement,
        // result or fault response for the message before timing out the request.
        public const string RequestTimeout = "DSRequestTimeout";

        // this header is used to transport the global flex client id in outbound messages, once it has been assigned
        // by the server.
        public const string FlexClientId = "DSId";
    }

    #endregion

    #region RemotingMessage

    [Rtmp("flex.messaging.messages.RemotingMessage")]
    class RemotingMessage : FlexMessage
    {
        [Rtmp("source")]
        public string Source;

        [Rtmp("operation")]
        public string Operation;
    }

    #endregion
}
