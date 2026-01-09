using System;
using System.Windows;

namespace WindowSpy
{
    public partial class EditStepWindow : Window
    {
        private ScriptStep _step;

        public EditStepWindow(ScriptStep step)
        {
            InitializeComponent();
            _step = step;
            LoadData();
        }

        private void LoadData()
        {
            TitleText.Text = $"编辑步骤: {_step.Type} ({_step.Target})";

            // Init values
            TxtDelay.Text = _step.DelayMs.ToString();
            TxtRandomDelay.Text = _step.RandomDelay.ToString();
            TxtDwell.Text = _step.DwellMs.ToString();
            TxtRandomDwell.Text = _step.RandomDwell.ToString();
            TxtPointX.Text = _step.Point.X.ToString();
            TxtPointY.Text = _step.Point.Y.ToString();
            TxtRandomX.Text = _step.RandomX.ToString();
            TxtRandomY.Text = _step.RandomY.ToString();
            TxtRectX.Text = _step.Rect.X.ToString();
            TxtRectY.Text = _step.Rect.Y.ToString();
            TxtRectW.Text = _step.Rect.Width.ToString();
            TxtRectH.Text = _step.Rect.Height.ToString();
            TxtCount.Text = _step.Count.ToString();
            TxtPattern.Text = _step.Pattern;
            TxtKey.Text = _step.Key;
            ChkJump.IsChecked = _step.JumpOnTrue;

            // Visibility Logic
            PanelDelay.Visibility = Visibility.Collapsed;
            PanelPoint.Visibility = Visibility.Collapsed;
            PanelDwell.Visibility = Visibility.Collapsed;
            PanelRect.Visibility = Visibility.Collapsed;
            PanelOcrOpt.Visibility = Visibility.Collapsed;
            PanelCount.Visibility = Visibility.Collapsed;
            PanelPattern.Visibility = Visibility.Collapsed;
            TxtExprHelp.Visibility = Visibility.Collapsed;
            PanelKey.Visibility = Visibility.Collapsed;
            PanelJump.Visibility = Visibility.Collapsed;

            // Common for most actions
            if (_step.Type != ActionType.LoopEnd)
            {
                PanelDelay.Visibility = Visibility.Visible;
            }

            switch (_step.Type)
            {
                case ActionType.Click:
                    PanelPoint.Visibility = Visibility.Visible;
                    PanelDwell.Visibility = Visibility.Visible;
                    break;
                case ActionType.Ocr:
                    PanelRect.Visibility = Visibility.Visible;
                    PanelOcrOpt.Visibility = Visibility.Visible;
                    ChkOcrNums.IsChecked = _step.OcrNumbersOnly;
                    ChkReuseOcrRoi.IsChecked = _step.ReuseOcrOnRoiUnchanged;
                    PanelDwell.Visibility = Visibility.Visible; // Dwell is reused as dwell time after ocr? No, in RunScript it's not used for OCR usually but let's keep it if logic uses it. Checked RunScript: OCR doesn't use DwellMs.
                    PanelDwell.Visibility = Visibility.Collapsed;
                    break;
                case ActionType.Condition:
                    PanelPattern.Visibility = Visibility.Visible;
                    PanelKey.Visibility = Visibility.Visible;
                    PanelJump.Visibility = Visibility.Visible;
                    LblPattern.Text = "正则模式:";
                    break;
                case ActionType.Expression:
                    PanelPattern.Visibility = Visibility.Visible;
                    TxtExprHelp.Visibility = Visibility.Visible;
                    PanelJump.Visibility = Visibility.Visible;
                    LblPattern.Text = "表达式:";
                    break;
                case ActionType.IfStart:
                case ActionType.ElseIf:
                    PanelPattern.Visibility = Visibility.Visible;
                    TxtExprHelp.Visibility = Visibility.Visible;
                    LblPattern.Text = "表达式:";
                    break;
                case ActionType.Save:
                    PanelKey.Visibility = Visibility.Visible;
                    PanelDelay.Visibility = Visibility.Collapsed; // Save is usually instant
                    break;
                case ActionType.LoopStart:
                    PanelCount.Visibility = Visibility.Visible;
                    PanelDelay.Visibility = Visibility.Collapsed;
                    break;
                case ActionType.BringFront:
                    // Only Delay
                    break;
            }
        }

