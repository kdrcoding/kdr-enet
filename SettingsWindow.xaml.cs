using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KdrEnet;

public partial class SettingsWindow : Window
{
    private CableKind _cable;
    private VehicleKind _vehicle;
    private static readonly SolidColorBrush Mark = Freeze(Color.FromRgb(0x1C, 0x69, 0xD4));
    private static readonly SolidColorBrush OnText = Freeze(Color.FromRgb(0xF4, 0xF7, 0xFB));
    private static readonly SolidColorBrush OffText = Freeze(Color.FromRgb(0x8E, 0xA0, 0xB5));

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
        button.Foreground = selected ? OnText : OffText;
        button.BorderBrush = Mark;
        button.BorderThickness = new Thickness(0, 0, 0, selected ? 2 : 0);
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
