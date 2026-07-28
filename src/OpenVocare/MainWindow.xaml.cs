using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenVocare.Models;
using OpenVocare.Services;
using OpenVocare.Views;
using Forms = System.Windows.Forms;

namespace OpenVocare;

[SuppressMessage("Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable", Justification = "WPF owns the window lifecycle; services are disposed during shutdown.")]
public partial class MainWindow : Window
{
    private sealed record MouseButtonChoice(MouseShortcutButton Button, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record MicrophoneChoice(string? DeviceId, string Name, bool IsAvailable = true)
    {
        public override string ToString() => Name;
    }

    private sealed record ActivationModeChoice(ShortcutActivationMode Mode, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record RecordingLimitChoice(int Minutes, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record RewriteModeChoice(RewriteMode Mode, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record HistoryEntryView(Guid Id, string Text, string DisplayTime);

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly TranscriptHistoryStore _historyStore;
    private readonly SocketsHttpHandler _codexHttpHandler;
    private readonly DirectCodexTranscriptionClient _transcriptionClient;
    private readonly DirectCodexRewriteService _directRewriteService;
    private readonly DictationController _controller;
    private readonly OverlayWindow _overlay = new();
    private GlobalInputService? _input;
    private TrayHost? _tray;
    private bool _quitting;
    private bool _isCapturingShortcut;
    private bool _controlsReady;
    private bool _loadingCustomProfileEditor;
    private string? _editingCustomProfileId;
    private string? _pendingDeleteCustomProfileId;
    private HotkeyModifiers _draftShortcutModifiers;
    private string _draftShortcutKey = "Space";
    private readonly List<CustomRewriteProfile> _draftCustomProfiles = [];
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private readonly DispatcherTimer _rewriteSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(700)
    };

    public MainWindow(
        AppSettings settings,
        SettingsStore settingsStore,
        TranscriptHistoryStore historyStore)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _historyStore = historyStore;
        _codexHttpHandler = DirectCodexTranscriptionClient.CreatePooledHandler();
        _transcriptionClient = new DirectCodexTranscriptionClient(_codexHttpHandler);
        _directRewriteService = new DirectCodexRewriteService(
            _transcriptionClient,
            _codexHttpHandler);
        _controller = new DictationController(
            new DirectCodexTranscriptionBridge(
                new WindowsAudioRecorder(
                    () => _settings.Microphone.DeviceId,
                    () => _settings.Microphone.DeviceName),
                _transcriptionClient),
            new TextInjectionService(),
            _directRewriteService,
            () => _settings.Rewrite,
            () => _settings.Clipboard.RestoreAfterPaste,
            () => TimeSpan.FromMinutes(
                _settings.Recording.MaximumDurationMinutes));
        _controller.StatusChanged += Controller_StatusChanged;
        _controller.TranscriptDelivered += Controller_TranscriptDelivered;
        InitializeComponent();
        _rewriteSaveTimer.Tick += RewriteSaveTimer_Tick;
        Icon = (ImageSource)Resources["AppIcon"];
        SourceInitialized += MainWindow_SourceInitialized;
        PopulateControls();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        Native.ApplyWindowFrame(handle);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppLog.Write("Settings window loaded.");
        _input = new GlobalInputService(Dispatcher, () => _controller.CanCancel);
        _input.HoldStarted += async (_, _) => await RunControllerActionAsync(_controller.StartHoldAsync);
        _input.HoldReleased += async (_, _) => await RunControllerActionAsync(_controller.StopHoldAsync);
        _input.CancelRequested += async (_, _) => await RunControllerActionAsync(_controller.CancelAsync);
        _input.RegistrationFailed += (_, message) => ShowStatus(message, true);
        _input.Start(this, _settings.Shortcut);

        _tray = new TrayHost(ShowSettings, () => _ = RunControllerActionAsync(_controller.ToggleAsync), () => _ = QuitAsync());
        await RefreshConnectionStatusAsync();
        if (!string.IsNullOrWhiteSpace(_settingsStore.LastLoadWarning))
        {
            ShowStatus(_settingsStore.LastLoadWarning, true);
        }
        await LoadMicrophonesAsync();
        await RefreshHistoryAsync();
        if (!string.IsNullOrWhiteSpace(_historyStore.LastLoadWarning))
        {
            ShowStatus(_historyStore.LastLoadWarning, true);
        }
        try
        {
            SetLaunchAtLogin(_settings.LaunchAtLogin);
        }
        catch (Exception exception)
        {
            AppLog.Write("Start-at-sign-in migration failed.", exception);
        }
        _ = _transcriptionClient.WarmUpAsync();
    }

    private void PopulateControls()
    {
        MouseButtonBox.ItemsSource = new[]
        {
            new MouseButtonChoice(MouseShortcutButton.None, "None"),
            new MouseButtonChoice(MouseShortcutButton.XButton1, "Back side button"),
            new MouseButtonChoice(MouseShortcutButton.XButton2, "Forward side button")
        };
        ActivationModeChoice[] activationModes =
        [
            new(ShortcutActivationMode.Hold, "Hold to talk"),
            new(ShortcutActivationMode.Toggle, "Toggle recording")
        ];
        KeyboardActivationModeBox.ItemsSource = activationModes;
        KeyboardActivationModeBox.SelectedValue = _settings.Shortcut.KeyboardActivationMode;
        MouseActivationModeBox.ItemsSource = activationModes;
        MouseActivationModeBox.SelectedValue = _settings.Shortcut.MouseActivationMode;
        UpdateActivationModeDescriptions();
        RecordingLimitBox.ItemsSource = new[]
        {
            new RecordingLimitChoice(1, "1 minute"),
            new RecordingLimitChoice(2, "2 minutes"),
            new RecordingLimitChoice(5, "5 minutes"),
            new RecordingLimitChoice(10, "10 minutes"),
            new RecordingLimitChoice(30, "30 minutes")
        };
        RecordingLimitBox.SelectedValue =
            _settings.Recording.MaximumDurationMinutes;
        RewriteModeBox.ItemsSource = new[]
        {
            new RewriteModeChoice(RewriteMode.Verbatim, "No rewrite"),
            new RewriteModeChoice(RewriteMode.Minimal, "Minimal cleanup"),
            new RewriteModeChoice(RewriteMode.Professional, "Professional"),
            new RewriteModeChoice(RewriteMode.Ramble, "Ramble to clear thoughts"),
            new RewriteModeChoice(RewriteMode.Translate, "Translate"),
            new RewriteModeChoice(RewriteMode.Custom, "Custom profile")
        };
        _draftCustomProfiles.Clear();
        _draftCustomProfiles.AddRange(
            _settings.Rewrite.CustomProfiles.Select(CloneCustomProfile));
        RefreshCustomProfileList(_settings.Rewrite.ActiveCustomProfileId);
        RewriteModeBox.SelectedValue = _settings.Rewrite.Mode;
        TranslationLanguageBox.Text = _settings.Rewrite.TranslationLanguage;
        KeyboardShortcutEnabled.IsChecked = _settings.Shortcut.KeyboardShortcutEnabled;
        _draftShortcutModifiers = _settings.Shortcut.Modifiers;
        _draftShortcutKey = _settings.Shortcut.Key;
        MouseButtonBox.SelectedValue = _settings.Shortcut.MouseButton;
        ConsumeMouseButton.IsChecked = _settings.Shortcut.ConsumeMouseButton;
        UpdateMouseShortcutAvailability();
        MicrophoneBox.ItemsSource = new[]
        {
            new MicrophoneChoice(
                _settings.Microphone.DeviceId,
                _settings.Microphone.DeviceName ?? "Loading microphones...")
        };
        MicrophoneBox.SelectedIndex = 0;
        MicrophoneBox.IsEnabled = false;
        ErrorNotificationsEnabled.IsChecked =
            _settings.Privacy.ErrorNotificationsEnabled;
        CompletionNotificationsEnabled.IsChecked =
            _settings.Privacy.CompletionNotificationsEnabled;
        RestorePreviousClipboard.IsChecked = _settings.Clipboard.RestoreAfterPaste;
        HistoryEnabled.IsChecked = _settings.History.Enabled;
        LaunchAtLogin.IsChecked = _settings.LaunchAtLogin;
        CloseButtonQuits.IsChecked = _settings.CloseButtonQuits;
        UpdateShortcutEditor();
        UpdateShortcutSummary(_settings.Shortcut);
        UpdatePrivacySummary();
        AboutVersionText.Text =
            $"Version {ProductIdentity.Version} · Unofficial ChatGPT desktop integration";
        _controlsReady = true;
        UpdateRewriteModeUi();
        RewriteSavedText.Text = _settings.Rewrite.Mode == RewriteMode.Verbatim
            ? "No rewrite is active. Changes are saved automatically."
            : $"{FormatRewriteMode(_settings.Rewrite.Mode)} is active. Changes are saved automatically.";
    }

    private async void SettingsControl_Changed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, KeyboardActivationModeBox)
            || ReferenceEquals(sender, MouseActivationModeBox))
        {
            UpdateActivationModeDescriptions();
        }
        if (ReferenceEquals(sender, MouseButtonBox))
        {
            UpdateMouseShortcutAvailability();
        }
        if (!_controlsReady
            || (ReferenceEquals(sender, MicrophoneBox) && !MicrophoneBox.IsEnabled))
        {
            return;
        }
        await ApplyGeneralSettingsAsync();
    }

    private async Task ApplyGeneralSettingsAsync()
    {
        await _settingsSaveGate.WaitAsync();
        ShortcutSettings previousShortcut = CloneShortcut(_settings.Shortcut);
        bool previousLaunchAtLogin = _settings.LaunchAtLogin;
        bool shortcutReconfigured = false;
        bool launchSettingChanged = false;

        try
        {
            ShortcutSettings shortcut = ReadShortcut();
            string? validation = GlobalInputService.GetShortcutValidationError(shortcut);
            ShortcutValidationText.Text = validation ?? string.Empty;
            ShortcutValidationText.Visibility =
                validation is null ? Visibility.Collapsed : Visibility.Visible;
            if (validation is not null)
            {
                SetAutoSaveState("Change not applied — choose a valid shortcut.", true);
                return;
            }

            AppSettings candidate = CloneSettings(_settings);
            candidate.Shortcut = shortcut;
            if (MicrophoneBox.SelectedItem is MicrophoneChoice microphone)
            {
                candidate.Microphone.DeviceId = microphone.DeviceId;
                candidate.Microphone.DeviceName = microphone.DeviceId is null
                    ? null
                    : microphone.IsAvailable
                        ? microphone.Name
                        : _settings.Microphone.DeviceName;
            }
            candidate.Privacy.ErrorNotificationsEnabled =
                ErrorNotificationsEnabled.IsChecked == true;
            candidate.Privacy.CompletionNotificationsEnabled =
                CompletionNotificationsEnabled.IsChecked == true;
            candidate.Clipboard.RestoreAfterPaste =
                RestorePreviousClipboard.IsChecked == true;
            candidate.Recording.MaximumDurationMinutes =
                RecordingLimitBox.SelectedValue is int recordingMinutes
                    ? recordingMinutes
                    : 2;
            candidate.LaunchAtLogin = LaunchAtLogin.IsChecked == true;
            candidate.CloseButtonQuits = CloseButtonQuits.IsChecked == true;

            if (!ShortcutsEqual(previousShortcut, shortcut))
            {
                if (_input is not null && !_input.Reconfigure(shortcut))
                {
                    SetAutoSaveState("Shortcut could not be activated.", true);
                    RestoreGeneralControls();
                    return;
                }
                shortcutReconfigured = true;
            }

            launchSettingChanged =
                candidate.LaunchAtLogin != previousLaunchAtLogin;
            if (launchSettingChanged)
            {
                SetLaunchAtLogin(candidate.LaunchAtLogin);
            }

            SetAutoSaveState("Saving…");
            await _settingsStore.SaveAsync(candidate);
            CommitSettings(candidate);
            UpdateShortcutSummary(shortcut);
            UpdateShortcutEditor();
            UpdatePrivacySummary();
            MicrophoneStatusText.Text =
                _controller.CanCancel
                    ? "Saved. This microphone will be used after the current dictation."
                    : candidate.Microphone.DeviceId is null
                        ? "Uses whichever recording device Windows marks as default."
                        : "Saved. This microphone is ready for the next dictation.";
            SetAutoSaveState("Changes are saved automatically.");
        }
        catch (Exception exception)
        {
            if (shortcutReconfigured)
            {
                _input?.Reconfigure(previousShortcut);
            }
            if (launchSettingChanged)
            {
                try { SetLaunchAtLogin(previousLaunchAtLogin); }
                catch (Exception rollbackException)
                {
                    AppLog.Write("Start-at-sign-in rollback failed.", rollbackException);
                }
            }
            RestoreGeneralControls();
            AppLog.Write("Automatic settings save failed.", exception);
            SetAutoSaveState($"Could not save: {exception.Message}", true);
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private async void RefreshMicrophones_Click(object sender, RoutedEventArgs e) =>
        await LoadMicrophonesAsync();

    private async Task LoadMicrophonesAsync()
    {
        string? selectedId = (MicrophoneBox.SelectedItem as MicrophoneChoice)?.DeviceId
            ?? _settings.Microphone.DeviceId;
        MicrophoneBox.IsEnabled = false;
        MicrophoneStatusText.Text = "Loading microphones...";

        try
        {
            IReadOnlyList<AudioInputDevice> devices =
                await AudioInputDeviceService.GetDevicesAsync();
            List<MicrophoneChoice> choices =
            [
                new(null, "System default")
            ];
            choices.AddRange(devices.Select(device =>
                new MicrophoneChoice(device.Id, device.Name)));

            if (!string.IsNullOrWhiteSpace(selectedId)
                && choices.All(choice =>
                    !string.Equals(
                        choice.DeviceId,
                        selectedId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                choices.Add(new MicrophoneChoice(
                    selectedId,
                    $"{_settings.Microphone.DeviceName ?? "Selected microphone"} (not connected)",
                    false));
            }

            MicrophoneBox.ItemsSource = choices;
            MicrophoneBox.SelectedItem = choices.First(choice =>
                string.Equals(
                    choice.DeviceId,
                    selectedId,
                    StringComparison.OrdinalIgnoreCase));
            MicrophoneBox.IsEnabled = true;
            MicrophoneStatusText.Text = selectedId is null
                ? "Uses whichever recording device Windows marks as default."
                : (MicrophoneBox.SelectedItem as MicrophoneChoice)?.IsAvailable == true
                    ? "This microphone is ready for dictation."
                    : "This microphone is not connected. Select an available device.";
        }
        catch (Exception)
        {
            MicrophoneStatusText.Text =
                "Microphones could not be loaded. Windows microphone access may be unavailable.";
        }
    }

    private ShortcutSettings ReadShortcut()
    {
        return new ShortcutSettings
        {
            KeyboardShortcutEnabled = KeyboardShortcutEnabled.IsChecked == true,
            Modifiers = _draftShortcutModifiers,
            Key = _draftShortcutKey,
            MouseButton = MouseButtonBox.SelectedValue is MouseShortcutButton button ? button : MouseShortcutButton.None,
            ConsumeMouseButton = ConsumeMouseButton.IsChecked == true,
            KeyboardActivationMode = KeyboardActivationModeBox.SelectedValue is ShortcutActivationMode keyboardMode
                ? keyboardMode : ShortcutActivationMode.Hold,
            MouseActivationMode = MouseActivationModeBox.SelectedValue is ShortcutActivationMode mouseMode
                ? mouseMode : ShortcutActivationMode.Hold
        };
    }

    private void ChangeShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_isCapturingShortcut)
        {
            CancelShortcutCapture();
            return;
        }

        _isCapturingShortcut = true;
        ChangeShortcutButton.Content = "Cancel";
        ShortcutCaptureHintText.Text = "Press your new shortcut now. Esc cancels.";
        ShortcutValidationText.Visibility = Visibility.Collapsed;
        ShortcutKeycapsPanel.Children.Clear();
        ShortcutKeycapsPanel.Children.Add(new TextBlock
        {
            Text = "Waiting for shortcut…",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(105, 115, 134)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        });
        ChangeShortcutButton.Focus();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isCapturingShortcut)
        {
            return;
        }

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelShortcutCapture();
            return;
        }

        if (IsModifierKey(key))
        {
            return;
        }

        _draftShortcutModifiers = ToHotkeyModifiers(Keyboard.Modifiers);
        _draftShortcutKey = key.ToString();
        _isCapturingShortcut = false;
        ChangeShortcutButton.Content = "Change";
        ShortcutCaptureHintText.Text = "Shortcut captured and applied.";
        UpdateShortcutEditor();

        string? validation = GlobalInputService.GetShortcutValidationError(ReadShortcut());
        ShortcutValidationText.Text = validation ?? string.Empty;
        ShortcutValidationText.Visibility =
            validation is null ? Visibility.Collapsed : Visibility.Visible;
        if (validation is null)
        {
            _ = ApplyGeneralSettingsAsync();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isCapturingShortcut)
        {
            CancelShortcutCapture();
        }
    }

    private void KeyboardShortcutEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (ShortcutEditControls is null)
        {
            return;
        }

        ShortcutEditControls.IsEnabled = KeyboardShortcutEnabled.IsChecked == true;
        if (KeyboardShortcutEnabled.IsChecked != true && _isCapturingShortcut)
        {
            CancelShortcutCapture();
        }
        if (_controlsReady)
        {
            _ = ApplyGeneralSettingsAsync();
        }
    }

    private void CancelShortcutCapture()
    {
        _isCapturingShortcut = false;
        ChangeShortcutButton.Content = "Change";
        ShortcutCaptureHintText.Text = "Shortcut unchanged.";
        UpdateShortcutEditor();
    }

    private void UpdateShortcutEditor()
    {
        ShortcutEditControls.IsEnabled = KeyboardShortcutEnabled.IsChecked == true;
        ShortcutKeycapsPanel.Children.Clear();

        List<string> parts = GetShortcutParts(_draftShortcutModifiers, _draftShortcutKey);
        for (int index = 0; index < parts.Count; index++)
        {
            if (index > 0)
            {
                ShortcutKeycapsPanel.Children.Add(new TextBlock
                {
                    Text = "+",
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(122, 131, 149)),
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 5, 0)
                });
            }

            Border keycap = new()
            {
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(217, 223, 232)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Child = new TextBlock
                {
                    Text = parts[index],
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            ShortcutKeycapsPanel.Children.Add(keycap);
        }
    }

    private void UpdateShortcutSummary(ShortcutSettings shortcut)
    {
        bool keyboardEnabled = shortcut.KeyboardShortcutEnabled;
        bool mouseEnabled = shortcut.MouseButton != MouseShortcutButton.None;

        KeyboardShortcutSummaryRow.Visibility =
            keyboardEnabled ? Visibility.Visible : Visibility.Collapsed;
        MouseShortcutSummaryRow.Visibility =
            mouseEnabled ? Visibility.Visible : Visibility.Collapsed;
        KeyboardShortcutSummaryText.Text = FormatKeyboardShortcut(shortcut);
        MouseShortcutSummaryText.Text = FormatMouseShortcut(shortcut.MouseButton);
        ShortcutSummaryDivider.Visibility =
            keyboardEnabled && mouseEnabled ? Visibility.Visible : Visibility.Collapsed;
        KeyboardActivationSummaryText.Text = FormatActivationMode(shortcut.KeyboardActivationMode);
        MouseActivationSummaryText.Text = FormatActivationMode(shortcut.MouseActivationMode);
    }

    private void UpdateActivationModeDescriptions()
    {
        ShortcutActivationMode keyboardMode =
            KeyboardActivationModeBox.SelectedValue is ShortcutActivationMode selectedKeyboardMode
                ? selectedKeyboardMode
                : ShortcutActivationMode.Hold;
        ShortcutActivationMode mouseMode =
            MouseActivationModeBox.SelectedValue is ShortcutActivationMode selectedMouseMode
                ? selectedMouseMode
                : ShortcutActivationMode.Hold;

        KeyboardShortcutDescriptionText.Text =
            GetKeyboardShortcutDescription(keyboardMode);
        KeyboardActivationDescriptionText.Text =
            GetKeyboardActivationDescription(keyboardMode);
        MouseActivationDescriptionText.Text =
            GetMouseActivationDescription(mouseMode);
    }

    internal static string GetKeyboardShortcutDescription(ShortcutActivationMode mode) =>
        mode == ShortcutActivationMode.Toggle
            ? "Press once to start, then focus the destination and press again to transcribe and paste."
            : "Hold while speaking, then focus the destination and release to transcribe and paste.";

    internal static string GetKeyboardActivationDescription(ShortcutActivationMode mode) =>
        mode == ShortcutActivationMode.Toggle
            ? "Press once to start recording and again to stop."
            : "Keep the shortcut held while speaking.";

    internal static string GetMouseActivationDescription(ShortcutActivationMode mode) =>
        mode == ShortcutActivationMode.Toggle
            ? "Click once to start, then focus the destination and click again to stop."
            : "Keep the side button held, then focus the destination and release.";

    private void UpdateMouseShortcutAvailability()
    {
        bool available =
            MouseButtonBox.SelectedValue is MouseShortcutButton button
            && button != MouseShortcutButton.None;
        MouseActivationRow.IsEnabled = available;
        MouseActivationRow.Opacity = available ? 1 : 0.55;
        BlockBrowserNavigationRow.IsEnabled = available;
        BlockBrowserNavigationRow.Opacity = available ? 1 : 0.55;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;

    private static HotkeyModifiers ToHotkeyModifiers(ModifierKeys modifiers)
    {
        HotkeyModifiers result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Win;
        return result;
    }

    private async void CheckConnection_Click(object sender, RoutedEventArgs e) =>
        await RefreshConnectionStatusAsync();

    private async Task RefreshConnectionStatusAsync()
    {
        ConnectionReadinessTitle.Text = "Checking";
        ConnectionReadinessTitle.Foreground =
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(105, 115, 134));
        ConnectionReadinessDescription.Text = "Checking your ChatGPT sign-in...";
        CheckReadinessButton.IsEnabled = false;

        DictationBridgeResult localResult = _controller.Probe();
        if (!localResult.IsSuccess)
        {
            ApplyConnectionResult(localResult);
            return;
        }

        DictationBridgeResult result = await _transcriptionClient.CheckReadinessAsync();
        ApplyConnectionResult(result);
    }

    private void ApplyConnectionResult(DictationBridgeResult result)
    {
        ConnectionStatusText.Text = result.IsSuccess
            ? "Signed in; service reachable."
            : result.Message;
        ConnectionStatusText.Foreground = result.IsSuccess
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(105, 115, 134))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 83, 9));
        ConnectionReadinessTitle.Text = result.IsSuccess ? "Signed in" : "Needs attention";
        ConnectionReadinessTitle.Foreground = new SolidColorBrush(result.IsSuccess
            ? System.Windows.Media.Color.FromRgb(76, 79, 105)
            : System.Windows.Media.Color.FromRgb(146, 64, 14));
        ConnectionReadinessDescription.Text = result.Message;
        CheckReadinessButton.Content = result.IsSuccess ? "Recheck" : "Retry";
        CheckReadinessButton.IsEnabled = true;
        if (_controller.State == DictationState.Ready)
        {
            ShowStatus(
                result.IsSuccess ? "Ready" : "ChatGPT connection needs attention.",
                !result.IsSuccess);
        }
    }

    private async Task RunControllerActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception)
        {
            AppLog.Write(
                $"Dictation operation failed ({exception.GetType().Name}).");
            ShowStatus($"Dictation failed: {exception.Message}", true);
        }
    }

    private void Controller_StatusChanged(object? sender, DictationStatus status)
    {
        _input?.SetCancellationEnabled(status.State != DictationState.Ready);
        if (status.State == DictationState.Ready)
        {
            _input?.ResetActivationState();
        }
        switch (status.State)
        {
            case DictationState.Listening:
                _overlay.SetStatus("Listening");
                _tray?.SetStatus("listening");
                break;
            case DictationState.Retrieving:
                _overlay.SetStatus("Retrieving transcript");
                _tray?.SetStatus("retrieving transcript");
                break;
            default:
                _overlay.HideOverlay();
                _tray?.SetStatus("ready");
                break;
        }
        ShowStatus(status.Message, status.IsError);
        if (status.IsError
            && status.Message.Contains("login expired", StringComparison.OrdinalIgnoreCase))
        {
            ApplyConnectionResult(new DictationBridgeResult(false, status.Message));
        }
        bool isCompletedPaste =
            !status.IsError
            && status.Message.Contains("pasted", StringComparison.OrdinalIgnoreCase);
        bool shouldNotify =
            status.IsError
                ? _settings.Privacy.ErrorNotificationsEnabled
                : isCompletedPaste
                    && _settings.Privacy.CompletionNotificationsEnabled;
        if (status.State == DictationState.Ready && shouldNotify)
        {
            _tray?.Notify(
                status.IsError ? "OpenVocare needs attention" : "OpenVocare dictation complete",
                status.Message,
                status.IsError ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        }
    }

    private async void Controller_TranscriptDelivered(object? sender, TranscriptDelivered delivered)
    {
        if (!_settings.History.Enabled)
        {
            return;
        }
        try
        {
            await _historyStore.AddAsync(delivered.Text);
            await RefreshHistoryAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write("Transcript history could not be saved.", exception);
        }
    }

    private void SettingsTab_Click(object sender, RoutedEventArgs e) => ShowTab("settings");

    private async void HistoryTab_Click(object sender, RoutedEventArgs e)
    {
        ShowTab("history");
        await RefreshHistoryAsync();
    }

    private void RewriteTab_Click(object sender, RoutedEventArgs e) => ShowTab("rewrite");

    private void RewriteModeCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton card
            || !Enum.TryParse(card.Tag as string, out RewriteMode mode))
        {
            return;
        }

        if (mode == RewriteMode.Custom && _draftCustomProfiles.Count == 0)
        {
            SetRewriteModeDraft(RewriteMode.Custom);
            NewCustomProfile_Click(sender, e);
            SetRewriteSaveState("Create a custom profile to activate this mode.");
            return;
        }
        RewriteModeBox.SelectedValue = mode;
    }

    private void ShowTab(string tab)
    {
        bool settings = tab == "settings";
        bool history = tab == "history";
        PageTitle.Text = settings ? "Settings" : history ? "History" : "Rewrite";
        PageSubtitle.Text = settings
            ? "Configure dictation, shortcuts, and app behavior."
            : history
                ? "Review and manage your saved transcripts."
                : "Choose how transcripts are transformed before they are pasted.";
        SettingsView.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        SettingsFooter.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        HistoryView.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        RewriteView.Visibility = tab == "rewrite" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabButton.Style = (Style)FindResource(settings ? "ActiveTabButton" : "TabButton");
        HistoryTabButton.Style = (Style)FindResource(history ? "ActiveTabButton" : "TabButton");
        RewriteTabButton.Style = (Style)FindResource(tab == "rewrite" ? "ActiveTabButton" : "TabButton");
    }

    private async void RewriteMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_controlsReady)
        {
            UpdateRewriteModeUi();
            await ApplyRewriteSettingsAsync();
        }
    }

    private void UpdateRewriteModeUi()
    {
        RewriteMode mode = RewriteModeBox.SelectedValue is RewriteMode selected
            ? selected
            : RewriteMode.Verbatim;
        TranslationLanguageRow.Visibility =
            mode == RewriteMode.Translate ? Visibility.Visible : Visibility.Collapsed;
        CustomProfileSection.Visibility =
            mode == RewriteMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        RewriteModeTitle.Text = FormatRewriteMode(mode);
        RewriteModeDescription.Text = mode switch
        {
            RewriteMode.Minimal => "Removes filler and fixes obvious grammar while staying close to what you said.",
            RewriteMode.Professional => "Produces concise professional writing without changing facts or technical meaning.",
            RewriteMode.Ramble => "Organizes a spoken brainstorm into structured, readable thoughts.",
            RewriteMode.Translate => "Faithfully translates the transcript into your selected language.",
            RewriteMode.Custom => "Applies one of your locally saved rewrite instructions with meaning protection always enabled.",
            _ => "Pastes the transcription exactly as ChatGPT returns it, with no added latency."
        };
        RewritePipelineBadge.Visibility =
            mode == RewriteMode.Verbatim ? Visibility.Collapsed : Visibility.Visible;
        SetRewriteModeCardState(mode);
    }

    private void SetRewriteModeCardState(RewriteMode mode)
    {
        RewriteVerbatimCard.IsChecked = mode == RewriteMode.Verbatim;
        RewriteMinimalCard.IsChecked = mode == RewriteMode.Minimal;
        RewriteProfessionalCard.IsChecked = mode == RewriteMode.Professional;
        RewriteRambleCard.IsChecked = mode == RewriteMode.Ramble;
        RewriteTranslateCard.IsChecked = mode == RewriteMode.Translate;
        RewriteCustomCard.IsChecked = mode == RewriteMode.Custom;
    }

    private void SetRewriteModeDraft(RewriteMode mode)
    {
        bool controlsReady = _controlsReady;
        _controlsReady = false;
        try
        {
            RewriteModeBox.SelectedValue = mode;
            UpdateRewriteModeUi();
        }
        finally
        {
            _controlsReady = controlsReady;
        }
    }

    private void RewriteText_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_controlsReady || _loadingCustomProfileEditor)
        {
            return;
        }
        SetRewriteSaveState("Saving…");
        _rewriteSaveTimer.Stop();
        _rewriteSaveTimer.Start();
    }

    private async void RewriteText_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (!_controlsReady || _loadingCustomProfileEditor)
        {
            return;
        }
        _rewriteSaveTimer.Stop();
        await ApplyRewriteSettingsAsync();
    }

    private async void RewriteSaveTimer_Tick(object? sender, EventArgs e)
    {
        _rewriteSaveTimer.Stop();
        await ApplyRewriteSettingsAsync();
    }

    private async Task ApplyRewriteSettingsAsync()
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            RewriteMode mode = RewriteModeBox.SelectedValue is RewriteMode selectedMode
                ? selectedMode
                : RewriteMode.Verbatim;
            string? selectedProfileId = CustomProfileBox.SelectedValue as string;
            string? draftValidation = ValidateDraftProfiles();
            if (draftValidation is not null)
            {
                ShowCustomProfileValidation(draftValidation);
                SetRewriteSaveState("Change not saved — complete the custom profile.", true);
                return;
            }
            if (mode == RewriteMode.Custom)
            {
                string? validation = ValidateCustomProfile(selectedProfileId);
                if (validation is not null)
                {
                    ShowCustomProfileValidation(validation);
                    SetRewriteSaveState("Change not saved — complete the custom profile.", true);
                    return;
                }
            }
            CustomProfileValidationText.Visibility = Visibility.Collapsed;

            AppSettings candidate = CloneSettings(_settings);
            candidate.Rewrite.Mode = mode;
            candidate.Rewrite.CustomProfiles =
                _draftCustomProfiles.Select(CloneCustomProfile).ToList();
            candidate.Rewrite.ActiveCustomProfileId = selectedProfileId;
            candidate.Rewrite.TranslationLanguage =
                string.IsNullOrWhiteSpace(TranslationLanguageBox.Text)
                    ? "English"
                    : TranslationLanguageBox.Text.Trim();

            SetRewriteSaveState("Saving…");
            await _settingsStore.SaveAsync(candidate);
            CommitSettings(candidate);
            CustomProfileBox.Items.Refresh();
            UpdateRewriteModeUi();
            RewriteSavedText.Text = _settings.Rewrite.Mode == RewriteMode.Verbatim
                ? "No rewrite is active. Changes are saved automatically."
                : $"{FormatRewriteMode(_settings.Rewrite.Mode)} is active. Changes are saved automatically.";
        }
        catch (Exception exception)
        {
            RestoreRewriteControls();
            AppLog.Write("Automatic rewrite settings save failed.", exception);
            SetRewriteSaveState($"Could not save: {exception.Message}", true);
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private async void CustomProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCustomProfileEditor)
        {
            return;
        }
        string? selectedId = CustomProfileBox.SelectedValue as string;
        CustomProfileValidationText.Visibility = Visibility.Collapsed;
        if (_controlsReady)
        {
            await ApplyRewriteSettingsAsync();
        }
    }

    private void NewCustomProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_draftCustomProfiles.Count >= SettingsStore.MaximumCustomProfiles)
        {
            ShowCustomProfileValidation(
                $"You can save up to {SettingsStore.MaximumCustomProfiles} custom profiles.");
            return;
        }
        _editingCustomProfileId = null;
        InlineCustomProfileEditorTitle.Text = "New custom profile";
        InlineCustomProfileNameBox.Text = CreateUniqueProfileName("New profile");
        InlineCustomProfileInstructionBox.Clear();
        InlineCustomProfileInstructionHint.Visibility = Visibility.Visible;
        CustomProfileValidationText.Visibility = Visibility.Collapsed;
        InlineCustomProfileEditor.Visibility = Visibility.Visible;
        InlineCustomProfileNameBox.Focus();
        InlineCustomProfileNameBox.SelectAll();
    }

    private void EditCustomProfile_Click(object sender, RoutedEventArgs e)
    {
        CustomRewriteProfile? source = FindDraftCustomProfile(
            CustomProfileBox.SelectedValue as string);
        if (source is null)
        {
            ShowCustomProfileValidation("Choose a profile to edit.");
            return;
        }
        _editingCustomProfileId = source.Id;
        InlineCustomProfileEditorTitle.Text = "Edit custom profile";
        InlineCustomProfileNameBox.Text = source.Name;
        InlineCustomProfileInstructionBox.Text = source.Instruction;
        InlineCustomProfileInstructionHint.Visibility = Visibility.Collapsed;
        CustomProfileValidationText.Visibility = Visibility.Collapsed;
        InlineCustomProfileEditor.Visibility = Visibility.Visible;
        InlineCustomProfileNameBox.Focus();
        InlineCustomProfileNameBox.SelectAll();
    }

    private void CancelInlineCustomProfile_Click(object sender, RoutedEventArgs e)
    {
        _editingCustomProfileId = null;
        InlineCustomProfileEditor.Visibility = Visibility.Collapsed;
        CustomProfileValidationText.Visibility = Visibility.Collapsed;
    }

    private void InlineCustomProfileInstruction_Changed(object sender, TextChangedEventArgs e)
    {
        InlineCustomProfileInstructionHint.Visibility =
            string.IsNullOrEmpty(InlineCustomProfileInstructionBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void SaveInlineCustomProfile_Click(object sender, RoutedEventArgs e)
    {
        string name = InlineCustomProfileNameBox.Text.Trim();
        string instruction = InlineCustomProfileInstructionBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowCustomProfileValidation("Give this profile a name.");
            InlineCustomProfileNameBox.Focus();
            return;
        }
        if (instruction.Length == 0)
        {
            ShowCustomProfileValidation("Describe how this profile should rewrite your transcript.");
            InlineCustomProfileInstructionBox.Focus();
            return;
        }
        if (name.Length > SettingsStore.MaximumCustomProfileNameLength)
        {
            ShowCustomProfileValidation($"Profile names can be up to {SettingsStore.MaximumCustomProfileNameLength} characters.");
            return;
        }
        if (instruction.Length > SettingsStore.MaximumCustomInstructionLength)
        {
            ShowCustomProfileValidation($"Profile instructions can be up to {SettingsStore.MaximumCustomInstructionLength} characters.");
            return;
        }

        CustomRewriteProfile profile = new()
        {
            Id = _editingCustomProfileId ?? Guid.NewGuid().ToString("N"),
            Name = name,
            Instruction = instruction
        };
        int index = _draftCustomProfiles.FindIndex(item => item.Id == profile.Id);
        if (index >= 0)
        {
            _draftCustomProfiles[index] = profile;
        }
        else
        {
            _draftCustomProfiles.Add(profile);
        }

        _editingCustomProfileId = null;
        InlineCustomProfileEditor.Visibility = Visibility.Collapsed;
        SetRewriteModeDraft(RewriteMode.Custom);
        RefreshCustomProfileList(profile.Id);
        await ApplyRewriteSettingsAsync();
    }

    private async void CustomProfileCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton card || card.Tag is not string profileId)
        {
            return;
        }
        _loadingCustomProfileEditor = true;
        try
        {
            CustomProfileBox.SelectedValue = profileId;
        }
        finally
        {
            _loadingCustomProfileEditor = false;
        }
        SetCustomProfileCardState(profileId);
        CustomProfileValidationText.Visibility = Visibility.Collapsed;
        if (_controlsReady)
        {
            await ApplyRewriteSettingsAsync();
        }
    }

    private void DeleteCustomProfile_Click(object sender, RoutedEventArgs e)
    {
        string? selectedId = CustomProfileBox.SelectedValue as string;
        int index = _draftCustomProfiles.FindIndex(
            profile => profile.Id == selectedId);
        if (index < 0)
        {
            ShowCustomProfileValidation("Choose a profile to delete.");
            return;
        }
        _pendingDeleteCustomProfileId = selectedId;
        InlineDeleteConfirmationText.Text =
            $"Delete “{_draftCustomProfiles[index].Name}”? This cannot be undone.";
        InlineDeleteConfirmation.Visibility = Visibility.Visible;
    }

    private void CancelDeleteCustomProfile_Click(object sender, RoutedEventArgs e)
    {
        _pendingDeleteCustomProfileId = null;
        InlineDeleteConfirmation.Visibility = Visibility.Collapsed;
    }

    private async void ConfirmDeleteCustomProfile_Click(object sender, RoutedEventArgs e)
    {
        string? selectedId = _pendingDeleteCustomProfileId;
        int index = _draftCustomProfiles.FindIndex(profile => profile.Id == selectedId);
        if (index < 0)
        {
            CancelDeleteCustomProfile_Click(sender, e);
            return;
        }
        _draftCustomProfiles.RemoveAt(index);
        _pendingDeleteCustomProfileId = null;
        InlineDeleteConfirmation.Visibility = Visibility.Collapsed;
        string? nextId = _draftCustomProfiles.Count == 0
            ? null
            : _draftCustomProfiles[Math.Min(index, _draftCustomProfiles.Count - 1)].Id;
        RefreshCustomProfileList(nextId);
        if (nextId is null)
        {
            SetRewriteModeDraft(RewriteMode.Verbatim);
        }
        await ApplyRewriteSettingsAsync();
    }

    private void RefreshCustomProfileList(string? selectedId)
    {
        _loadingCustomProfileEditor = true;
        try
        {
            CustomProfileBox.ItemsSource = null;
            CustomProfileBox.ItemsSource = _draftCustomProfiles;
            bool hasProfiles = _draftCustomProfiles.Count > 0;
            CustomProfileBox.Visibility = Visibility.Collapsed;
            CustomProfileEmptyPlaceholder.Visibility =
                hasProfiles ? Visibility.Collapsed : Visibility.Visible;
            string? availableId = _draftCustomProfiles.Any(
                profile => profile.Id == selectedId)
                    ? selectedId
                    : _draftCustomProfiles.FirstOrDefault()?.Id;
            CustomProfileBox.SelectedValue = availableId;

            CustomProfileCardsPanel.Children.Clear();
            foreach (CustomRewriteProfile profile in _draftCustomProfiles)
            {
                ToggleButton card = new()
                {
                    Tag = profile.Id,
                    Style = (Style)FindResource("RewriteModeCard"),
                    Width = 190,
                    MinHeight = 94,
                    Margin = new Thickness(0, 0, 10, 10),
                    Content = CreateCustomProfileCardContent(profile)
                };
                card.Click += CustomProfileCard_Click;
                CustomProfileCardsPanel.Children.Add(card);
            }
            SetCustomProfileCardState(availableId);
            EditCustomProfileButton.IsEnabled = hasProfiles;
            DeleteCustomProfileButton.IsEnabled = hasProfiles;
        }
        finally
        {
            _loadingCustomProfileEditor = false;
        }
    }

    private static Grid CreateCustomProfileCardContent(CustomRewriteProfile profile)
    {
        Grid content = new();
        StackPanel copy = new()
        {
            Margin = new Thickness(0, 0, 28, 0)
        };
        copy.Children.Add(new TextBlock
        {
            Text = profile.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        copy.Children.Add(new TextBlock
        {
            Text = profile.Instruction,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(105, 115, 134)),
            FontSize = 11,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 30,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        content.Children.Add(copy);
        content.Children.Add(new System.Windows.Shapes.Path
        {
            Width = 17,
            Height = 17,
            Stretch = Stretch.Uniform,
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 48, 68)),
            StrokeThickness = 1.45,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M3,13 L4,9 L11,2 L14,5 L7,12 Z M10,3 L13,6 M3,13 L2,16 L5,15"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Opacity = 0.82
        });
        return content;
    }

    private void SetCustomProfileCardState(string? selectedId)
    {
        foreach (ToggleButton card in CustomProfileCardsPanel.Children.OfType<ToggleButton>())
        {
            card.IsChecked = string.Equals(card.Tag as string, selectedId, StringComparison.Ordinal);
        }
    }

    private string? ValidateCustomProfile(string? profileId)
    {
        CustomRewriteProfile? profile = FindDraftCustomProfile(profileId);
        if (profile is null)
        {
            return "Create or choose a custom profile first.";
        }
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            return "Give this profile a name.";
        }
        if (string.IsNullOrWhiteSpace(profile.Instruction))
        {
            return "Describe how this profile should rewrite your transcript.";
        }
        return null;
    }

    private string? ValidateDraftProfiles()
    {
        CustomRewriteProfile? invalid = _draftCustomProfiles.FirstOrDefault(profile =>
            string.IsNullOrWhiteSpace(profile.Name)
            || string.IsNullOrWhiteSpace(profile.Instruction));
        if (invalid is null)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(invalid.Name)
            ? "Give each custom profile a name."
            : $"Add a rewrite instruction to “{invalid.Name}”.";
    }

    private void ShowCustomProfileValidation(string message)
    {
        CustomProfileValidationText.Text = message;
        CustomProfileValidationText.Visibility = Visibility.Visible;
    }

    private CustomRewriteProfile? FindDraftCustomProfile(string? profileId) =>
        _draftCustomProfiles.FirstOrDefault(
            profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal));

    private string CreateUniqueProfileName(string preferredName)
    {
        string baseName = preferredName.Length <= SettingsStore.MaximumCustomProfileNameLength
            ? preferredName
            : preferredName[..SettingsStore.MaximumCustomProfileNameLength];
        string candidate = baseName;
        int suffix = 2;
        while (_draftCustomProfiles.Any(
                   profile => string.Equals(
                       profile.Name,
                       candidate,
                       StringComparison.OrdinalIgnoreCase)))
        {
            string suffixText = $" {suffix++}";
            int baseLength = Math.Min(
                baseName.Length,
                SettingsStore.MaximumCustomProfileNameLength - suffixText.Length);
            candidate = baseName[..baseLength] + suffixText;
        }
        return candidate;
    }

    private static CustomRewriteProfile CloneCustomProfile(
        CustomRewriteProfile profile) => new()
        {
            Id = profile.Id,
            Name = profile.Name,
            Instruction = profile.Instruction
        };

    private async Task RefreshHistoryAsync()
    {
        IReadOnlyList<TranscriptHistoryEntry> entries = await _historyStore.LoadAsync();
        HistoryList.ItemsSource = entries.Select(entry => new HistoryEntryView(
            entry.Id,
            entry.Text,
            entry.CreatedAt.LocalDateTime.ToString(
                "ddd, d MMM · HH:mm", CultureInfo.CurrentCulture))).ToList();
        HistoryEmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.IsEnabled = entries.Count > 0;
        HistoryEmptyDescription.Text = _settings.History.Enabled
            ? "Use your shortcut to dictate. Successful transcripts will appear here."
            : "Turn on Save history above to keep successful transcripts on this device.";
        HistorySavingStateText.Text = _settings.History.Enabled
            ? "New transcripts are saved locally on this device."
            : "History saving is off. Existing entries remain until you delete them.";
    }

    private void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not HistoryEntryView entry)
        {
            return;
        }
        System.Windows.Clipboard.SetText(entry.Text);
    }

    private async void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not HistoryEntryView entry)
        {
            return;
        }
        await _historyStore.DeleteAsync(entry.Id);
        await RefreshHistoryAsync();
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirmation = System.Windows.MessageBox.Show(
            this,
            "Delete all transcript history? This cannot be undone.",
            "Clear transcript history",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }
        await _historyStore.ClearAsync();
        await RefreshHistoryAsync();
    }

    private async void HistoryEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_controlsReady)
        {
            return;
        }
        await _settingsSaveGate.WaitAsync();
        try
        {
            AppSettings candidate = CloneSettings(_settings);
            candidate.History.Enabled = HistoryEnabled.IsChecked == true;
            await _settingsStore.SaveAsync(candidate);
            CommitSettings(candidate);
            UpdatePrivacySummary();
            await RefreshHistoryAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write("History setting could not be saved.", exception);
            _controlsReady = false;
            HistoryEnabled.IsChecked = _settings.History.Enabled;
            _controlsReady = true;
            ShowStatus($"History setting could not be saved: {exception.Message}", true);
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private void UpdatePrivacySummary()
    {
        string history = _settings.History.Enabled
            ? "History stays on this device."
            : "History is off.";
        string clipboard = _settings.Clipboard.RestoreAfterPaste
            ? "Previous clipboard is restored after paste."
            : "Pasted text stays on the clipboard.";
        PrivacySummaryText.Text =
            $"Audio goes to OpenAI; no audio is saved. {history} {clipboard}";
    }

    private void SetAutoSaveState(string message, bool error = false)
    {
        AutoSaveText.Text = message;
        AutoSaveText.Foreground = new SolidColorBrush(error
            ? System.Windows.Media.Color.FromRgb(239, 68, 68)
            : System.Windows.Media.Color.FromRgb(105, 115, 134));
    }

    private void SetRewriteSaveState(string message, bool error = false)
    {
        RewriteSavedText.Text = message;
        RewriteSavedText.Foreground = new SolidColorBrush(error
            ? System.Windows.Media.Color.FromRgb(180, 35, 24)
            : System.Windows.Media.Color.FromRgb(105, 115, 134));
    }

    private void RestoreGeneralControls()
    {
        _controlsReady = false;
        try
        {
            KeyboardShortcutEnabled.IsChecked =
                _settings.Shortcut.KeyboardShortcutEnabled;
            _draftShortcutModifiers = _settings.Shortcut.Modifiers;
            _draftShortcutKey = _settings.Shortcut.Key;
            KeyboardActivationModeBox.SelectedValue =
                _settings.Shortcut.KeyboardActivationMode;
            MouseButtonBox.SelectedValue = _settings.Shortcut.MouseButton;
            MouseActivationModeBox.SelectedValue =
                _settings.Shortcut.MouseActivationMode;
            UpdateActivationModeDescriptions();
            ConsumeMouseButton.IsChecked = _settings.Shortcut.ConsumeMouseButton;
            UpdateMouseShortcutAvailability();
            ErrorNotificationsEnabled.IsChecked =
                _settings.Privacy.ErrorNotificationsEnabled;
            CompletionNotificationsEnabled.IsChecked =
                _settings.Privacy.CompletionNotificationsEnabled;
            RestorePreviousClipboard.IsChecked =
                _settings.Clipboard.RestoreAfterPaste;
            RecordingLimitBox.SelectedValue =
                _settings.Recording.MaximumDurationMinutes;
            LaunchAtLogin.IsChecked = _settings.LaunchAtLogin;
            CloseButtonQuits.IsChecked = _settings.CloseButtonQuits;

            if (MicrophoneBox.ItemsSource is IEnumerable<MicrophoneChoice> microphones)
            {
                MicrophoneChoice? selected = microphones.FirstOrDefault(choice =>
                    string.Equals(
                        choice.DeviceId,
                        _settings.Microphone.DeviceId,
                        StringComparison.OrdinalIgnoreCase));
                if (selected is not null)
                {
                    MicrophoneBox.SelectedItem = selected;
                }
            }
            UpdateShortcutEditor();
            UpdateShortcutSummary(_settings.Shortcut);
        }
        finally
        {
            _controlsReady = true;
        }
    }

    private void RestoreRewriteControls()
    {
        _controlsReady = false;
        try
        {
            _draftCustomProfiles.Clear();
            _draftCustomProfiles.AddRange(
                _settings.Rewrite.CustomProfiles.Select(CloneCustomProfile));
            RefreshCustomProfileList(_settings.Rewrite.ActiveCustomProfileId);
            RewriteModeBox.SelectedValue = _settings.Rewrite.Mode;
            TranslationLanguageBox.Text = _settings.Rewrite.TranslationLanguage;
            UpdateRewriteModeUi();
        }
        finally
        {
            _controlsReady = true;
        }
    }

    private void CommitSettings(AppSettings candidate)
    {
        _settings.Shortcut = candidate.Shortcut;
        _settings.Microphone = candidate.Microphone;
        _settings.Privacy = candidate.Privacy;
        _settings.Clipboard = candidate.Clipboard;
        _settings.History = candidate.History;
        _settings.Rewrite = candidate.Rewrite;
        _settings.Recording = candidate.Recording;
        _settings.LaunchAtLogin = candidate.LaunchAtLogin;
        _settings.CloseButtonQuits = candidate.CloseButtonQuits;
    }

    private static AppSettings CloneSettings(AppSettings source) => new()
    {
        Shortcut = CloneShortcut(source.Shortcut),
        Microphone = new MicrophoneSettings
        {
            DeviceId = source.Microphone.DeviceId,
            DeviceName = source.Microphone.DeviceName
        },
        Privacy = new PrivacySettings
        {
            ErrorNotificationsEnabled = source.Privacy.ErrorNotificationsEnabled,
            CompletionNotificationsEnabled =
                source.Privacy.CompletionNotificationsEnabled
        },
        Clipboard = new ClipboardSettings
        {
            RestoreAfterPaste = source.Clipboard.RestoreAfterPaste
        },
        History = new HistorySettings
        {
            Enabled = source.History.Enabled
        },
        Recording = new RecordingSettings
        {
            MaximumDurationMinutes = source.Recording.MaximumDurationMinutes
        },
        Rewrite = new RewriteSettings
        {
            Mode = source.Rewrite.Mode,
            TranslationLanguage = source.Rewrite.TranslationLanguage,
            ActiveCustomProfileId = source.Rewrite.ActiveCustomProfileId,
            CustomProfiles = source.Rewrite.CustomProfiles
                .Select(CloneCustomProfile)
                .ToList()
        },
        LaunchAtLogin = source.LaunchAtLogin,
        CloseButtonQuits = source.CloseButtonQuits
    };

    private static ShortcutSettings CloneShortcut(ShortcutSettings shortcut) => new()
    {
        KeyboardShortcutEnabled = shortcut.KeyboardShortcutEnabled,
        Modifiers = shortcut.Modifiers,
        Key = shortcut.Key,
        MouseButton = shortcut.MouseButton,
        ConsumeMouseButton = shortcut.ConsumeMouseButton,
        KeyboardActivationMode = shortcut.KeyboardActivationMode,
        MouseActivationMode = shortcut.MouseActivationMode
    };

    private static bool ShortcutsEqual(
        ShortcutSettings left,
        ShortcutSettings right) =>
        left.KeyboardShortcutEnabled == right.KeyboardShortcutEnabled
        && left.Modifiers == right.Modifiers
        && string.Equals(left.Key, right.Key, StringComparison.Ordinal)
        && left.MouseButton == right.MouseButton
        && left.ConsumeMouseButton == right.ConsumeMouseButton
        && left.KeyboardActivationMode == right.KeyboardActivationMode
        && left.MouseActivationMode == right.MouseActivationMode;

    private void ShowStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusDot.Fill = new SolidColorBrush(error
            ? System.Windows.Media.Color.FromRgb(239, 68, 68)
            : System.Windows.Media.Color.FromRgb(83, 213, 138));
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        string? directory = System.IO.Path.GetDirectoryName(AppLog.Path);
        if (directory is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }

    public void ShowFromSecondInstance() => ShowSettings();

    private void ShowSettings()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _ = RefreshConnectionStatusAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        AppLog.Write($"Settings window closing. Quitting={_quitting}, closeButtonQuits={_settings.CloseButtonQuits}.");
        if (_quitting) return;
        if (_settings.CloseButtonQuits)
        {
            e.Cancel = true;
            _ = QuitAsync();
        }
        else
        {
            e.Cancel = true;
            Hide();
        }
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _controlsReady = false;
        try
        {
            bool rewriteSavePending = _rewriteSaveTimer.IsEnabled;
            _rewriteSaveTimer.Stop();
            if (rewriteSavePending)
            {
                await ApplyRewriteSettingsAsync();
            }
            if (_controller.CanCancel) await _controller.CancelAsync();
            await DrainPendingPersistenceAsync();
            _input?.Dispose();
            _tray?.Dispose();
            _directRewriteService.Dispose();
            _transcriptionClient.Dispose();
            _codexHttpHandler.Dispose();
            _overlay.Close();
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            AppLog.Write("Shutdown failed.", exception);
            System.Windows.Application.Current.Shutdown(1);
        }
    }

    private async Task DrainPendingPersistenceAsync()
    {
        await _settingsSaveGate.WaitAsync();
        _settingsSaveGate.Release();
        await _historyStore.DrainAsync();
    }

    private static void SetLaunchAtLogin(bool enabled)
    {
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (run is null) throw new InvalidOperationException("Windows startup settings are unavailable.");
        const string valueName = "OpenVocare";
        if (enabled)
        {
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The executable path is unavailable.");
            run.SetValue(valueName, $"\"{executable}\"");
        }
        else
        {
            run.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    internal static string FormatActiveShortcuts(ShortcutSettings shortcut)
    {
        List<string> active = [];
        if (shortcut.KeyboardShortcutEnabled)
        {
            active.Add(string.Join(" + ", GetShortcutParts(shortcut.Modifiers, shortcut.Key)));
        }
        if (shortcut.MouseButton != MouseShortcutButton.None)
        {
            active.Add(FormatMouseShortcut(shortcut.MouseButton));
        }
        return string.Join(Environment.NewLine, active);
    }

    private static string FormatKeyboardShortcut(ShortcutSettings shortcut)
    {
        if (!shortcut.KeyboardShortcutEnabled)
        {
            return "Disabled";
        }

        return string.Join(" + ", GetShortcutParts(shortcut.Modifiers, shortcut.Key));
    }

    private static List<string> GetShortcutParts(HotkeyModifiers modifiers, string key)
    {
        List<string> parts = [];
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(FormatKeyName(key));
        return parts;
    }

    private static string FormatKeyName(string key) =>
        key switch
        {
            "Return" => "Enter",
            "Escape" => "Esc",
            "Prior" => "Page Up",
            "Next" => "Page Down",
            _ when key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) =>
                key[1].ToString(),
            _ => key
        };

    private static string FormatMouseShortcut(MouseShortcutButton button) =>
        button switch
        {
            MouseShortcutButton.XButton1 => "Back side button",
            MouseShortcutButton.XButton2 => "Forward side button",
            _ => "Disabled"
        };

    private static string FormatActivationMode(ShortcutActivationMode mode) =>
        mode == ShortcutActivationMode.Toggle ? "Toggle recording" : "Hold to talk";

    private string FormatRewriteMode(RewriteMode mode) => mode switch
    {
        RewriteMode.Minimal => "Minimal cleanup",
        RewriteMode.Professional => "Professional rewrite",
        RewriteMode.Ramble => "Ramble mode",
        RewriteMode.Translate => "Translation",
        RewriteMode.Custom => _settings.Rewrite.CustomProfiles.FirstOrDefault(
            profile => profile.Id == _settings.Rewrite.ActiveCustomProfileId)?.Name
            ?? "Custom profile",
        _ => "No rewrite"
    };

    private static class Native
    {
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;
        private const int DwmWindowCornerRound = 2;
        private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
        private const int GwlExStyle = -20;
        private const int WsExDlgModalFrame = 0x00000001;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;

        public static void ApplyWindowFrame(IntPtr window)
        {
            int rounded = DwmWindowCornerRound;
            int border = DwmColorNone;
            _ = DwmSetWindowAttribute(
                window,
                DwmwaWindowCornerPreference,
                ref rounded,
                sizeof(int));
            _ = DwmSetWindowAttribute(
                window,
                DwmwaBorderColor,
                ref border,
                sizeof(int));

            if (!SystemParameters.HighContrast)
            {
                int caption = ColorRef(0xE7, 0xEA, 0xEF);
                int text = ColorRef(0x4C, 0x4F, 0x69);
                _ = DwmSetWindowAttribute(
                    window,
                    DwmwaCaptionColor,
                    ref caption,
                    sizeof(int));
                _ = DwmSetWindowAttribute(
                    window,
                    DwmwaTextColor,
                    ref text,
                    sizeof(int));
            }

            int extendedStyle = GetWindowLong(window, GwlExStyle);
            _ = SetWindowLong(window, GwlExStyle, extendedStyle | WsExDlgModalFrame);
            _ = SetWindowPos(
                window,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }

        private static int ColorRef(byte red, byte green, byte blue) =>
            red | (green << 8) | (blue << 16);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(
            IntPtr window,
            int attribute,
            ref int value,
            int valueSize);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr window, int index, int value);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}
