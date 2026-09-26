using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KdrEnet.Services;

namespace KdrEnet;

public partial class MainWindow : Window
{
    private readonly SessionViewModel _vm = new();
    private readonly SessionService _session = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _pingTimer;
    private int _scanGate;
    private string _signature = "";
    private ScanResult? _lastScan;
    private string? _heldVehicleIp;
    private int _quietScans;
    private int _pingGate;
    private bool _sideChosen;
    private bool _shutdown;
    private bool _closeWarned;

    public MainWindow()
    {
        InitializeComponent();
        WireSlots();
        DataContext = _vm;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        _vm.VersionText = version is null ? "1.0" : version.Major + "." + version.Minor;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += async (_, _) => await ScanAsync(probe: _lastScan?.CableState == "ok");
        _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _pingTimer.Tick += (_, _) => QueuePing();
        Loaded += OnLoaded;
        Closing += OnClosing;
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SessionViewModel.IsCarSide) or nameof(SessionViewModel.CanSwitch))
                PaintRoles();
            if (args.PropertyName is nameof(SessionViewModel.CodeInput) or nameof(SessionViewModel.SessionCode) or nameof(SessionViewModel.SessionOn) or nameof(SessionViewModel.RelayReady))
                SetNextStep(_lastScan);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitle.Apply(this);
        PaintRoles();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppSettings.Load();
        if (!AcceptanceStore.IsAccepted())
        {
            var about = new AboutWindow(requireConsent: true) { Owner = this };
            about.ShowDialog();
            if (!about.Accepted)
                _vm.AddLog("Read About and agree to the terms before a session can start.", alert: true);
        }

        _vm.RelayReady = RelaySettings.TryGet(out _, out _);
        try
        {
            if (await _session.HasLeftoverSessionAsync())
            {
                _vm.Session.Detail = "Firewall still off";
                _vm.Session.State = "warn";
                _vm.SessionOn = true;
                _vm.AddLog("Windows Firewall is still off from the last session. Click Stop to turn the previous settings back on.", alert: true);
            }
        }
        catch (Exception ex)
        {
            _vm.AddLog(ex.Message, alert: true);
        }

        await ScanAsync(probe: true);
        QueuePing();
        _timer.Start();
        _pingTimer.Start();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdown)
            return;

        if (_vm.IsBusy)
        {
            e.Cancel = true;
            if (_closeWarned)
                return;
            _closeWarned = true;
            _vm.AddLog("Let the current step finish, then close the window.", alert: true);
            return;
        }

        _closeWarned = false;
        _timer.Stop();
        _pingTimer.Stop();
        if (!_vm.SessionOn && !FirewallControl.HasSavedState())
            return;

        e.Cancel = true;
        _shutdown = true;
        _vm.IsBusy = true;
        try
        {
            await _session.StopAsync();
        }
        catch
        {
            // The window still closes. The next launch will offer Stop again if a rule remains.
        }

        _vm.SessionOn = false;
        Close();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAccepted() || string.IsNullOrEmpty(_vm.VehicleIp))
            return;

        _timer.Stop();
        _vm.IsBusy = true;
        try
        {
            var code = SessionLink.NewCode();
            if (!_vm.RelayReady)
            {
                _vm.LastLineAlert = false;
                _vm.SessionCode = SessionLink.FormatCode(code);
                _vm.SessionOn = true;
                _vm.AddLog("Code " + SessionLink.Digits(code) + " stays on this laptop. The other laptop cannot join until the session server is set.");
                return;
            }

            var progress = new Progress<string>(message => _vm.AddLog(message));
            await _session.StartLinkAsync(code, _vm.VehicleIp, carSide: true, progress);
            _vm.LastLineAlert = false;
            _vm.SessionCode = SessionLink.FormatCode(code);
            _vm.SessionOn = true;
            _vm.Session.Detail = _vm.SessionCode;
            _vm.Session.State = "ok";
            _vm.AddLog("Leave this window open. The other person types " + _vm.SessionCode + ".");
        }
        catch (Exception ex)
        {
            ExplainFailure(ex);
            try { await _session.StopAsync(); } catch { /* best effort rollback */ }
            _vm.SessionOn = false;
            _vm.Session.Detail = "Off";
            _vm.Session.State = "wait";
        }
        finally
        {
            _vm.IsBusy = false;
            if (!_shutdown)
                _timer.Start();
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _vm.IsBusy = true;
        try
        {
            await _session.StopAsync();
            _vm.SessionOn = false;
            _vm.Session.Detail = "Off";
            _vm.Session.State = "wait";
            _vm.SessionCode = "—";
            _vm.AddLog("Session off.");
        }
        catch (Exception ex)
        {
            _vm.AddLog(ex.Message, alert: true);
        }
        finally
        {
            _vm.IsBusy = false;
            _timer.Start();
            await ScanAsync(probe: true);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _signature = "";
        await ScanAsync(probe: true);
    }

    private bool _fillingSlots;

    private TextBox[] Slots() => [Digit0, Digit1, Digit2, Digit3, Digit4, Digit5];

    private void WireSlots()
    {
        foreach (var slot in Slots())
        {
            slot.PreviewTextInput += Slot_Preview;
            slot.PreviewKeyDown += Slot_Key;
            slot.TextChanged += Slot_Changed;
            DataObject.AddPastingHandler(slot, Slot_Paste);
        }
    }

    private int SlotIndex(TextBox box)
    {
        var slots = Slots();
        for (var i = 0; i < slots.Length; i++)
        {
            if (ReferenceEquals(slots[i], box))
                return i;
        }

        return 0;
    }

    private void Slot_Preview(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox box || e.Text.Length != 1 || e.Text[0] is < '0' or > '9')
        {
            e.Handled = true;
            return;
        }

        _fillingSlots = true;
        box.Text = e.Text;
        _fillingSlots = false;
        e.Handled = true;
        PullCode();
        var next = SlotIndex(box) + 1;
        if (next < 6)
            Slots()[next].Focus();
    }

    private void Slot_Key(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        var index = SlotIndex(box);
        var slots = Slots();
        if (e.Key is Key.Back or Key.Delete)
        {
            e.Handled = true;
            if (box.Text.Length > 0)
            {
                box.Text = "";
                return;
            }

            if (e.Key == Key.Back && index > 0)
            {
                slots[index - 1].Text = "";
                slots[index - 1].Focus();
            }
        }
        else if (e.Key == Key.Left && index > 0)
        {
            slots[index - 1].Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Right && index < 5)
        {
            slots[index + 1].Focus();
            e.Handled = true;
        }
    }

    private void Slot_Changed(object sender, TextChangedEventArgs e)
    {
        if (_fillingSlots)
            return;
        PullCode();
    }

    private void Slot_Paste(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (sender is not TextBox box || !e.DataObject.GetDataPresent(DataFormats.Text))
            return;
        var digits = SessionLink.Digits(e.DataObject.GetData(DataFormats.Text) as string);
        if (digits.Length == 0)
            return;
        if (digits.Length > 6)
            digits = digits[..6];
        var start = digits.Length == 6 ? 0 : SlotIndex(box);
        var slots = Slots();
        _fillingSlots = true;
        var at = start;
        foreach (var character in digits)
        {
            if (at >= slots.Length)
                break;
            slots[at].Text = character.ToString();
            at++;
        }
        _fillingSlots = false;
        PullCode();
        slots[Math.Min(at, 5)].Focus();
    }

    private void PullCode()
    {
        var text = string.Concat(Slots().Select(slot => SessionLink.Digits(slot.Text)));
        if (text.Length > 6)
            text = text[..6];
        if (_vm.CodeInput != text)
            _vm.CodeInput = text;
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAccepted())
            return;

        var digits = SessionLink.Digits(_vm.CodeInput);
        if (digits.Length != 6)
            return;

        _timer.Stop();
        _vm.IsBusy = true;
        try
        {
            var progress = new Progress<string>(message => _vm.AddLog(message));
            await _session.StartLinkAsync(digits, vehicleIp: null, carSide: false, progress);
            _vm.LastLineAlert = false;
            _vm.SessionCode = SessionLink.FormatCode(digits);
            _vm.SessionOn = true;
            _vm.Session.Detail = "Joined";
            _vm.Session.State = "ok";
            _vm.AddLog("In E-Sys use tcp://127.0.0.1:6801. Leave this window open.");
        }
        catch (Exception ex)
        {
            ExplainFailure(ex);
            try { await _session.StopAsync(); } catch { /* best effort rollback */ }
            _vm.SessionOn = false;
            _vm.Session.Detail = "Off";
            _vm.Session.State = "wait";
        }
        finally
        {
            _vm.IsBusy = false;
            if (!_shutdown)
                _timer.Start();
        }
    }

    private void ExplainFailure(Exception ex)
    {
        var missingServer = ex.Message.Contains("session server", StringComparison.OrdinalIgnoreCase);
        _vm.AddLog(missingServer ? "The session server is not set." : ex.Message, alert: true);
        if (!missingServer)
            return;
        _vm.NextDetail = _vm.IsCarSide
            ? "The car is ready. The code cannot reach the other laptop until the session server is set up."
            : "The code is typed in. Join cannot reach the car laptop until the session server is set up.";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanCopy)
            return;
        var text = _vm.IsCarSide ? SessionLink.Digits(_vm.SessionCode) : "tcp://127.0.0.1:6801";
        Clipboard.SetText(text);
        _vm.AddLog("Copied " + text + ".");
    }

    private void ChooseCar_Click(object sender, RoutedEventArgs e) => ChooseSide(car: true);

    private void ChooseEsy_Click(object sender, RoutedEventArgs e) => ChooseSide(car: false);

    private void ChooseSide(bool car)
    {
        if (!_vm.CanSwitch || _vm.IsCarSide == car)
            return;
        _sideChosen = true;
        _vm.IsCarSide = car;
        _signature = "";
        PaintRoles();
        SetNextStep(_lastScan);
        if (!car)
            Digit0.Focus();
    }

    private void PaintRoles()
    {
        if (CarRoleButton is null || EsyRoleButton is null)
            return;
        PaintRole(CarRoleButton, _vm.IsCarSide);
        PaintRole(EsyRoleButton, !_vm.IsCarSide);
        CarRoleButton.IsEnabled = _vm.CanSwitch || _vm.IsCarSide;
        EsyRoleButton.IsEnabled = _vm.CanSwitch || !_vm.IsCarSide;
    }

    private static void PaintRole(System.Windows.Controls.Button button, bool selected)
    {
        button.Background = System.Windows.Media.Brushes.Transparent;
        button.BorderThickness = new Thickness(0, 0, 0, selected ? 2 : 0);
        button.BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x1C, 0x69, 0xD4));
        button.Foreground = new System.Windows.Media.SolidColorBrush(selected
            ? System.Windows.Media.Color.FromRgb(0xF4, 0xF7, 0xFB)
            : System.Windows.Media.Color.FromRgb(0x8E, 0xA0, 0xB5));
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = this };
        if (settings.ShowDialog() != true)
            return;
        _signature = "";
        _vm.AddLog("Settings saved. Cable is " + AppSettings.CableLabel + ", vehicle is " + AppSettings.VehicleWord + ".");
        await ScanAsync(probe: true);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow(requireConsent: !AcceptanceStore.IsAccepted()) { Owner = this };
        about.ShowDialog();
    }

    private bool EnsureAccepted()
    {
        if (AcceptanceStore.IsAccepted())
            return true;

        var about = new AboutWindow(requireConsent: true) { Owner = this };
        about.ShowDialog();
        if (about.Accepted)
            return true;

        _vm.AddLog("A session stays off until you agree to the terms.", alert: true);
        return false;
    }

    private void Site_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://kdrcoding.com") { UseShellExecute = true });
    }

    private void QueuePing()
    {
        if (_shutdown || _vm.IsBusy || _vm.SessionOn)
            return;
        if (!RelaySettings.TryGet(out var host, out var port))
        {
            _vm.NotePing(null);
            return;
        }

        if (Interlocked.Exchange(ref _pingGate, 1) == 1)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var milliseconds = SessionLink.MeasureMilliseconds(host, port);
                Dispatcher.Invoke(() =>
                {
                    if (_shutdown)
                        return;
                    _vm.NotePing(milliseconds);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _pingGate, 0);
            }
        });
    }

    private async Task ScanAsync(bool probe = false)
    {
        if (_vm.IsBusy || _shutdown)
            return;
        if (Interlocked.Exchange(ref _scanGate, 1) == 1)
            return;

        try
        {
            var result = await _session.ScanAsync(probe && !_vm.SessionOn);
            if (_shutdown)
                return;
            Apply(result);
        }
        catch (Exception ex)
        {
            _vm.AddLog(ex.Message, alert: true);
        }
        finally
        {
            Interlocked.Exchange(ref _scanGate, 0);
        }
    }

    private void HoldVehicle(ScanResult result)
    {
        if (result.CableState != "ok")
        {
            _quietScans = 0;
            _heldVehicleIp = null;
            _vm.VehicleIp = null;
            return;
        }

        if (!string.IsNullOrEmpty(result.VehicleIp))
        {
            _quietScans = 0;
            _heldVehicleIp = result.VehicleIp;
            _vm.VehicleIp = result.VehicleIp;
            return;
        }

        if (!string.IsNullOrEmpty(_heldVehicleIp) && _quietScans < 2)
        {
            _quietScans++;
            _vm.VehicleIp = _heldVehicleIp;
            _vm.StatusLine = "Awake  ·  " + result.CableDetail;
            _vm.Power.Detail = "Awake";
            _vm.Power.State = "ok";
            return;
        }

        _heldVehicleIp = null;
        _vm.VehicleIp = null;
    }

    private void Apply(ScanResult result)
    {
        _vm.Cable.Detail = result.CableDetail;
        _vm.Cable.State = result.CableState;
        _vm.Power.Detail = result.PowerDetail;
        _vm.Power.State = result.PowerState;
        _vm.Car.Rename(result.VehicleTitle);
        _vm.StatusLine = result.CableState == "ok"
            ? result.PowerDetail + "  ·  " + result.CableDetail
            : result.CableDetail;

        if (_vm.SessionOn)
        {
            _vm.Car.Detail = string.IsNullOrEmpty(_vm.VehicleIp) ? result.VehicleDetail : _vm.VehicleIp;
            _vm.Car.State = result.VehicleIp is null ? "warn" : "ok";
            if (_vm.Session.State != "warn")
            {
                _vm.Session.Detail = _vm.IsCarSide ? _vm.SessionCode : "Joined";
                _vm.Session.State = "ok";
            }
        }
        else
        {
            _vm.Car.Detail = result.VehicleDetail;
            _vm.Car.State = result.VehicleState;
            HoldVehicle(result);
            if (_vm.Session.State != "warn")
            {
                _vm.Session.Detail = "Off";
                _vm.Session.State = "wait";
            }
            if (!_sideChosen)
                _vm.IsCarSide = result.VehicleIp is not null || result.CableState == "ok";
        }

        _lastScan = result;
        SetNextStep(result);

        var signature = result.CableDetail + "|" + result.PowerDetail + "|" + result.VehicleIp + "|" + result.VehicleTitle;
        if (signature == _signature)
            return;

        _signature = signature;
        foreach (var note in result.Notes)
            _vm.AddLog(note);
    }

    private void Driver_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.DriverUrl))
            return;
        Process.Start(new ProcessStartInfo(_vm.DriverUrl) { UseShellExecute = true });
    }

    private void Mark(string tone, string sentence)
    {
        _vm.CheckTone = tone;
        _vm.CheckWord = sentence;
        _vm.NextDetail = sentence;
    }

    private void SetNextStep(ScanResult? result)
    {
        _vm.ShowDriver = false;
        if (_vm.SessionOn && _vm.Session.State == "warn")
        {
            Mark("wrong", "Wrong: the firewall is still off from the last session. Click Stop.");
            return;
        }

        if (_vm.SessionOn)
        {
            if (_vm.IsCarSide && !_vm.RelayReady)
            {
                Mark("good", "All good. This code stays until you click Stop. The other laptop cannot join until the session server is set.");
                return;
            }

            Mark("good", _vm.IsCarSide
                ? "All good. This code stays. Read it to the other laptop. Leave this window open."
                : "All good. In E-Sys use tcp://127.0.0.1:6801. Leave this window open.");
            return;
        }

        if (!_vm.IsCarSide)
        {
            if (SessionLink.Digits(_vm.CodeInput).Length != 6)
            {
                Mark("missing", "The 6-digit code is missing. Type it in the boxes above. It comes from the car laptop.");
                return;
            }

            Mark(_vm.RelayReady ? "good" : "missing",
                _vm.RelayReady
                    ? "All good. Click Join, then in E-Sys use tcp://127.0.0.1:6801."
                    : "The session server is missing, so Join stays off. In E-Sys the line will be tcp://127.0.0.1:6801.");
            return;
        }

        if (AppSettings.Cable == CableKind.Kdcan && result is { CableState: "ok" })
        {
            Mark("wrong", "Wrong: K+DCAN is plugged in. The code session is for ENET, MHD, and ICOM.");
            return;
        }

        if (result is not null && result.CableState != "ok")
        {
            if (!string.IsNullOrEmpty(result.DriverMessage))
            {
                _vm.DriverUrl = result.DriverUrl;
                _vm.ShowDriver = result.DriverUrl.Length > 0;
                Mark("wrong", "Wrong: " + result.DriverMessage);
            }
            else
            {
                Mark("missing", "The cable is missing. " + AppSettings.PlugSentence);
            }
            return;
        }

        if (string.IsNullOrEmpty(_vm.VehicleIp))
        {
            Mark("missing", "The car is quiet. Turn the ignition on. If it stays quiet, the battery may be too low.");
            return;
        }

        if (!_vm.RelayReady)
        {
            Mark("good", SessionLink.Digits(_vm.SessionCode).Length == 6
                ? "All good. Read the code above out loud. The other laptop cannot join until the session server is set."
                : "All good. The car is awake. Click Get code.");
            return;
        }

        Mark("good", SessionLink.Digits(_vm.SessionCode).Length == 6
            ? "All good. Read the code above to the other laptop."
            : "All good. Click Get code, then read it to the other laptop.");
    }
}
