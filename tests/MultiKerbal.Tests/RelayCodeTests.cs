using MultiKerbal.Common.Relay;

namespace MultiKerbal.Tests;

public class ServerCodeTests
{
    [Theory]
    [InlineData("k7m q2p")]
    [InlineData("K7MQ2P")]
    [InlineData("k7m-q2p")]
    public void TryNormalize_AcceptsTheWayPlayersTypeIt(string input)
    {
        Assert.True(ServerCode.TryNormalize(input, out string code));
        Assert.Equal("K7MQ2P", code);
    }

    [Theory]
    [InlineData("gamepc")]
    [InlineData("192.168.1.5")]
    [InlineData("localhost")]
    [InlineData("abc12")]
    [InlineData("abc1234")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abcdef")]
    [InlineData("234567")]
    [InlineData("k7mq2:")]
    public void LooksLikeCode_IsFalseForAddressesAndWrongShapes(string? input)
    {
        Assert.False(ServerCode.LooksLikeCode(input));
        Assert.False(ServerCode.TryNormalize(input, out _));
    }

    [Fact]
    public void LooksLikeCode_TrueButNotNormalizable_WhenCharacterIsOutsideAlphabet()
    {
        // La O no está en el alfabeto: parece código (se avisa de "código no válido") pero no se normaliza.
        Assert.True(ServerCode.LooksLikeCode("K7M-Q2O"));
        Assert.False(ServerCode.TryNormalize("K7M-Q2O", out string code));
        Assert.Equal(string.Empty, code);
    }

    [Fact]
    public void Format_InsertsDash()
    {
        Assert.Equal("K7M-Q2P", ServerCode.Format("K7MQ2P"));
    }

    [Fact]
    public void Generate_ProducesValidCodes()
    {
        var random = new Random(1);
        for (int i = 0; i < 1000; i++)
        {
            string code = ServerCode.Generate(random.Next);

            Assert.Equal(ServerCode.Length, code.Length);
            Assert.True(ServerCode.TryNormalize(code, out string normalized));
            Assert.Equal(code, normalized);
        }
    }

    [Fact]
    public void Generate_RetriesUntilItHasLetterAndDigit()
    {
        // Primero seis letras (A), después seis dígitos (índice 23 = '2') y por último una mezcla válida.
        int[] sequence = [0, 0, 0, 0, 0, 0, 23, 23, 23, 23, 23, 23, 0, 23, 0, 23, 0, 23];
        int position = 0;

        string code = ServerCode.Generate(n => sequence[position++]);

        Assert.Equal("A2A2A2", code);
        Assert.Equal(sequence.Length, position);
    }
}

public class RelayEndpointTests
{
    [Fact]
    public void TryParse_HostAndPort()
    {
        Assert.True(RelayEndpoint.TryParse("relay.example:7000", 6760, out string host, out int port));
        Assert.Equal("relay.example", host);
        Assert.Equal(7000, port);
    }

    [Fact]
    public void TryParse_HostOnly_UsesDefaultPort()
    {
        Assert.True(RelayEndpoint.TryParse("relay.example", 6760, out string host, out int port));
        Assert.Equal("relay.example", host);
        Assert.Equal(6760, port);
    }

    [Fact]
    public void TryParse_BracketedIPv6()
    {
        Assert.True(RelayEndpoint.TryParse("[::1]:7000", 6760, out string host, out int port));
        Assert.Equal("::1", host);
        Assert.Equal(7000, port);

        Assert.True(RelayEndpoint.TryParse("[::1]", 6760, out host, out port));
        Assert.Equal("::1", host);
        Assert.Equal(6760, port);
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(":7000")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:")]
    [InlineData("[::1")]
    [InlineData("[]:7000")]
    [InlineData("[::1]x")]
    public void TryParse_RejectsMalformed(string? text)
    {
        Assert.False(RelayEndpoint.TryParse(text, 6760, out string host, out int port));
        Assert.Equal(string.Empty, host);
        Assert.Equal(0, port);
    }
}
