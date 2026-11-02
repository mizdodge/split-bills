using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Splitbill.ViewModels;

public sealed record PaginationViewModel(int Page, int PageSize, int TotalItems, string PageParameter)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalItems / PageSize));
    public int FirstItem => TotalItems == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int LastItem => Math.Min(Page * PageSize, TotalItems);
    public string SizeParameter => PageParameter == "page" ? "pageSize" : PageParameter + "Size";
    public IReadOnlyList<int?> VisiblePages => TotalPages <= 5
        ? Enumerable.Range(1, TotalPages).Select(x => (int?)x).ToArray()
        : Page <= 3 ? [1, 2, 3, null, TotalPages]
        : Page >= TotalPages - 2 ? [1, null, TotalPages - 2, TotalPages - 1, TotalPages]
        : [1, null, Page, null, TotalPages];

    public string PageUrl(HttpRequest request, int page)
    {
        var query = request.Query.ToDictionary(x => x.Key, x => (string?)x.Value.ToString());
        query[PageParameter] = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        query[SizeParameter] = PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return QueryHelpers.AddQueryString(request.PathBase + request.Path, query);
    }
}

public static class PaginationExtensions
{
    public static List<T> Paginate<T>(this Controller controller, IEnumerable<T> source, string parameter = "page")
    {
        var rows = source.ToList();
        var query = controller.HttpContext?.Request.Query;
        var sizeParameter = parameter == "page" ? "pageSize" : parameter + "Size";
        var size = int.TryParse(query?[sizeParameter].ToString(), out var requestedSize) && requestedSize is 10 or 25 or 50 or 100 ? requestedSize : 10;
        var pages = Math.Max(1, (int)Math.Ceiling((double)rows.Count / size));
        var page = int.TryParse(query?[parameter].ToString(), out var requestedPage) ? Math.Clamp(requestedPage, 1, pages) : 1;
        controller.ViewData["Pagination:" + parameter] = new PaginationViewModel(page, size, rows.Count, parameter);
        return rows.Skip((page - 1) * size).Take(size).ToList();
    }
}
