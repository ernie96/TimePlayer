using System.Reflection;

namespace TimePlayer.Core.Configuration;

/// <summary>
/// Where TimePlayer keeps its data. Normally %LOCALAPPDATA%\TimePlayer; in portable mode (a
/// "portable.txt" or "settings.json" next to TimePlayer.exe) everything stays in the program folder.
/// </summary>
public static class AppPaths
{
    public static string ProgramFolder => AppContext.BaseDirectory;

    public static bool IsPortable =>
        File.Exists(Path.Combine(ProgramFolder, "portable.txt")) ||
        File.Exists(Path.Combine(ProgramFolder, "settings.json"));

    public static string DataFolder => IsPortable
        ? ProgramFolder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimePlayer");

    public static string LibraryFolder => Path.Combine(DataFolder, "library");
    public static string LogsFolder => Path.Combine(DataFolder, "logs");

    /// <summary>"1.0.0", from the product version the build stamps into the assembly.</summary>
    public static string Version
    {
        get
        {
            var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(AppPaths).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0";
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
    }
}
