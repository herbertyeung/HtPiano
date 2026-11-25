using System.Windows.Controls;
using System.Windows.Input;

namespace HtPiano;

public partial class MainWindow
{
    private void InitializeLanguagePicker()
    {
        LanguagePicker.ItemsSource = UiLanguage.Available;
        LanguagePicker.SelectedItem = UiLanguage.Current;
        LanguagePicker.DropDownClosed += ReleasePickerMouseFocus;
    }

    private static void ReleasePickerMouseFocus(object? sender, EventArgs args)
    {
        if (sender is ComboBox { IsKeyboardFocusWithin: true } &&
            InputManager.Current.MostRecentInputDevice is MouseDevice)
            Keyboard.ClearFocus();
    }

    private void OnLanguageSelected(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: LanguagePack pack } || ReferenceEquals(pack, UiLanguage.Current)) return;
        UiLanguage.Select(pack);
        RefreshLocalizedState();
    }
}
