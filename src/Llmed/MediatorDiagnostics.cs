using System.Diagnostics;

namespace Llmed;

internal static class MediatorDiagnostics
{
    public const string ActivitySourceName = "Llmed.Mediator";

    public static readonly ActivitySource ActivitySource = new(
        ActivitySourceName,
        typeof(MediatorDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}
