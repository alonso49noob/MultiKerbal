using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Mods;

namespace MultiKerbal.Tests;

public class ModCompareTests
{
    [Fact]
    public void Compare_ClassifiesEachMod()
    {
        List<ModDifference> differences = ModCompare.Compare(
            [Mod("ModuleManager", "4.2.3"), Mod("Restock", "1.4.3"), Mod("KAS", "1.12")],
            [Mod("ModuleManager", "4.2.3"), Mod("Restock", "1.4.0"), Mod("Kopernicus", "1.12.1")]);

        Assert.Equal(ModStatus.Missing, Status(differences, "KAS"));
        Assert.Equal(ModStatus.OtherVersion, Status(differences, "Restock"));
        Assert.Equal(ModStatus.Extra, Status(differences, "Kopernicus"));
        Assert.Equal(ModStatus.Ok, Status(differences, "ModuleManager"));

        // Primero los problemas, para que la ventana los enseñe arriba.
        Assert.Equal("KAS", differences[0].Name);
        Assert.Equal(ModStatus.Ok, differences[^1].Status);
    }

    [Fact]
    public void Compare_IgnoresCase_AndVersionWhenOneSideHasNone()
    {
        List<ModDifference> differences = ModCompare.Compare(
            [Mod("Squad", string.Empty), Mod("modulemanager", "4.2.3")],
            [Mod("squad", string.Empty), Mod("ModuleManager", string.Empty)]);

        Assert.All(differences, d => Assert.Equal(ModStatus.Ok, d.Status));
    }

    [Fact]
    public void Summarize_ListsProblems_AndTrimsLongLists()
    {
        List<ModDifference> none = ModCompare.Compare([Mod("Squad", "1.12.5")], [Mod("Squad", "1.12.5")]);
        List<ModDifference> many = ModCompare.Compare(
            [Mod("A", ""), Mod("B", ""), Mod("C", ""), Mod("D", ""), Mod("E", "")],
            []);

        Assert.Equal("los mismos mods que el servidor", ModCompare.Summarize(none));
        Assert.Equal("faltan A, B, C y 2 más", ModCompare.Summarize(many));
    }

    private static ModInfo Mod(string name, string version) => new() { Name = name, Version = version };

    private static ModStatus Status(List<ModDifference> differences, string name) =>
        differences.Single(d => d.Name == name).Status;
}

public sealed class ModPolicyTests : IDisposable
{
    private static readonly ModInfo[] Reference = [new() { Name = "Squad", Version = "1.12.5" }, new() { Name = "MultiKerbal", Version = "0.1.0" }];
    private static readonly ModInfo[] Incomplete = [new() { Name = "Squad", Version = "1.12.5" }];

    private readonly List<TestServer> _servers = [];

    [Fact]
    public void FirstPlayer_SetsTheReferenceList_AndItSurvivesRestarts()
    {
        TestServer server = NewServer();
        TestClient jeb = server.Join("Jeb", Reference);
        Assert.Equal(2, jeb.Welcome!.Mods.Length); // El primero fija la lista y la recibe de vuelta.

        TestClient bill = server.Join("Bill", Reference);
        Assert.Equal(2, bill.Welcome!.Mods.Length);

        server.Restart();
        TestClient val = server.Join("Val", Reference);
        Assert.Contains(val.Welcome!.Mods, m => m.Name == "MultiKerbal" && m.Version == "0.1.0");
    }

    [Fact]
    public void Warn_LetsPlayersInAndTellsEveryone()
    {
        TestServer server = NewServer();
        TestClient jeb = server.Join("Jeb", Reference);

        TestClient bill = server.Join("Bill", Incomplete);

        Assert.True(bill.Welcome!.Accepted);
        ChatMessage notice = jeb.WaitFor<ChatMessage>(m => m.Text.Contains("Bill") && m.Text.Contains("MultiKerbal"));
        Assert.Contains("faltan", notice.Text);
    }

    [Fact]
    public void Strict_RejectsThem_ButStillSendsTheList()
    {
        TestServer server = NewServer("strict");
        server.Join("Jeb", Reference);

        TestClient bill = server.TryJoin("Bill", Incomplete);

        Assert.False(bill.Welcome!.Accepted);
        Assert.Contains("MultiKerbal", bill.Welcome.RejectReason);
        Assert.Equal(2, bill.Welcome.Mods.Length); // Para poder ver qué falta.
    }

    [Fact]
    public void Off_IgnoresModsCompletely()
    {
        TestServer server = NewServer("off");
        TestClient jeb = server.Join("Jeb", Reference);

        TestClient bill = server.Join("Bill", []);

        Assert.True(bill.Welcome!.Accepted);
        Assert.Empty(bill.Welcome.Mods);
        jeb.WaitFor<ChatMessage>(m => m.Text.Contains("Bill se ha unido"));
        Assert.DoesNotContain(jeb.Received<ChatMessage>(), m => m.Text.Contains("no coinciden"));
    }

    public void Dispose()
    {
        foreach (TestServer server in _servers)
            server.Dispose();
    }

    private TestServer NewServer(string policy = "warn")
    {
        var server = new TestServer(policy);
        _servers.Add(server);
        return server;
    }
}
