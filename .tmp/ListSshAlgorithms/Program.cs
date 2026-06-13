using Renci.SshNet;

var connectionInfo = new ConnectionInfo(
    "localhost",
    22,
    "user",
    new PasswordAuthenticationMethod("user", "password"));

Print("Ciphers", connectionInfo.Encryptions.Keys);
Print("MACs", connectionInfo.HmacAlgorithms.Keys);
Print("Key exchanges", connectionInfo.KeyExchangeAlgorithms.Keys);

static void Print(string title, IEnumerable<string> values)
{
    Console.WriteLine($"[{title}]");
    foreach (var value in values)
        Console.WriteLine(value);
    Console.WriteLine();
}
