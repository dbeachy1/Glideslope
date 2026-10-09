using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Glideslope.Core;
using Glideslope.Domain;

namespace Glideslope.App;

/// <summary>
/// Settings controls stage changes until Save commits them together. Cancel or closing the window discards
/// staged changes; Reset window positions applies immediately. Card scale is staged but does not scale this window.
/// </summary>
internal sealed class SettingsWindow : Window
{
    /// <summary>The minimum height that keeps the message area and buttons visible above the scrolling rows.</summary>
    internal const double MinimumWindowHeight = 300;
    /// <summary>The margin between the window frame and the working area's top and bottom.</summary>
    internal const double WorkingAreaMargin = 16;
    /// <summary>The retention range accepted by AppSettings validation and shown by the control.</summary>
    internal const int MinimumRetentionDays = 7;
    internal const int MaximumRetentionDays = 365;
    private const double CardScaleSliderWidth = 160;
    private static readonly string[] ProviderOrder = [ProviderIds.Codex, ProviderIds.Claude, ProviderIds.Gemini];
    private readonly Dictionary<string, CheckBox> _providers = new(StringComparer.Ordinal);
    private readonly CheckBox _startAtSignIn;
    private readonly CheckBox _alwaysOnTop;
    private readonly CheckBox _snapToScreenEdge;
    private readonly NumericUpDown _refreshMinutes;
    private readonly NumericUpDown _retentionDays;
    private readonly ComboBox _themeMode;
    private readonly ComboBox _languageChoice;
    private readonly Slider _cardScale;
    private readonly TextBlock _cardScaleValue;
    private bool _syncingCardScale;
    // Mini card size and startup mode controls.
    private readonly Slider _miniCardScale;
    private readonly TextBlock _miniCardScaleValue;
    private bool _syncingMiniCardScale;
    private readonly TextBlock _shiftProjectionNote;
    private readonly CheckBox _startInMiniMode;
    // Ctrl peek preference, staged and applied on Save.
    private readonly CheckBox _ctrlPeekFullCard;
    private readonly HyperlinkButton? _logFileLink;
    internal HyperlinkButton? LogFileLinkControl => _logFileLink;
    private readonly TextBlock _error;
    private readonly TextBlock _startupStatus;
    private readonly TextBlock _languageSaved;
    private readonly Func<AppSettings, long, bool, Task<SettingsSaveResult>> _save;
    private readonly Func<Task>? _restart;
    private readonly string _activeLanguageChoice;
    private readonly IDiagnosticSink _diagnostics;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly Button _restartButton;
    private readonly StackPanel _buttonRow;
    private readonly StackPanel _messageArea;
    private readonly Button _resetPositions;
    // Keep commit tracking independent of the Avalonia window for direct behavioral testing.
    private readonly SettingsCommitTracker _commits;
    private bool _saveInProgress;

    internal bool StartAtSignInIsChecked => _startAtSignIn.IsChecked == true;
    internal CheckBox AlwaysOnTopControl => _alwaysOnTop;
    internal CheckBox SnapToScreenEdgeControl => _snapToScreenEdge;
    internal Button ResetPositionsControl => _resetPositions;
    internal Button SaveControl => _saveButton;
    internal Button RestartControl => _restartButton;
    internal Button CancelControl => _cancelButton;
    internal StackPanel ButtonRow => _buttonRow;
    internal ScrollViewer ScrollControl => _scroll;
    private readonly ScrollViewer _scroll;
    internal Slider CardScaleControl => _cardScale;
    internal string CardScaleValueText => _cardScaleValue.Text ?? string.Empty;
    internal Slider MiniCardScaleControl => _miniCardScale;
    internal string MiniCardScaleValueText => _miniCardScaleValue.Text ?? string.Empty;
    internal CheckBox StartInMiniModeControl => _startInMiniMode;
    internal TextBlock ShiftProjectionNoteControl => _shiftProjectionNote;
    internal CheckBox CtrlPeekFullCardControl => _ctrlPeekFullCard;
    internal NumericUpDown RetentionDaysControl => _retentionDays;

