namespace Erp.Modules.Reporting.Components;

public sealed class ReportPaginationState
{
    public const int DefaultPageSize = 100;
    public int PageSize { get; private set; } = DefaultPageSize;
    public int Page { get; private set; } = 1;
    public int Total { get; private set; }
    public int ItemCount { get; private set; }
    public int PageCount => LastPage(Total, PageSize);
    public int FirstRow => ItemCount == 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int LastRow => ItemCount == 0 ? 0 : FirstRow + (ItemCount - 1);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < PageCount;

    public static bool IsSupportedSize(int pageSize) => pageSize is 25 or 50 or 100;

    public static int LastPage(int total, int pageSize = DefaultPageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        return total <= 0 ? 1 : ((total - 1) / pageSize) + 1;
    }

    public void SetPage(int page, int total, int itemCount, int pageSize = DefaultPageSize)
    {
        if (!IsSupportedSize(pageSize)) throw new ArgumentOutOfRangeException(nameof(pageSize));
        PageSize = pageSize;
        Total = Math.Max(0, total);
        Page = Math.Clamp(page, 1, PageCount);
        ItemCount = Math.Clamp(itemCount, 0, Math.Min(PageSize, Total - ((Page - 1) * PageSize)));
    }
}
