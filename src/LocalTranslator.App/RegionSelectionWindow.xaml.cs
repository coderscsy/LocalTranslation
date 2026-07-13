using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalTranslator.Core.Models;

namespace LocalTranslator.App;

public partial class RegionSelectionWindow : Window
{
    private System.Windows.Point? _startPoint;
    private NativePoint _startScreenPixel;

    public RegionSelectionWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public ScreenRegion SelectedRegion { get; private set; }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(this);
        GetCursorPos(out _startScreenPixel);
        SelectionBorder.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_startPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateSelection(e.GetPosition(this));
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_startPoint is null)
        {
            return;
        }

        var endPoint = e.GetPosition(this);
        GetCursorPos(out var endScreenPixel);
        UpdateSelection(endPoint);
        ReleaseMouseCapture();

        SelectedRegion = new ScreenRegion(
            Math.Min(_startScreenPixel.X, endScreenPixel.X),
            Math.Min(_startScreenPixel.Y, endScreenPixel.Y),
            Math.Abs(endScreenPixel.X - _startScreenPixel.X),
            Math.Abs(endScreenPixel.Y - _startScreenPixel.Y));

        DialogResult = SelectedRegion.IsValid;
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    private void UpdateSelection(System.Windows.Point currentPoint)
    {
        var x = Math.Min(_startPoint!.Value.X, currentPoint.X);
        var y = Math.Min(_startPoint.Value.Y, currentPoint.Y);
        SelectionBorder.Width = Math.Abs(currentPoint.X - _startPoint.Value.X);
        SelectionBorder.Height = Math.Abs(currentPoint.Y - _startPoint.Value.Y);
        Canvas.SetLeft(SelectionBorder, x);
        Canvas.SetTop(SelectionBorder, y);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
