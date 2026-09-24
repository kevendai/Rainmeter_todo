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
                var timeBox = FindTimeWheelTarget(args.OriginalSource as DependencyObject);
                if (timeBox is not null && !timeBox.IsDropDownOpen)
                {
                    var step = args.GetCurrentPoint(timeBox).Properties.MouseWheelDelta;
                    if (step != 0)
                    {
                        timeBox.SelectedIndex = Math.Clamp(timeBox.SelectedIndex - Math.Sign(step),
                            0, timeBox.Items.Count - 1);
                        args.Handled = true;
                    }
                    return;
                }
                if (scroller.ScrollableHeight <= 0) return;
                var delta = args.GetCurrentPoint(scroller).Properties.MouseWheelDelta;
                if (delta == 0) return;
                scroller.ChangeView(null, Math.Clamp(scroller.VerticalOffset - delta / 2.0,
                    0, scroller.ScrollableHeight), null, true);
                args.Handled = true;
            }), true);
    }

    private static ComboBox? FindTimeWheelTarget(DependencyObject? target)
    {
        while (target is not null)
        {
            if (target is ComboBox { Tag: "time-wheel" } box) return box;
            target = VisualTreeHelper.GetParent(target);
        }
        return null;
    }

    private ComboBox TimePart(int count, int selected)
    {
        var box = EditorControl(new ComboBox { MinWidth = 78, Tag = "time-wheel" });
        for (var value = 0; value < count; value++) box.Items.Add(value.ToString("00"));
        box.SelectedIndex = selected;
        return box;
    }

    private static DateTimeOffset LocalPickerDate(DateTime value)
        => new(value.Date, TimeZoneInfo.Local.GetUtcOffset(value.Date));

    private static DateTimeOffset? ExistingLocalDate(string? iso)
        => DateTimeOffset.TryParse(iso, out var value) ? value.ToLocalTime() : null;

    private static string? PickerIso(CalendarDatePicker date, ComboBox hour, ComboBox minute)
    {
        if (date.Date is null) return null;
        var local = date.Date.Value.LocalDateTime.Date
            .AddHours(hour.SelectedIndex).AddMinutes(minute.SelectedIndex);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToString("O");
    }

    private StackPanel CompactTodoTimeRow(string label, CalendarDatePicker date,
        ComboBox hour, ComboBox minute, bool clearDate = true)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        if (clearDate) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
        date.PlaceholderText = "选择日期";
        date.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.Children.Add(date);
        hour.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(hour, 1);
        row.Children.Add(hour);
        minute.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(minute, 2);
        row.Children.Add(minute);
        if (clearDate)
        {
            var clear = Action("清除日期", () => date.Date = null);
            clear.MinHeight = 38;
            Grid.SetColumn(clear, 3);
            row.Children.Add(clear);
        }
        var section = new StackPanel { Spacing = 5 };
        section.Children.Add(Text(label, 13, true));
        section.Children.Add(row);
        return section;
    }

}
