using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KdrEnet;

public partial class SettingsWindow : Window
{
    private CableKind _cable;
    private VehicleKind _vehicle;
    private static readonly SolidColorBrush SelectedFill = Freeze(Color.FromRgb(0x00, 0x96, 0xD6));
    private static readonly SolidColorBrush SelectedText = Freeze(Colors.White);
    private static readonly SolidColorBrush IdleFill = Freeze(Color.FromRgb(0x10, 0x15, 0x1F));
    private static readonly SolidColorBrush IdleText = Freeze(Color.FromRgb(0xF4, 0xF7, 0xFB));

    public SettingsWindow()
    {
        InitializeComponent();
        _cable = AppSettings.Cable;
        _vehicle = AppSettings.Vehicle;
        StyleChoice(EnetButton);
        StyleChoice(KdcanButton);
        StyleChoice(IcomButton);
        StyleChoice(MhdButton);
        StyleChoice(CarButton);
        StyleChoice(BikeButton);
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

    private static void StyleChoice(Button button)
    {
        button.Height = 44;
        button.FontSize = 15;
        button.FontWeight = FontWeights.SemiBold;
        button.Cursor = System.Windows.Input.Cursors.Hand;
        button.BorderThickness = new Thickness(1);
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x30, 0x44));
    }

    private static void PaintOne(Button button, bool selected)
    {
        button.Background = selected ? SelectedFill : IdleFill;
        button.Foreground = selected ? SelectedText : IdleText;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
