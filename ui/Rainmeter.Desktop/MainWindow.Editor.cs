using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Composition.SystemBackdrops;
using System.Runtime.CompilerServices;
using Windows.UI;

namespace Rainmeter.Desktop;

public sealed partial class MainWindow
{
    private Brush EditorSurface => DarkTheme
        ? Brush(41, 35, 51, studioStyle ? 42 : 62)
        : Brush(250, 247, 253, studioStyle ? 42 : 62);
    private Brush EditorField => DarkTheme
        ? studioStyle ? Brush(59, 51, 70, 185) : Brush(53, 47, 64, 220)
        : studioStyle ? Brush(249, 246, 253, 195) : Brush(242, 238, 245, 224);
    private Brush EditorStroke => DarkTheme ? Brush(180, 161, 198, 76) : Brush(108, 82, 139, 70);
    private Brush DialogStroke => DarkTheme
        ? Brush(255, 255, 255, studioStyle ? 52 : 36)
        : Brush(255, 255, 255, studioStyle ? 185 : 145);

    private ContentDialog MaterialDialog(ContentDialog dialog)
    {
        if (!dialog.Resources.ContainsKey("ContentDialogMaxWidth"))
            SetDialogWidth(dialog, 480, false);
        var primaryStyle = new Style(typeof(Button))
        {
            BasedOn = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        primaryStyle.Setters.Add(new Setter(Control.BackgroundProperty, PrimaryFill));
        primaryStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush(255, 255, 255)));
        var secondaryStyle = new Style(typeof(Button));
        secondaryStyle.Setters.Add(new Setter(Control.BackgroundProperty, EditorField));
        secondaryStyle.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        secondaryStyle.Setters.Add(new Setter(Control.BorderBrushProperty, EditorStroke));
        secondaryStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        secondaryStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));

        dialog.UseLayoutRounding = true;
        dialog.XamlRoot = Shell.XamlRoot;
        dialog.RequestedTheme = DarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        dialog.Background = EditorSurface;
        dialog.Foreground = Ink;
        dialog.BorderBrush = DialogStroke;
        dialog.BorderThickness = new Thickness(1);
        dialog.CornerRadius = new CornerRadius(20);
        if (dialog.Title is string title) dialog.Title = Text(title, 21, true);
        dialog.PrimaryButtonStyle = primaryStyle;
        dialog.SecondaryButtonStyle = secondaryStyle;
        dialog.CloseButtonStyle = secondaryStyle;
        dialog.Resources["ContentDialogTopOverlay"] = DarkTheme
            ? Brush(255, 255, 255, 10)
            : Brush(255, 255, 255, 18);
        dialog.Resources["AccentFillColorDefaultBrush"] = PrimaryFill;
        dialog.Resources["AccentFillColorSecondaryBrush"] = PrimaryFill;
        dialog.Loaded += (_, _) => AddDialogBackdrop(dialog);
        return dialog;
    }

    private void AddDialogBackdrop(ContentDialog dialog)
    {
        static T? FindNamed<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            if (parent is T element && element.Name == name) return element;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FindNamed<T>(VisualTreeHelper.GetChild(parent, index), name) is { } found)
                    return found;
            return null;
        }

        var background = FindNamed<Border>(dialog, "BackgroundElement");
        if (background?.Child is not Grid original ||
            original.Children.OfType<SystemBackdropElement>().Any()) return;
        var backdrop = new SystemBackdropElement
        {
            CornerRadius = dialog.CornerRadius,
            Margin = new Thickness(0),
            SystemBackdrop = studioStyle
                ? new DesktopAcrylicBackdrop()
                : new MicaBackdrop { Kind = MicaKind.Base }
        };
        // Cover the outer surface rather than the padded title/content/button grid.
        var surface = new Grid { CornerRadius = dialog.CornerRadius };
        background.Child = null;
        surface.Children.Add(backdrop);
        surface.Children.Add(original);
        background.Child = surface;
        if (FindNamed<ScrollViewer>(dialog, "ContentScrollViewer")?.Content is Grid contentGrid)
            contentGrid.Background = Brush(255, 255, 255, 0);
    }

    private T EditorControl<T>(T control) where T : Control
    {
        control.MinHeight = 40;
        control.Background = EditorField;
        control.BorderBrush = EditorStroke;
        control.BorderThickness = new Thickness(1);
        control.CornerRadius = new CornerRadius(9);
        control.Foreground = Ink;
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (control is TextBox text)
        {
            text.VerticalContentAlignment = text.AcceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center;
            text.Padding = text.AcceptsReturn ? new Thickness(12, 10, 12, 10) : new Thickness(12, 8, 12, 8);
        }
        else if (control is PasswordBox password)
        {
            password.VerticalContentAlignment = VerticalAlignment.Center;
            password.Padding = new Thickness(12, 8, 12, 8);
        }
        control.Resources["TextControlBorderBrushFocused"] = PrimaryFill;
        return control;
    }

    private void SetDialogWidth(ContentDialog dialog, double preferredWidth, bool fixedWidth)
    {
        var availableWidth = Shell.ActualWidth > 0 ? Math.Max(0, Shell.ActualWidth - 48) : preferredWidth;
        var width = Math.Min(preferredWidth, availableWidth);
        dialog.Resources["ContentDialogMinWidth"] = fixedWidth ? width : Math.Min(320, width);
        dialog.Resources["ContentDialogMaxWidth"] = width;
    }

    private ContentDialog EditorDialog(string title, UIElement content, string primaryText,
        double preferredWidth = 520)
    {
        var dialog = new ContentDialog
        {
            Title = Text(title, 23, true),
            Content = new ScrollViewer
            {
                UseLayoutRounding = true,
                Content = content,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        SetDialogWidth(dialog, preferredWidth, true);
        if (dialog.Content is ScrollViewer scroller)
            ForwardHandledWheel(content, scroller);
        return MaterialDialog(dialog);
    }

    private static Action<DependencyObject> ForwardHandledWheel(UIElement content, ScrollViewer scroller)
    {
        var registered = new HashSet<UIElement>();
        var seen = new ConditionalWeakTable<PointerRoutedEventArgs, object>();
        var handler = new PointerEventHandler((_, args) =>
            {
                // A wheel event can visit several registered ancestors. Only the
                // first (deepest) handler may route it.
                if (seen.TryGetValue(args, out _)) return;
                seen.Add(args, new object());
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
                // Handle the wheel on the content before the parent ScrollViewer sees it.
                // Cards, text fields and pickers do not consistently let WinUI's native
                // wheel handling run, particularly after reaching the top edge.
                if (scroller.ScrollableHeight <= 0) return;
                var delta = args.GetCurrentPoint(scroller).Properties.MouseWheelDelta;
                if (delta == 0) return;
                var next = Math.Clamp(scroller.VerticalOffset - delta / 2.0,
                    0, scroller.ScrollableHeight);
                if (Math.Abs(next - scroller.VerticalOffset) >= 0.5)
                    scroller.ChangeView(null, next, null, true);
                args.Handled = true;
            });
        void Register(DependencyObject node)
        {
            if (node is UIElement element && registered.Add(element))
                element.AddHandler(UIElement.PointerWheelChangedEvent, handler, true);
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                Register(VisualTreeHelper.GetChild(node, index));
        }
        Register(content);
        if (content is FrameworkElement framework)
            framework.Loaded += (_, _) => Register(content);
        return Register;
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
