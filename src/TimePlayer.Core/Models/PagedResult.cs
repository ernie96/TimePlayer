namespace TimePlayer.Core.Models;

/// <summary>
/// Paginated container for search results or catalog items.
/// </summary>
public record PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int PageIndex { get; init; } = 1;
    public bool HasNextPage { get; init; }
    public string? NextPageToken { get; init; }
    public long? TotalEstimatedResults { get; init; }

    public static PagedResult<T> Empty() => new()
    {
        Items = Array.Empty<T>(),
        PageIndex = 1,
        HasNextPage = false
    };

    public static PagedResult<T> SinglePage(IReadOnlyList<T> items) => new()
    {
        Items = items ?? Array.Empty<T>(),
        PageIndex = 1,
        HasNextPage = false,
        TotalEstimatedResults = items?.Count ?? 0
    };
}
