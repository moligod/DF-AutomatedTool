using System;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Diagnostics;
using Microsoft.Win32;
using System.Text.Json;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using WindowSpy.Ocr;

namespace WindowSpy
{
    public partial class MainWindow : System.Windows.Window
    {
        private IntPtr _capturedHwnd = IntPtr.Zero;
        private bool _dragging = false;
        private IntPtr _boundAHwnd = IntPtr.Zero;
        private IntPtr _boundBHwnd = IntPtr.Zero;
        private System.Drawing.Rectangle? _rectA = null;
        private System.Drawing.Rectangle? _rectB = null;
        private System.Drawing.Point? _clickA = null;
        private System.Drawing.Point? _clickB = null;
        private readonly OnnxOcrHelper _ocr = new();
        private volatile bool _stopAll = false;
        private bool _bindingHotkey = false;
        private System.Windows.Input.Key _stopKey = System.Windows.Input.Key.F12;
        private bool _stopCtrl = false, _stopAlt = false, _stopShift = false;
        private IntPtr _hwnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1001;
        private bool _singleModifierOnly = false;
        private System.Windows.Input.ModifierKeys _singleModifier = System.Windows.Input.ModifierKeys.None;
        private System.Windows.Input.ModifierKeys _pressedModsDuringBinding = System.Windows.Input.ModifierKeys.None;
        private bool _nonModifierPressedDuringBinding = false;
        private readonly System.Collections.Generic.List<ScriptStep> _steps = new();
        private int _dragIndex = -1;
        private readonly System.Collections.Generic.Dictionary<string, string> _vars = new();
        private NativeMethods.LowLevelMouseProc _hookProc;
        private IntPtr _hookID = IntPtr.Zero;
        private readonly System.Collections.Generic.Queue<DateTime> _rightClickTimes = new();

        private readonly Random _rng = new Random();

