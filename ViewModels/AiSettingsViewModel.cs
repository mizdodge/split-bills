using System.ComponentModel.DataAnnotations;
using Splitbill.Models;

namespace Splitbill.ViewModels;

public sealed class AiSettingsViewModel
{
    public int Id { get; set; }
    public AiProvider Provider { get; set; } = AiProvider.OpenAi;
    [Url] public string? Endpoint { get; set; }
    [DataType(DataType.Password)] public string? ApiKey { get; set; }
    [Required] public string Model { get; set; } = "gpt-5.6-luna";
    public string? DeploymentName { get; set; }
    public string? ApiVersion { get; set; }
    public AiApiMode ApiMode { get; set; } = AiApiMode.Responses;
    public DateTimeOffset? LastTestAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public string? LastError { get; set; }
    public bool HasStoredApiKey { get; set; }
}

public sealed class AiModelLookupViewModel
{
    public AiProvider Provider { get; set; } = AiProvider.OpenAi;
    public string? Endpoint { get; set; }
    public string? ApiVersion { get; set; }
    public string? ApiKey { get; set; }
}
