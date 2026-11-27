using System.Windows;

namespace BatteryWidget.UI;

public partial class ConsentWindow : Window
{
    public bool Accepted { get; private set; }

    public ConsentWindow()
    {
        InitializeComponent();
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }
}
