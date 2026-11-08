using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class SettingsFileStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AsheTraderSettingsTests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(directory, "settings.json");

    [Fact]
    public void LoadOfMissingSettingsCreatesNothing()
    {
        var store = new SettingsFileStore(SettingsPath);

        Assert.Null(store.Load());
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void SaveRequiresAnExplicitBaseline()
    {
        var store = new SettingsFileStore(SettingsPath);

        Assert.False(store.TrySave("{}", out var error));
        Assert.Contains("loaded before", error);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void FirstSaveAndOwnSubsequentSaveUpdateTheBaseline()
    {
        var store = new SettingsFileStore(SettingsPath);
        Assert.Null(store.Load());

        Assert.True(store.TrySave("{\"version\":1}", out var error), error);
        Assert.True(store.TrySave("{\"version\":2}", out error), error);
        Assert.Equal("{\"version\":2}", File.ReadAllText(SettingsPath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void StaleSecondClientCannotOverwriteFirstClientsSave()
    {
        var first = new SettingsFileStore(SettingsPath);
        var second = new SettingsFileStore(SettingsPath);
        Assert.Null(first.Load());
        Assert.Null(second.Load());

        Assert.True(first.TrySave("{\"list\":\"first client\"}", out var error), error);
        Assert.False(second.TrySave("{\"list\":\"second client\"}", out error));
        Assert.Contains("another client", error);
        Assert.Equal("{\"list\":\"first client\"}", File.ReadAllText(SettingsPath));
        Assert.False(second.TrySave("{\"list\":\"retry\"}", out _));
    }

    [Fact]
    public void ExplicitReloadAllowsReapplyingAnEdit()
    {
        var first = new SettingsFileStore(SettingsPath);
        var second = new SettingsFileStore(SettingsPath);
        first.Load();
        second.Load();
        Assert.True(first.TrySave("{\"first\":true}", out _));
        Assert.False(second.TrySave("{\"second\":true}", out _));

        Assert.Equal("{\"first\":true}", second.Load());
        Assert.True(second.TrySave("{\"first\":true,\"second\":true}", out var error), error);
        Assert.Equal("{\"first\":true,\"second\":true}", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void DeletionByAnotherClientIsAlsoAConflict()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, "{\"keep\":true}");
        var store = new SettingsFileStore(SettingsPath);
        store.Load();
        File.Delete(SettingsPath);

        Assert.False(store.TrySave("{\"restore\":true}", out var error));
        Assert.Contains("another client", error);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void AnotherWritersLockPreventsAnySave()
    {
        Directory.CreateDirectory(directory);
        var store = new SettingsFileStore(SettingsPath);
        store.Load();
        using var heldLock = new FileStream(SettingsPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        Assert.False(store.TrySave("{}", out var error));
        Assert.Contains("Could not save settings", error);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task ConcurrentClientsProduceOneWinnerAndOneConflict()
    {
        var first = new SettingsFileStore(SettingsPath);
        var second = new SettingsFileStore(SettingsPath);
        first.Load();
        second.Load();
        using var start = new ManualResetEventSlim();
        var tasks = new[] { first, second }.Select((store, index) => Task.Run(() =>
        {
            start.Wait();
            return store.TrySave($"{{\"writer\":{index}}}", out _);
        })).ToArray();
        start.Set();

        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        Assert.Contains(File.ReadAllText(SettingsPath), new[] { "{\"writer\":0}", "{\"writer\":1}" });
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void StorePreservesMalformedTextForCallerToHandle()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, "not valid JSON");
        var store = new SettingsFileStore(SettingsPath);

        Assert.Equal("not valid JSON", store.Load());
        Assert.Equal("not valid JSON", File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".lock"));
    }

    public void Dispose()
    {
        // The only recursively deleted path is this test's freshly generated GUID directory.
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AsheTraderSettingsTests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
