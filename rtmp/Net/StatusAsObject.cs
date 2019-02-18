using System;

namespace Rtmp.Net
{
    #region StatusAsObject

    // we use this for responding to invoke requests. but this version of rtmp-sharp doesn't include functionality for
    // handling invocation requests, and is thus currently unused
    class StatusAsObject : AsObject
    {
        readonly Exception exception;

        public StatusAsObject(Exception exception) =>
            this.exception = exception;

        //public StatusAsObject(string code, string description, ObjectEncoding encoding,
        //    object data = null, object application = null)
        //{
        //    this["level"] = "status";
        //    this["code"] = code;
        //    this["description"] = description;
        //    if (data != null)
        //        this["data"] = data;
        //    if (application != null)
        //        this["application"] = application;
        //    this["objectEncoding"] = (double)encoding;
        //}

        public StatusAsObject(string code, string description, ObjectEncoding? encoding = null)
        {
            this["level"] = "status";
            this["code"] = code;
            this["description"] = description;
            if (encoding != null)
                this["objectEncoding"] = (double)encoding.Value;
        }

        public static class Codes
        {
            public const string PublishStart = "NetStream.Publish.Start";
            public const string ConnectSuccess = "NetConnection.Connect.Success";
            public const string ConnectFailed = "NetConnection.Connect.Failed";
            public const string CallSuccess = "NetConnection.Call.Success";
            public const string CallFailed = "NetConnection.Call.Failed";
        }
    }

    #endregion
}
