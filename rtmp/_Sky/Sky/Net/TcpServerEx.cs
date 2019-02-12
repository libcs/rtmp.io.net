using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Hina.Net
{
    static class TcpListenerEx
    {
        public static async Task<TcpListener> AcceptClientAsync(string host, int port, bool exclusiveAddressUse = true)
        {
            var entry = await Dns.GetHostEntryAsync(host);
            if (entry.AddressList.Length == 0)
                throw new Exception("No address");

            var x = new TcpListener(entry.AddressList[0], port) { ExclusiveAddressUse = exclusiveAddressUse };

            //SocketEx.FastSocket(x.Server);

            return x;
        }

        public static async Task<TcpClient> AcceptTcpClientExAsync(this TcpListener listener)
        {
            var x = await listener.AcceptTcpClientAsync();

            //SocketEx.FastSocket(x.Client);

            return x;
        }
    }
}
