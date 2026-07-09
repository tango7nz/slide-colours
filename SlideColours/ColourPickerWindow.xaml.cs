using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SlideColours.Services;
using WpfColor = System.Windows.Media.Color;

namespace SlideColours;

/// <summary>
/// A modern dark HSV colour picker: a saturation/value square, a hue bar, a hex field and a
/// live preview. Styled to match the floating pill.
/// </summary>
public partial class ColourPickerWindow : Window
{
    private const double SvW = 230, SvH = 170, HueH = 170;

    private double _hue;   // 0..360
    private double _sat;   // 0..1
    private double _val;   // 0..1

    public WpfColor SelectedColour => ColorExtractor.HsvToRgb(_hue, _sat, _val);

    public ColourPickerWindow(WpfColor initial)
    {
        InitializeComponent();

        ColorExtractor.RgbToHsv(initial.R, initial.G, initial.B, out _hue, out _sat, out _val);
        SyncUi();

        Loaded += (_, _) => HexBox.Focus();
    }

    private void SyncUi()
    {
        SvBase.Fill = new SolidColorBrush(ColorExtractor.HsvToRgb(_hue, 1, 1));

        Canvas.SetLeft(SvThumb, _sat * SvW - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, (1 - _val) * SvH - SvThumb.Height / 2);
        Canvas.SetTop(HueThumb, _hue / 360.0 * HueH - HueThumb.Height / 2);

        var colour = SelectedColour;
        PreviewBrush.Color = colour;
        HexBox.Text = $"{colour.R:X2}{colour.G:X2}{colour.B:X2}";
    }

    // --- Saturation / Value square ---

    private void SvCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SvCanvas.CaptureMouse();
        UpdateSv(e.GetPosition(SvCanvas));
    }

    private void SvCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && SvCanvas.IsMouseCaptured)
            UpdateSv(e.GetPosition(SvCanvas));
    }

    private void SvCanvas_MouseUp(object sender, MouseButtonEventArgs e) => SvCanvas.ReleaseMouseCapture();

    private void UpdateSv(Point p)
    {
        _sat = Math.Clamp(p.X / SvW, 0, 1);
        _val = 1 - Math.Clamp(p.Y / SvH, 0, 1);
        SyncUi();
    }

    // --- Hue bar ---

    private void HueCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HueCanvas.CaptureMouse();
        UpdateHue(e.GetPosition(HueCanvas));
    }

    private void HueCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && HueCanvas.IsMouseCaptured)
            UpdateHue(e.GetPosition(HueCanvas));
    }

    private void HueCanvas_MouseUp(object sender, MouseButtonEventArgs e) => HueCanvas.ReleaseMouseCapture();

    private void UpdateHue(Point p)
    {
        _hue = Math.Clamp(p.Y / HueH, 0, 1) * 360.0;
        SyncUi();
    }

    // --- Hex entry ---

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHex();
            e.Handled = true;
        }
    }

    private void HexBox_LostFocus(object sender, RoutedEventArgs e) => CommitHex();

    private void CommitHex()
    {
        string text = HexBox.Text.Trim().TrimStart('#');
        if (text.Length == 3)
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2]);

        if (text.Length == 6
            && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            var colour = WpfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            ColorExtractor.RgbToHsv(colour.R, colour.G, colour.B, out _hue, out _sat, out _val);
        }

        SyncUi(); // also repairs invalid text back to the current colour
    }

    // --- Window chrome ---

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
