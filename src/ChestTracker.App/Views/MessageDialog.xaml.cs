using System.Windows;
using System.Windows.Input;

namespace ChestTracker.App.Views;

public partial class MessageDialog : Window
{
    private Func<string, string?>? _validate;

    public MessageDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    public string InputText => Input.Text;

    public static MessageDialog Create(string title, string message, bool withInput, string initial, bool danger,
        Func<string, string?>? validate)
    {
        var dialog = new MessageDialog();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog._validate = validate;
        if (danger)
        {
            dialog.OkButton.Style = (Style)dialog.FindResource("Button.Danger");
        }

        if (withInput)
        {
            dialog.Input.Visibility = Visibility.Visible;
            dialog.Input.Text = initial;
            dialog.Loaded += (_, _) =>
            {
                dialog.Input.Focus();
                dialog.Input.SelectAll();
            };
        }

        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != dialog)
                    ?? (Application.Current.MainWindow?.IsVisible == true ? Application.Current.MainWindow : null);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_validate is not null && Input.Visibility == Visibility.Visible)
        {
            var error = _validate(Input.Text);
            if (error is not null)
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
