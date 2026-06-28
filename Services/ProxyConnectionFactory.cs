using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CxShell.Models;
using Renci.SshNet;

namespace CxShell.Services;

public static class ProxyConnectionFactory
{
    public static ConnectionInfo CreateSshConnectionInfo(
        SessionInfo session,
        IReadOnlyList<AuthenticationMethod> authMethods)
    {
        if (session.Proxy?.IsEnabled != true)
            return new ConnectionInfo(session.Host, session.Port, session.Username, authMethods.ToArray());

        return new ConnectionInfo(
            session.Host,
            session.Port,
            session.Username,
            ToSshProxyType(session.Proxy),
            session.Proxy.Host,
            session.Proxy.Port,
            session.Proxy.Username,
            session.Proxy.Password,
            authMethods.ToArray());
    }

    public static async Task<TcpClient> ConnectTcpAsync(
        string host,
        int port,
        ProxySettings? proxy,
        CancellationToken cancellationToken,
        string ipVersion = "Auto")
    {
        if (proxy?.IsEnabled != true)
        {
            var directClient = new TcpClient();
            await ConnectClientAsync(directClient, host, port, ipVersion, cancellationToken);
            return directClient;
        }

        var client = new TcpClient();
        try
        {
            await ConnectClientAsync(client, proxy.Host, proxy.Port, ipVersion, cancellationToken);
            var stream = client.GetStream();
            switch (proxy.Protocol)
            {
                case ProxyProtocol.Http:
                    await ConnectHttpAsync(stream, host, port, proxy, cancellationToken);
                    break;
                case ProxyProtocol.Socks4:
                case ProxyProtocol.Socks4A:
                    await ConnectSocks4Async(stream, host, port, proxy, cancellationToken);
                    break;
                case ProxyProtocol.Socks5:
                    await ConnectSocks5Async(stream, host, port, proxy, cancellationToken);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported proxy protocol: {proxy.Protocol}");
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task ConnectClientAsync(
        TcpClient client,
        string host,
        int port,
        string ipVersion,
        CancellationToken cancellationToken)
    {
        if (string.Equals(ipVersion, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            await client.ConnectAsync(host, port, cancellationToken);
            return;
        }

        var family = string.Equals(ipVersion, "IPv6", StringComparison.OrdinalIgnoreCase)
            ? AddressFamily.InterNetworkV6
            : AddressFamily.InterNetwork;
        var addresses = await Dns.GetHostAddressesAsync(host, family, cancellationToken);
        var address = addresses.FirstOrDefault();
        if (address == null)
            throw new SocketException((int)SocketError.HostNotFound);

        await client.ConnectAsync(address, port, cancellationToken);
    }

    private static ProxyTypes ToSshProxyType(ProxySettings proxy)
    {
        return proxy.Protocol switch
        {
            ProxyProtocol.Http => ProxyTypes.Http,
            ProxyProtocol.Socks4 => ProxyTypes.Socks4,
            ProxyProtocol.Socks4A => ProxyTypes.Socks4,
            ProxyProtocol.Socks5 => ProxyTypes.Socks5,
            ProxyProtocol.SshPassthrough or ProxyProtocol.JumpHost =>
                throw new NotSupportedException($"{proxy.TypeDisplay} proxy connection is not implemented yet."),
            _ => ProxyTypes.None
        };
    }

    private static async Task ConnectHttpAsync(
        NetworkStream stream,
        string host,
        int port,
        ProxySettings proxy,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInvariant($"CONNECT {host}:{port} HTTP/1.1\r\n"));
        builder.Append(CultureInvariant($"Host: {host}:{port}\r\n"));
        if (!string.IsNullOrWhiteSpace(proxy.Username))
        {
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{proxy.Username}:{proxy.Password}"));
            builder.Append(CultureInvariant($"Proxy-Authorization: Basic {token}\r\n"));
        }

        builder.Append("\r\n");
        await WriteAsync(stream, Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken);

        var response = await ReadHttpHeaderAsync(stream, cancellationToken);
        if (!response.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ||
            response.IndexOf(" 200 ", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new IOException("HTTP proxy CONNECT failed: " + response.Split('\r', '\n')[0]);
        }
    }

    private static async Task ConnectSocks4Async(
        NetworkStream stream,
        string host,
        int port,
        ProxySettings proxy,
        CancellationToken cancellationToken)
    {
        var userBytes = Encoding.ASCII.GetBytes(proxy.Username ?? string.Empty);
        using var payload = new MemoryStream();
        payload.WriteByte(0x04);
        payload.WriteByte(0x01);
        payload.WriteByte((byte)(port >> 8));
        payload.WriteByte((byte)(port & 0xff));
        payload.Write(new byte[] { 0, 0, 0, 1 });
        payload.Write(userBytes);
        payload.WriteByte(0);
        payload.Write(Encoding.ASCII.GetBytes(host));
        payload.WriteByte(0);

        await WriteAsync(stream, payload.ToArray(), cancellationToken);
        var response = await ReadExactAsync(stream, 8, cancellationToken);
        if (response[1] != 0x5a)
            throw new IOException($"SOCKS4 proxy connect failed: 0x{response[1]:x2}");
    }

    private static async Task ConnectSocks5Async(
        NetworkStream stream,
        string host,
        int port,
        ProxySettings proxy,
        CancellationToken cancellationToken)
    {
        var usePassword = !string.IsNullOrWhiteSpace(proxy.Username);
        await WriteAsync(stream, usePassword ? new byte[] { 0x05, 0x02, 0x00, 0x02 } : new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
        var method = await ReadExactAsync(stream, 2, cancellationToken);
        if (method[0] != 0x05 || method[1] == 0xff)
            throw new IOException("SOCKS5 proxy did not accept authentication.");

        if (method[1] == 0x02)
            await AuthenticateSocks5Async(stream, proxy, cancellationToken);

        var hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length > byte.MaxValue)
            throw new IOException("SOCKS5 destination host is too long.");

        using var request = new MemoryStream();
        request.WriteByte(0x05);
        request.WriteByte(0x01);
        request.WriteByte(0x00);
        request.WriteByte(0x03);
        request.WriteByte((byte)hostBytes.Length);
        request.Write(hostBytes);
        Span<byte> portBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        request.Write(portBytes);

        await WriteAsync(stream, request.ToArray(), cancellationToken);
        var head = await ReadExactAsync(stream, 4, cancellationToken);
        if (head[0] != 0x05 || head[1] != 0x00)
            throw new IOException($"SOCKS5 proxy connect failed: 0x{head[1]:x2}");

        var addressLength = head[3] switch
        {
            0x01 => 4,
            0x03 => (await ReadExactAsync(stream, 1, cancellationToken))[0],
            0x04 => 16,
            _ => throw new IOException("SOCKS5 proxy returned an invalid address type.")
        };
        await ReadExactAsync(stream, addressLength + 2, cancellationToken);
    }

    private static async Task AuthenticateSocks5Async(NetworkStream stream, ProxySettings proxy, CancellationToken cancellationToken)
    {
        var username = Encoding.ASCII.GetBytes(proxy.Username);
        var password = Encoding.ASCII.GetBytes(proxy.Password ?? string.Empty);
        if (username.Length > byte.MaxValue || password.Length > byte.MaxValue)
            throw new IOException("SOCKS5 username or password is too long.");

        using var payload = new MemoryStream();
        payload.WriteByte(0x01);
        payload.WriteByte((byte)username.Length);
        payload.Write(username);
        payload.WriteByte((byte)password.Length);
        payload.Write(password);
        await WriteAsync(stream, payload.ToArray(), cancellationToken);

        var response = await ReadExactAsync(stream, 2, cancellationToken);
        if (response[1] != 0x00)
            throw new IOException("SOCKS5 proxy authentication failed.");
    }

    private static async Task WriteAsync(NetworkStream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
                throw new IOException("Proxy server closed the connection.");
            offset += read;
        }

        return buffer;
    }

    private static async Task<string> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var bytes = new List<byte>();
        while (bytes.Count < 8192)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            bytes.Add(buffer[0]);
            if (bytes.Count >= 4 &&
                bytes[^4] == '\r' &&
                bytes[^3] == '\n' &&
                bytes[^2] == '\r' &&
                bytes[^1] == '\n')
                break;
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static string CultureInvariant(FormattableString value)
    {
        return FormattableString.Invariant(value);
    }
}
