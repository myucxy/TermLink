using System;
using System.IO;
using System.Linq;
using TermLink.LocalIpc;

internal static class TerminalHandshakeValidation
{
    internal static void Run()
    {
        var secret = Enumerable.Range(0,32).Select(v => (byte)v).ToArray();
        var hello = LocalPayloadCodec.TerminalHello(Environment.ProcessId,secret);
        Require(LocalPayloadCodec.ParseTerminalHello(hello,secret) == Environment.ProcessId);
        Reject(LocalPayloadCodec.Hello(Environment.ProcessId,secret),secret,true);
        for (int index=36;index<hello.Length;index++)
        {
            var bad = hello.ToArray(); bad[index]^=1; Reject(bad,secret,true);
        }
        for (int index=4;index<36;index++)
        {
            var bad = hello.ToArray(); bad[index]^=1; Reject(bad,secret,false);
        }
        Reject(hello.Concat(new byte[1]).ToArray(),secret,true);
        Reject(hello.Take(35).ToArray(),secret,false);
        Console.WriteLine("Local terminal handshake: authenticated identity, parser semantics, width/resource profile and legacy rejection passed.");
    }
    private static void Reject(byte[] bytes, byte[] token, bool protocol)
    {
        try { LocalPayloadCodec.ParseTerminalHello(bytes,token); throw new Exception("Incompatible hello accepted"); }
        catch(TerminalLocalProtocolException) { Require(protocol); }
        catch(InvalidDataException) { Require(!protocol); }
    }
    private static void Require(bool value) { if(!value) throw new Exception("Incorrect terminal handshake result"); }
}
