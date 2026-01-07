using System.Windows;
using System.Windows.Controls;

namespace WindowSpy
{
    public class InputDialog : Window
    {
        private TextBox _textBox;
        public string InputText => _textBox.Text;

        public InputDialog(string title, string prompt)
        {
            Title = title;
            Width = 400;
            Height = 200;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var stack = new StackPanel { Margin = new Thickness(10) };
            
            stack.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 5) });
            
            _textBox = new TextBox { Height = 80, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            stack.Children.Add(_textBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            
            var btnOk = new Button { Content = "确定", Width = 80, Height = 26, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
            btnOk.Click += (s, e) => { DialogResult = true; Close(); };
            
            var btnCancel = new Button { Content = "取消", Width = 80, Height = 26, IsCancel = true };
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            stack.Children.Add(btnPanel);

            Content = stack;
        }
    }
}
