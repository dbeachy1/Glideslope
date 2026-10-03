using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Tracks each card's live mode. A missing provider id defaults to <see cref="CardMode.Full"/>; the coordinator
/// sets the mode before showing a card and removes it when the card closes or its provider is disabled.
/// </summary>
internal sealed class CardModes
{
    private readonly Dictionary<string, CardMode> _modes = new(StringComparer.Ordinal);

    public CardMode Of(string providerId) => _modes.GetValueOrDefault(providerId, CardMode.Full);

    /// <summary>Sets every id in <paramref name="providerIds"/> to <paramref name="mode"/>. Used both for a
    /// group's mode switch (every member moves together) and for a single new card.</summary>
    public void Set(IEnumerable<string> providerIds, CardMode mode)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        foreach (var providerId in providerIds) _modes[providerId] = mode;
    }

    /// <summary>A card closed, or its provider disabled: this coordinator no longer tracks its mode.</summary>
    public void Remove(string providerId) => _modes.Remove(providerId);

    public IReadOnlyCollection<string> FullIds => _modes.Where(pair => pair.Value == CardMode.Full)
        .Select(pair => pair.Key).ToArray();

    public IReadOnlyCollection<string> MiniIds => _modes.Where(pair => pair.Value == CardMode.Mini)
        .Select(pair => pair.Key).ToArray();
}
