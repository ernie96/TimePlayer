using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;

namespace TimePlayer.Wpf;

/// <summary>
/// The browser extension talks to TimePlayer directly (Chrome's native messaging): Chrome starts
/// TimePlayer.exe with the extension's origin and sends the link on standard input. No "Open TimePlayer?"
/// question, and nothing for web pages to call: only this extension may use it.
/// </summary>
public static class NativeMessagingHost
{
    public const string HostName = "com.timeplayer.links";

    /// <summary>The extension's fixed ID (from the public key in its manifest).</summary>
    public const string ExtensionId = "lcfhohlccjpekkeeiggdjbcgbfedjijg";

    private static readonly string[] BrowserKeys =
    {
        @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + HostName,
    };

    private static string ManifestPath => Path.Combine(AppPaths.DataFolder, "native-messaging-host.json");

    /// <summary>Lets Chrome, Edge and Brave start TimePlayer for the extension, or stops them.</summary>
    public static void Apply(bool enabled)
    {
        var exe = Environment.ProcessPath;
        if (!enabled || string.IsNullOrEmpty(exe))
        {
            foreach (var key in BrowserKeys)
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            if (File.Exists(ManifestPath))
                File.Delete(ManifestPath);
            return;
        }

        var manifest = JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "TimePlayer: plays YouTube links from the Play in TimePlayer extension",
            path = exe,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" }
        }, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        if (!File.Exists(ManifestPath) || File.ReadAllText(ManifestPath) != manifest)
            File.WriteAllText(ManifestPath, manifest);
        foreach (var key in BrowserKeys)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            if (k.GetValue(null) as string != ManifestPath)
                k.SetValue(null, ManifestPath);
        }
    }

    /// <summary>Whether this start is Chrome asking for the extension (its origin is the argument).</summary>
    public static bool IsHostStart(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the extension's message, hands the link to the open TimePlayer (or starts one), answers, and
    /// returns. Runs without a window.
    /// </summary>
    public static void Run(string[] args)
    {
        string reply;
        try
        {
            if (!args.Any(a => a.StartsWith($"chrome-extension://{ExtensionId}/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("not the TimePlayer extension");
            using var input = Console.OpenStandardInput();
            var message = ReadMessage(input);
            using var doc = JsonDocument.Parse(message);
            var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            var link = IncomingLink.Normalize(url);
            if (link is null)
            {
                reply = """{"ok":false,"error":"not a YouTube video or playlist"}""";
            }
            else
            {
                if (!SingleInstance.SendLinkToOtherCopy(link))
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { link }, UseShellExecute = false });
                reply = """{"ok":true}""";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            reply = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        try
        {
            using var output = Console.OpenStandardOutput();
            WriteMessage(output, reply);
        }
        catch (IOException)
        {
            // Chrome went away; nothing to answer.
        }
    }

    // Each message is a 4-byte little-endian length and then that many bytes of UTF-8 JSON.
    public static string ReadMessage(Stream input)
    {
        var header = new byte[4];
        ReadExactly(input, header);
        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > 64 * 1024)
            throw new IOException("message too long");
        var body = new byte[length];
        ReadExactly(input, body);
        return Encoding.UTF8.GetString(body);
    }

    public static void WriteMessage(Stream output, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        output.Write(BitConverter.GetBytes(body.Length));
        output.Write(body);
        output.Flush();
    }

    private static void ReadExactly(Stream input, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = input.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                throw new IOException("the browser closed the connection");
            read += n;
        }
    }
}
