namespace Glideslope.Providers;

public sealed record ProviderDiagnostic(string Code, string ProviderId, string Status, long DurationMilliseconds);

public interface IProviderDiagnosticSink
{
    void Record(ProviderDiagnostic diagnostic);
}

public sealed class NullProviderDiagnosticSink : IProviderDiagnosticSink
{
    public void Record(ProviderDiagnostic diagnostic) { }
}
