using System;
using System.Net;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Hina.Net
{
    static class TcpListenerEx
    {
        static async Task<IPAddress> GetAddressAsync(string host)
        {
            if (host == null || string.Equals(host, "anyv6", StringComparison.OrdinalIgnoreCase)) return IPAddress.IPv6Any;
            else if (string.Equals(host, "any", StringComparison.OrdinalIgnoreCase)) return IPAddress.Any;
            else return (await Dns.GetHostEntryAsync(host)).AddressList.FirstOrDefault();
        }

        public static async Task<TcpListener> AcceptClientAsync(string host, int port, bool exclusiveAddressUse = true)
        {
            var address = await GetAddressAsync(host);
            if (address == null)
                throw new ArgumentOutOfRangeException(nameof(host), $"unable to resolve: {host}");

            var x = new TcpListener(address, port) { ExclusiveAddressUse = exclusiveAddressUse };

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
