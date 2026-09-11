using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class AiModelCatalogParserTests
{
    [Fact]
    public void Parse_ReturnsDistinctModelIdsInAlphabeticalOrder()
    {
        const string response = """
            {"object":"list","data":[{"id":"gpt-5"},{"id":"gpt-4o"},{"id":"GPT-5"},{"id":"o3"}]}
            """;

        var result = AiModelCatalogParser.Parse(response);

        Assert.Equal(["gpt-4o", "gpt-5", "o3"], result);
    }

    [Fact]
    public void Parse_IgnoresEntriesWithoutUsableIds()
    {
        const string response = """
            {"data":[{}, {"id":null}, {"id":"  "}, {"id":"gpt-4.1-mini"}]}
            """;

        var result = AiModelCatalogParser.Parse(response);

        Assert.Equal(["gpt-4.1-mini"], result);
    }

    [Fact]
    public void Parse_RejectsMissingDataArray()
    {
        Assert.Throws<AiServiceException>(() => AiModelCatalogParser.Parse("{\"object\":\"list\"}"));
    }
}