        public MainWindow()
        {
            InitializeComponent();
            _ocr.Logger = AppendLog;
            _ocr.UseGpu = true; // 默认尝试使用 GPU
            _hookProc = HookCallback;
            
            // 绑定 CheckBox 事件
            if (UseGpuCheck != null)
            {
                UseGpuCheck.Checked += (s, e) => { if (_ocr != null) { _ocr.UseGpu = true; AppendLog("设置：已启用 GPU 加速请求"); } };
                UseGpuCheck.Unchecked += (s, e) => { if (_ocr != null) { _ocr.UseGpu = false; AppendLog("设置：已强制切换为 CPU 模式"); } };
            }
        }
        private volatile bool _isRunning = false;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            _hwnd = helper.Handle;
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);
            RegisterStopHotkey();
        }
        protected override void OnClosed(EventArgs e)
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
            base.OnClosed(e);
        }
        private void RegisterStopHotkey()
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
            // 仅在运行时或绑定后显示文本更新，但只有运行时才真正生效，这里只负责更新UI文本
            string label;
            if (_singleModifierOnly)
            {
                label = $"{(_singleModifier.HasFlag(System.Windows.Input.ModifierKeys.Control) ? "Ctrl" : _singleModifier.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? "Alt" : "Shift")}";
            }
            else
            {
                label = $"{(_stopCtrl ? "Ctrl+" : "")}{(_stopAlt ? "Alt+" : "")}{(_stopShift ? "Shift+" : "")}{_stopKey}";
            }
            if (StopAllButton != null) StopAllButton.Content = $"停止全部步骤({label})";
            
            // 如果正在运行，则立即注册
            if (_isRunning)
            {
                DoRegisterHotkey();
            }
        }
        
        private void DoRegisterHotkey()
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
            
            if (_singleModifierOnly)
            {
                // 单修饰键无法通过 RegisterHotKey 注册，只能通过键盘钩子或 KeyDown 事件捕获
                // 这里暂不处理，依赖全局键盘钩子或者仅支持组合键
                // 如果必须支持单键，需要全局钩子。目前代码使用 RegisterHotKey，所以单键实际上在后台无法生效。
                // 为了简单起见，如果用户设置了单键，我们尝试注册一个特殊的无效热键或提示
                // 现有的 PreviewKeyDown 逻辑只在窗口激活时有效。
                // 若要全局生效，必须用 RegisterHotKey。RegisterHotKey 不支持单 Ctrl/Shift。
                // 因此这里如果用户设置单键，仅在窗口激活时有效，不注册全局热键。
                return; 
            }

            uint mods = 0;
            if (_stopCtrl) mods |= 0x0002;
            if (_stopAlt) mods |= 0x0001;
            if (_stopShift) mods |= 0x0004;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(_stopKey);
            if (vk != 0)
            {
                NativeMethods.RegisterHotKey(_hwnd, HOTKEY_ID, mods, vk);
            }
        }

        private void UnregisterStopHotkey()
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
        }

        private void InstallHook()
        {
            if (_hookID != IntPtr.Zero) return;
            bool enabled = false;
            Dispatcher.Invoke(() => enabled = FailSafeCheck?.IsChecked == true);
            if (!enabled) return;

            lock (_rightClickTimes) _rightClickTimes.Clear();
            using (Process curProcess = Process.GetCurrentProcess())
            {
                using ProcessModule? curModule = curProcess.MainModule;
                if (curModule != null)
                {
                    _hookID = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _hookProc, NativeMethods.GetModuleHandle(curModule.ModuleName), 0);
                }
            }
        }

        private void UninstallHook()
        {
            if (_hookID != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (int)wParam == NativeMethods.WM_RBUTTONDOWN)
            {
                lock (_rightClickTimes)
                {
                    var now = DateTime.Now;
                    _rightClickTimes.Enqueue(now);
                    
                    // 移除5秒前的记录
                    while (_rightClickTimes.Count > 0 && (now - _rightClickTimes.Peek()).TotalSeconds > 5)
                    {
                        _rightClickTimes.Dequeue();
                    }

                    if (_rightClickTimes.Count >= 10)
                    {
                        _stopAll = true;
                        Dispatcher.Invoke(() => AppendLog("触发防卡死保护(5秒内右键10次)"));
                        _rightClickTimes.Clear(); // 防止重复触发
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                _stopAll = true;
                AppendLog("系统快捷键停止全部步骤");
                handled = true; // 表示消息已处理，但这可能会阻止其他应用接收按键
            }
            return IntPtr.Zero;
        }

        private void Icon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            Mouse.Capture((IInputElement)sender);
            Cursor = Cursors.Cross;
        }

        private void Icon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Mouse.Capture(null);
            Cursor = Cursors.Arrow;
            if (NativeMethods.GetCursorPos(out var pt))
            {
                var hwnd = NativeMethods.WindowFromPoint(pt);
                const uint GA_ROOT = 2;
                hwnd = NativeMethods.GetAncestor(hwnd, GA_ROOT);
                _capturedHwnd = hwnd;
                AppendLog("已选择窗口");
            }
        }

        private void UpdateBoundTitles()
        {
            BoundATitle.Text = _boundAHwnd == IntPtr.Zero ? "" : NativeMethods.GetWindowTitle(_boundAHwnd);
            BoundBTitle.Text = _boundBHwnd == IntPtr.Zero ? "" : NativeMethods.GetWindowTitle(_boundBHwnd);
        }

        private async void ShotButtonA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero)
            {
                AppendLog("请先绑定窗口A");
                return;
            }
            await Task.Run(() =>
            {
                try
                {
                    using Bitmap? bmp = NativeMethods.CaptureWindow(_boundAHwnd);
                    if (bmp == null) return;
                    string ocrText = "";
                    if (_rectA is { } r)
                    {
                        using var matFull = BitmapConverter.ToMat(bmp);
                        var x = Math.Max(0, Math.Min(matFull.Cols - 1, r.X));
                        var y = Math.Max(0, Math.Min(matFull.Rows - 1, r.Y));
                        var w = Math.Max(1, Math.Min(matFull.Cols - x, r.Width));
                        var h = Math.Max(1, Math.Min(matFull.Rows - y, r.Height));
                        var roi = new OpenCvSharp.Rect(x, y, w, h);
                        using var matRoi = new Mat(matFull, roi);
                        var regions = _ocr.OcrAsync(matRoi).GetAwaiter().GetResult();
                        var nums = regions.Select(z => z.Text ?? "").Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                          .Where(s => s.Any(char.IsDigit)).ToList();
                        if (nums.Count > 0) ocrText = nums.OrderByDescending(s => s.Length).First();
                        using var g = Graphics.FromImage(bmp);
                        using var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 3);
                        g.DrawRectangle(pen, r);
                    }
                    if (!string.IsNullOrWhiteSpace(ocrText)) Dispatcher.Invoke(() => OcrResultTextA.Text = ocrText);
                    var path = NativeMethods.SaveBitmap(bmp);
                    Dispatcher.Invoke(() => AppendLog($"已保存：{path}"));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"截图失败：{ex.Message}"));
                }
            });
        }

        private void SelectAreaButtonA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero)
            {
                AppendLog("请先绑定窗口A");
                return;
            }
            var overlay = new OverlaySelectWindow();
            var ok = overlay.ShowDialog();
            if (ok != true) return;
            var sel = overlay.SelectedRect;
            var wrect = NativeMethods.GetRect(_boundAHwnd);
            var interLeft = Math.Max(wrect.Left, (int)sel.Left);
            var interTop = Math.Max(wrect.Top, (int)sel.Top);
            var interRight = Math.Min(wrect.Right, (int)(sel.Left + sel.Width));
            var interBottom = Math.Min(wrect.Bottom, (int)(sel.Top + sel.Height));
            if (interRight <= interLeft || interBottom <= interTop)
            {
                AppendLog("A：选择区域不在目标窗口内");
                return;
            }
            _rectA = System.Drawing.Rectangle.FromLTRB(
                interLeft - wrect.Left,
                interTop - wrect.Top,
                interRight - wrect.Left,
                interBottom - wrect.Top
            );
            OcrResultTextA.Text = "";
            AppendLog($"A已选择区域：{_rectA.Value.Width}x{_rectA.Value.Height}");
        }

        private void BindBButton_Click(object sender, RoutedEventArgs e)
        {
            if (_capturedHwnd == IntPtr.Zero) { AppendLog("请先通过圆形图标选择一个窗口"); return; }
            _boundBHwnd = _capturedHwnd;
            UpdateBoundTitles();
            AppendLog("已绑定窗口B");
        }

        private void BindAButton_Click(object sender, RoutedEventArgs e)
        {
            if (_capturedHwnd == IntPtr.Zero) { AppendLog("请先通过圆形图标选择一个窗口"); return; }
            _boundAHwnd = _capturedHwnd;
            UpdateBoundTitles();
            AppendLog("已绑定窗口A");
        }
        
        private void BindA_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var picker = new OverlayPickWindow();
            var ok = picker.ShowDialog();
            if (ok == true)
            {
                var p = picker.ClickPoint;
                var pt = new WindowSpy.NativeMethods.POINT { X = (int)p.X, Y = (int)p.Y };
                var hwnd = NativeMethods.WindowFromPoint(pt);
                const uint GA_ROOT = 2;
                hwnd = NativeMethods.GetAncestor(hwnd, GA_ROOT);
                _boundAHwnd = hwnd;
                UpdateBoundTitles();
                AppendLog("已绑定窗口A(拖拽)");
            }
        }

        private void BindB_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var picker = new OverlayPickWindow();
            var ok = picker.ShowDialog();
            if (ok == true)
            {
                var p = picker.ClickPoint;
                var pt = new WindowSpy.NativeMethods.POINT { X = (int)p.X, Y = (int)p.Y };
                var hwnd = NativeMethods.WindowFromPoint(pt);
                const uint GA_ROOT = 2;
                hwnd = NativeMethods.GetAncestor(hwnd, GA_ROOT);
                _boundBHwnd = hwnd;
                UpdateBoundTitles();
                AppendLog("已绑定窗口B(拖拽)");
            }
        }

        private async void ShotButtonB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero)
            {
                AppendLog("请先绑定窗口B");
                return;
            }
            await Task.Run(() =>
            {
                try
                {
                    using Bitmap? bmp = NativeMethods.CaptureWindow(_boundBHwnd);
                    if (bmp == null) return;
                    string ocrText = "";
                    if (_rectB is { } r)
                    {
                        using var matFull = BitmapConverter.ToMat(bmp);
                        var x = Math.Max(0, Math.Min(matFull.Cols - 1, r.X));
                        var y = Math.Max(0, Math.Min(matFull.Rows - 1, r.Y));
                        var w = Math.Max(1, Math.Min(matFull.Cols - x, r.Width));
                        var h = Math.Max(1, Math.Min(matFull.Rows - y, r.Height));
                        var roi = new OpenCvSharp.Rect(x, y, w, h);
                        using var matRoi = new Mat(matFull, roi);
                        var regions = _ocr.OcrAsync(matRoi).GetAwaiter().GetResult();
                        var nums = regions.Select(z => z.Text ?? "").Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                          .Where(s => s.Any(char.IsDigit)).ToList();
                        if (nums.Count > 0) ocrText = nums.OrderByDescending(s => s.Length).First();
                        using var g = Graphics.FromImage(bmp);
                        using var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 3);
                        g.DrawRectangle(pen, r);
                    }
                    if (!string.IsNullOrWhiteSpace(ocrText)) Dispatcher.Invoke(() => OcrResultTextB.Text = ocrText);
                    var path = NativeMethods.SaveBitmap(bmp);
                    Dispatcher.Invoke(() => AppendLog($"已保存：{path}"));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"截图失败：{ex.Message}"));
                }
            });
        }

        private void SelectAreaButtonB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero)
            {
                AppendLog("请先绑定窗口B");
                return;
            }
            var overlay = new OverlaySelectWindow();
            var ok = overlay.ShowDialog();
            if (ok != true) return;
            var sel = overlay.SelectedRect;
            var wrect = NativeMethods.GetRect(_boundBHwnd);
            var interLeft = Math.Max(wrect.Left, (int)sel.Left);
            var interTop = Math.Max(wrect.Top, (int)sel.Top);
            var interRight = Math.Min(wrect.Right, (int)(sel.Left + sel.Width));
            var interBottom = Math.Min(wrect.Bottom, (int)(sel.Top + sel.Height));
            if (interRight <= interLeft || interBottom <= interTop)
            {
                AppendLog("B：选择区域不在目标窗口内");
                return;
            }
            _rectB = System.Drawing.Rectangle.FromLTRB(
                interLeft - wrect.Left,
                interTop - wrect.Top,
                interRight - wrect.Left,
                interBottom - wrect.Top
            );
            OcrResultTextB.Text = "";
            AppendLog($"B已选择区域：{_rectB.Value.Width}x{_rectB.Value.Height}");
        }

        private void SelectClickPosA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            var picker = new OverlayPickWindow();
            var ok = picker.ShowDialog();
            if (ok == true)
            {
                var wrect = NativeMethods.GetRect(_boundAHwnd);
                int sx = (int)picker.ClickPoint.X;
                int sy = (int)picker.ClickPoint.Y;
                if (sx < wrect.Left || sy < wrect.Top || sx >= wrect.Right || sy >= wrect.Bottom)
                {
                    AppendLog("A：点击位置不在绑定窗口内");
                    return;
                }
                _clickA = new System.Drawing.Point(sx - wrect.Left, sy - wrect.Top);
                AppendLog($"A已选择点击位置：{_clickA.Value.X},{_clickA.Value.Y}");
            }
        }

        private void ClickSelectedPosA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            if (_clickA == null) { AppendLog("A：尚未选择点击位置"); return; }
            var wrect = NativeMethods.GetRect(_boundAHwnd);
            int sx = wrect.Left + _clickA.Value.X;
            int sy = wrect.Top + _clickA.Value.Y;
            if (NativeMethods.IsIconic(_boundAHwnd)) NativeMethods.ShowWindow(_boundAHwnd, 9);
            NativeMethods.SetForegroundWindow(_boundAHwnd);
            int dwell = ParseInt(DwellMsA?.Text, 100);
            NativeMethods.ClickAtScreen(sx, sy, dwell);
            AppendLog($"A已点击位置：{sx},{sy}");
        }

        private void SelectClickPosB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            var picker = new OverlayPickWindow();
            var ok = picker.ShowDialog();
            if (ok == true)
            {
                var wrect = NativeMethods.GetRect(_boundBHwnd);
                int sx = (int)picker.ClickPoint.X;
                int sy = (int)picker.ClickPoint.Y;
                if (sx < wrect.Left || sy < wrect.Top || sx >= wrect.Right || sy >= wrect.Bottom)
                {
                    AppendLog("B：点击位置不在绑定窗口内");
                    return;
                }
                _clickB = new System.Drawing.Point(sx - wrect.Left, sy - wrect.Top);
                AppendLog($"B已选择点击位置：{_clickB.Value.X},{_clickB.Value.Y}");
            }
        }

        private void ClickSelectedPosB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            if (_clickB == null) { AppendLog("B：尚未选择点击位置"); return; }
            var wrect = NativeMethods.GetRect(_boundBHwnd);
            int sx = wrect.Left + _clickB.Value.X;
            int sy = wrect.Top + _clickB.Value.Y;
            if (NativeMethods.IsIconic(_boundBHwnd)) NativeMethods.ShowWindow(_boundBHwnd, 9);
            NativeMethods.SetForegroundWindow(_boundBHwnd);
            int dwell = ParseInt(DwellMsB?.Text, 100);
            NativeMethods.ClickAtScreen(sx, sy, dwell);
            AppendLog($"B已点击位置：{sx},{sy}");
        }

        private int ParseInt(string? s, int def)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            var digits = new string(s.Where(char.IsDigit).ToArray());
            if (string.IsNullOrEmpty(digits)) return def;
            if (int.TryParse(digits, out var v)) return v;
            return def;
        }

        private void AddOcrStepA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            if (_rectA == null) { AppendLog("A：尚未选择识别区域"); return; }
            _steps.Add(new ScriptStep { 
                Target = TargetType.A, Type = ActionType.Ocr, Rect = _rectA.Value, 
                DelayMs = ParseInt(DelayMsA?.Text, 300), 
                RandomDelay = ParseInt(RandomDelayA?.Text, 0),
                DwellMs = ParseInt(DwellMsA?.Text, 100),
                RandomDwell = ParseInt(RandomDwellA?.Text, 0),
                RandomX = ParseInt(RandomXA?.Text, 0),
                RandomY = ParseInt(RandomYA?.Text, 0)
            });
            AppendLog("A步骤已添加：识别");
            RefreshSteps();
        }

        private void AddClickStepA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            if (_clickA == null) { AppendLog("A：尚未选择点击位置"); return; }
            _steps.Add(new ScriptStep { 
                Target = TargetType.A, Type = ActionType.Click, Point = _clickA.Value, 
                DelayMs = ParseInt(DelayMsA?.Text, 300), 
                RandomDelay = ParseInt(RandomDelayA?.Text, 0),
                DwellMs = ParseInt(DwellMsA?.Text, 100),
                RandomDwell = ParseInt(RandomDwellA?.Text, 0),
                RandomX = ParseInt(RandomXA?.Text, 0),
                RandomY = ParseInt(RandomYA?.Text, 0)
            });
            AppendLog("A步骤已添加：点击");
            RefreshSteps();
        }
        private void AddBringFrontA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            _steps.Add(new ScriptStep { Target = TargetType.A, Type = ActionType.BringFront, DelayMs = ParseInt(DelayMsA?.Text, 0) });
            AppendLog("A步骤已添加：置顶窗口");
            RefreshSteps();
        }

        private int GetRandomVal(int baseVal, int randomRange)
        {
            if (randomRange <= 0) return baseVal;
            return baseVal + _rng.Next(-randomRange, randomRange + 1);
        }

        private async void RunScriptA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            var list = _steps.Where(s => s.Target == TargetType.A).ToList();
            if (list.Count == 0) { AppendLog("A：步骤为空"); return; }
            
            _stopAll = false;
            _isRunning = true;
            DoRegisterHotkey();
            InstallHook();
            
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string lastOcrA = OcrResultTextA?.Text ?? "";
                    foreach (var step in list)
                {
                    if (_stopAll) break;
                    int delay = Math.Max(0, GetRandomVal(step.DelayMs, step.RandomDelay));
                    System.Threading.Thread.Sleep(delay);
                    
                    if (step.Type == ActionType.Click)
                    {
                        var wrect = NativeMethods.GetRect(_boundAHwnd);
                        int offX = GetRandomVal(0, step.RandomX);
                        int offY = GetRandomVal(0, step.RandomY);
                        int sx = wrect.Left + step.Point.X + offX;
                        int sy = wrect.Top + step.Point.Y + offY;
                        
                        if (NativeMethods.IsIconic(_boundAHwnd)) NativeMethods.ShowWindow(_boundAHwnd, 9);
                        NativeMethods.SetForegroundWindow(_boundAHwnd);
                        
                        int dwell = Math.Max(0, GetRandomVal(step.DwellMs, step.RandomDwell));
                        NativeMethods.ClickAtScreen(sx, sy, dwell);
                        Dispatcher.Invoke(() => AppendLog($"A执行：点击 {sx},{sy} (延{delay} 停{dwell})"));
                    }
                    else if (step.Type == ActionType.Ocr)
                    {
                        using var bmp = NativeMethods.CaptureWindow(_boundAHwnd);
                        using var mat = BitmapConverter.ToMat(bmp);
                        var r = step.Rect;
                        var x = Math.Max(0, Math.Min(mat.Cols - 1, r.X));
                        var y = Math.Max(0, Math.Min(mat.Rows - 1, r.Y));
                        var w = Math.Max(1, Math.Min(mat.Cols - x, r.Width));
                        var h = Math.Max(1, Math.Min(mat.Rows - y, r.Height));
                        var roi = new OpenCvSharp.Rect(x, y, w, h);
                        using var matRoi = new Mat(mat, roi);
                        var regions = _ocr.OcrAsync(matRoi).GetAwaiter().GetResult();
                        var nums = regions.Select(z => z.Text ?? "").Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                          .Where(s => s.Any(char.IsDigit)).ToList();
                        var ocrText = nums.Count > 0 ? nums.OrderByDescending(s => s.Length).First() : "";
                        if (!string.IsNullOrWhiteSpace(ocrText)) Dispatcher.Invoke(() => { if (OcrResultTextA != null) OcrResultTextA.Text = ocrText; });
                        lastOcrA = ocrText;
                        Dispatcher.Invoke(() => AppendLog($"A执行：识别 {ocrText}"));
                    }
                    else if (step.Type == ActionType.Condition)
                    {
                        string text = (!string.IsNullOrWhiteSpace(step.Key) && _vars.TryGetValue(step.Key, out var v)) ? v : lastOcrA;
                        bool match = !string.IsNullOrEmpty(text) && System.Text.RegularExpressions.Regex.IsMatch(text, step.Pattern);
                        step.LastResult = match;
                        Dispatcher.Invoke(() => AppendLog($"A条件检查: {(match ? "匹配" : "不匹配")} 模式 {step.Pattern} 文本 {text}"));
                        Dispatcher.Invoke(() => RefreshSteps());
                        if ((step.JumpOnTrue && match) || (!step.JumpOnTrue && !match)) break;
                    }
                    else if (step.Type == ActionType.Expression)
                    {
                        bool ok = EvaluateExpression(step.Pattern);
                        step.LastResult = ok;
                        Dispatcher.Invoke(() => AppendLog($"A表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}，跳出条件={ (step.JumpOnTrue ? "为真" : "为假") }"));
                        Dispatcher.Invoke(() => AppendExprLog($"A表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}"));
                        Dispatcher.Invoke(() => RefreshSteps());
                        if ((step.JumpOnTrue && ok) || (!step.JumpOnTrue && !ok)) break;
                    }
                }
            }
            finally
            {
                _isRunning = false;
                Dispatcher.Invoke(() => UnregisterStopHotkey());
                UninstallHook();
            }
            });
        }

        private void ClearScriptA_Click(object sender, RoutedEventArgs e)
        {
            _steps.RemoveAll(s => s.Target == TargetType.A);
            AppendLog("A：已清空步骤");
            RefreshSteps();
        }

        private void AddOcrStepB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            if (_rectB == null) { AppendLog("B：尚未选择识别区域"); return; }
            _steps.Add(new ScriptStep { 
                Target = TargetType.B, Type = ActionType.Ocr, Rect = _rectB.Value, 
                DelayMs = ParseInt(DelayMsB?.Text, 300), 
                RandomDelay = ParseInt(RandomDelayB?.Text, 0),
                DwellMs = ParseInt(DwellMsB?.Text, 100),
                RandomDwell = ParseInt(RandomDwellB?.Text, 0),
                RandomX = ParseInt(RandomXB?.Text, 0),
                RandomY = ParseInt(RandomYB?.Text, 0)
            });
            AppendLog("B步骤已添加：识别");
            RefreshSteps();
        }

        private void AddClickStepB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            if (_clickB == null) { AppendLog("B：尚未选择点击位置"); return; }
            _steps.Add(new ScriptStep { 
                Target = TargetType.B, Type = ActionType.Click, Point = _clickB.Value, 
                DelayMs = ParseInt(DelayMsB?.Text, 300), 
                RandomDelay = ParseInt(RandomDelayB?.Text, 0),
                DwellMs = ParseInt(DwellMsB?.Text, 100),
                RandomDwell = ParseInt(RandomDwellB?.Text, 0),
                RandomX = ParseInt(RandomXB?.Text, 0),
                RandomY = ParseInt(RandomYB?.Text, 0)
            });
            AppendLog("B步骤已添加：点击");
            RefreshSteps();
        }
        private void AddBringFrontB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            _steps.Add(new ScriptStep { Target = TargetType.B, Type = ActionType.BringFront, DelayMs = ParseInt(DelayMsB?.Text, 0) });
            AppendLog("B步骤已添加：置顶窗口");
            RefreshSteps();
        }

        private async void RunScriptB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            var list = _steps.Where(s => s.Target == TargetType.B).ToList();
            if (list.Count == 0) { AppendLog("B：步骤为空"); return; }
            
            _stopAll = false;
            _isRunning = true;
            DoRegisterHotkey();
            InstallHook();
            
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string lastOcrB = OcrResultTextB?.Text ?? "";
                foreach (var step in list)
                {
                    if (_stopAll) break;
                    int delay = Math.Max(0, GetRandomVal(step.DelayMs, step.RandomDelay));
                    System.Threading.Thread.Sleep(delay);
                    
                    if (step.Type == ActionType.Click)
                    {
                        var wrect = NativeMethods.GetRect(_boundBHwnd);
                        int offX = GetRandomVal(0, step.RandomX);
                        int offY = GetRandomVal(0, step.RandomY);
                        int sx = wrect.Left + step.Point.X + offX;
                        int sy = wrect.Top + step.Point.Y + offY;
                        
                        if (NativeMethods.IsIconic(_boundBHwnd)) NativeMethods.ShowWindow(_boundBHwnd, 9);
                        NativeMethods.SetForegroundWindow(_boundBHwnd);
                        
                        int dwell = Math.Max(0, GetRandomVal(step.DwellMs, step.RandomDwell));
                        NativeMethods.ClickAtScreen(sx, sy, dwell);
                        Dispatcher.Invoke(() => AppendLog($"B执行：点击 {sx},{sy} (延{delay} 停{dwell})"));
                    }
                    else if (step.Type == ActionType.Ocr)
                    {
                        using var bmp = NativeMethods.CaptureWindow(_boundBHwnd);
                        using var mat = BitmapConverter.ToMat(bmp);
                        var r = step.Rect;
                        var x = Math.Max(0, Math.Min(mat.Cols - 1, r.X));
                        var y = Math.Max(0, Math.Min(mat.Rows - 1, r.Y));
                        var w = Math.Max(1, Math.Min(mat.Cols - x, r.Width));
                        var h = Math.Max(1, Math.Min(mat.Rows - y, r.Height));
                        var roi = new OpenCvSharp.Rect(x, y, w, h);
                        using var matRoi = new Mat(mat, roi);
                        var regions = _ocr.OcrAsync(matRoi).GetAwaiter().GetResult();
                        var nums = regions.Select(z => z.Text ?? "").Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                          .Where(s => s.Any(char.IsDigit)).ToList();
                        var ocrText = nums.Count > 0 ? nums.OrderByDescending(s => s.Length).First() : "";
                        if (!string.IsNullOrWhiteSpace(ocrText)) Dispatcher.Invoke(() => { if (OcrResultTextB != null) OcrResultTextB.Text = ocrText; });
                        lastOcrB = ocrText;
                        Dispatcher.Invoke(() => AppendLog($"B执行：识别 {ocrText}"));
                    }
                    else if (step.Type == ActionType.Condition)
                    {
                        string text = (!string.IsNullOrWhiteSpace(step.Key) && _vars.TryGetValue(step.Key, out var v)) ? v : lastOcrB;
                        bool match = !string.IsNullOrEmpty(text) && System.Text.RegularExpressions.Regex.IsMatch(text, step.Pattern);
                        step.LastResult = match;
                        Dispatcher.Invoke(() => AppendLog($"B条件检查: {(match ? "匹配" : "不匹配")} 模式 {step.Pattern} 文本 {text}"));
                        Dispatcher.Invoke(() => RefreshSteps());
                        if ((step.JumpOnTrue && match) || (!step.JumpOnTrue && !match)) break;
                    }
                    else if (step.Type == ActionType.Expression)
                    {
                        bool ok = EvaluateExpression(step.Pattern);
                        step.LastResult = ok;
                        Dispatcher.Invoke(() => AppendLog($"B表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}，跳出条件={ (step.JumpOnTrue ? "为真" : "为假") }"));
                        Dispatcher.Invoke(() => AppendExprLog($"B表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}"));
                        Dispatcher.Invoke(() => RefreshSteps());
                        if ((step.JumpOnTrue && ok) || (!step.JumpOnTrue && !ok)) break;
                    }
                }
            }
            finally
            {
                _isRunning = false;
                Dispatcher.Invoke(() => UnregisterStopHotkey());
                UninstallHook();
            }
            });
        }

        private void ClearScriptB_Click(object sender, RoutedEventArgs e)
        {
            _steps.RemoveAll(s => s.Target == TargetType.B);
            AppendLog("B：已清空步骤");
            RefreshSteps();
        }

        private string FormatStep(ScriptStep s)
        {
            if (s.Type == ActionType.Click)
                return $"{s.Target} 延 {s.DelayMs}±{s.RandomDelay}ms → 点 ({s.Point.X}±{s.RandomX},{s.Point.Y}±{s.RandomY}) 停 {s.DwellMs}±{s.RandomDwell}ms";
            if (s.Type == ActionType.Condition)
                return $"条件: 模式 \"{s.Pattern}\" 来源键 \"{s.Key}\" 跳出={(s.JumpOnTrue ? "为真" : "为假")}";
            if (s.Type == ActionType.Save)
                return $"保存 {s.Target} 结果 到键 \"{s.Key}\"";
            if (s.Type == ActionType.Expression)
                return $"表达式条件: {s.Pattern} 跳出={(s.JumpOnTrue ? "为真" : "为假")}";
            if (s.Type == ActionType.LoopStart)
                return $"循环开始 次数 {s.Count}";
            if (s.Type == ActionType.LoopEnd)
                return $"循环结束";
            if (s.Type == ActionType.BringFront)
                return $"{s.Target} 置顶窗口";
            return $"{s.Target} 延 {s.DelayMs}±{s.RandomDelay}ms → 识别 区域 ({s.Rect.X},{s.Rect.Y},{s.Rect.Width},{s.Rect.Height})";
        }

        private void RefreshSteps()
        {
            StepsList.Items.Clear();
            foreach (var s in _steps) StepsList.Items.Add(FormatStep(s));
        }

        private void StepsList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragIndex = GetIndexAtPoint(StepsList, e.GetPosition(StepsList));
            if (_dragIndex >= 0) StepsList.SelectedIndex = _dragIndex;
        }
        private void StepsList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && _dragIndex >= 0)
            {
                System.Windows.DragDrop.DoDragDrop(StepsList, StepsList.SelectedItem, System.Windows.DragDropEffects.Move);
            }
        }
        private void StepsList_Drop(object sender, System.Windows.DragEventArgs e)
        {
            var targetIndex = GetIndexAtPoint(StepsList, e.GetPosition(StepsList));
            if (targetIndex < 0) targetIndex = _steps.Count - 1;
            if (_dragIndex >= 0 && targetIndex >= 0 && targetIndex != _dragIndex)
            {
                var item = _steps[_dragIndex];
                _steps.RemoveAt(_dragIndex);
                if (targetIndex >= _steps.Count) _steps.Add(item);
                else _steps.Insert(targetIndex, item);
                RefreshSteps();
                StepsList.SelectedIndex = targetIndex;
            }
            _dragIndex = -1;
        }
        private void StepsList_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
        }
        private int GetIndexAtPoint(System.Windows.Controls.ListBox list, System.Windows.Point p)
        {
            var hit = VisualTreeHelper.HitTest(list, p);
            if (hit == null) return -1;
            DependencyObject obj = hit.VisualHit;
            while (obj != null && obj is not System.Windows.Controls.ListBoxItem)
                obj = VisualTreeHelper.GetParent(obj);
            if (obj is System.Windows.Controls.ListBoxItem item)
            {
                return list.ItemContainerGenerator.IndexFromContainer(item);
            }
            return -1;
        }

        private void MenuItem_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (StepsList.SelectedIndex < 0) return;
            _steps.RemoveAt(StepsList.SelectedIndex);
            RefreshSteps();
            AppendLog("已删除所选步骤");
        }

        private void MenuItem_Edit_Click(object sender, RoutedEventArgs e)
        {
            if (StepsList.SelectedIndex < 0) return;
            var step = _steps[StepsList.SelectedIndex];
            var editor = new EditStepWindow(step);
            editor.Owner = this;
            if (editor.ShowDialog() == true)
            {
                RefreshSteps();
                AppendLog("已更新所选步骤");
            }
        }

        private void AddConditionStep_Click(object sender, RoutedEventArgs e)
        {
            var pattern = ConditionPattern?.Text ?? "";
            if (string.IsNullOrWhiteSpace(pattern)) { AppendLog("条件模式不能为空"); return; }
            var key = ConditionKey?.Text ?? "";
            var s = new ScriptStep { Type = ActionType.Condition, Pattern = pattern, Key = key, Target = TargetType.A, JumpOnTrue = JumpOnCondTrue?.IsChecked == true };
            string src = (!string.IsNullOrWhiteSpace(key) && _vars.TryGetValue(key, out var v)) ? v : (OcrResultTextA?.Text ?? "");
            s.LastResult = !string.IsNullOrEmpty(src) && System.Text.RegularExpressions.Regex.IsMatch(src, pattern);
            _steps.Add(s);
            RefreshSteps();
            AppendLog($"已添加条件步骤：{pattern}");
        }
        private void AddExprStep_Click(object sender, RoutedEventArgs e)
        {
            var expr = ExprBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(expr)) { AppendLog("表达式不能为空"); return; }
            var s = new ScriptStep { Type = ActionType.Expression, Pattern = expr, JumpOnTrue = JumpOnExprTrue?.IsChecked == true };
            s.LastResult = EvaluateExpression(expr);
            _steps.Add(s);
            RefreshSteps();
            AppendLog($"已添加表达式条件：{expr}");
        }

        private void AddLoopStart_Click(object sender, RoutedEventArgs e)
        {
            int cnt = ParseInt(LoopInnerCount?.Text, 2);
            if (cnt < 1) cnt = 1;
            _steps.Add(new ScriptStep { Type = ActionType.LoopStart, Count = cnt });
            RefreshSteps();
            AppendLog($"已添加循环开始：{cnt}");
        }
        private void AddLoopEnd_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.LoopEnd });
            RefreshSteps();
            AppendLog("已添加循环结束");
        }

        private void AddSaveA_Click(object sender, RoutedEventArgs e)
        {
            var key = SaveKey?.Text ?? "";
            if (string.IsNullOrWhiteSpace(key)) { AppendLog("保存键不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.Save, Target = TargetType.A, Key = key, DelayMs = 0, DwellMs = 0 });
            RefreshSteps();
            AppendLog($"已添加保存A结果步骤：{key}");
        }

        private void AddSaveB_Click(object sender, RoutedEventArgs e)
        {
            var key = SaveKey?.Text ?? "";
            if (string.IsNullOrWhiteSpace(key)) { AppendLog("保存键不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.Save, Target = TargetType.B, Key = key, DelayMs = 0, DwellMs = 0 });
            RefreshSteps();
            AppendLog($"已添加保存B结果步骤：{key}");
        }

        private async void RunScriptAll_Click(object sender, RoutedEventArgs e)
        {
            int loops = ParseInt(LoopCount?.Text, 1);
            bool breakOnEmpty = BreakOnEmpty?.IsChecked == true;
            string lastOcrA = "", lastOcrB = "";
            _stopAll = false;
            _isRunning = true;
            DoRegisterHotkey(); // 开始运行时注册热键
            InstallHook();
            
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < Math.Max(1, loops); i++)
                {
                    bool breakAll = false;
                    var stack = new System.Collections.Generic.Stack<(int start, int remain)>();
                    int idx = 0;
                    while (idx < _steps.Count)
                    {
                        if (_stopAll) { breakAll = true; break; }
                        var step = _steps[idx];
                        int delay = Math.Max(0, GetRandomVal(step.DelayMs, step.RandomDelay));
                        System.Threading.Thread.Sleep(delay);
                        
                        if (step.Type == ActionType.Click)
                        {
                            var hwnd = step.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
                            if (hwnd == IntPtr.Zero) { Dispatcher.Invoke(() => AppendLog($"{step.Target}：未绑定窗口")); breakAll = true; break; }
                            var wrect = NativeMethods.GetRect(hwnd);
                            int offX = GetRandomVal(0, step.RandomX);
                            int offY = GetRandomVal(0, step.RandomY);
                            int sx = wrect.Left + step.Point.X + offX;
                            int sy = wrect.Top + step.Point.Y + offY;
                            
                            if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, 9);
                            NativeMethods.SetForegroundWindow(hwnd);
                            
                            int dwell = Math.Max(0, GetRandomVal(step.DwellMs, step.RandomDwell));
                            NativeMethods.ClickAtScreen(sx, sy, dwell);
                            Dispatcher.Invoke(() => AppendLog($"{step.Target}执行：点击 {sx},{sy} (延{delay} 停{dwell})"));
                        }
                        else if (step.Type == ActionType.Ocr)
                        {
                            var hwnd = step.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
                            if (hwnd == IntPtr.Zero) { Dispatcher.Invoke(() => AppendLog($"{step.Target}：未绑定窗口")); breakAll = true; break; }
                            using var bmp = NativeMethods.CaptureWindow(hwnd);
                            using var mat = BitmapConverter.ToMat(bmp);
                            var r = step.Rect;
                            var x = Math.Max(0, Math.Min(mat.Cols - 1, r.X));
                            var y = Math.Max(0, Math.Min(mat.Rows - 1, r.Y));
                            var w = Math.Max(1, Math.Min(mat.Cols - x, r.Width));
                            var h = Math.Max(1, Math.Min(mat.Rows - y, r.Height));
                            var roi = new OpenCvSharp.Rect(x, y, w, h);
                            using var matRoi = new Mat(mat, roi);
                            var regions = _ocr.OcrAsync(matRoi).GetAwaiter().GetResult();
                        var nums = regions.Select(z => z.Text ?? "").Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                          .Where(s => s.Any(char.IsDigit)).ToList();
                        var ocrText = nums.Count > 0 ? nums.OrderByDescending(s => s.Length).First() : "";
                            if (step.Target == TargetType.A) { lastOcrA = ocrText; Dispatcher.Invoke(() => { if (OcrResultTextA != null) OcrResultTextA.Text = ocrText; }); }
                            else { lastOcrB = ocrText; Dispatcher.Invoke(() => { if (OcrResultTextB != null) OcrResultTextB.Text = ocrText; }); }
                            Dispatcher.Invoke(() => AppendLog($"{step.Target}执行：识别 {ocrText}"));
                            if (breakOnEmpty && string.IsNullOrWhiteSpace(ocrText)) { breakAll = true; break; }
                        }
                        else if (step.Type == ActionType.Save)
                        {
                            var txt = step.Target == TargetType.A ? lastOcrA : lastOcrB;
                            var numOnly = new string((txt ?? "").Where(ch => char.IsDigit(ch)).ToArray());
                            _vars[step.Key] = numOnly;
                            Dispatcher.Invoke(() => AppendLog($"保存 {step.Target} 结果到 {step.Key}: {numOnly}"));
                        }
                        else if (step.Type == ActionType.Condition)
                        {
                            string text;
                            if (!string.IsNullOrWhiteSpace(step.Key) && _vars.TryGetValue(step.Key, out var v))
                                text = v;
                            else
                                text = !string.IsNullOrEmpty(lastOcrA) ? lastOcrA : lastOcrB;
                            bool match = !string.IsNullOrEmpty(text) && System.Text.RegularExpressions.Regex.IsMatch(text, step.Pattern);
                            step.LastResult = match;
                            Dispatcher.Invoke(() => AppendLog($"条件检查: {(match ? "匹配" : "不匹配")} 模式 {step.Pattern} 文本 {text}"));
                            Dispatcher.Invoke(() => RefreshSteps());
                            if ((step.JumpOnTrue && match) || (!step.JumpOnTrue && !match))
                            {
                                if (stack.Count > 0)
                                {
                                    int j = idx;
                                    int level = 0;
                                    bool skipped = false;
                                    while (j + 1 < _steps.Count)
                                    {
                                        j++;
                                        var s2 = _steps[j];
                                        if (s2.Type == ActionType.LoopStart) level++;
                                        else if (s2.Type == ActionType.LoopEnd)
                                        {
                                            if (level == 0)
                                            {
                                                stack.Pop();
                                                idx = j + 1;
                                                skipped = true;
                                                break;
                                            }
                                            else level--;
                                        }
                                    }
                                    if (skipped) continue;
                                }
                                breakAll = true; break;
                            }
                        }
                        else if (step.Type == ActionType.Expression)
                        {
                            bool ok = EvaluateExpression(step.Pattern);
                            step.LastResult = ok;
                            Dispatcher.Invoke(() => AppendLog($"表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}，跳出条件={ (step.JumpOnTrue ? "为真" : "为假") }"));
                            Dispatcher.Invoke(() => AppendExprLog($"表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}"));
                            Dispatcher.Invoke(() => RefreshSteps());
                            if ((step.JumpOnTrue && ok) || (!step.JumpOnTrue && !ok))
                            {
                                if (stack.Count > 0)
                                {
                                    int j = idx;
                                    int level = 0;
                                    bool skipped = false;
                                    while (j + 1 < _steps.Count)
                                    {
                                        j++;
                                        var s2 = _steps[j];
                                        if (s2.Type == ActionType.LoopStart) level++;
                                        else if (s2.Type == ActionType.LoopEnd)
                                        {
                                            if (level == 0)
                                            {
                                                stack.Pop();
                                                idx = j + 1;
                                                skipped = true;
                                                break;
                                            }
                                            else level--;
                                        }
                                    }
                                    if (skipped) continue;
                                }
                                breakAll = true; break;
                            }
                        }
                        else if (step.Type == ActionType.BringFront)
                        {
                            var hwnd = step.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
                            if (hwnd == IntPtr.Zero) { Dispatcher.Invoke(() => AppendLog($"{step.Target}：未绑定窗口")); breakAll = true; break; }
                            if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, 9);
                            NativeMethods.SetForegroundWindow(hwnd);
                            Dispatcher.Invoke(() => AppendLog($"{step.Target}执行：置顶窗口"));
                        }
                        else if (step.Type == ActionType.LoopStart)
                        {
                            stack.Push((idx, Math.Max(1, step.Count)));
                        }
                        else if (step.Type == ActionType.LoopEnd)
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                if (top.remain > 1)
                                {
                                    stack.Push((top.start, top.remain - 1));
                                    idx = top.start + 1;
                                    continue;
                                }
                            }
                        }
                        idx++;
                    }
                    if (breakAll) break;
                }
            }
            finally
            {
                _isRunning = false;
                Dispatcher.Invoke(() => UnregisterStopHotkey()); // 运行结束注销热键
                UninstallHook();
                Dispatcher.Invoke(() => AppendLog("全部步骤执行完毕/停止"));
            }
            });
            _stopAll = false;
        }

        private bool EvaluateExpression(string expr)
        {
            // 替换变量名为数值，支持 A1,A2,任意保存键（字母数字下划线）
            var tokenRegex = new System.Text.RegularExpressions.Regex(@"\b[A-Za-z_]\w*\b");
            var tokens = tokenRegex.Matches(expr).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).Distinct().ToList();
            var maps = new System.Collections.Generic.Dictionary<string, string>();
            string replaced = tokenRegex.Replace(expr, m =>
            {
                var name = m.Value;
                if (_vars.TryGetValue(name, out var val))
                {
                    // 去逗号，提取数字
                    var s = new string((val ?? "").Where(ch => char.IsDigit(ch) || ch == '.' || ch == '-').ToArray());
                    if (string.IsNullOrWhiteSpace(s)) s = "0";
                    maps[name] = s;
                    return s;
                }
                maps[name] = "0";
                return "0";
            });

            // 支持 C# 风格操作符
            replaced = replaced.Replace("||", " OR ")
                               .Replace("&&", " AND ")
                               .Replace("!=", "<>")
                               .Replace("==", "=");

            try
            {
                // 使用 DataTable.Compute 计算布尔/数值表达式
                var dt = new System.Data.DataTable();
                dt.Columns.Add("x", typeof(double));
                object result = dt.Compute(replaced, "");

                // 尝试计算每个子表达式的算术结果用于显示
                string displayReplaced = replaced;
                try
                {
                    // 1. 按逻辑运算符分割: AND, OR
                    var logicParts = System.Text.RegularExpressions.Regex.Split(replaced, @"( AND | OR )", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    for (int i = 0; i < logicParts.Length; i++)
                    {
                        var part = logicParts[i];
                        if (part.Trim().ToUpper() == "AND" || part.Trim().ToUpper() == "OR") continue;

                        // 2. 按关系运算符分割: <=, >=, <>, =, <, >
                        // 注意顺序：先匹配长的符号
                        string[] ops = new[] { "<=", ">=", "<>", "=", "<", ">" };
                        string? foundOp = null;
                        foreach (var op in ops) { if (part.Contains(op)) { foundOp = op; break; } }

                        if (foundOp != null)
                        {
                            int idx = part.IndexOf(foundOp);
                            string left = part.Substring(0, idx);
                            string right = part.Substring(idx + foundOp.Length);

                            string valLeft = EvalSimple(left, dt);
                            string valRight = EvalSimple(right, dt);
                            logicParts[i] = $"{valLeft}{foundOp}{valRight}";
                        }
                        else
                        {
                            // 可能是单纯的算术表达式或布尔值
                            logicParts[i] = EvalSimple(part, dt);
                        }
                    }
                    displayReplaced = string.Join("", logicParts);
                }
                catch { }

                string mapping = string.Join(", ", maps.Where(kv => kv.Value != "").Select(kv => kv.Key + "=" + kv.Value));
                Dispatcher.Invoke(() => AppendExprLog($" 原='{expr}' | [{mapping}]\n 结果='{displayReplaced}' | {result}"));
                if (result is bool b) return b;
                if (result is IConvertible c)
                {
                    double v = c.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                    return v != 0;
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendExprLog($"表达式错误：{ex.Message}"));
            }
            return false;
        }

        private string EvalSimple(string expr, System.Data.DataTable dt)
        {
            if (string.IsNullOrWhiteSpace(expr)) return expr;
            try
            {
                var res = dt.Compute(expr, "");
                if (res is IConvertible conv)
                {
                    double d = conv.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                    // 如果是整数，去掉小数点
                    if (Math.Abs(d % 1) < 1e-9) return ((long)d).ToString();
                    return d.ToString("0.##");
                }
                return res.ToString() ?? "";
            }
            catch 
            {
                return expr; 
            }
        }

        private class StepDto
        {
            public string Type { get; set; } = "";
            public string Target { get; set; } = "";
            public int DelayMs { get; set; }
            public int RandomDelay { get; set; }
            public int DwellMs { get; set; }
            public int RandomDwell { get; set; }
            public int RandomX { get; set; }
            public int RandomY { get; set; }
            public int Count { get; set; }
            public string Pattern { get; set; } = "";
            public string Key { get; set; } = "";
            public int RectX { get; set; }
            public int RectY { get; set; }
            public int RectW { get; set; }
            public int RectH { get; set; }
            public int PointX { get; set; }
            public int PointY { get; set; }
            public bool JumpOnTrue { get; set; }
        }

        private void SaveSteps_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog();
            dlg.Filter = "JSON 文件|*.json";
            dlg.FileName = "steps.json";
            if (dlg.ShowDialog() == true)
            {
                var stepList = _steps.Select(s => new StepDto
                {
                    Type = s.Type.ToString(),
                    Target = s.Target.ToString(),
                    DelayMs = s.DelayMs,
                    RandomDelay = s.RandomDelay,
                    DwellMs = s.DwellMs,
                    RandomDwell = s.RandomDwell,
                    RandomX = s.RandomX,
                    RandomY = s.RandomY,
                    Count = s.Count,
                    Pattern = s.Pattern,
                    Key = s.Key,
                    RectX = s.Rect.X, RectY = s.Rect.Y, RectW = s.Rect.Width, RectH = s.Rect.Height,
                    PointX = s.Point.X, PointY = s.Point.Y,
                    JumpOnTrue = s.JumpOnTrue
                }).ToList();

                var inputDialog = new InputDialog("步骤队列使用事项", "请输入使用说明（可选）：");
                if (inputDialog.ShowDialog() == true)
                {
                    var fileData = new SavedFileDto
                    {
                        Note = inputDialog.InputText,
                        Steps = stepList
                    };
                    var json = JsonSerializer.Serialize(fileData, new JsonSerializerOptions { WriteIndented = true });
                    System.IO.File.WriteAllText(dlg.FileName, json);
                    AppendLog($"已保存队列：{dlg.FileName}");
                }
            }
        }

        private class SavedFileDto
        {
            public string Note { get; set; } = "";
            public System.Collections.Generic.List<StepDto> Steps { get; set; } = new();
        }

        private void LoadSteps_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = "JSON 文件|*.json";
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(dlg.FileName);
                    // 尝试作为新格式（带Note）解析
                    SavedFileDto? fileData = null;
                    try
                    {
                        fileData = JsonSerializer.Deserialize<SavedFileDto>(json);
                    }
                    catch
                    {
                        // 忽略解析错误，说明可能不是 SavedFileDto 格式
                    }

                    var list = fileData?.Steps;
                    string note = fileData?.Note ?? "";

                    // 如果解析失败或Steps为空，尝试旧格式（直接List）
                    if (list == null || list.Count == 0)
                    {
                        try 
                        {
                            list = JsonSerializer.Deserialize<System.Collections.Generic.List<StepDto>>(json);
                            note = ""; // 旧格式无说明
                        }
                        catch {}
                    }

                    if (list == null) list = new System.Collections.Generic.List<StepDto>();

                    _steps.Clear();
                    foreach (var d in list)
                    {
                        Enum.TryParse<ActionType>(d.Type, out var t);
                        Enum.TryParse<TargetType>(d.Target, out var tg);
                        var s = new ScriptStep
                        {
                            Type = t,
                            Target = tg,
                            DelayMs = d.DelayMs,
                            RandomDelay = d.RandomDelay,
                            DwellMs = d.DwellMs,
                            RandomDwell = d.RandomDwell,
                            RandomX = d.RandomX,
                            RandomY = d.RandomY,
                            Count = d.Count,
                            Pattern = d.Pattern ?? "",
                            Key = d.Key ?? "",
                            Rect = new System.Drawing.Rectangle(d.RectX, d.RectY, d.RectW, d.RectH),
                            Point = new System.Drawing.Point(d.PointX, d.PointY),
                            JumpOnTrue = d.JumpOnTrue
                        };
                        _steps.Add(s);
                    }
                    RefreshSteps();
                    
                    // 显示加载的说明
                    if (UsageNoteText != null)
                    {
                        UsageNoteText.Text = $"步骤队列使用事项：{(string.IsNullOrWhiteSpace(note) ? "(无)" : note)}";
                    }

                    AppendLog($"已加载队列：{dlg.FileName}");
                }
                catch (Exception ex)
                {
                    AppendLog($"加载失败：{ex.Message}");
                }
            }
        }

        private void StopAll_Click(object sender, RoutedEventArgs e)
        {
            _stopAll = true;
            AppendLog("已请求停止全部步骤");
        }

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_bindingHotkey)
            {
                if (IsModifier(e.Key))
                {
                    _pressedModsDuringBinding |= ToModifierFlag(e.Key);
                    e.Handled = true;
                    return;
                }
                else
                {
                    _nonModifierPressedDuringBinding = true;
                    _singleModifierOnly = false;
                    _stopKey = GetEventKey(e);
                    var modsNow = System.Windows.Input.Keyboard.Modifiers;
                    _stopCtrl = modsNow.HasFlag(System.Windows.Input.ModifierKeys.Control);
                    _stopShift = modsNow.HasFlag(System.Windows.Input.ModifierKeys.Shift);
                    _stopAlt = false;
                    _bindingHotkey = false;
                    RegisterStopHotkey();
                    var label = $"{(_stopCtrl ? "Ctrl+" : "")}{(_stopAlt ? "Alt+" : "")}{(_stopShift ? "Shift+" : "")}{_stopKey}";
                    AppendLog($"已绑定快捷键：{label}");
                    _pressedModsDuringBinding = System.Windows.Input.ModifierKeys.None;
                    _nonModifierPressedDuringBinding = false;
                    e.Handled = true;
                    return;
                }
            }
            bool ctrl = (e.KeyboardDevice.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
            bool alt = (e.KeyboardDevice.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0;
            bool shift = (e.KeyboardDevice.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            if (_singleModifierOnly)
            {
                if ((_singleModifier.HasFlag(System.Windows.Input.ModifierKeys.Control) && ctrl && !alt && !shift && IsModifier(e.Key)) ||
                    (_singleModifier.HasFlag(System.Windows.Input.ModifierKeys.Shift) && shift && !ctrl && !alt && IsModifier(e.Key)))
                {
                    _stopAll = true;
                    AppendLog("快捷键停止全部步骤");
                    e.Handled = true;
                    return;
                }
            }
            var ek = GetEventKey(e);
            if (ek == _stopKey && ctrl == _stopCtrl && alt == _stopAlt && shift == _stopShift)
            {
                _stopAll = true;
                AppendLog("快捷键停止全部步骤");
                e.Handled = true;
            }
        }
        protected override void OnPreviewKeyUp(System.Windows.Input.KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            if (_bindingHotkey && IsModifier(e.Key))
            {
                var flag = ToModifierFlag(e.Key);
                if (_pressedModsDuringBinding == flag && !_nonModifierPressedDuringBinding)
                {
                    if (flag.HasFlag(System.Windows.Input.ModifierKeys.Control) || flag.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                    {
                        _singleModifierOnly = true;
                        _singleModifier = flag;
                        _bindingHotkey = false;
                        RegisterStopHotkey();
                        var labelSingle = $"{(flag.HasFlag(System.Windows.Input.ModifierKeys.Control) ? "Ctrl" : "Shift")}";
                        AppendLog($"已绑定快捷键：{labelSingle}");
                        _pressedModsDuringBinding = System.Windows.Input.ModifierKeys.None;
                        _nonModifierPressedDuringBinding = false;
                        e.Handled = true;
                    }
                }
                else
                {
                    _pressedModsDuringBinding &= ~flag;
                }
            }
        }

        private void BindHotkey_Click(object sender, RoutedEventArgs e)
        {
            _bindingHotkey = true;
            _pressedModsDuringBinding = System.Windows.Input.ModifierKeys.None;
            _nonModifierPressedDuringBinding = false;
            AppendLog("请按下要绑定的停止快捷键：单独Ctrl/Shift或组合键");
            this.Focus();
        }

        private static bool IsModifier(System.Windows.Input.Key key)
        {
            return key == System.Windows.Input.Key.LeftCtrl
                || key == System.Windows.Input.Key.RightCtrl
                || key == System.Windows.Input.Key.LeftAlt
                || key == System.Windows.Input.Key.RightAlt
                || key == System.Windows.Input.Key.LeftShift
                || key == System.Windows.Input.Key.RightShift;
        }
        private static System.Windows.Input.ModifierKeys ToModifierFlag(System.Windows.Input.Key key)
        {
            if (key == System.Windows.Input.Key.LeftCtrl || key == System.Windows.Input.Key.RightCtrl) return System.Windows.Input.ModifierKeys.Control;
            if (key == System.Windows.Input.Key.LeftAlt || key == System.Windows.Input.Key.RightAlt) return System.Windows.Input.ModifierKeys.Alt;
            if (key == System.Windows.Input.Key.LeftShift || key == System.Windows.Input.Key.RightShift) return System.Windows.Input.ModifierKeys.Shift;
            return System.Windows.Input.ModifierKeys.None;
        }
        private static System.Windows.Input.Key GetEventKey(System.Windows.Input.KeyEventArgs e)
        {
            return e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        }

        private void ClearScriptAll_Click(object sender, RoutedEventArgs e)
        {
            _steps.Clear();
            RefreshSteps();
            AppendLog("已清空全部步骤");
        }
        private void BringATopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            _steps.Add(new ScriptStep { Target = TargetType.A, Type = ActionType.BringFront, DelayMs = ParseInt(DelayMsA?.Text, 0) });
            RefreshSteps();
            AppendLog("A步骤已添加：置顶窗口");
        }
        private void BringBTopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            _steps.Add(new ScriptStep { Target = TargetType.B, Type = ActionType.BringFront, DelayMs = ParseInt(DelayMsB?.Text, 0) });
            RefreshSteps();
            AppendLog("B步骤已添加：置顶窗口");
        }

/*
        private void DetectGpu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                string info = "";
                foreach (var obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "Unknown";
                    string driver = obj["DriverVersion"]?.ToString() ?? "Unknown";
                    info += $"{name} (Driver: {driver}); ";
                }
                if (string.IsNullOrWhiteSpace(info)) info = "未检测到显卡信息";
                // GpuInfoText.Text = info;
                AppendLog($"显卡检测结果：{info}");
            }
            catch (Exception ex)
            {
                // GpuInfoText.Text = "检测失败";
                AppendLog($"显卡检测失败：{ex.Message}");
            }
        }

        private void CheckEnv_Click(object sender, RoutedEventArgs e)
        {
            // EnvCheckResultBox.Text = "正在运行环境检测脚本...";
            RunPythonScript("check_gpu_env.py");
        }

        private void OpenNvidiaDriver_Click(object sender, RoutedEventArgs e) => OpenUrl("https://www.nvidia.com/Download/index.aspx");
        private void OpenCudaDownload_Click(object sender, RoutedEventArgs e) => OpenUrl("https://developer.nvidia.com/cuda-11-8-0-download-archive");
        private void OpenCudnnDownload_Click(object sender, RoutedEventArgs e) => OpenUrl("https://developer.nvidia.com/rdp/cudnn-archive");

        private void InstallNvidiaEnv_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("即将执行以下操作：\n1. 卸载当前的 onnxruntime 相关库\n2. 安装 onnxruntime-gpu (支持 CUDA)\n\n确定要继续吗？", "确认安装", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
            
            // 检测显卡系列，如果是50系则提示需要 CUDA 12
            bool isRTX50 = false;
            try
            {
                var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    if (name.Contains("RTX 50") || name.Contains("RTX50"))
                    {
                        isRTX50 = true;
                        break;
                    }
                }
            }
            catch {}

            string pkg = "onnxruntime-gpu";
            if (isRTX50)
            {
                var r = MessageBox.Show("检测到您使用的是 RTX 50 系列显卡。\n该显卡需要 CUDA 12.x 环境，且官方 pip 源可能尚未更新适配版本。\n\n是否尝试安装支持 CUDA 12 的预览版或兼容包？\n(如果失败，建议使用 DirectML 模式)", "显卡适配提示", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Yes)
                {
                    // 尝试安装支持 CUDA 12 的版本，通常需要指定 extra-index-url 或者特定版本
                    // 这里暂时使用默认命令，实际部署时建议提供离线包
                    // 或者指引用户去下载我们提供的 "RTX50专用离线包"
                    pkg = "onnxruntime-gpu --extra-index-url https://aiinfra.pkgs.visualstudio.com/PublicPackages/_packaging/onnxruntime-cuda-12/pypi/simple/"; 
                }
            }

            RunPipCommand("uninstall onnxruntime onnxruntime-gpu onnxruntime-directml -y", () => 
            {
                RunPipCommand($"install {pkg}", () => 
                {
                     Dispatcher.Invoke(() => MessageBox.Show("NVIDIA 环境安装完成！请重新运行检测脚本验证。", "完成", MessageBoxButton.OK, MessageBoxImage.Information));
                });
            });
        }

        private void InstallDirectMlEnv_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("即将执行以下操作：\n1. 卸载当前的 onnxruntime 相关库\n2. 安装 onnxruntime-directml (支持 AMD/Intel)\n\n确定要继续吗？", "确认安装", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            RunPipCommand("uninstall onnxruntime onnxruntime-gpu onnxruntime-directml -y", () => 
            {
                RunPipCommand("install onnxruntime-directml", () => 
                {
                     Dispatcher.Invoke(() => MessageBox.Show("DirectML 环境安装完成！请重新运行检测脚本验证。", "完成", MessageBoxButton.OK, MessageBoxImage.Information));
                });
            });
        }

        private void InstallCpuEnv_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("即将切换回仅 CPU 模式。\n这会卸载 GPU 加速库并安装标准版 onnxruntime。\n\n确定要继续吗？", "确认安装", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            RunPipCommand("uninstall onnxruntime onnxruntime-gpu onnxruntime-directml -y", () => 
            {
                RunPipCommand("install onnxruntime", () => 
                {
                     Dispatcher.Invoke(() => MessageBox.Show("CPU 环境已恢复。", "完成", MessageBoxButton.OK, MessageBoxImage.Information));
                });
            });
        }

        private void RunPythonScript(string scriptName)
        {
             Task.Run(() => 
            {
                // 使用临时文件路径，避免污染程序目录
                var scriptPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"df_{Guid.NewGuid()}_{scriptName}");
                try
                {
                    // 始终从嵌入资源中释放最新版本
                    try 
                    {
                        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                        var resourceName = "WindowSpy." + scriptName;
                        using (var stream = assembly.GetManifestResourceStream(resourceName))
                        {
                            if (stream != null)
                            {
                                using (var fileStream = System.IO.File.Create(scriptPath))
                                {
                                    stream.CopyTo(fileStream);
                                }
                            }
                            else
                            {
                                // Dispatcher.Invoke(() => EnvCheckResultBox.Text = $"内部错误：未找到嵌入资源 {resourceName}");
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Dispatcher.Invoke(() => EnvCheckResultBox.Text = $"释放脚本失败: {ex.Message}");
                        return;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"\"{scriptPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };

                    // 尝试使用项目自带的嵌入式 Python
                    var embeddedPython = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "python.exe");
                    if (System.IO.File.Exists(embeddedPython))
                    {
                        psi.FileName = embeddedPython;
                    }
                    
                    using var proc = Process.Start(psi);
                    if (proc == null) 
                    {
                        // Dispatcher.Invoke(() => EnvCheckResultBox.Text = "无法启动 Python 进程");
                        return;
                    }

                    var output = proc.StandardOutput.ReadToEnd();
                    var err = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    Dispatcher.Invoke(() => 
                    {
                        // EnvCheckResultBox.Text = output + (string.IsNullOrEmpty(err) ? "" : "\n错误:\n" + err);
                        // EnvCheckResultBox.ScrollToEnd();
                        AppendLog("环境检测输出:\n" + output);
                    });
                }
                catch (Exception ex)
                {
                    // Dispatcher.Invoke(() => EnvCheckResultBox.Text = $"运行失败: {ex.Message}");
                }
                finally
                {
                    // 运行完后清理临时文件
                    try { if (System.IO.File.Exists(scriptPath)) System.IO.File.Delete(scriptPath); } catch { }
                }
            });
        }

        private void RunPipCommand(string args, Action? onComplete = null)
        {
            // Dispatcher.Invoke(() => EnvCheckResultBox.Text = $"正在执行: pip {args}...\n");
            
            Task.Run(() => 
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "pip", // 假设 pip 在 PATH 中，或者使用 "python" Arguments = "-m pip ..."
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };
                    
                    // 尝试使用 python -m pip 以获得更好的兼容性
                    psi.FileName = "python";
                    psi.Arguments = $"-m pip {args}";

                    using var proc = Process.Start(psi);
                    if (proc == null) 
                    {
                        // Dispatcher.Invoke(() => EnvCheckResultBox.AppendText("\n无法启动 pip 进程"));
                        return;
                    }

                    // proc.OutputDataReceived += (s, e) => { if (e.Data != null) Dispatcher.Invoke(() => { EnvCheckResultBox.AppendText(e.Data + "\n"); EnvCheckResultBox.ScrollToEnd(); }); };
                    // proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Dispatcher.Invoke(() => { EnvCheckResultBox.AppendText(e.Data + "\n"); EnvCheckResultBox.ScrollToEnd(); }); };
                    
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit();

                    if (onComplete != null) onComplete();
                }
                catch (Exception ex)
                {
                    // Dispatcher.Invoke(() => EnvCheckResultBox.AppendText($"\n执行失败: {ex.Message}"));
                }
            });
        }
*/

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog($"打开链接失败：{ex.Message}");
                MessageBox.Show($"无法打开浏览器，请手动复制链接：\n{url}");
            }
        }

        private void AppendExprLog(string text)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendExprLog(text));
                return;
            }
            if (ExprLogBox != null)
            {
                ExprLogBox.AppendText($"{DateTime.Now:HH:mm:ss} {text}\n");
                ExprLogBox.ScrollToEnd();
            }
        }
        private void AppendLog(string text)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendLog(text));
                return;
            }
            if (OutputBox != null)
            {
                OutputBox.AppendText($"{DateTime.Now:HH:mm:ss} {text}\n");
                OutputBox.ScrollToEnd();
            }
            try
            {
                var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jietu");
                System.IO.Directory.CreateDirectory(dir);
                var log = System.IO.Path.Combine(dir, "app.log");
                System.IO.File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}\n");
            }
            catch { }
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            if (OutputBox != null && !string.IsNullOrEmpty(OutputBox.Text))
            {
                try
                {
                    Clipboard.SetText(OutputBox.Text);
                    MessageBox.Show("日志已复制到剪贴板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"复制失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
