using Glideslope.App;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Verifies the native contract seams without creating an icon, window, or
/// desktop. Real click delivery remains a user-desktop acceptance check.
/// </summary>
internal static class WindowsTrayNativeContractProof
{
    public static void Run()
    {
        Assert(IntPtr.Size == 8, "windows_tray_contract_proof_requires_64_bit_runtime");
        Assert(WindowsTrayService.NotifyIconDataSizeForSpecs == 976, "notify_icon_data_size_mismatch");
        Assert(WindowsTrayService.NotifyIconVersionOffsetForSpecs == 816, "notify_icon_version_union_offset_mismatch");
        Assert(WindowsTrayService.IconIdForSpecs <= ushort.MaxValue, "v4_registered_icon_id_must_fit_16_bits");

        Assert(WindowsTrayService.RegistrationConfirmedForSpecs(true, true), "successful_version_negotiation_should_confirm_registration");
        Assert(!WindowsTrayService.RegistrationConfirmedForSpecs(true, false), "version_negotiation_failure_must_not_confirm_registration");
        Assert(!WindowsTrayService.RegistrationConfirmedForSpecs(false, true), "add_failure_must_not_confirm_registration");

        var expectedIconId = WindowsTrayService.IconIdForSpecs;
        var packedLeftClick = (expectedIconId << 16) | 0x0202;
        Assert(WindowsTrayService.DecodeCallbackForSpecs(packedLeftClick, out var callback), "packed_callback_icon_id_should_match");
        Assert(callback == 0x0202, "packed_callback_should_decode_low_word_event");
        Assert(!WindowsTrayService.DecodeCallbackForSpecs(((expectedIconId ^ 1) << 16) | 0x0202, out _), "foreign_icon_callback_should_be_ignored");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }
}
