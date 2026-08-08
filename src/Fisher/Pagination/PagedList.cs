namespace Fisher.Pagination;

/// <summary>
///     A page of results plus everything needed to render a pager (fisher#27).
/// </summary>
/// <remarks>
///     Offset paging: it can jump to an arbitrary page and it reports a total. Neither is true of
///     keyset paging, and keyset paging is stable under concurrent writes where this is not — so the
///     two answer different questions and Fisher carries both, as Polecat and Marten do.
/// </remarks>
public interface IPagedList<out T> : IReadOnlyList<T>
{
    /// <summary>How many items match the query, ignoring paging.</summary>
    long TotalItemCount { get; }

    /// <summary>How many pages of <see cref="PageSize" /> the total makes.</summary>
    int PageCount { get; }

    /// <summary>The 1-based page this is.</summary>
    int PageNumber { get; }

    int PageSize { get; }

    bool HasPreviousPage { get; }

    bool HasNextPage { get; }

    bool IsFirstPage { get; }

    bool IsLastPage { get; }

    /// <summary>The 1-based index of this page's first item in the whole result, or 0 when empty.</summary>
    int FirstItemOnPage { get; }

    /// <inheritdoc cref="FirstItemOnPage" />
    int LastItemOnPage { get; }
}

internal sealed class PagedList<T> : IPagedList<T>
{
    private readonly IReadOnlyList<T> _items;

    internal PagedList(IReadOnlyList<T> items, long totalItemCount, int pageNumber, int pageSize)
    {
        _items = items;
        TotalItemCount = totalItemCount;
        PageNumber = pageNumber;
        PageSize = pageSize;
        PageCount = pageSize <= 0 ? 0 : (int)Math.Ceiling(totalItemCount / (double)pageSize);
    }

    public T this[int index] => _items[index];
    public int Count => _items.Count;
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public long TotalItemCount { get; }
    public int PageCount { get; }
    public int PageNumber { get; }
    public int PageSize { get; }

    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < PageCount;
    public bool IsFirstPage => PageNumber == 1;
    public bool IsLastPage => PageNumber >= PageCount;

    // Zero rather than a negative or a phantom index when the page is empty — a page past the end has
    // no first item, and reporting one would be worse than reporting none.
    public int FirstItemOnPage => Count == 0 ? 0 : ((PageNumber - 1) * PageSize) + 1;
    public int LastItemOnPage => Count == 0 ? 0 : FirstItemOnPage + Count - 1;
}
