using TimePlayer.Core.Updates;
using TimePlayer.Wpf.Views;
using Xunit;

namespace TimePlayer.Tests.Updates;

public class UpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "timeplayer-tests", Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Signature_VerifiesOnlyTheSignedFileUnderItsName()
    {
        var (privateKey, publicKey) = UpdateSignature.GenerateKeyPair();
        var file = WriteFile("TimePlayer.zip", "release bytes");
        var signature = UpdateSignature.Sign(privateKey, file, "TimePlayer.zip");

        Assert.True(UpdateSignature.Verify(publicKey, file, "TimePlayer.zip", signature));
        // The same bytes offered as the installer must not pass.
        Assert.False(UpdateSignature.Verify(publicKey, file, "TimePlayer_setup.exe", signature));

        File.AppendAllText(file, "tampered");
        Assert.False(UpdateSignature.Verify(publicKey, file, "TimePlayer.zip", signature));

        var (_, otherPublicKey) = UpdateSignature.GenerateKeyPair();
        File.WriteAllText(file, "release bytes");
        Assert.False(UpdateSignature.Verify(otherPublicKey, file, "TimePlayer.zip", signature));
        Assert.False(UpdateSignature.Verify(publicKey, file, "TimePlayer.zip", "not base64!"));
    }

    [Fact]
    public void ShippedPublicKey_IsAValidEd25519Key()
    {
        Assert.Equal(32, Convert.FromBase64String(UpdateService.PublicKey).Length);
    }

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V2.0.1-beta", "2.0.1")]
    [InlineData("1.0.0+abc", "1.0.0")]
    public void ParseVersion_ReadsTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));

    [Fact]
    public void ParseVersion_RejectsGarbage() => Assert.Null(UpdateService.ParseVersion("latest"));

    [Fact]
    public void PickAssets_ChoosesTheFileForThisInstallKind_AndRequiresItsSignature()
    {
        var release = new GitHubRelease
        {
            TagName = "v1.1.0",
            Body = "Notes",
            Assets = new()
            {
                new() { Name = "TimePlayer.zip", DownloadUrl = "https://x/zip" },
                new() { Name = "TimePlayer.zip.sig", DownloadUrl = "https://x/zip.sig" },
                new() { Name = "TimePlayer_setup.exe", DownloadUrl = "https://x/exe" }
            }
        };

        var portable = UpdateService.PickAssets(release, new Version(1, 1, 0), InstallKind.Portable);
        Assert.Equal("https://x/zip", portable!.DownloadUrl);
        Assert.Equal("https://x/zip.sig", portable.SignatureUrl);
        // No signature for the installer: it is not offered.
        Assert.Null(UpdateService.PickAssets(release, new Version(1, 1, 0), InstallKind.Installer));
    }

    [Fact]
    public void Scripts_QuotePathsForPowerShell()
    {
        var installer = UpdateService.InstallerScript(42, @"C:\Users\O'Brien\Temp\TimePlayer_setup.exe", @"C:\Apps\TimePlayer.exe");
        Assert.Contains("Wait-Process -Id 42", installer);
        Assert.Contains(@"'C:\Users\O''Brien\Temp\TimePlayer_setup.exe'", installer);
        Assert.Contains("'/VERYSILENT'", installer);

        var portable = UpdateService.PortableScript(7, @"C:\Temp\TimePlayer.zip", @"D:\My Apps\TimePlayer\", @"D:\My Apps\TimePlayer\TimePlayer.exe");
        Assert.Contains(@"Expand-Archive -LiteralPath 'C:\Temp\TimePlayer.zip'", portable);
        Assert.Contains(@"-Destination 'D:\My Apps\TimePlayer\'", portable);
        Assert.EndsWith(@"Start-Process -FilePath 'D:\My Apps\TimePlayer\TimePlayer.exe'", portable);
    }

    [Fact]
    public void ReleaseNotes_AreReadAsPlainText()
    {
        var text = UpdateFlow.PlainText("## What's new\n\n* **Resume** works after replay\n- See [the guide](https://x)\n\n\n\nThanks");
        Assert.Equal("What's new\n\n- Resume works after replay\n- See the guide\n\nThanks", text);
    }
}
