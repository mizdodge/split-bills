using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class InvariantFormValueProviderFactoryTests
{
    [Fact]
    public async Task ParsesHtmlNumberDecimalWithoutTreatingDotAsThousandsSeparator()
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["Amount"] = "10000.50"
        });
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor(), new ModelStateDictionary());
        var context = new ValueProviderFactoryContext(action);

        await new InvariantFormValueProviderFactory().CreateValueProviderAsync(context);

        var value = Assert.Single(context.ValueProviders).GetValue("Amount");
        Assert.Equal(CultureInfo.InvariantCulture, value.Culture);
        Assert.Equal(10_000.50m, decimal.Parse(value.FirstValue!, value.Culture));
    }
}
