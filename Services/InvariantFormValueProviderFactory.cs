using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Splitbill.Services;

/// <summary>
/// HTML number inputs always submit decimals with a dot. Parse form values with invariant culture
/// while keeping the application's Indonesian display culture unchanged.
/// </summary>
public sealed class InvariantFormValueProviderFactory : IValueProviderFactory
{
    public async Task CreateValueProviderAsync(ValueProviderFactoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.ActionContext.HttpContext.Request.HasFormContentType) return;

        var form = await context.ActionContext.HttpContext.Request.ReadFormAsync();
        context.ValueProviders.Add(new FormValueProvider(BindingSource.Form, form, CultureInfo.InvariantCulture));
    }
}
