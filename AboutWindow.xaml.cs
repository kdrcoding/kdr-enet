using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace KdrEnet;

public partial class AboutWindow : Window
{
    public AboutWindow(bool requireConsent)
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var label = version is null ? "1.1" : version.ToString(3);
        VersionLine.Text = label;
        CreatorLine.Text = "Created by " + LegalCopy.Creator;
        SummaryLine.Text = LegalCopy.Summary;
        TermsBox.Text = LegalCopy.Terms;
        if (!requireConsent)
        {
            ConsentButtons.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        }
    }

    public bool Accepted { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitle.Apply(this);
    }

    private void Agree_Click(object sender, RoutedEventArgs e)
    {
        AcceptanceStore.Accept();
        Accepted = true;
        DialogResult = true;
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Site_Click(object sender, RoutedEventArgs e)
        => Open(LegalCopy.Site);

    private void Mail_Click(object sender, RoutedEventArgs e)
        => Open("mailto:" + LegalCopy.Email);

    private static void Open(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
