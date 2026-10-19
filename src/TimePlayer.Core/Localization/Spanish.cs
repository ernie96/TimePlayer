namespace TimePlayer.Core.Localization;

/// <summary>Spanish translations, keyed by the English text. Plural entries hold their forms separated by "|".</summary>
internal static partial class Spanish
{
    public static readonly IReadOnlyDictionary<string, string> Table = Build();

    private static Dictionary<string, string> Build()
    {
        var table = new Dictionary<string, string>(System.StringComparer.Ordinal);
        AddAll(table);
        return table;
    }

    static partial void AddAll(Dictionary<string, string> table);
}
