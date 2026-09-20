using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MagicViewWallpaper.App;

public enum DialogKind { Info, Success, Warning, Error, Question }

public partial class ModernDialog : Window
{
    ModernDialog(string title, string message, DialogKind kind, bool showCancel, string confirmText, string cancelText)
    {
        InitializeComponent();
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

        (string icon, string color) = kind switch
        {
            DialogKind.Success => ("✓", "#2DBA86"),
            DialogKind.Error => ("×", "#E35E66"),
            DialogKind.Question => ("?", "#5793EE"),
            DialogKind.Info => ("i", "#5793EE"),
            _ => ("!", "#E4A447")
        };
        DialogIcon.Text = icon;
        IconCircle.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    public static bool Show(Window? owner, string title, string message, DialogKind kind = DialogKind.Info,
        bool showCancel = false, string confirmText = "确定", string cancelText = "取消")
    {
        var dialog = new ModernDialog(title, message, kind, showCancel, confirmText, cancelText);
        double oldOpacity = 1;
        if (owner != null && owner.IsVisible)
        {
            dialog.Owner = owner;
            oldOpacity = owner.Opacity;
            owner.Opacity = 0.48;
        }
        try { return dialog.ShowDialog() == true; }
        finally { if (owner != null) owner.Opacity = oldOpacity; }
    }

    void Confirm_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
