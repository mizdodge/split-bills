using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Splitbill.ViewModels;

namespace Splitbill.Tests;

public sealed class PaginationTests
{
    [Theory]
    [InlineData("", 1, 10, 1, 10)]
    [InlineData("?page=2", 2, 10, 11, 20)]
    [InlineData("?page=999999", 4, 10, 31, 35)]
    [InlineData("?page=-1&pageSize=-10", 1, 10, 1, 10)]
    [InlineData("?page=bad&pageSize=25", 1, 25, 1, 25)]
    [InlineData("?pageSize=2147483647", 1, 10, 1, 10)]
    public void PagesHaveBoundedSizesAndStableRanges(string query, int page, int size, int first, int last)
    {
        var controller = Controller(query);
        var rows = controller.Paginate(Enumerable.Range(1, 35));
        var meta = Assert.IsType<PaginationViewModel>(controller.ViewData["Pagination:page"]);
        Assert.Equal(page, meta.Page); Assert.Equal(size, meta.PageSize);
        Assert.Equal(first, rows.First()); Assert.Equal(last, rows.Last()); Assert.Equal(35, meta.TotalItems);
    }

    [Fact]
    public void IndependentPagersPreserveFiltersAndPathBaseInLinks()
    {
        var controller = Controller("?from=2026-10-01&status=Unpaid&pickupStrategy=RoundRobin&page=2&pickupPage=3&pickupPageSize=10&uploaderId=a%26b");
        controller.HttpContext.Request.Path = "/Reports";
        controller.HttpContext.Request.PathBase = "/splitbill";
        Assert.Equal(11, controller.Paginate(Enumerable.Range(1, 50)).First());
        Assert.Equal(21, controller.Paginate(Enumerable.Range(1, 40), "pickupPage").First());
        var meta = Assert.IsType<PaginationViewModel>(controller.ViewData["Pagination:pickupPage"]);
        var url = meta.PageUrl(controller.Request, 4);
        Assert.StartsWith("/splitbill/Reports?", url);
        var query = QueryHelpers.ParseQuery(new Uri("https://example.com" + url).Query);
        Assert.Equal("2", query["page"]); Assert.Equal("4", query["pickupPage"]);
        Assert.Equal("2026-10-01", query["from"]); Assert.Equal("RoundRobin", query["pickupStrategy"]);
        Assert.Equal("a&b", query["uploaderId"]);
    }

    [Fact]
    public void EmptyListStillHasValidPageMetadata()
    {
        var controller = Controller("?page=999");
        Assert.Empty(controller.Paginate(Array.Empty<int>()));
        var meta = Assert.IsType<PaginationViewModel>(controller.ViewData["Pagination:page"]);
        Assert.Equal(1, meta.Page); Assert.Equal(1, meta.TotalPages);
        Assert.Equal(0, meta.FirstItem); Assert.Equal(0, meta.LastItem);
    }

    private static Controller Controller(string query) => new TestController
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { Request = { QueryString = new QueryString(query) } } }
    };

    private sealed class TestController : Controller { }

    [Theory]
    [InlineData(1, "1,2,3,...,13")]
    [InlineData(2, "1,2,3,...,13")]
    [InlineData(7, "1,...,7,...,13")]
    [InlineData(12, "1,...,11,12,13")]
    [InlineData(13, "1,...,11,12,13")]
    public void CompactPageWindowKeepsCurrentAndBoundaryPages(int page, string expected)
    {
        var meta = new PaginationViewModel(page, 10, 126, "page");
        Assert.Equal(expected, string.Join(",", meta.VisiblePages.Select(x => x?.ToString() ?? "...")));
        Assert.Contains(page, meta.VisiblePages);
        Assert.True(meta.VisiblePages.Count <= 5);
    }
}
