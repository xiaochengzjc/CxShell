using System.Reflection;
var asm = Assembly.LoadFrom(@"C:\Users\xiaoc\.nuget\packages\ssh.net\2024.2.0\lib\net8.0\Renci.SshNet.dll");
var type = asm.GetType("Renci.SshNet.ProxyTypes", true)!;
foreach (var name in Enum.GetNames(type)) Console.WriteLine(name + "=" + Convert.ToInt32(Enum.Parse(type, name)));
