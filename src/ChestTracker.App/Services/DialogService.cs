using System.Globalization;
using ChestTracker.App.Localization;
using ChestTracker.App.Views;

namespace ChestTracker.App.Services;

/// <summary>Themed modal dialogs.</summary>
public sealed class DialogService
{
    public bool Confirm(string title, string message, bool danger = false) =>
        MessageDialog.Create(title, message, withInput: false, "", danger, null).ShowDialog() == true;

    public string? PromptText(string title, string message, string initial, bool danger = false)
    {
        var dialog = MessageDialog.Create(title, message, withInput: true, initial, danger, null);
        return dialog.ShowDialog() == true ? dialog.InputText : null;
    }

    public int? PromptInt(string title, string message, int initial, int min, int max)
    {
        var dialog = MessageDialog.Create(title, message, withInput: true, initial.ToString(CultureInfo.InvariantCulture), false,
            text => int.TryParse(text.Trim().Replace('−', '-'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
                ? null
                : Loc.T("Dialog.InvalidNumber") + $" ({min}…{max})");
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        return int.Parse(dialog.InputText.Trim().Replace('−', '-'), NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
