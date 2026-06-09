using System;
using System.Text.Json.Serialization;

namespace ChiXueSsh.Models;

public enum AuthMethod
{
    Password,
    PrivateKey
}

public class SessionInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid? GroupId { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;

    public string? PrivateKeyPath { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
