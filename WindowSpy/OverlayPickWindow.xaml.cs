using System;
using System.Windows;
using System.Windows.Input;

namespace WindowSpy
{
    public partial class OverlayPickWindow : Window
    {
        public Point ClickPoint { get; private set; }

        public OverlayPickWindow()
        {
            InitializeComponent();
            Cursor = Cursors.Cross;
        }

        private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Optional: Handle initial click logic if needed
        }

        private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
             var pos = e.GetPosition(this);
             var screenPos = PointToScreen(pos);
             ClickPoint = screenPos;
             DialogResult = true;
             Close();
        }
    }
}