    /// <summary>A message block is visible only while it has text, so an empty one takes no height.</summary>
    private static void CollapseWhenEmpty(TextBlock block)
    {
        block.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBlock.TextProperty)
                block.IsVisible = !string.IsNullOrEmpty(block.Text);
        };
    }
    internal StackPanel MessageArea => _messageArea;
    internal TextBlock StartupStatusControl => _startupStatus;
    internal TextBlock ErrorControl => _error;
    internal ComboBox ThemeSelector => _themeMode;
    internal ComboBox LanguageSelector => _languageChoice;
    internal CheckBox ProviderCheckbox(string providerId) => _providers[providerId];
    internal string ErrorText => _error.Text ?? string.Empty;
    internal string StartupStatusText => _startupStatus.Text ?? string.Empty;

    public SettingsWindow(
        AppSettings current,
        StartupRegistrationState startup,
        Func<AppSettings, long, bool, Task<SettingsSaveResult>> save,
        bool startupChoicePending = false,
        Action? resetPositions = null,
        IDiagnosticSink? diagnostics = null,
        string? logFilePath = null,
        Func<Task>? restart = null,
        string? activeLanguageChoice = null)
    {
        var committed = current.Clone();
        committed.StartAtSignIn = startup.IsRegistered;
        _commits = new SettingsCommitTracker(committed);
        _save = save;
        _restart = restart;
        _activeLanguageChoice = activeLanguageChoice ?? current.LanguageChoice;
        _diagnostics = diagnostics ?? new NullDiagnosticSink();

        Title = LocalizedText.SettingsTitle;
        Icon = AppIcon.Get(_diagnostics);
        Width = 500;
        // Size to content initially; FitToScreen caps it to the current working area and the rows scroll if needed.
        SizeToContent = SizeToContent.Height;
        MinHeight = MinimumWindowHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Keep Settings above ordinary windows; the coordinator re-raises it after bringing cards forward.
        Topmost = true;
        Opened += (_, _) => FitToScreen();
        var content = new StackPanel { Spacing = 13, Margin = new Thickness(28) };
        content.Children.Add(new TextBlock { Text = LocalizedText.SettingsHeading, FontSize = 25, FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = LocalizedText.SettingsEnabledProviders, FontSize = 12 });
        foreach (var id in ProviderOrder)
        {
            // Staged only: no change handler here. The checked state is read fresh from each
            // control when Save is pressed and committed in the one settings-store write.
            var box = new CheckBox { Content = LocalizedText.ProviderName(id), IsChecked = current.EnabledSet.Contains(id) };
            _providers.Add(id, box);
            content.Children.Add(box);
        }

        content.Children.Add(new TextBlock { Text = LocalizedText.SettingsTheme, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
        _themeMode = new ComboBox
        {
            ItemsSource = new[]
            {
                new ComboBoxItem { Content = LocalizedText.SettingsThemeSystem, Tag = "system" },
                new ComboBoxItem { Content = LocalizedText.SettingsThemeDark, Tag = "dark" },
                new ComboBoxItem { Content = LocalizedText.SettingsThemeLight, Tag = "light" },
            },
            SelectedIndex = current.ThemeMode switch { "dark" => 1, "light" => 2, _ => 0 }
        };
        content.Children.Add(_themeMode);

        content.Children.Add(new TextBlock { Text = LocalizedText.SettingsLanguage, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
        _languageChoice = new ComboBox
        {
            ItemsSource = AppSettings.SupportedLanguageChoices.Select(id => new ComboBoxItem
            {
                Content = id == "auto" ? LocalizedText.SettingsLanguageSystem : LocaleResolver.DisplayNames[id],
                Tag = id,
            }).ToArray(),
            SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.SupportedLanguageChoices.ToArray(), current.LanguageChoice)),
        };
        AutomationProperties.SetName(_languageChoice, LocalizedText.SettingsLanguage);
        content.Children.Add(_languageChoice);
        content.Children.Add(new TextBlock { Text = LocalizedText.SettingsLanguageRestart, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        // Card size uses the same range and steps as the card's own flyout and is staged until Save.
        _cardScale = new Slider
        {
            Minimum = CardScale.MinimumPercent,
            Maximum = CardScale.MaximumPercent,
            TickFrequency = CardScale.StepPercent,
            IsSnapToTickEnabled = true,
            TickPlacement = TickPlacement.BottomRight,
            SmallChange = CardScale.StepPercent,
            LargeChange = CardScale.StepPercent,
            Width = CardScaleSliderWidth,
            VerticalAlignment = VerticalAlignment.Center,
            Value = CardScale.Snap(current.FullCardScalePercent)
        };
        AutomationProperties.SetName(_cardScale, LocalizedText.SettingsCardSize);
        _cardScaleValue = new TextBlock
        {
            Text = LocalizedText.Percentage(CardScale.Snap(current.FullCardScalePercent) / 100.0),
            MinWidth = 48,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _cardScale.ValueChanged += (_, e) => OnCardScaleChanged(e.NewValue);
        var cardScaleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        cardScaleRow.Children.Add(_cardScale);
        cardScaleRow.Children.Add(_cardScaleValue);
        content.Children.Add(LabeledControl(LocalizedText.SettingsCardSize, cardScaleRow));

        // Use the same size control for mini cards.
        _miniCardScale = new Slider
        {
            Minimum = CardScale.MinimumPercent,
            Maximum = CardScale.MaximumPercent,
            TickFrequency = CardScale.StepPercent,
            IsSnapToTickEnabled = true,
            TickPlacement = TickPlacement.BottomRight,
            SmallChange = CardScale.StepPercent,
            LargeChange = CardScale.StepPercent,
            Width = CardScaleSliderWidth,
            VerticalAlignment = VerticalAlignment.Center,
            Value = CardScale.Snap(current.MiniCardScalePercent)
        };
        AutomationProperties.SetName(_miniCardScale, LocalizedText.SettingsMiniCardSize);
        _miniCardScaleValue = new TextBlock
        {
            Text = LocalizedText.Percentage(CardScale.Snap(current.MiniCardScalePercent) / 100.0),
            MinWidth = 48,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _miniCardScale.ValueChanged += (_, e) => OnMiniCardScaleChanged(e.NewValue);
        var miniCardScaleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        miniCardScaleRow.Children.Add(_miniCardScale);
        miniCardScaleRow.Children.Add(_miniCardScaleValue);
        content.Children.Add(LabeledControl(LocalizedText.SettingsMiniCardSize, miniCardScaleRow));

        _shiftProjectionNote = new TextBlock
        {
            Text = LocalizedText.SettingsShiftProjectionNote,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        content.Children.Add(_shiftProjectionNote);

        // Startup mode applies on the next launch and to newly created cards.
        _startInMiniMode = new CheckBox { Content = LocalizedText.SettingsStartInMiniMode, IsChecked = current.StartInMiniMode };
        content.Children.Add(_startInMiniMode);

        // Saving an opt-out ends an active peek immediately.
        _ctrlPeekFullCard = new CheckBox { Content = LocalizedText.SettingsCtrlPeek, IsChecked = current.CtrlPeekFullCard };
        content.Children.Add(_ctrlPeekFullCard);

        _alwaysOnTop = new CheckBox { Content = LocalizedText.SettingsAlwaysOnTop, IsChecked = current.AlwaysOnTop };
        content.Children.Add(_alwaysOnTop);
        _snapToScreenEdge = new CheckBox
        {
            Content = LocalizedText.SettingsSnapToScreenEdge,
            IsChecked = current.SnapToScreenEdge
        };
        content.Children.Add(_snapToScreenEdge);
        _startAtSignIn = new CheckBox
        {
            Content = LocalizedText.SettingsStartAtSignIn,
            IsChecked = startupChoicePending || startup.IsRegistered
        };
        content.Children.Add(_startAtSignIn);
        _resetPositions = new Button { Content = LocalizedText.SettingsResetPositions, HorizontalAlignment = HorizontalAlignment.Left };
        _resetPositions.Click += (_, _) => resetPositions?.Invoke();
        content.Children.Add(_resetPositions);
        _refreshMinutes = new NumericUpDown { Minimum = AppSettings.MinimumRefreshMinutes, Maximum = AppSettings.MaximumRefreshMinutes, Increment = 1, Value = current.RefreshMinutes };
        // Build the labels from the same range constants as their controls.
        content.Children.Add(LabeledControl(LocalizedText.SettingsRefreshInterval(AppSettings.MinimumRefreshMinutes, AppSettings.MaximumRefreshMinutes), _refreshMinutes));
        _retentionDays = new NumericUpDown { Minimum = MinimumRetentionDays, Maximum = MaximumRetentionDays, Increment = 1, Value = current.RetentionDays };
        content.Children.Add(LabeledControl(LocalizedText.SettingsRetentionDays(MinimumRetentionDays, MaximumRetentionDays), _retentionDays));

        // The log path is a link to its folder, not a staged setting.
        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            content.Children.Add(new TextBlock { Text = LocalizedText.SettingsLogFile, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
            _logFileLink = new HyperlinkButton
            {
                Content = new TextBlock { Text = logFilePath, TextWrapping = TextWrapping.Wrap },
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            ToolTip.SetTip(_logFileLink, LocalizedText.SettingsOpenLogFolder);
            AutomationProperties.SetName(_logFileLink, logFilePath);
            _logFileLink.Click += async (_, _) => await OpenLogFolderAsync(logFilePath).ConfigureAwait(true);
            content.Children.Add(_logFileLink);
        }

        // Keep startup and save messages outside the scroll area; collapsed empty messages preserve balanced spacing.
        _startupStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        _languageSaved = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        CollapseWhenEmpty(_startupStatus);
        CollapseWhenEmpty(_error);
        CollapseWhenEmpty(_languageSaved);
        SetStartupState(startup, startupChoicePending);
        _messageArea = new StackPanel { Spacing = 0, Margin = new Thickness(28, 0, 28, 0) };
        _messageArea.Children.Add(_startupStatus);
        _messageArea.Children.Add(_error);
        _messageArea.Children.Add(_languageSaved);

        _buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 20, 24, 20)
        };
        _cancelButton = new Button { Content = LocalizedText.SettingsCancel, Padding = new Thickness(16, 8) };
        _cancelButton.Click += (_, _) => Close();
        _saveButton = new Button { Content = LocalizedText.SettingsSave, Padding = new Thickness(16, 8) };
        _saveButton.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        _restartButton = new Button { Content = LocalizedText.SettingsRestartNow, Padding = new Thickness(16, 8), IsVisible = false };
        _restartButton.Click += async (_, _) => await RestartAsync().ConfigureAwait(true);
        UpdateRestartOffer(current.LanguageChoice);
        _buttonRow.Children.Add(_restartButton);
        _buttonRow.Children.Add(_cancelButton);
        _buttonRow.Children.Add(_saveButton);
        var root = new Grid { RowDefinitions = RowDefinitions.Parse("*,Auto,Auto") };
        _scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        root.Children.Add(_scroll);
        Grid.SetRow(_messageArea, 1);
        root.Children.Add(_messageArea);
        Grid.SetRow(_buttonRow, 2);
        root.Children.Add(_buttonRow);
        Content = root;
        ActualThemeVariantChanged += (_, _) => ApplyWindowTheme();
        ApplyWindowTheme();
    }

    private void UpdateRestartOffer(string committedLanguageChoice)
    {
        var restartNeeded = committedLanguageChoice != _activeLanguageChoice;
        _languageSaved.Text = restartNeeded ? LocalizedText.SettingsLanguageSavedRestart : string.Empty;
        _restartButton.IsVisible = restartNeeded && _restart is not null;
    }

    private async Task RestartAsync()
    {
        if (_restart is null || !_restartButton.IsEnabled) return;
        _restartButton.IsEnabled = false;
        try
        {
            await _restart().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _diagnostics.Record(new DiagnosticEvent("settings_restart_failed", Status: ex.GetType().Name));
            _error.Text = LocalizedText.SettingsRestartFailed;
            _restartButton.IsEnabled = true;
        }
    }

    private async Task SaveAsync()
    {
        if (_saveInProgress) return;
        var selected = SelectedProviderIds;
        if (selected.Count == 0)
        {
            _error.Text = LocalizedText.SettingsAtLeastOneProvider;
            _diagnostics.Record(new DiagnosticEvent("settings_save_rejected", Status: "no_provider_selected"));
            return;
        }

        _saveInProgress = true;
        _saveButton.IsEnabled = false;
        SetProviderControlsEnabled(false);
        try
        {
            var requested = _commits.CreateRequest();
            requested.EnabledProviderIds = selected.ToList();
            requested.StartAtSignIn = _startAtSignIn.IsChecked == true;
            requested.AlwaysOnTop = _alwaysOnTop.IsChecked == true;
            requested.SnapToScreenEdge = _snapToScreenEdge.IsChecked == true;
            requested.ThemeMode = SelectedThemeMode;
            requested.LanguageChoice = (_languageChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
            requested.RefreshMinutes = (int)(_refreshMinutes.Value ?? requested.RefreshMinutes);
            requested.RetentionDays = (int)(_retentionDays.Value ?? requested.RetentionDays);
            requested.FullCardScalePercent = CardScale.Snap(_cardScale.Value);
            requested.MiniCardScalePercent = CardScale.Snap(_miniCardScale.Value);
            requested.StartInMiniMode = _startInMiniMode.IsChecked == true;
            requested.CtrlPeekFullCard = _ctrlPeekFullCard.IsChecked == true;
            var retentionChanged = requested.RetentionDays != _commits.AppliedRetentionDays;
            var result = await _save(requested, _commits.ExpectedRevision, retentionChanged).ConfigureAwait(true);
            _commits.RecordSaveResult(requested, result);
            ShowSaveResult(requested, result);
        }
        catch (Exception ex)
        {
            // Keep the window usable and log only the exception type, not a potentially sensitive message.
            _diagnostics.Record(new DiagnosticEvent("settings_save_exception", Status: ex.GetType().Name));
            _error.Text = LocalizedText.SettingsApplyFailed("settings_save_io_error");
        }
        finally
        {
            _saveInProgress = false;
            _saveButton.IsEnabled = true;
            SetProviderControlsEnabled(true);
        }
    }

    /// <summary>What Settings shows after the save delegate returns.</summary>
    private void ShowSaveResult(AppSettings requested, SettingsSaveResult result)
    {
        if (result.CommittedRevision is not null)
            UpdateRestartOffer(requested.LanguageChoice);

        if (result.Succeeded && result.WarningCode is { } warning)
        {
            // Settings were applied; registration failed, so show its warning without reporting a settings failure.
            _error.Text = string.Empty;
            _diagnostics.Record(new DiagnosticEvent("settings_save_shown", Status:
                $"saved_with_warning,code={warning},requestedStartAtSignIn={requested.StartAtSignIn},revision={result.CommittedRevision}"));
            return;
        }

        if (result.Succeeded)
        {
            _error.Text = string.Empty;
            _diagnostics.Record(new DiagnosticEvent("settings_save_shown", Status:
                $"{(_restartButton.IsVisible ? "restart_offered" : "closed")},revision={result.CommittedRevision}"));
            if (!_restartButton.IsVisible) Close();
            return;
        }

        // Keep the staged controls for retry. A failure with a committed revision can happen
        // after the settings write (for example, while applying history retention).
        _error.Text = LocalizedText.SettingsApplyFailed(result.IssueCode);
        _diagnostics.Record(new DiagnosticEvent("settings_save_shown", Status: $"failed,code={result.IssueCode ?? "none"}"));
    }

    public void SetStartupState(StartupRegistrationState state, bool preserveFirstLaunchOffer = false)
    {
        _startAtSignIn.IsChecked = preserveFirstLaunchOffer || state.IsRegistered;
        _startupStatus.Text = LocalizedText.StartupRegistrationIssue(state) ?? string.Empty;
    }

    /// <summary>
    /// Applies a newer committed settings revision while preserving staged controls whose committed values did
    /// not change. Returns false without changes for an older revision. The card-scale values follow the same
    /// rule as provider checkboxes, so unrelated commits do not overwrite staged slider edits.
    /// </summary>
    internal bool UpdateCommitted(long revision, LayoutSettings layout, IReadOnlyCollection<string> enabledProviderIds,
        int? fullCardScalePercent = null, int? miniCardScalePercent = null)
    {
        var previous = new HashSet<string>(_commits.EnabledProviderIds, StringComparer.Ordinal);
        var previousScale = _commits.FullCardScalePercent;
        var previousMiniScale = _commits.MiniCardScalePercent;
        if (!_commits.ApplyCommitted(revision, layout, enabledProviderIds, fullCardScalePercent, miniCardScalePercent))
            return false;
        var enabled = new HashSet<string>(enabledProviderIds, StringComparer.Ordinal);
        foreach (var (id, box) in _providers)
        {
            var committedNow = enabled.Contains(id);
            if (previous.Contains(id) != committedNow)
                box.IsChecked = committedNow;
        }
        if (fullCardScalePercent is { } scale && scale != previousScale)
        {
            SetCardScaleQuietly(CardScale.Snap(scale));
            _diagnostics.Record(new DiagnosticEvent("settings_card_scale_resynced", Status: $"from={previousScale},to={scale},revision={revision}"));
        }
        // Preserve a staged mini-size edit across unrelated commits.
        if (miniCardScalePercent is { } miniScale && miniScale != previousMiniScale)
        {
            SetMiniCardScaleQuietly(CardScale.Snap(miniScale));
            _diagnostics.Record(new DiagnosticEvent("settings_mini_card_scale_resynced", Status: $"from={previousMiniScale},to={miniScale},revision={revision}"));
        }
        return true;
    }

    /// <summary>Card size: a slider step snaps to the nearest 10 % step and shows the value. Nothing is applied
    /// until Save.</summary>
    private void OnCardScaleChanged(double value)
    {
        if (_syncingCardScale) return;
        var percent = CardScale.Snap(value);
        if (Math.Abs(_cardScale.Value - percent) > 0.001)
            SetCardScaleQuietly(percent);
        _cardScaleValue.Text = LocalizedText.Percentage(percent / 100.0);
    }

    private void SetCardScaleQuietly(int percent)
    {
        _syncingCardScale = true;
        try
        {
            _cardScale.Value = percent;
        }
        finally
        {
            _syncingCardScale = false;
        }
        _cardScaleValue.Text = LocalizedText.Percentage(percent / 100.0);
    }

    /// <summary>The mini slider follows the same snap-and-show rule as the full-size slider.</summary>
    private void OnMiniCardScaleChanged(double value)
    {
        if (_syncingMiniCardScale) return;
        var percent = CardScale.Snap(value);
        if (Math.Abs(_miniCardScale.Value - percent) > 0.001)
            SetMiniCardScaleQuietly(percent);
        _miniCardScaleValue.Text = LocalizedText.Percentage(percent / 100.0);
    }

    private void SetMiniCardScaleQuietly(int percent)
    {
        _syncingMiniCardScale = true;
        try
        {
            _miniCardScale.Value = percent;
        }
        finally
        {
            _syncingMiniCardScale = false;
        }
        _miniCardScaleValue.Text = LocalizedText.Percentage(percent / 100.0);
    }

    /// <summary>
    /// Fits the window to the working area of the screen it opened on.
    /// </summary>
    private void FitToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            _diagnostics.Record(new DiagnosticEvent("settings_window_fit", Status: "no_screen"));
            return;
        }
        FitToWorkingArea(screen.WorkingArea, screen.Scaling);
    }

    /// <summary>
    /// Caps the content height to the working area with a 16 DIP top and bottom margin, while keeping at least
    /// MinHeight. Moves the window into the working area and centers it vertically when capped. Rows scroll
    /// while the message area and buttons remain visible; later messages reduce the scroll area.
    /// </summary>
    internal void FitToWorkingArea(PixelRect workingArea, double scaling)
    {
        if (scaling <= 0 || workingArea.Width <= 0 || workingArea.Height <= 0)
        {
            _diagnostics.Record(new DiagnosticEvent("settings_window_fit", Status: $"invalid_area,{workingArea},scaling={scaling}"));
            return;
        }

        var natural = NaturalClientHeight();
        var frame = FrameSize;
        var chromeHeight = frame is { } frameHeight ? Math.Max(0, frameHeight.Height - ClientSize.Height) : 0;
        var chromeWidth = frame is { } frameWidth ? Math.Max(0, frameWidth.Width - ClientSize.Width) : 0;
        var areaHeight = workingArea.Height / scaling;
        var available = areaHeight - chromeHeight - 2 * WorkingAreaMargin;
        var clamped = natural > available;
        var height = Math.Max(MinHeight, Math.Min(natural, available));
        SizeToContent = SizeToContent.Manual;
        Height = height;
        // Apply the new height before moving, so the platform uses the updated frame size.
        UpdateLayout();

        var framePixelsWide = (int)Math.Ceiling((Width + chromeWidth) * scaling);
        var framePixelsHigh = (int)Math.Ceiling((height + chromeHeight) * scaling);
        var x = Math.Clamp(Position.X, workingArea.X, Math.Max(workingArea.X, workingArea.Right - framePixelsWide));
        var y = clamped
            ? workingArea.Y + Math.Max(0, (workingArea.Height - framePixelsHigh) / 2)
            : Math.Clamp(Position.Y, workingArea.Y, Math.Max(workingArea.Y, workingArea.Bottom - framePixelsHigh));
        var moved = x != Position.X || y != Position.Y;
        if (moved) Position = new PixelPoint(x, y);
        _diagnostics.Record(new DiagnosticEvent("settings_window_fit", Status:
            $"area={areaHeight:0},scaling={scaling:0.##},chrome={chromeHeight:0},natural={natural:0},height={height:0},clamped={clamped},moved={moved}"));
    }

    /// <summary>The client height the content wants with nothing clamping it: the rows, the message area and the
    /// buttons measured with unlimited height (ClientSize can't tell once the window has been capped).</summary>
    private double NaturalClientHeight()
    {
        if (Content is not Control root) return ClientSize.Height;
        var width = ClientSize.Width > 0 ? ClientSize.Width : Width;
        root.Measure(new Size(width, double.PositiveInfinity));
        var natural = root.DesiredSize.Height;
        root.InvalidateMeasure();
        return natural;
    }

    private void ApplyWindowTheme()
    {
        var dark = ActualThemeVariant != ThemeVariant.Light;
        Background = Brush(dark ? "#171D29" : "#F6F8FC");
        foreach (var text in this.GetVisualDescendants().OfType<TextBlock>())
        {
            if (ReferenceEquals(text, _error)) text.Foreground = Brush(dark ? "#FF8A65" : "#BB4932");
            else text.Foreground = Brush(dark ? "#F5F7FB" : "#172438");
        }
        foreach (var box in _providers.Values.Append(_startAtSignIn).Append(_alwaysOnTop))
            box.Foreground = Brush(dark ? "#F5F7FB" : "#172438");
    }

    private void SetProviderControlsEnabled(bool enabled)
    {
        foreach (var box in _providers.Values) box.IsEnabled = enabled;
    }

    private IReadOnlyCollection<string> SelectedProviderIds => ProviderOrder
        .Where(id => _providers[id].IsChecked == true).ToArray();

    private string SelectedThemeMode => (_themeMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";

    /// <summary>Opens the folder that holds the log in the system file manager, and logs the outcome (the path is the
    /// app's own log folder, not user content).</summary>
    private async Task OpenLogFolderAsync(string logFilePath)
    {
        var folder = Path.GetDirectoryName(logFilePath);
        try
        {
            var launched = folder is not null && Directory.Exists(folder) && Launcher is { } launcher &&
                await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)).ConfigureAwait(true);
            _diagnostics.Record(new DiagnosticEvent("settings_log_folder_opened", Status: $"launched={launched}"));
        }
        catch (Exception ex)
        {
            _diagnostics.Record(new DiagnosticEvent("settings_log_folder_open_failed", Status: ex.GetType().Name));
        }
    }

    private static Control LabeledControl(string label, Control control)
    {
        var panel = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 18 };
        // Allow longer translations to wrap instead of running under the control.
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(control, 1);
        panel.Children.Add(control);
        return panel;
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}

/// <summary>Tracks committed settings for an open window. Coordinator pushes and save results update the same
/// state, and older revisions never replace newer ones.</summary>
internal sealed class SettingsCommitTracker
{
    private AppSettings _committed;

    public SettingsCommitTracker(AppSettings committed)
    {
        _committed = committed.Clone();
        ExpectedRevision = committed.Revision;
        AppliedRetentionDays = committed.RetentionDays;
    }

    /// <summary>The revision the next Save must send as its expected revision.</summary>
    public long ExpectedRevision { get; private set; }

    /// <summary>The retention value last applied to history; a Save prunes only when it differs.</summary>
    public int AppliedRetentionDays { get; private set; }

    /// <summary>The committed provider selection, in the committed order.</summary>
    public IReadOnlyList<string> EnabledProviderIds => _committed.EnabledProviderIds.ToArray();

    /// <summary>The committed full-card size in percent.</summary>
    public int FullCardScalePercent => _committed.FullCardScalePercent;

    /// <summary>The committed mini-card size in percent.</summary>
    public int MiniCardScalePercent => _committed.MiniCardScalePercent;

    /// <summary>A copy of the committed settings for the window to overlay its staged controls on.</summary>
    public AppSettings CreateRequest() => _committed.Clone();

    /// <summary>Takes a coordinator commit. Returns false without changes when its revision is older than the
    /// held revision. Optional card sizes update independently when provided.</summary>
    public bool ApplyCommitted(long revision, LayoutSettings layout, IEnumerable<string> enabledProviderIds,
        int? fullCardScalePercent = null, int? miniCardScalePercent = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(enabledProviderIds);
        if (revision < ExpectedRevision) return false;
        ExpectedRevision = revision;
        _committed.Revision = revision;
        _committed.Layout = layout.Clone();
        _committed.EnabledProviderIds = enabledProviderIds.ToList();
        if (fullCardScalePercent is { } scale) _committed.FullCardScalePercent = scale;
        if (miniCardScalePercent is { } miniScale) _committed.MiniCardScalePercent = miniScale;
        return true;
    }

    /// <summary>
    /// Records this window's save result. Retention counts as applied when the save succeeded or failed for a
    /// reason other than history.
    /// The requested settings become the committed ones only when the result's revision is not older than
    /// a revision already pushed; the layout always stays the pushed one, since the window never edits it.
    /// </summary>
    public void RecordSaveResult(AppSettings requested, SettingsSaveResult result)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(result);
        if (result.CommittedRevision is not { } revision) return;
        if (revision >= ExpectedRevision)
        {
            var committed = requested.Clone();
            committed.Revision = revision;
            committed.Layout = _committed.Layout.Clone();
            _committed = committed;
            ExpectedRevision = revision;
        }
        if (result.Succeeded || result.IssueCode is not ("history_unavailable" or "history_prune_failed"))
            AppliedRetentionDays = requested.RetentionDays;
    }
}
