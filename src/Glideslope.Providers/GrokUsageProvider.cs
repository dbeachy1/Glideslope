using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glideslope.Domain;

namespace Glideslope.Providers;

/// <summary>Reads only Grok's authenticated subscription quota through the official CLI ACP interface.</summary>
public sealed class GrokUsageProvider : IUsageProvider
{
    public const string HistoryScopeUnavailableCode = "grok_history_scope_unavailable";
    private static readonly Uri Instructions = new("https://docs.x.ai/build/overview");
    private readonly IGrokUsageTransport _transport;
    private readonly AccountScopeResolver _scopeResolver;
    private readonly IProviderDiagnosticSink _diagnostics;
    private readonly byte[] _sessionScopeKey = RandomNumberGenerator.GetBytes(32);
    private int _scopeWarningIssued;

    public GrokUsageProvider(IGrokUsageTransport transport, AccountScopeResolver scopeResolver,
        IProviderDiagnosticSink? diagnostics = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _diagnostics = diagnostics ?? new NullProviderDiagnosticSink();
    }

    public async ValueTask<ProviderReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        ProviderReadResult? result = null;
        try
        {
            var exchange = await _transport.ReadAsync(cancellationToken).ConfigureAwait(false);
            GrokCliUsageTransport.ValidateInitialize(exchange.Initialize.ResultJson);

            var now = DateTimeOffset.UtcNow;
            var parsed = GrokUsageParser.Parse(exchange.AuthInfo.ResultJson, exchange.Billing.ResultJson, now);
            string accountScope;
            try
            {
                accountScope = await _scopeResolver(ProviderIds.Grok, "xai.grok", parsed.Subject, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _diagnostics.Record(new ProviderDiagnostic("grok_scope_resolve_failed", ProviderIds.Grok, exception.GetType().Name, 0));
                accountScope = SessionScope(parsed.Subject);
                result = Failure(ProviderStatus.Ready, HistoryScopeUnavailableCode,
                    new AccountSnapshot(ProviderIds.Grok, accountScope, parsed.Plan, now,
                        now, GrokUsageParser.SourceId, [parsed.WeeklyWindow]));
                return result;
            }

            result = new ProviderReadResult(ProviderStatus.Ready,
                new AccountSnapshot(ProviderIds.Grok, accountScope, parsed.Plan, now, now,
                    GrokUsageParser.SourceId, [parsed.WeeklyWindow]));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GrokUsageTransportException exception)
        {
            result = Failure(exception.Status, exception.SafeErrorCode);
            return result;
        }
        catch (GrokUsageAccountException exception)
        {
            result = Failure(exception.Status, exception.SafeErrorCode);
            return result;
        }
        catch (GrokUsageSchemaException exception)
        {
            result = Failure(ProviderStatus.SchemaChanged, exception.SafeErrorCode);
            return result;
        }
        catch (JsonException)
        {
            result = Failure(ProviderStatus.SchemaChanged, "grok_response_invalid");
            return result;
        }
        catch (Exception exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("grok_read_failed", ProviderIds.Grok, exception.GetType().Name,
                Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
            result = Failure(ProviderStatus.Offline, "grok_read_failed");
            return result;
        }
        finally
        {
            if (result?.SafeErrorCode == HistoryScopeUnavailableCode)
            {
                if (Interlocked.Exchange(ref _scopeWarningIssued, 1) == 0)
                    _diagnostics.Record(new ProviderDiagnostic("provider_history_scope_unavailable", ProviderIds.Grok, "session_scope",
                        Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
            }
            else Interlocked.Exchange(ref _scopeWarningIssued, 0);
            _diagnostics.Record(new ProviderDiagnostic("provider_read_finished", ProviderIds.Grok,
                $"{result?.Status.ToString().ToLowerInvariant() ?? "cancelled"},code={result?.SafeErrorCode ?? "none"}",
                Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
        }
    }

    private string SessionScope(string subject)
    {
        var input = Encoding.UTF8.GetBytes($"{ProviderIds.Grok}\0xai.grok\0{subject}");
        try { return $"session:{Convert.ToHexString(HMACSHA256.HashData(_sessionScopeKey, input)).ToLowerInvariant()}"; }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    private ProviderReadResult Failure(ProviderStatus status, string code, AccountSnapshot? snapshot = null)
    {
        var details = status == ProviderStatus.Ready ? code : null;

        var actions = status switch
        {
            ProviderStatus.NeedsSignIn or ProviderStatus.AuthenticationExpired => new[]
            {
                new ProviderAction(ProviderActionKind.LaunchOfficialApplication, "grok"),
                new ProviderAction(ProviderActionKind.OpenOfficialInstructions, Instructions.AbsoluteUri),
                new ProviderAction(ProviderActionKind.Retry)
            },
            ProviderStatus.MissingApplication => new[] { new ProviderAction(ProviderActionKind.OpenOfficialInstructions, Instructions.AbsoluteUri) },
            _ => new[] { new ProviderAction(ProviderActionKind.Retry) }
        };
        return new ProviderReadResult(status, snapshot, actions: actions, safeErrorCode: details ?? code);
    }
}
