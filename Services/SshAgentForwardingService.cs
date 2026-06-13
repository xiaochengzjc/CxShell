using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Messages;
using Renci.SshNet.Messages.Connection;
using Renci.SshNet.Security;
using SshNet.Agent;

namespace ChiXueSsh.Services;

internal sealed class SshAgentForwardingService : IDisposable
{
    private const string AgentChannelType = "auth-agent@openssh.com";
    private const string AgentRequestName = "auth-agent-req@openssh.com";
    private const uint LocalWindowSize = 1024 * 1024;
    private const uint LocalPacketSize = 32 * 1024;
    private const byte SshAgentFailure = 5;
    private const byte Ssh2AgentcRequestIdentities = 11;
    private const byte Ssh2AgentIdentitiesAnswer = 12;
    private const byte Ssh2AgentcSignRequest = 13;
    private const byte Ssh2AgentSignResponse = 14;
    private const uint SshAgentSignRsaSha2_256 = 2;
    private const uint SshAgentSignRsaSha2_512 = 4;

    private readonly Dictionary<uint, AgentChannel> _channels = new();
    private readonly Dictionary<string, AgentIdentity> _identities = new();
    private readonly List<(EventInfo Event, Delegate Handler)> _eventHandlers = new();
    private object? _session;
    private uint _nextLocalChannel = 0x6f000000;

    public bool Start(ShellStream shellStream)
    {
        var channel = GetPrivateField(shellStream, "_channel");
        if (channel == null)
            return false;

        _session = GetPrivateField(channel, "_session");
        if (_session == null)
            return false;

        LoadIdentities();
        if (_identities.Count == 0)
            throw new InvalidOperationException("SSH agent forwarding failed: no agent identities were found.");

        AttachSessionEvent("ChannelOpenReceived", nameof(OnChannelOpenReceived));
        AttachSessionEvent("ChannelDataReceived", nameof(OnChannelDataReceived));
        AttachSessionEvent("ChannelEofReceived", nameof(OnChannelEofReceived));
        AttachSessionEvent("ChannelCloseReceived", nameof(OnChannelCloseReceived));

        return SendAgentForwardRequest(channel);
    }

    public void Dispose()
    {
        foreach (var (eventInfo, handler) in _eventHandlers)
        {
            try { eventInfo.RemoveEventHandler(_session, handler); } catch { }
        }

        _eventHandlers.Clear();
        _channels.Clear();
        _identities.Clear();
        _session = null;
    }

    private bool SendAgentForwardRequest(object channel)
    {
        var remoteChannelNumber = GetUIntProperty(channel, "RemoteChannelNumber");
        var requestInfo = new AgentForwardRequestInfo();
        SendMessage(new ChannelRequestMessage(remoteChannelNumber, requestInfo));
        return true;
    }

    private void LoadIdentities()
    {
        _identities.Clear();
        foreach (var agentFactory in SshAgentAuthService.CreateAgentFactories())
        {
            try
            {
                var identities = agentFactory().RequestIdentities();
                foreach (var identity in identities)
                {
                    var algorithm = identity.HostKeyAlgorithms.FirstOrDefault();
                    if (algorithm == null)
                        continue;

                    var key = Convert.ToBase64String(algorithm.Data);
                    _identities.TryAdd(key, new AgentIdentity(identity, algorithm));
                }

                if (_identities.Count > 0)
                    return;
            }
            catch
            {
                // Try the next local agent backend.
            }
        }
    }

    private void OnChannelOpenReceived(object? sender, EventArgs e)
    {
        var message = GetMessage<ChannelOpenMessage>(e);
        if (message == null)
            return;

        var channelType = Encoding.ASCII.GetString(message.ChannelType);
        if (!string.Equals(channelType, AgentChannelType, StringComparison.Ordinal))
            return;

        var localChannel = _nextLocalChannel++;
        _channels[localChannel] = new AgentChannel(message.LocalChannelNumber);
        SendMessage(new ChannelOpenConfirmationMessage(localChannel, LocalWindowSize, LocalPacketSize, message.LocalChannelNumber));
    }

    private void OnChannelDataReceived(object? sender, EventArgs e)
    {
        var message = GetMessage<ChannelDataMessage>(e);
        if (message == null || !_channels.TryGetValue(message.LocalChannelNumber, out var channel))
            return;

        channel.Buffer.Write(message.Data, message.Offset, message.Size);
        while (TryReadAgentPacket(channel.Buffer, out var packet))
        {
            var response = HandleAgentPacket(packet);
            SendMessage(new ChannelDataMessage(channel.RemoteChannelNumber, response));
        }
    }

    private void OnChannelEofReceived(object? sender, EventArgs e)
    {
        var message = GetMessage<ChannelEofMessage>(e);
        if (message != null && _channels.TryGetValue(message.LocalChannelNumber, out var channel))
            SendMessage(new ChannelEofMessage(channel.RemoteChannelNumber));
    }

    private void OnChannelCloseReceived(object? sender, EventArgs e)
    {
        var message = GetMessage<ChannelCloseMessage>(e);
        if (message == null || !_channels.Remove(message.LocalChannelNumber, out var channel))
            return;

        SendMessage(new ChannelCloseMessage(channel.RemoteChannelNumber));
        channel.Buffer.Dispose();
    }

