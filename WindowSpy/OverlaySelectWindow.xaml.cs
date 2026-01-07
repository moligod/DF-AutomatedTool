using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace WindowSpy
{
    public partial class OverlaySelectWindow : Window
    {
        private Point _startPoint;
        private bool _isDragging;

        public System.Drawing.Rectangle SelectedRect { get; private set; }

        public OverlaySelectWindow()
        {
            InitializeComponent();
            Cursor = Cursors.Cross;
        }

        private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _startPoint = e.GetPosition(RootCanvas);
            SelectionRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(SelectionRect, _startPoint.X);
            Canvas.SetTop(SelectionRect, _startPoint.Y);
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
            RootCanvas.CaptureMouse();
        }

        private void RootCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;

            var currentPoint = e.GetPosition(RootCanvas);
            var x = Math.Min(_startPoint.X, currentPoint.X);
            var y = Math.Min(_startPoint.Y, currentPoint.Y);
            var w = Math.Abs(_startPoint.X - currentPoint.X);
            var h = Math.Abs(_startPoint.Y - currentPoint.Y);

            Canvas.SetLeft(SelectionRect, x);
            Canvas.SetTop(SelectionRect, y);
            SelectionRect.Width = w;
            SelectionRect.Height = h;
        }

        private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            RootCanvas.ReleaseMouseCapture();

            var left = Canvas.GetLeft(SelectionRect);
            var top = Canvas.GetTop(SelectionRect);
            var width = SelectionRect.Width;
            var height = SelectionRect.Height;

            var topLeft = RootCanvas.PointToScreen(new Point(left, top));
            
            SelectedRect = new System.Drawing.Rectangle((int)topLeft.X, (int)topLeft.Y, (int)width, (int)height);

            DialogResult = true;
            Close();
        }
    }
}
