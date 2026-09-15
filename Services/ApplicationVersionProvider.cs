using System.Reflection;

namespace Splitbill.Services;

public sealed record ApplicationVersionDetails(
    string SemanticVersion,
    string DisplayVersion,
    string InformationalVersion,
    string AssemblyVersion,
    string? SourceRevision);

public interface IApplicationVersionProvider
{
    ApplicationVersionDetails Current { get; }
}

public sealed class ApplicationVersionProvider : IApplicationVersionProvider
{
    public ApplicationVersionProvider()
    {
        Current = Read(typeof(ApplicationVersionProvider).Assembly);
    }

    public ApplicationVersionDetails Current { get; }

    public static ApplicationVersionDetails Read(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var assemblyVersion = assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) informational = assemblyVersion;

        var parts = informational.Split('+', 2, StringSplitOptions.TrimEntries);
        var semanticVersion = parts[0].TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(semanticVersion)) semanticVersion = assemblyVersion;
        var revision = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1])
            ? parts[1].Split('.', 2, StringSplitOptions.TrimEntries)[0]
            : null;
        if (revision is { Length: > 12 }) revision = revision[..12];

        return new ApplicationVersionDetails(
            semanticVersion,
            $"v{semanticVersion}",
            informational,
            assemblyVersion,
            revision);
    }
}
