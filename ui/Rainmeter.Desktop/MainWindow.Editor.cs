using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;

namespace Rainmeter.Desktop;

public sealed partial class MainWindow
{
    private Brush EditorSurface => DarkTheme ? Brush(41, 35, 51) : Brush(250, 248, 252);
    private Brush EditorField => DarkTheme ? Brush(53, 47, 64) : Brush(242, 238, 245);
    private Brush EditorStroke => DarkTheme ? Brush(68, 59, 80) : Brush(221, 213, 226);

    private T EditorControl<T>(T control) where T : Control
    {
        control.MinHeight = 40;
        control.Background = EditorField;
        control.BorderBrush = EditorStroke;
        control.BorderThickness = new Thickness(1);
        control.CornerRadius = new CornerRadius(9);
        control.Foreground = Ink;
        control.Resources["TextControlBorderBrushFocused"] = PrimaryFill;
        return control;
    }

    private ContentDialog EditorDialog(string title, UIElement content, string primaryText)
    {
        var primaryStyle = new Style(typeof(Button))
        {
            BasedOn = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        primaryStyle.Setters.Add(new Setter(Control.BackgroundProperty, PrimaryFill));
        primaryStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush(255, 255, 255)));
        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            RequestedTheme = DarkTheme ? ElementTheme.Dark : ElementTheme.Light,
            Background = EditorSurface,
            Foreground = Ink,
            CornerRadius = new CornerRadius(20),
            Title = title,
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            },
            Width = 600,
            PrimaryButtonText = primaryText,
            PrimaryButtonStyle = primaryStyle,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["AccentFillColorDefaultBrush"] = PrimaryFill;
        dialog.Resources["AccentFillColorSecondaryBrush"] = PrimaryFill;
        if (dialog.Content is ScrollViewer scroller)
            ForwardHandledWheel(content, scroller);
        return dialog;
    }

    private static void ForwardHandledWheel(UIElement content, ScrollViewer scroller)
    {
        content.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, args) =>
            {
                if (!args.Handled || scroller.ScrollableHeight <= 0) return;
                var delta = args.GetCurrentPoint(scroller).Properties.MouseWheelDelta;
                scroller.ChangeView(null, Math.Clamp(scroller.VerticalOffset - delta / 2.0,
                    0, scroller.ScrollableHeight), null, true);
            }), true);
    }

    private static DateTimeOffset LocalPickerDate(DateTime value)
        => new(value.Date, TimeZoneInfo.Local.GetUtcOffset(value.Date));

    private static DateTimeOffset? ExistingLocalDate(string? iso)
        => DateTimeOffset.TryParse(iso, out var value) ? value.ToLocalTime() : null;

    private static string? PickerIso(CalendarDatePicker date, TimePicker time)
    {
        if (date.Date is null) return null;
        var local = date.Date.Value.LocalDateTime.Date.Add(time.Time);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToString("O");
    }

    private StackPanel EditorDateTimeRow(string label, CalendarDatePicker date, TimePicker time)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(265) });
        var caption = Text(label, 13, true);
        date.HorizontalAlignment = HorizontalAlignment.Stretch;
        date.PlaceholderText = "选择日期";
        row.Children.Add(date);
        time.HorizontalAlignment = HorizontalAlignment.Stretch;
        time.ClockIdentifier = "24HourClock";
        time.MinuteIncrement = 5;
        Grid.SetColumn(time, 1);
        row.Children.Add(time);
        var section = new StackPanel { Spacing = 5 };
        section.Children.Add(caption);
        section.Children.Add(row);
        return section;
    }
}
