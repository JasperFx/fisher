using JasperFx.Pagination;

namespace Fisher.Pagination;

/// <summary>
///     Offset paging's result (fisher#27), as JasperFx's shared <see cref="IPagedList{T}" />.
/// </summary>
/// <remarks>
///     Fisher declared its own <c>Fisher.Pagination.IPagedList&lt;T&gt;</c> until 2.0. Polecat's was the
///     same interface, and jasperfx#1054 lifted it to <c>JasperFx.Pagination</c> (fisher#465). Offset paging
///     can jump to an arbitrary page and reports a total; keyset paging (<see cref="CursorPage{T}" />)
///     can do neither but is stable under concurrent writes, so Fisher carries both.
/// </remarks>
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
