using System.Windows;
using System.Windows.Input;

namespace PclDownloadHelper.UI;

public partial class NoticeDialog : Window
{
    private bool _result;

    public NoticeDialog()
    {
        InitializeComponent();
    }

    /// <summary>显示提示框，返回是否点击了主按钮。</summary>
    public static bool Show(Window? owner, string title, string body, string primary, string? secondary = null)
    {
        var dialog = new NoticeDialog { Owner = owner };
        dialog.TitleText.Text = title;
        dialog.BodyText.Text = body;
        dialog.PrimaryButton.Content = primary;
        dialog.SecondaryButton.Content = secondary ?? "";
        dialog.SecondaryButton.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
        dialog.ShowDialog();
        return dialog._result;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        _result = true;
        DialogResult = true;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        _result = false;
        DialogResult = false;
        Close();
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
