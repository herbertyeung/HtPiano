using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HtPiano;

internal sealed class PianoBoard : Panel
{
    public static bool IsBlack(int offset) => (offset - PianoInput.LowestOffset) % 12 is 1 or 3 or 6 or 8 or 10;
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (PianoKey key in InternalChildren)
        {
            key.Measure(availableSize);
        }

        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size size)
    {
        int whiteCount = 0;
        foreach (PianoKey key in InternalChildren)
        {
            if (key.Visibility != Visibility.Collapsed && !key.IsBlack)
            {
                whiteCount++;
            }
        }

        if (whiteCount == 0)
        {
            return size;
        }

        double whiteWidth = size.Width / whiteCount;
        int whiteIndex = 0;
        foreach (PianoKey key in InternalChildren)
        {
            if (key.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            double x = whiteIndex * whiteWidth;
            if (key.IsBlack)
            {
                double width = whiteWidth * 0.62;
                key.Arrange(new Rect(x - width / 2, 0, width, size.Height * 0.64));
            }
            else
            {
                key.Arrange(new Rect(x + 1, 0, Math.Max(0, whiteWidth - 2), size.Height));
                whiteIndex++;
            }
        }
        return size;
    }
}

internal sealed class PianoKey : Button
{
    private static readonly Brush White = ColorBrush(244, 237, 217);
    private static readonly Brush Black = ColorBrush(27, 38, 37);
    private static readonly Brush Held = ColorBrush(124, 214, 175);
    private static readonly Brush Sustained = ColorBrush(248, 208, 133);
    private static readonly Brush ScreenInk = ColorBrush(16, 29, 26);
    private static readonly Brush DarkEdge = ColorBrush(10, 21, 19);
    private static readonly Brush LightEdge = ColorBrush(174, 167, 142);
    private readonly TextBlock _state = new() { FontSize = 9, FontWeight = FontWeights.Bold, Height = 13 };
    private readonly TextBlock _note = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _binding = new() { FontSize = 11, Opacity = 0.86 };
    private NoteState? _displayedState;
    private bool _replaying;

    public int Offset { get; }
    public bool IsBlack => PianoBoard.IsBlack(Offset);

    public PianoKey(int offset)
    {
        Offset = offset;
        SetResourceReference(AutomationProperties.HelpTextProperty, "Lang.PianoHelp");
        _note.FontSize = IsBlack ? 13 : 17;
        var labels = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (TextBlock label in new[] { _state, _note, _binding })
        {
            label.TextAlignment = TextAlignment.Center;
            labels.Children.Add(label);
        }

        Content = labels;
        Panel.SetZIndex(this, IsBlack ? 1 : 0);
        KeyboardNavigation.SetTabIndex(this, offset);
    }

    public void UpdateLabel(string note, string? key, bool compact)
    {
        _note.Text = note;
        _binding.Text = key ?? "";
        _binding.Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
        _note.FontSize = compact ? (IsBlack ? 9 : 12) : (IsBlack ? 13 : 17);
        AutomationProperties.SetName(this, key == null ? note : UiLanguage.Format("Lang.TypingKeyHint", note, key));
    }

    public void UpdateState(NoteState state) => UpdateState(state, replaying: false);

    public bool ShowPlaybackState(bool sounding) =>
        UpdateState(sounding ? NoteState.Held : NoteState.Idle, replaying: sounding);

    private bool UpdateState(NoteState state, bool replaying)
    {
        if (_displayedState == state && _replaying == replaying) return false;
        _displayedState = state;
        _replaying = replaying;
        if (state == NoteState.Idle) _state.Text = "";
        else _state.SetResourceReference(TextBlock.TextProperty,
            replaying ? "Lang.BoardReplay" : state == NoteState.Held ? "Lang.BoardHeld" : "Lang.BoardPedal");
        Background = state switch { NoteState.Held => Held, NoteState.Sustained => Sustained, _ => IsBlack ? Black : White };
        Foreground = state == NoteState.Idle && IsBlack ? White : ScreenInk;
        BorderBrush = IsBlack ? DarkEdge : LightEdge;
        SetResourceReference(AutomationProperties.ItemStatusProperty, replaying ? "Lang.BoardSounding" : state switch
        {
            NoteState.Held => "Lang.KeyDown", NoteState.Sustained => "Lang.BoardKeySustained", _ => "Lang.BoardOff"
        });
        return true;
    }

    private static Brush ColorBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