        private void BtnPickPoint_Click(object sender, RoutedEventArgs e)
        {
            if (Owner is not MainWindow main) return;
            var hwnd = main.GetBoundHwnd(_step.Target);
            if (hwnd == IntPtr.Zero)
            {
                MessageBox.Show($"{_step.Target}窗口未绑定", "提示");
                return;
            }
            
            var picker = new OverlayPickWindow();
            if (picker.ShowDialog() == true)
            {
                var wrect = NativeMethods.GetRect(hwnd);
                int sx = (int)picker.ClickPoint.X;
                int sy = (int)picker.ClickPoint.Y;
                if (sx < wrect.Left || sy < wrect.Top || sx >= wrect.Right || sy >= wrect.Bottom)
                {
                    MessageBox.Show("点击位置不在目标窗口内", "提示");
                    return;
                }
                TxtPointX.Text = (sx - wrect.Left).ToString();
                TxtPointY.Text = (sy - wrect.Top).ToString();
            }
        }

        private void BtnSelectRect_Click(object sender, RoutedEventArgs e)
        {
            if (Owner is not MainWindow main) return;
            var hwnd = main.GetBoundHwnd(_step.Target);
            if (hwnd == IntPtr.Zero)
            {
                MessageBox.Show($"{_step.Target}窗口未绑定", "提示");
                return;
            }

            var overlay = new OverlaySelectWindow();
            if (overlay.ShowDialog() == true)
            {
                var sel = overlay.SelectedRect;
                var wrect = NativeMethods.GetRect(hwnd);
                var interLeft = Math.Max(wrect.Left, (int)sel.Left);
                var interTop = Math.Max(wrect.Top, (int)sel.Top);
                var interRight = Math.Min(wrect.Right, (int)(sel.Left + sel.Width));
                var interBottom = Math.Min(wrect.Bottom, (int)(sel.Top + sel.Height));

                if (interRight <= interLeft || interBottom <= interTop)
                {
                    MessageBox.Show("选择区域不在目标窗口内", "提示");
                    return;
                }
                
                TxtRectX.Text = (interLeft - wrect.Left).ToString();
                TxtRectY.Text = (interTop - wrect.Top).ToString();
                TxtRectW.Text = (interRight - interLeft).ToString();
                TxtRectH.Text = (interBottom - interTop).ToString();
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxtDelay.Text, out int delay)) _step.DelayMs = delay;
            if (int.TryParse(TxtRandomDelay.Text, out int rDelay)) _step.RandomDelay = rDelay;
            if (int.TryParse(TxtDwell.Text, out int dwell)) _step.DwellMs = dwell;
            if (int.TryParse(TxtRandomDwell.Text, out int rDwell)) _step.RandomDwell = rDwell;
            
            if (int.TryParse(TxtPointX.Text, out int px) && int.TryParse(TxtPointY.Text, out int py))
                _step.Point = new System.Drawing.Point(px, py);

            if (int.TryParse(TxtRandomX.Text, out int rx)) _step.RandomX = rx;
            if (int.TryParse(TxtRandomY.Text, out int ry)) _step.RandomY = ry;
            
            if (int.TryParse(TxtRectX.Text, out int rx2) && int.TryParse(TxtRectY.Text, out int ry2) &&
                int.TryParse(TxtRectW.Text, out int rw) && int.TryParse(TxtRectH.Text, out int rh))
                _step.Rect = new System.Drawing.Rectangle(rx2, ry2, rw, rh);

            if (int.TryParse(TxtCount.Text, out int count)) _step.Count = count;

            _step.Pattern = TxtPattern.Text;
            _step.Key = TxtKey.Text;
            _step.JumpOnTrue = ChkJump.IsChecked == true;
            _step.OcrNumbersOnly = ChkOcrNums.IsChecked == true;
            _step.ReuseOcrOnRoiUnchanged = ChkReuseOcrRoi.IsChecked == true;

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