    private byte[] HandleAgentPacket(byte[] packet)
    {
        try
        {
            using var input = new BinaryReader(new MemoryStream(packet));
            var type = input.ReadByte();
            return type switch
            {
                Ssh2AgentcRequestIdentities => CreateIdentitiesAnswer(),
                Ssh2AgentcSignRequest => CreateSignResponse(input),
                _ => CreateFailure()
            };
        }
        catch
        {
            return CreateFailure();
        }
    }

    private byte[] CreateIdentitiesAnswer()
    {
        using var payload = new MemoryStream();
        payload.WriteByte(Ssh2AgentIdentitiesAnswer);
        WriteUInt32(payload, (uint)_identities.Count);

        foreach (var identity in _identities.Values)
        {
            WriteString(payload, identity.AdvertisedAlgorithm.Data);
            WriteString(payload, identity.AdvertisedAlgorithm.Name);
        }

        return WrapAgentPacket(payload.ToArray());
    }

    private byte[] CreateSignResponse(BinaryReader input)
    {
        var keyBlob = ReadString(input);
        var data = ReadString(input);
        var flags = ReadUInt32(input);
        var key = Convert.ToBase64String(keyBlob);
        if (!_identities.TryGetValue(key, out var identity))
            return CreateFailure();

        var algorithm = SelectSigningAlgorithm(identity.PrivateKey, flags);
        if (algorithm == null)
            return CreateFailure();

        var signature = algorithm.Sign(data);
        using var signatureBlob = new MemoryStream();
        WriteString(signatureBlob, algorithm.Name);
        WriteString(signatureBlob, signature);

        using var payload = new MemoryStream();
        payload.WriteByte(Ssh2AgentSignResponse);
        WriteString(payload, signatureBlob.ToArray());
        return WrapAgentPacket(payload.ToArray());
    }

    private static HostAlgorithm? SelectSigningAlgorithm(SshAgentPrivateKey privateKey, uint flags)
    {
        var algorithms = privateKey.HostKeyAlgorithms;
        if ((flags & SshAgentSignRsaSha2_512) != 0)
            return algorithms.FirstOrDefault(a => string.Equals(a.Name, "rsa-sha2-512", StringComparison.Ordinal));
        if ((flags & SshAgentSignRsaSha2_256) != 0)
            return algorithms.FirstOrDefault(a => string.Equals(a.Name, "rsa-sha2-256", StringComparison.Ordinal));

        return algorithms.FirstOrDefault();
    }

    private byte[] CreateFailure()
    {
        return WrapAgentPacket([SshAgentFailure]);
    }

    private static bool TryReadAgentPacket(MemoryStream buffer, out byte[] packet)
    {
        packet = [];
        var data = buffer.ToArray();
        if (data.Length < 4)
            return false;

        var length = ReadUInt32(data, 0);
        if (length > 256 * 1024)
            throw new InvalidOperationException("SSH agent packet is too large.");

        var packetEnd = 4 + (int)length;
        if (data.Length < packetEnd)
            return false;

        packet = data[4..packetEnd];
        buffer.SetLength(0);
        if (data.Length > packetEnd)
            buffer.Write(data, packetEnd, data.Length - packetEnd);
        return true;
    }

    private static byte[] WrapAgentPacket(byte[] payload)
    {
        using var output = new MemoryStream();
        WriteUInt32(output, (uint)payload.Length);
        output.Write(payload, 0, payload.Length);
        return output.ToArray();
    }

    private static byte[] ReadString(BinaryReader reader)
    {
        var length = ReadUInt32(reader);
        if (length > 256 * 1024)
            throw new InvalidOperationException("SSH agent string is too large.");

        return reader.ReadBytes((int)length);
    }

    private static uint ReadUInt32(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
            throw new EndOfStreamException();
        return ReadUInt32(bytes, 0);
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteString(Stream stream, string value)
    {
        WriteString(stream, Encoding.UTF8.GetBytes(value));
    }

    private static void WriteString(Stream stream, byte[] value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value, 0, value.Length);
    }

    private void AttachSessionEvent(string eventName, string handlerName)
    {
        if (_session == null)
            return;

        var eventInfo = _session.GetType().GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var handlerMethod = GetType().GetMethod(handlerName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (eventInfo == null || handlerMethod == null)
            return;

        var handler = Delegate.CreateDelegate(eventInfo.EventHandlerType!, this, handlerMethod);
        eventInfo.AddEventHandler(_session, handler);
        _eventHandlers.Add((eventInfo, handler));
    }

    private void SendMessage(Message message)
    {
        if (_session == null)
            return;

        var method = _session.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name.EndsWith("SendMessage", StringComparison.Ordinal) &&
                                 m.GetParameters().Length == 1 &&
                                 m.GetParameters()[0].ParameterType == typeof(Message));
        method?.Invoke(_session, [message]);
    }

    private static T? GetMessage<T>(EventArgs args) where T : class
    {
        return args.GetType().GetProperty("Message")?.GetValue(args) as T;
    }

    private static object? GetPrivateField(object instance, string name)
    {
        return instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance);
    }

    private static uint GetUIntProperty(object instance, string name)
    {
        return (uint)(instance.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance) ?? 0u);
    }

    private sealed record AgentIdentity(SshAgentPrivateKey PrivateKey, HostAlgorithm AdvertisedAlgorithm);

    private sealed record AgentChannel(uint RemoteChannelNumber)
    {
        public MemoryStream Buffer { get; } = new();
    }

    private sealed class AgentForwardRequestInfo : RequestInfo
    {
        public AgentForwardRequestInfo()
        {
            WantReply = true;
        }

        public override string RequestName => AgentRequestName;
    }
}
