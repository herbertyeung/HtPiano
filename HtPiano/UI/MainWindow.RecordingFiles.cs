using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace HtPiano;

public partial class MainWindow
{
    private string? _startupRecording;
    private bool _takeNeedsSaving;

    private static string RecordingFilter => UiLanguage.Get("Lang.RecordingFileType").Replace('|', ' ') + " (*.htkeys)|*.htkeys";

    private void SaveTake(object sender, RoutedEventArgs args)
    {
        if (_take == null || _playingTake || _recorder != null) return;

        var dialog = new SaveFileDialog { Filter = RecordingFilter, DefaultExt = ".htkeys",
            Title = UiLanguage.Get("Lang.SavePerformance") };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            TakeFile.Save(dialog.FileName, _take);
            _takeNeedsSaving = false;
            SetIdleTakeStatus(new("Lang.PerformanceSaved"));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ReportRecordingError(UiLanguage.Get("Lang.SavePerformanceFailed"), error);
        }
    }

    private void OpenTake(object sender, RoutedEventArgs args)
    {
        if (!_audioReady || _playingTake || _recorder != null) return;
        var dialog = new OpenFileDialog { Filter = RecordingFilter, DefaultExt = ".htkeys",
            Title = UiLanguage.Get("Lang.OpenPerformance") };
        if (dialog.ShowDialog(this) == true) OpenRecording(dialog.FileName);
    }

    private void OpenRecording(string path)
    {
        PerformanceTake loaded;
        try
        {
            loaded = TakeFile.Load(path);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ReportRecordingError(UiLanguage.Get("Lang.OpenPerformanceFailed"), error);
            return;
        }

        if (_takeNeedsSaving && MessageBox.Show(this, UiLanguage.Get("Lang.ReplaceUnsavedPerformance"),
                "HtPiano", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _take = loaded;
        _takeNeedsSaving = false;
        StartTakePlayback();
    }

    private void ReportRecordingError(string message, Exception error) =>
        MessageBox.Show(this, message + Environment.NewLine + error.Message, "HtPiano",
            MessageBoxButton.OK, MessageBoxImage.Warning);
}
