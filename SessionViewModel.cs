using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using KdrEnet.Services;

namespace KdrEnet;

public sealed class SessionViewModel : INotifyPropertyChanged
{
    private string _techIp = "127.0.0.1";
    private string _techName = "This laptop";
    private string _techExtra = "";
    private string _techHint = "The other person types this code. Radmin is not used.";
    private string _sessionCode = "—";
    private string _codeInput = "";
    private string _radminLine = "";
    private string _nextTitle = "Do this now";
    private string _nextDetail = "Plug the cable into the laptop that is with the car.";
    private string _statusLine = "Checking the cable";
    private string _lastLine = "";
    private bool _lastLineAlert;
    private string _versionText = "1.0";
    private bool _isBusy;
    private bool _sessionOn;
    private bool _isCarSide;
    private bool _relayReady;
    private string? _vehicleIp;

    public SessionViewModel()
    {
        Cards = new[] { Cable, Power, Car, Session };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CardModel Cable { get; } = new("Cable", "Checking");
    public CardModel Power { get; } = new("Power", "Checking");
    public CardModel Car { get; } = new("Car", "Waiting");
    public CardModel Session { get; } = new("Session", "Off");
    public IReadOnlyList<CardModel> Cards { get; }

    public ObservableCollection<LogLine> Lines { get; } = new();

    public string TechIp
    {
        get => _techIp;
        set => Set(ref _techIp, value);
    }

    public string TechName
    {
        get => _techName;
        set => Set(ref _techName, value);
    }

    public string TechExtra
    {
        get => _techExtra;
        set => Set(ref _techExtra, value);
    }

    public string TechHint
    {
        get => _techHint;
        set => Set(ref _techHint, value);
    }

    public string SessionCode
    {
        get => _sessionCode;
        set
        {
            if (!Set(ref _sessionCode, value))
                return;
            NotifyCommands();
        }
    }

    public string CodeInput
    {
        get => _codeInput;
        set
        {
            var digits = SessionLink.Digits(value);
            if (digits.Length > 6)
                digits = digits[..6];
            if (!Set(ref _codeInput, digits))
                return;
            NotifyCommands();
        }
    }

    public bool IsCarSide
    {
        get => _isCarSide;
        set
        {
            if (!Set(ref _isCarSide, value))
                return;
            NotifyCommands();
        }
    }

    public bool IsTechSide => !IsCarSide;

    public bool RelayReady
    {
        get => _relayReady;
        set
        {
            if (!Set(ref _relayReady, value))
                return;
            NotifyCommands();
        }
    }

    public string SideSwitchLabel => IsCarSide ? "This laptop has E-Sys" : "This laptop has the car";

    public string RadminLine
    {
        get => _radminLine;
        set => Set(ref _radminLine, value);
    }

    public string NextTitle
    {
        get => _nextTitle;
        set => Set(ref _nextTitle, value);
    }

    public string NextDetail
    {
        get => _nextDetail;
        set => Set(ref _nextDetail, value);
    }

    public string StatusLine
    {
        get => _statusLine;
        set => Set(ref _statusLine, value);
    }

    public string LastLine
    {
        get => _lastLine;
        set => Set(ref _lastLine, value);
    }

    public bool LastLineAlert
    {
        get => _lastLineAlert;
        set => Set(ref _lastLineAlert, value);
    }

    public string VersionText
    {
        get => _versionText;
        set => Set(ref _versionText, value);
    }

    public bool CanCopy => IsCarSide ? SessionOn && SessionLink.Digits(SessionCode).Length == 6 : true;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!Set(ref _isBusy, value))
                return;
            NotifyCommands();
        }
    }

    public bool SessionOn
    {
        get => _sessionOn;
        set
        {
            if (!Set(ref _sessionOn, value))
                return;
            NotifyCommands();
        }
    }

    public string? VehicleIp
    {
        get => _vehicleIp;
        set
        {
            if (!Set(ref _vehicleIp, value))
                return;
            NotifyCommands();
        }
    }

    public string StartLabel => IsBusy ? "Working…" : "Get code";

    public string JoinLabel => IsBusy ? "Working…" : "Join";

    public string DisplayCode => SessionLink.Digits(SessionCode).Length == 6 ? SessionLink.FormatCode(SessionCode) : "—";

    public bool CodeReady => SessionLink.Digits(SessionCode).Length == 6;

    public string Shown0 => ShownAt(0);
    public string Shown1 => ShownAt(1);
    public string Shown2 => ShownAt(2);
    public string Shown3 => ShownAt(3);
    public string Shown4 => ShownAt(4);
    public string Shown5 => ShownAt(5);

    private string ShownAt(int index)
    {
        var digits = SessionLink.Digits(SessionCode);
        return digits.Length == 6 ? digits[index].ToString() : "–";
    }

    public string CarHelp => SessionLink.Digits(SessionCode).Length == 6
        ? "Read this code to the E-Sys laptop. Leave this window open."
        : "Click Get code. The number shows here. Read it to the E-Sys laptop.";

    public string CopyLabel => IsCarSide ? "Copy code" : "Copy address";

    public bool ShowCopy => !IsCarSide || SessionLink.Digits(SessionCode).Length == 6;

    public bool CanStart => !IsBusy && !SessionOn && IsCarSide && !string.IsNullOrEmpty(VehicleIp);

    public bool CanJoin => !IsBusy && !SessionOn && IsTechSide && SessionLink.Digits(CodeInput).Length == 6;

    public bool CanSwitch => !IsBusy && !SessionOn;

    public bool CanStop => !IsBusy && SessionOn;

    public void AddLog(string text, bool alert = false)
    {
        Lines.Add(new LogLine(text, alert));
        while (Lines.Count > 200)
            Lines.RemoveAt(0);
        if (!alert)
            return;
        LastLine = text;
        LastLineAlert = true;
    }

    public void NotifyCommands()
    {
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(JoinLabel));
        OnPropertyChanged(nameof(DisplayCode));
        OnPropertyChanged(nameof(CodeReady));
        OnPropertyChanged(nameof(Shown0));
        OnPropertyChanged(nameof(Shown1));
        OnPropertyChanged(nameof(Shown2));
        OnPropertyChanged(nameof(Shown3));
        OnPropertyChanged(nameof(Shown4));
        OnPropertyChanged(nameof(Shown5));
        OnPropertyChanged(nameof(CarHelp));
        OnPropertyChanged(nameof(CopyLabel));
        OnPropertyChanged(nameof(ShowCopy));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanSwitch));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(IsTechSide));
        OnPropertyChanged(nameof(SideSwitchLabel));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class CardModel : INotifyPropertyChanged
{
    private string _detail;
    private string _state = "wait";

    public CardModel(string title, string detail)
    {
        Title = title;
        _detail = detail;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; private set; }

    public void Rename(string title)
    {
        if (Title == title)
            return;
        Title = title;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
    }

    public string Detail
    {
        get => _detail;
        set
        {
            if (_detail == value)
                return;
            _detail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }

    public string State
    {
        get => _state;
        set
        {
            if (_state == value)
                return;
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }
    }
}

public sealed class LogLine
{
    public LogLine(string text, bool isAlert)
    {
        TimeText = DateTime.Now.ToString("HH:mm");
        Text = text;
        IsAlert = isAlert;
    }

    public string TimeText { get; }
    public string Text { get; }
    public bool IsAlert { get; }
}
