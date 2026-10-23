using TeraDrive.Cloud;
using TeraDrive.Core;
using Xunit;

namespace TeraDrive.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "teradrive-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() { Directory.CreateDirectory(Path); }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class CloudPathTests
{
    [Theory]
    [InlineData("", "/")] [InlineData("/", "/")] [InlineData("a/b", "/a/b")] [InlineData("//a///b/", "/a/b")] [InlineData("/./a", "/a")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, CloudPath.Normalize(input));

    [Theory]
    [InlineData("../x")] [InlineData("/a/../../b")] [InlineData("a\\b")] [InlineData("/a/b?")] [InlineData("/a/trailingdot.")]
    public void RejectsUnsafe(string input) => Assert.Throws<ArgumentException>(() => CloudPath.Normalize(input));

    [Fact] public void UnicodeNamesAreAccepted() => Assert.Equal("/ファイル/résumé ☃.txt", CloudPath.Normalize("/ファイル/résumé ☃.txt"));
    [Fact] public void ParentAndName() { Assert.Equal("/a", CloudPath.Parent("/a/b")); Assert.Equal("/", CloudPath.Parent("/a")); Assert.Equal("b", CloudPath.NameOf("/a/b")); }

    [Fact] public void PathGuardBlocksEscape()
    {
        using var t = new TempDir();
        Assert.StartsWith(System.IO.Path.GetFullPath(t.Path), PathGuard.ResolveUnder(t.Path, "/x/y.txt"));
        Assert.Throws<ArgumentException>(() => PathGuard.ResolveUnder(t.Path, "/../etc"));
    }
}

public class SettingsTests
{
    [Fact] public void DefaultsAreValid() => Assert.Empty(SettingsValidator.Validate(new AppSettings()));

    [Theory]
    [InlineData("Theme", "Neon")]
    public void BadThemeRejected(string _, string value) => Assert.NotEmpty(SettingsValidator.Validate(new AppSettings { Theme = value }));

    [Fact] public void RangesEnforced()
    {
        Assert.NotEmpty(SettingsValidator.Validate(new AppSettings { UploadConcurrency = 0 }));
        Assert.NotEmpty(SettingsValidator.Validate(new AppSettings { DownloadConcurrency = 99 }));
        Assert.NotEmpty(SettingsValidator.Validate(new AppSettings { CacheLimitGb = 0 }));
        Assert.NotEmpty(SettingsValidator.Validate(new AppSettings { DriveLetter = "C" }));
        Assert.NotEmpty(SettingsValidator.Validate(new AppSettings { DriveLetter = "TT" }));
        Assert.NotEmpty(SettingsValidator.Validate(new AppSettings { CacheDirectory = "relative/path" }));
    }

    [Fact] public void SaveLoadRoundTripAndCorruptFallback()
    {
        using var t = new TempDir(); var p = System.IO.Path.Combine(t.Path, "s.json");
        SettingsStore.Save(p, new AppSettings { CacheLimitGb = 25, Theme = "Light" });
        Assert.Equal(25, SettingsStore.Load(p).CacheLimitGb);
        File.WriteAllText(p, "{ not json");
        Assert.Equal(10, SettingsStore.Load(p).CacheLimitGb);
        Assert.True(File.Exists(p + ".corrupt"));
        Assert.Throws<ArgumentException>(() => SettingsStore.Save(p, new AppSettings { MaxRetries = 99 }));
    }
}

public class RedactionAndErrorTests
{
    [Theory]
    [InlineData("password=hunter2 ok", "hunter2")]
    [InlineData("Authorization: Bearer abc.def-123", "abc.def-123")]
    [InlineData("cookie: ndus=SECRETVALUE", "SECRETVALUE")]
    [InlineData("jsToken=XYZ123", "XYZ123")]
    public void SecretsAreRedacted(string input, string secret) => Assert.DoesNotContain(secret, Redactor.Redact(input));

    [Fact] public void LogRedactsOnWrite() { var l = new DiagnosticsLog(); l.Info("token=abc123"); Assert.DoesNotContain("abc123", string.Join("\n", l.Snapshot())); }

    [Fact] public void ErrorTranslation()
    {
        Assert.Contains("not supported", ErrorTranslator.Describe(new UnsupportedOperationException("X", "p")));
        Assert.Contains("not connected", ErrorTranslator.Describe(new NotConnectedException("p")));
        Assert.Contains("disk space", ErrorTranslator.Describe(new InsufficientSpaceException(10, 1)));
        Assert.Contains("already exists", ErrorTranslator.Describe(new IOException("'x' already exists.")));
        Assert.DoesNotContain("at ", ErrorTranslator.Describe(new InvalidOperationException("boom")));
    }
}

public class ProviderTests
{
    [Fact] public async Task TeraBoxReportsNoCapabilitiesAndNeverFakesSuccess()
    {
        var p = new TeraBoxProvider();
        Assert.Equal(ProviderCapabilities.None, p.Capabilities);
        Assert.False(p.IsConnected);
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => p.ConnectAsync(default));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => p.ListDirectoryAsync("/", default));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => p.UploadAsync("/a", new MemoryStream(), false, null, default));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => p.DeleteAsync("/a", default));
    }

    [Fact] public async Task LocalProviderRequiresConnectAndHasNoQuota()
    {
        using var t = new TempDir(); var p = new LocalFileSystemProvider(t.Path);
        await Assert.ThrowsAsync<NotConnectedException>(() => p.ListDirectoryAsync("/", default));
        await p.ConnectAsync(default);
        Assert.False(p.Supports(ProviderCapabilities.Quota));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => p.GetQuotaAsync(default));
    }

    [Fact] public async Task LocalProviderCrudAndSafety()
    {
        using var t = new TempDir(); var p = new LocalFileSystemProvider(t.Path); await p.ConnectAsync(default);
        await p.CreateDirectoryAsync("/docs", default);
        var item = await p.UploadAsync("/docs/ñ ü.txt", new MemoryStream("hello"u8.ToArray()), false, null, default);
        Assert.Equal(5, item.Size);
        await Assert.ThrowsAsync<IOException>(() => p.UploadAsync("/docs/ñ ü.txt", new MemoryStream(), false, null, default)); // no silent overwrite
        var list = await p.ListDirectoryAsync("/docs", default);
        Assert.Single(list);
        var renamed = await p.RenameAsync("/docs/ñ ü.txt", "b.txt", default);
        Assert.Equal("/docs/b.txt", renamed.Path);
        await using (var s = await p.OpenReadAsync("/docs/b.txt", 2, default)) { using var r = new StreamReader(s); Assert.Equal("llo", await r.ReadToEndAsync()); }
        await Assert.ThrowsAsync<ArgumentException>(() => p.GetMetadataAsync("/../outside", default));
        await Assert.ThrowsAsync<ArgumentException>(() => p.DeleteAsync("/", default));
        await p.DeleteAsync("/docs", default);
        Assert.Empty(await p.ListDirectoryAsync("/", default));
    }
}
