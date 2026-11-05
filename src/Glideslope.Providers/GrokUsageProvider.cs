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
    public const string UsageOmittedCode = "grok_usage_percent_omitted";
    private static readonly Uri Instructions = new("https://docs.x.ai/build/overview");
    private static readonly TimeSpan SamePeriodTolerance = TimeSpan.FromMinutes(1);
    /// <summary>How long an omission may persist after usage was seen before it is accepted as zero, so a
    /// genuine mid-period reset by xAI cannot hold the card offline for the rest of the week.</summary>
    internal static readonly TimeSpan OmissionGapLimit = TimeSpan.FromMinutes(30);
    private readonly object _periodGate = new();
    private (string Subject, DateTimeOffset ResetAtUtc, bool UsageSeen, DateTimeOffset? FirstOmissionUtc)? _lastPeriod;
    private readonly IGrokUsageTransport _transport;
    private readonly AccountScopeResolver _scopeResolver;
    private readonly IProviderDiagnosticSink _diagnostics;
    private readonly TimeProvider _time;
    private readonly byte[] _sessionScopeKey = RandomNumberGenerator.GetBytes(32);
    private int _scopeWarningIssued;

    public GrokUsageProvider(IGrokUsageTransport transport, AccountScopeResolver scopeResolver,
        IProviderDiagnosticSink? diagnostics = null, TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _diagnostics = diagnostics ?? new NullProviderDiagnosticSink();
        _time = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ProviderReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        ProviderReadResult? result = null;
        try
        {
            var exchange = await _transport.ReadAsync(cancellationToken).ConfigureAwait(false);
            GrokCliUsageTransport.ValidateInitialize(exchange.Initialize.ResultJson);

            var now = _time.GetUtcNow();
            var parsed = GrokUsageParser.Parse(exchange.AuthInfo.ResultJson, exchange.Billing.ResultJson, now);
            if (!AcceptUsage(parsed, now))
            {
                result = Failure(ProviderStatus.Offline, UsageOmittedCode);
                return result;
            }
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

    /// <summary>An omitted percentage normally means zero usage. After this account reported usage above
    /// zero in the same period, a short omission is treated as a backend gap: the read fails as Offline, so
    /// the card keeps the last reading and the scheduler retries, instead of publishing a false 0 percent.
    /// Isolated reads failed the usage-percentage check mid-period on 2026-11-02..04 between normal
    /// readings. An omission lasting <see cref="OmissionGapLimit"/> is accepted as zero, which covers a
    /// genuine mid-period reset.</summary>
    private bool AcceptUsage(ParsedGrokUsage parsed, DateTimeOffset now)
    {
        var reset = parsed.WeeklyWindow.ResetAtUtc ?? DateTimeOffset.MinValue;
        lock (_periodGate)
        {
            var samePeriod = _lastPeriod is { } last && StringComparer.Ordinal.Equals(last.Subject, parsed.Subject) &&
                (last.ResetAtUtc - reset).Duration() <= SamePeriodTolerance;
            var usageSeen = samePeriod && _lastPeriod!.Value.UsageSeen;
            if (!parsed.UsageReported && usageSeen)
            {
                var firstOmission = _lastPeriod!.Value.FirstOmissionUtc ?? now;
                if (now - firstOmission < OmissionGapLimit)
                {
                    _diagnostics.Record(new ProviderDiagnostic(UsageOmittedCode, ProviderIds.Grok, "after_usage_in_period", 0));
                    _lastPeriod = (parsed.Subject, reset, true, firstOmission);
                    return false;
                }
                _diagnostics.Record(new ProviderDiagnostic(UsageOmittedCode, ProviderIds.Grok, "persisted_read_as_zero", 0));
                usageSeen = false;
            }
            else if (!parsed.UsageReported && !samePeriod)
                _diagnostics.Record(new ProviderDiagnostic(UsageOmittedCode, ProviderIds.Grok, "read_as_zero", 0));
            _lastPeriod = (parsed.Subject, reset, usageSeen || parsed.WeeklyWindow.RemainingFraction < 1, null);
            return true;
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
