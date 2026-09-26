using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KdrEnet;

public partial class SettingsWindow : Window
{
    private CableKind _cable;
    private VehicleKind _vehicle;
    private static readonly SolidColorBrush Mark = Freeze(Color.FromRgb(0x1C, 0x69, 0xD4));
    private static readonly SolidColorBrush OffBg = Freeze(Color.FromRgb(0x12, 0x16, 0x1E));
    private static readonly SolidColorBrush Line = Freeze(Color.FromRgb(0x3D, 0x4D, 0x66));
    private static readonly SolidColorBrush OnText = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush OffText = Freeze(Color.FromRgb(0xD5, 0xDE, 0xEA));

    public SettingsWindow()
    {
        InitializeComponent();
        _cable = AppSettings.Cable;
        _vehicle = AppSettings.Vehicle;
        Paint();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitle.Apply(this);
    }

    private void Cable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<CableKind>(tag, out var cable))
        {
            _cable = cable;
            Paint();
        }
    }

    private void Vehicle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<VehicleKind>(tag, out var vehicle))
        {
            _vehicle = vehicle;
            Paint();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Save(_cable, _vehicle);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Paint()
    {
        PaintOne(EnetButton, _cable == CableKind.Enet);
        PaintOne(KdcanButton, _cable == CableKind.Kdcan);
        PaintOne(IcomButton, _cable == CableKind.Icom);
        PaintOne(MhdButton, _cable == CableKind.Mhd);
        PaintOne(CarButton, _vehicle == VehicleKind.Car);
        PaintOne(BikeButton, _vehicle == VehicleKind.Bike);
    }

    private static void PaintOne(Button button, bool selected)
    {
        button.Background = selected ? Mark : OffBg;
        button.Foreground = selected ? OnText : OffText;
        button.BorderBrush = selected ? Mark : Line;
        button.BorderThickness = new Thickness(1);
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
