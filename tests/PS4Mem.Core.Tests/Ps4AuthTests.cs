using System.Buffers.Binary;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Tests.Fakes;
using static PS4Mem.Core.Tests.Fakes.FakeConsole;

namespace PS4Mem.Core.Tests;

public class Ps4AuthTests
{
    // First 16 keystream bytes, computed separately (Python) from the C in NG/source/proc.c:2078-2096.
    private const string Keystream16 = "A0488FED8232B5EE44C1DC1CE467A658";

    [Fact]
    public void Response_to_a_zero_challenge_is_the_keystream()
    {
        Assert.Equal(Keystream16, Convert.ToHexString(Ps4Auth.Respond(new byte[16])));
    }

    [Fact]
    public void Response_is_challenge_xor_keystream_and_restarts_each_time()
    {
        var challenge = Enumerable.Range(1, 16).Select(i => (byte)(i * 17)).ToArray();
        var key = Convert.FromHexString(Keystream16);

        var first = Ps4Auth.Respond(challenge);
        var second = Ps4Auth.Respond(challenge);

        Assert.Equal(challenge.Zip(key, (c, k) => (byte)(c ^ k)), first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Authenticate_sends_magic_and_flags_and_answers_the_challenge()
    {
        var challenge = Enumerable.Range(0, 64).Select(i => (byte)(i * 7 + 3)).ToArray();
        FakeRequest? request = null;
        byte[]? response = null;
        using var server = new FakePs4Server(c =>
        {
            request = c.Expect(Ps4Command.ProcAuth, 8);
            c.SendStatus(Ps4Status.Success);
            c.Send(U16(64), challenge);
            response = c.ReadBytes(64);
            c.SendStatus(Ps4Status.Success);
        });
        using var client = new PS4DebugClient();
        client.Connect("127.0.0.1", server.Port);

        client.Authenticate();
        server.WaitForScript();

        Assert.Equal(0xBB40E64DU, request!.U32(0));
        Assert.Equal(2U, request.U32(4));
        Assert.Equal(Ps4Auth.Respond(challenge), response);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void Rejected_auth_closes_the_connection()
    {
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.ProcAuth, 8);
            c.SendStatus(Ps4Status.Success);
            c.Send(U16(4), [1, 2, 3, 4]);
            c.ReadBytes(4);
            c.SendStatus(Ps4Status.DataNull);
            c.Close();
        });
        using var client = new PS4DebugClient();
        client.Connect("127.0.0.1", server.Port);

        var e = Assert.Throws<Ps4StatusException>(() => client.Authenticate());
        Assert.Equal(Ps4Status.DataNull, e.Status);
        Assert.True(e.ConnectionClosed);
        Assert.False(client.IsConnected);
    }
}
