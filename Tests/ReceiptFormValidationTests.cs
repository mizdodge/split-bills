using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Splitbill.ViewModels;

namespace Splitbill.Tests;

public sealed class ReceiptFormValidationTests
{
    [Fact]
    public void Review_OptionalFieldsAndDisplayMetadataDoNotBlockSubmission()
    {
        var state = Validate(new ReviewTransactionViewModel
        {
            Id = 1, GrandTotal = 50_000,
            TransactionNumber = null!, ReceiptImagePath = null!,
            Items = [new ReviewItemViewModel { Quantity = 0, TotalPrice = 50_000 }]
        });
        Assert.True(state.IsValid);
    }

    [Fact]
    public void Split_OnlyParticipantInputIsRequired()
    {
        var state = Validate(new SplitTransactionViewModel
        {
            TransactionId = 1, MerchantName = null!, ParticipantNames = "Andi\nFifi"
        });
        Assert.True(state.IsValid);
    }

    private static ModelStateDictionary Validate(object model)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var provider = services.BuildServiceProvider();
        var state = new ModelStateDictionary();
        var context = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), state);
        provider.GetRequiredService<IObjectModelValidator>().Validate(context, null, string.Empty, model);
        return state;
    }
}
