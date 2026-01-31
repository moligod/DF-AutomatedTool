using System;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Diagnostics;
using Microsoft.Win32;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using WindowSpy.Ocr;
using System.Management;

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
        private System.Windows.Point? _lastMouseDown = null;
        private readonly System.Collections.Generic.Dictionary<string, string> _vars = new();
        private NativeMethods.LowLevelMouseProc _hookProc;
        private IntPtr _hookID = IntPtr.Zero;
        private readonly System.Collections.Generic.Queue<DateTime> _rightClickTimes = new();

        private readonly Random _rng = new Random();
        private readonly object _logLock = new object();
        private readonly System.Collections.Generic.Queue<(DateTime ts, string text)> _pendingLogs = new();
        private bool _logFlushScheduled = false;

        private readonly object _exprLogLock = new object();
        private readonly System.Collections.Generic.Queue<(DateTime ts, string text)> _pendingExprLogs = new();
        private bool _exprLogFlushScheduled = false;

        private readonly System.Collections.Generic.Dictionary<ScriptStep, OcrRoiCacheEntry> _ocrRoiCache = new();
        private int _firstInferenceHintLogged = 0;
        private readonly string _savedQueuesDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedQueues");

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            _ocr.Logger = AppendLog;
            _ocr.UseGpu = UseGpuCheck?.IsChecked == true;
            _hookProc = HookCallback;
            
            if (UseGpuCheck != null)
            {
                UseGpuCheck.Checked += (s, e) => { if (_ocr != null) { _ocr.UseGpu = true; AppendLog("设置：已启用 GPU 加速请求"); } };
                UseGpuCheck.Unchecked += (s, e) => { if (_ocr != null) { _ocr.UseGpu = false; AppendLog("设置：已强制切换为 CPU 模式"); } };
            }
        }

        public List<string> GetNetworkAdapters()
        {
            var list = new List<string>();
            try
            {
                var query = new SelectQuery("Win32_NetworkAdapter", "PhysicalAdapter=True AND NetConnectionID IS NOT NULL");
                using var searcher = new ManagementObjectSearcher(query);
                foreach (ManagementObject mo in searcher.Get())
                {
                    string name = mo["NetConnectionID"]?.ToString() ?? mo["Name"]?.ToString() ?? "Unknown";
                    list.Add(name);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[网络] 加载网卡列表失败: {ex.Message}");
            }
            return list;
        }


        private volatile bool _isRunning = false;

        private readonly struct OcrRoiCacheEntry
        {
            public readonly ulong H1;
            public readonly ulong H2;
            public readonly bool NumbersOnly;
            public readonly string Text;

            public OcrRoiCacheEntry(ulong h1, ulong h2, bool numbersOnly, string text)
            {
                H1 = h1;
                H2 = h2;
                NumbersOnly = numbersOnly;
                Text = text;
            }
        }

        private static (ulong h1, ulong h2) ComputeRoiSignature(Mat roi)
        {
            using var gray = new Mat();
            if (roi.Channels() == 1) roi.CopyTo(gray);
            else Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);

            using var small = new Mat();
            Cv2.Resize(gray, small, new OpenCvSharp.Size(48, 48), 0, 0, InterpolationFlags.Area);

            using var u8 = new Mat();
            if (small.Type() == MatType.CV_8UC1) small.CopyTo(u8);
            else small.ConvertTo(u8, MatType.CV_8UC1);

            using var cont = u8.IsContinuous() ? u8 : u8.Clone();
            int byteLen = checked((int)(cont.Total() * cont.ElemSize()));
            var bytes = new byte[byteLen];
            Marshal.Copy(cont.Data, bytes, 0, byteLen);

            var digest = SHA256.HashData(bytes);
            return (BitConverter.ToUInt64(digest, 0), BitConverter.ToUInt64(digest, 8));
        }

        private (string text, bool reused) PerformOcrWithCache(ScriptStep step, Mat matRoi)
        {
            if (step.ReuseOcrOnRoiUnchanged)
            {
                var sig = ComputeRoiSignature(matRoi);
                if (_ocrRoiCache.TryGetValue(step, out var cached) &&
                    cached.H1 == sig.h1 && cached.H2 == sig.h2 &&
                    cached.NumbersOnly == step.OcrNumbersOnly)
                {
                    return (cached.Text ?? "", true);
                }

                string text = PerformOcrInternal(step, matRoi);
                _ocrRoiCache[step] = new OcrRoiCacheEntry(sig.h1, sig.h2, step.OcrNumbersOnly, text);
                return (text, false);
            }
            else
            {
                return (PerformOcrInternal(step, matRoi), false);
            }
        }

        private string PerformOcrInternal(ScriptStep step, Mat matRoi)
        {
            if (Interlocked.Exchange(ref _firstInferenceHintLogged, 1) == 0)
            {
                AppendLog("提示：首次推理识别会慢很多，这属于正常现象");
            }

            var regions = _ocr.OcrAsync(matRoi).GetAwaiter().GetResult();
            var texts = regions.Select(z => z.Text ?? "").ToList();
            if (step.OcrNumbersOnly)
            {
                var nums = texts.Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                .Where(s => s.Any(char.IsDigit)).ToList();
                return nums.Count > 0 ? nums.OrderByDescending(s => s.Length).First() : "";
            }
            else
            {
                return string.Join(" ", texts).Trim();
            }
        }

        public IntPtr GetBoundHwnd(TargetType target)
        {
            return target == TargetType.A ? _boundAHwnd : _boundBHwnd;
        }

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

        private void ShotButtonA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero)
            {
                AppendLog("请先绑定窗口A");
                return;
            }
            bool onlyNums = OcrAOnlyNums?.IsChecked == true;
            Task.Run(() =>
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
                        var texts = regions.Select(z => z.Text ?? "").ToList();
                        
                        if (onlyNums)
                        {
                            var nums = texts.Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                          .Where(s => s.Any(char.IsDigit)).ToList();
                            if (nums.Count > 0) ocrText = nums.OrderByDescending(s => s.Length).First();
                        }
                        else
                        {
                            ocrText = string.Join(" ", texts).Trim();
                        }

                        using var g = Graphics.FromImage(bmp);
                        using var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 3);
                        g.DrawRectangle(pen, r);
                    }
                    Dispatcher.Invoke(() => SetOcrResult(OcrResultTextA, ocrText));
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
            OcrResultTextA.Foreground = System.Windows.Media.Brushes.Black;
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

        private void ShotButtonB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero)
            {
                AppendLog("请先绑定窗口B");
                return;
            }
            bool onlyNums = OcrBOnlyNums?.IsChecked == true;
            Task.Run(() =>
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
                        var texts = regions.Select(z => z.Text ?? "").ToList();

                        if (onlyNums)
                        {
                            var nums = texts.Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                          .Where(s => s.Any(char.IsDigit)).ToList();
                            if (nums.Count > 0) ocrText = nums.OrderByDescending(s => s.Length).First();
                        }
                        else
                        {
                            ocrText = string.Join(" ", texts).Trim();
                        }

                        using var g = Graphics.FromImage(bmp);
                        using var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 3);
                        g.DrawRectangle(pen, r);
                    }
                    Dispatcher.Invoke(() => SetOcrResult(OcrResultTextB, ocrText));
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
            OcrResultTextB.Foreground = System.Windows.Media.Brushes.Black;
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
                RandomY = ParseInt(RandomYA?.Text, 0),
                OcrNumbersOnly = OcrAOnlyNums?.IsChecked == true,
                ReuseOcrOnRoiUnchanged = ReuseOcrRoiCacheCheck?.IsChecked == true
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
                        var ocr = PerformOcrWithCache(step, matRoi);
                        lastOcrA = ocr.text;
                        Dispatcher.Invoke(() => SetOcrResult(OcrResultTextA, ocr.text));
                        var tag = step.ReuseOcrOnRoiUnchanged ? (ocr.reused ? "(复用)" : "(重新识别)") : "";
                        Dispatcher.Invoke(() => AppendLog($"A执行：识别{tag} {ocr.text}"));
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
                        bool ok = EvaluateExpression(step.Pattern, out string? errorMsg);
                        step.LastResult = ok;
                        if (errorMsg != null) Dispatcher.Invoke(() => AppendLog($"A表达式错误: {errorMsg}"));
                        Dispatcher.Invoke(() => AppendLog($"A表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}，跳出条件={ (step.JumpOnTrue ? "为真" : "为假") }"));
                        Dispatcher.Invoke(() => AppendExprLog($"A表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}"));
                        Dispatcher.Invoke(() => RefreshSteps());
                        if ((step.JumpOnTrue && ok) || (!step.JumpOnTrue && !ok)) break;
                    }
                    else if (step.Type == ActionType.KeyPress)
                    {
                        var keyStr = step.Key;
                        if (Enum.TryParse<System.Windows.Input.Key>(keyStr, true, out var k))
                        {
                            var vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
                            NativeMethods.keybd_event((byte)vk, 0, 0, 0);
                            if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
                            NativeMethods.keybd_event((byte)vk, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
                            Dispatcher.Invoke(() => AppendLog($"A执行按键：{keyStr}"));
                        }
                        else
                        {
                            Dispatcher.Invoke(() => AppendLog($"A未知按键：{keyStr}"));
                        }
                    }
                    else if (step.Type == ActionType.Network)
                    {
                        if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
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
            foreach (var s in _steps.Where(s => s.Target == TargetType.A).ToList())
            {
                _ocrRoiCache.Remove(s);
            }
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
                RandomY = ParseInt(RandomYB?.Text, 0),
                OcrNumbersOnly = OcrBOnlyNums?.IsChecked == true,
                ReuseOcrOnRoiUnchanged = ReuseOcrRoiCacheCheck?.IsChecked == true
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
                        var ocr = PerformOcrWithCache(step, matRoi);
                        lastOcrB = ocr.text;
                        Dispatcher.Invoke(() => SetOcrResult(OcrResultTextB, ocr.text));
                        var tag = step.ReuseOcrOnRoiUnchanged ? (ocr.reused ? "(复用)" : "(重新识别)") : "";
                        Dispatcher.Invoke(() => AppendLog($"B执行：识别{tag} {ocr.text}"));
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
                        bool ok = EvaluateExpression(step.Pattern, out string? errorMsg);
                        step.LastResult = ok;
                        if (errorMsg != null) Dispatcher.Invoke(() => AppendLog($"B表达式错误: {errorMsg}"));
                        Dispatcher.Invoke(() => AppendLog($"B表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}，跳出条件={ (step.JumpOnTrue ? "为真" : "为假") }"));
                        Dispatcher.Invoke(() => AppendExprLog($"B表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}"));
                        Dispatcher.Invoke(() => RefreshSteps());
                        if ((step.JumpOnTrue && ok) || (!step.JumpOnTrue && !ok)) break;
                    }
                    else if (step.Type == ActionType.KeyPress)
                    {
                        var keyStr = step.Key;
                        if (Enum.TryParse<System.Windows.Input.Key>(keyStr, true, out var k))
                        {
                            var vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
                            NativeMethods.keybd_event((byte)vk, 0, 0, 0);
                            if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
                            NativeMethods.keybd_event((byte)vk, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
                            Dispatcher.Invoke(() => AppendLog($"B执行按键：{keyStr}"));
                        }
                        else
                        {
                            Dispatcher.Invoke(() => AppendLog($"B未知按键：{keyStr}"));
                        }
                    }
                    else if (step.Type == ActionType.Network)
                    {
                        if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
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
            foreach (var s in _steps.Where(s => s.Target == TargetType.B).ToList())
            {
                _ocrRoiCache.Remove(s);
            }
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
            if (s.Type == ActionType.KeyPress)
                return $"按键 \"{s.Key}\" (按压 {s.DwellMs}ms)";
            if (s.Type == ActionType.Comment)
                return $"注释: {s.Pattern}";
            if (s.Type == ActionType.IfStart) return $"If {s.Pattern}";
            if (s.Type == ActionType.ElseIf) return $"ElseIf {s.Pattern}";
            if (s.Type == ActionType.Else) return "Else";
            if (s.Type == ActionType.EndIf) return "EndIf";
            if (s.Type == ActionType.BreakLoop) return "跳出循环";
            if (s.Type == ActionType.ContinueLoop) return "跳入下次循环";
            if (s.Type == ActionType.Goto) return $"跳转到 {s.Pattern}";
            if (s.Type == ActionType.Label) return $"标签 {s.Pattern}:";
            if (s.Type == ActionType.BreakBlock) return "跳出代码块(If/Else)";
            if (s.Type == ActionType.Network)
                return $"网络: {s.NetworkAdapterName} -> {(s.NetworkEnable ? "恢复" : "断开")}{(s.NetworkSync ? " (同步)" : "")}{(s.DelayMs > 0 ? $" 延{s.DelayMs}ms" : "")}{(s.DwellMs > 0 ? $" 停{s.DwellMs}ms" : "")}";
            return $"{s.Target} 延 {s.DelayMs}±{s.RandomDelay}ms → 识别{(s.OcrNumbersOnly ? "(仅数字)" : "")}{(s.ReuseOcrOnRoiUnchanged ? "(ROI复用)" : "")} 区域 ({s.Rect.X},{s.Rect.Y},{s.Rect.Width},{s.Rect.Height})";
        }

        private void RefreshSteps()
        {
            StepsList.Items.Clear();
            int indent = 0;
            foreach (var s in _steps)
            {
                int currentIndent = indent;
                if (s.Type == ActionType.ElseIf || s.Type == ActionType.Else)
                {
                    currentIndent = Math.Max(0, indent - 1);
                }
                else if (s.Type == ActionType.EndIf || s.Type == ActionType.LoopEnd)
                {
                    indent = Math.Max(0, indent - 1);
                    currentIndent = indent;
                }

                string prefix = new string(' ', currentIndent * 4);
                var item = new System.Windows.Controls.ListBoxItem
                {
                    Content = prefix + FormatStep(s)
                };
                if (s.Type == ActionType.Comment)
                {
                    item.Foreground = System.Windows.Media.Brushes.Gray;
                }
                StepsList.Items.Add(item);

                if (s.Type == ActionType.IfStart || s.Type == ActionType.LoopStart)
                {
                    indent++;
                }
            }
        }

        private void SetOcrResult(System.Windows.Controls.TextBlock? block, string text)
        {
            if (block == null) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                block.Text = "未识别到数据";
                block.Foreground = System.Windows.Media.Brushes.Red;
            }
            else
            {
                block.Text = text;
                block.Foreground = System.Windows.Media.Brushes.Black;
            }
        }

        private void StepsList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(StepsList);
            var idx = GetIndexAtPoint(StepsList, pos);
            if (idx >= 0)
            {
                var item = StepsList.Items[idx];
                if (StepsList.SelectedItems.Contains(item))
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                    {
                        _lastMouseDown = pos;
                        e.Handled = true;
                        StepsList.Focus();
                    }
                }
            }
        }

        private void StepsList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_lastMouseDown != null)
            {
                var pos = e.GetPosition(StepsList);
                var idx = GetIndexAtPoint(StepsList, pos);
                if (idx >= 0)
                {
                    StepsList.SelectedItems.Clear();
                    StepsList.SelectedItems.Add(StepsList.Items[idx]);
                }
                _lastMouseDown = null;
            }
        }

        private void StepsList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                if (_lastMouseDown != null)
                {
                    var current = e.GetPosition(StepsList);
                    if (Math.Abs(current.X - _lastMouseDown.Value.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(current.Y - _lastMouseDown.Value.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        StartDrag(current);
                        _lastMouseDown = null;
                    }
                }
                else if (StepsList.SelectedItems.Count > 0)
                {
                    StartDrag(e.GetPosition(StepsList));
                }
            }
        }

        private void StartDrag(System.Windows.Point pos)
        {
            var idx = GetIndexAtPoint(StepsList, pos);
            if (idx >= 0 && StepsList.SelectedItems.Contains(StepsList.Items[idx]))
            {
                var indices = StepsList.SelectedItems.Cast<System.Windows.Controls.ListBoxItem>()
                               .Select(item => StepsList.Items.IndexOf(item))
                               .OrderBy(i => i).ToList();
                System.Windows.DragDrop.DoDragDrop(StepsList, indices, System.Windows.DragDropEffects.Move);
            }
        }
        private void StepsList_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(System.Collections.Generic.List<int>)))
            {
                var indices = (System.Collections.Generic.List<int>)e.Data.GetData(typeof(System.Collections.Generic.List<int>));
                var targetIndex = GetIndexAtPoint(StepsList, e.GetPosition(StepsList));
                if (targetIndex < 0) targetIndex = _steps.Count; // Drop at end if invalid

                var itemsToMove = new System.Collections.Generic.List<ScriptStep>();
                foreach (var i in indices) itemsToMove.Add(_steps[i]);

                for (int i = indices.Count - 1; i >= 0; i--)
                {
                    _steps.RemoveAt(indices[i]);
                }

                int adjustment = indices.Count(i => i < targetIndex);
                int newTarget = Math.Max(0, targetIndex - adjustment);
                if (newTarget > _steps.Count) newTarget = _steps.Count;

                _steps.InsertRange(newTarget, itemsToMove);
                RefreshSteps();

                StepsList.SelectedItems.Clear();
                for (int i = 0; i < itemsToMove.Count; i++)
                {
                    StepsList.SelectedItems.Add(StepsList.Items[newTarget + i]);
                }
            }
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
            if (StepsList.SelectedItems.Count == 0) return;
            var indices = new System.Collections.Generic.List<int>();
            foreach (var item in StepsList.SelectedItems)
            {
                indices.Add(StepsList.Items.IndexOf(item));
            }
            indices.Sort();

            for (int i = indices.Count - 1; i >= 0; i--)
            {
                int idx = indices[i];
                if (idx >= 0 && idx < _steps.Count)
                {
                    _ocrRoiCache.Remove(_steps[idx]);
                    _steps.RemoveAt(idx);
                }
            }
            RefreshSteps();
            AppendLog($"已删除 {indices.Count} 个步骤");
        }

        private void MenuItem_ExecuteSelected_Click(object sender, RoutedEventArgs e)
        {
            var indices = new System.Collections.Generic.List<int>();
            foreach (var item in StepsList.SelectedItems)
            {
                indices.Add(StepsList.Items.IndexOf(item));
            }
            indices.Sort();

            var selected = new System.Collections.Generic.List<ScriptStep>();
            foreach (var idx in indices)
            {
                if (idx >= 0 && idx < _steps.Count) selected.Add(_steps[idx]);
            }

            if (selected.Count > 0)
            {
                AppendLog($"开始执行选中的 {selected.Count} 个步骤...");
                RunSteps(selected);
            }
            else
            {
                AppendLog("未选择要执行的步骤");
            }
        }

        private void MenuItem_Edit_Click(object sender, RoutedEventArgs e)
        {
            if (StepsList.SelectedIndex < 0) return;
            var step = _steps[StepsList.SelectedIndex];
            var editor = new EditStepWindow(step, GetNetworkAdapters());
            editor.Owner = this;
            if (editor.ShowDialog() == true)
            {
                RefreshSteps();
                AppendLog("已更新所选步骤");
            }
        }

        private void AddBreakLoop_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.BreakLoop });
            RefreshSteps();
            AppendLog("已添加跳出循环");
        }
        private void AddBreakBlock_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.BreakBlock });
            RefreshSteps();
            AppendLog("已添加跳出If/Else块");
        }

        private void ExprBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (ExprHelpText != null) ExprHelpText.Visibility = Visibility.Visible;
        }

        private void ExprBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ExprHelpText != null) ExprHelpText.Visibility = Visibility.Collapsed;
        }

        private void AddContinue_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.ContinueLoop });
            RefreshSteps();
            AppendLog("已添加跳入下次循环");
        }
        private void AddLabel_Click(object sender, RoutedEventArgs e)
        {
            var lbl = LabelBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(lbl)) { AppendLog("标签名不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.Label, Pattern = lbl });
            RefreshSteps();
            AppendLog($"已添加标签: {lbl}");
        }
        private void AddGoto_Click(object sender, RoutedEventArgs e)
        {
            var lbl = LabelBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(lbl)) { AppendLog("标签名不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.Goto, Pattern = lbl });
            RefreshSteps();
            AppendLog($"已添加跳转: {lbl}");
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

        private void AddKeyPressStep_Click(object sender, RoutedEventArgs e)
        {
            var key = KeyPressBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(key)) { AppendLog("按键名称不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.KeyPress, Key = key, DwellMs = 50 });
            RefreshSteps();
            AppendLog($"已添加按键步骤：{key}");
        }

        private void AddCommentStep_Click(object sender, RoutedEventArgs e)
        {
            var text = CommentText?.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) { AppendLog("注释内容不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.Comment, Pattern = text, DelayMs = 0, DwellMs = 0 });
            RefreshSteps();
            AppendLog($"已添加注释步骤：{text}");
        }

        private void MenuItem_Duplicate_Click(object sender, RoutedEventArgs e)
        {
            var selectedIndices = new System.Collections.Generic.List<int>();
            foreach (var item in StepsList.SelectedItems)
            {
                selectedIndices.Add(StepsList.Items.IndexOf(item));
            }
            selectedIndices.Sort();

            if (selectedIndices.Count == 0) return;

            // 获取要复制的步骤对象
            var selectedSteps = new System.Collections.Generic.List<ScriptStep>();
            foreach (var idx in selectedIndices)
            {
                if (idx >= 0 && idx < _steps.Count)
                {
                    selectedSteps.Add(_steps[idx]);
                }
            }

            // 找到插入位置：最后一个选中项的后面
            int lastIndex = selectedIndices[selectedIndices.Count - 1];

            foreach (var item in selectedSteps)
            {
                // 深拷贝
                var newItem = new ScriptStep
                {
                    Type = item.Type,
                    Target = item.Target,
                    Rect = item.Rect,
                    Point = item.Point,
                    DelayMs = item.DelayMs,
                    RandomDelay = item.RandomDelay,
                    DwellMs = item.DwellMs,
                    RandomDwell = item.RandomDwell,
                    RandomX = item.RandomX,
                    RandomY = item.RandomY,
                    Pattern = item.Pattern,
                    Key = item.Key,
                    Count = item.Count,
                    JumpOnTrue = item.JumpOnTrue,
                    OcrNumbersOnly = item.OcrNumbersOnly,
                    ReuseOcrOnRoiUnchanged = item.ReuseOcrOnRoiUnchanged,
                    NetworkAdapterName = item.NetworkAdapterName,
                    NetworkEnable = item.NetworkEnable,
                    NetworkSync = item.NetworkSync
                };
                
                _steps.Insert(lastIndex + 1, newItem);
                lastIndex++;
            }

            RefreshSteps();
            AppendLog($"已复制 {selectedSteps.Count} 个步骤");
        }

        private void NumberOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = new System.Text.RegularExpressions.Regex("[^0-9]+").IsMatch(e.Text);
        }

        // ============================
        // 定时任务相关
        // ============================
        private System.Windows.Threading.DispatcherTimer? _scheduleTimer;
        private DateTime? _scheduleStartTargetTime;
        private DateTime? _scheduleStopTargetTime;
        private bool _isStartScheduleEnabled = false;
        private bool _isStopScheduleEnabled = false;

        private void ToggleStartScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isStartScheduleEnabled)
            {
                // 停止启动定时
                _isStartScheduleEnabled = false;
                _scheduleStartTargetTime = null;
                ToggleStartScheduleButton.Content = "启用启动";
                AppendLog("已关闭定时启动");
            }
            else
            {
                // 启用启动定时
                string h = ScheduleStartH.Text.Trim().PadLeft(2, '0');
                string m = ScheduleStartM.Text.Trim().PadLeft(2, '0');
                string s = ScheduleStartS.Text.Trim().PadLeft(2, '0');
                string timeStr = $"{h}:{m}:{s}";

                if (!DateTime.TryParse(timeStr, out DateTime dt))
                {
                    AppendLog("启动时间格式错误，请使用 HH:mm:ss 格式");
                    MessageBox.Show("启动时间格式错误，请使用 HH:mm:ss 格式");
                    return;
                }

                DateTime now = DateTime.Now;
                var target = new DateTime(now.Year, now.Month, now.Day, dt.Hour, dt.Minute, dt.Second);
                if (target <= now)
                {
                    target = target.AddDays(1);
                }
                _scheduleStartTargetTime = target;
                _isStartScheduleEnabled = true;
                ToggleStartScheduleButton.Content = "取消启动";
                AppendLog($"已启用定时启动，目标时间：{target:yyyy-MM-dd HH:mm:ss}");
            }
            EnsureScheduleTimer();
            UpdateScheduleStatus();
        }

        private void ToggleStopScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isStopScheduleEnabled)
            {
                // 停止停止定时
                _isStopScheduleEnabled = false;
                _scheduleStopTargetTime = null;
                ToggleStopScheduleButton.Content = "启用停止";
                AppendLog("已关闭定时停止");
            }
            else
            {
                // 启用停止定时
                string h = ScheduleStopH.Text.Trim().PadLeft(2, '0');
                string m = ScheduleStopM.Text.Trim().PadLeft(2, '0');
                string s = ScheduleStopS.Text.Trim().PadLeft(2, '0');
                
                // Only enable stop time if at least one field is filled, or default to 00:00:00?
                // Actually the user probably wants to set a specific time.
                // If fields are empty, we can default to 00, but let's check if they are valid.
                
                string timeStr = $"{h.PadLeft(2, '0')}:{m.PadLeft(2, '0')}:{s.PadLeft(2, '0')}";

                if (!DateTime.TryParse(timeStr, out DateTime dt))
                {
                    AppendLog("停止时间格式错误，请使用 HH:mm:ss 格式");
                    MessageBox.Show("停止时间格式错误，请使用 HH:mm:ss 格式");
                    return;
                }

                DateTime now = DateTime.Now;
                var target = new DateTime(now.Year, now.Month, now.Day, dt.Hour, dt.Minute, dt.Second);
                if (target <= now)
                {
                    target = target.AddDays(1);
                }
                _scheduleStopTargetTime = target;
                _isStopScheduleEnabled = true;
                ToggleStopScheduleButton.Content = "取消停止";
                AppendLog($"已启用定时停止，目标时间：{target:yyyy-MM-dd HH:mm:ss}");
            }
            EnsureScheduleTimer();
            UpdateScheduleStatus();
        }

        private void EnsureScheduleTimer()
        {
            if (_isStartScheduleEnabled || _isStopScheduleEnabled)
            {
                if (_scheduleTimer == null)
                {
                    _scheduleTimer = new System.Windows.Threading.DispatcherTimer();
                    _scheduleTimer.Interval = TimeSpan.FromSeconds(1);
                    _scheduleTimer.Tick += ScheduleTimer_Tick;
                }
                if (!_scheduleTimer.IsEnabled)
                {
                    _scheduleTimer.Start();
                }
            }
            else
            {
                if (_scheduleTimer != null && _scheduleTimer.IsEnabled)
                {
                    _scheduleTimer.Stop();
                }
            }
        }

        private void ScheduleTimer_Tick(object? sender, EventArgs e)
        {
            var now = DateTime.Now;

            if (_isStartScheduleEnabled && _scheduleStartTargetTime.HasValue)
            {
                if (now >= _scheduleStartTargetTime.Value)
                {
                    AppendLog("定时启动时间到达，开始执行...");
                    RunScriptAll();
                    // 推迟到下一天
                    _scheduleStartTargetTime = _scheduleStartTargetTime.Value.AddDays(1);
                }
            }

            if (_isStopScheduleEnabled && _scheduleStopTargetTime.HasValue)
            {
                if (now >= _scheduleStopTargetTime.Value)
                {
                    AppendLog("定时停止时间到达，正在停止脚本...");
                    StopScript();
                    // 推迟到下一天
                    _scheduleStopTargetTime = _scheduleStopTargetTime.Value.AddDays(1);
                }
            }

            UpdateScheduleStatus();
        }

        private void UpdateScheduleStatus()
        {
            string status = "";
            if (_isStartScheduleEnabled && _scheduleStartTargetTime.HasValue)
            {
                status += $"启: {_scheduleStartTargetTime.Value:MM-dd HH:mm:ss} ";
            }
            else
            {
                status += "启: 未启用 ";
            }

            if (_isStopScheduleEnabled && _scheduleStopTargetTime.HasValue)
            {
                status += $"| 停: {_scheduleStopTargetTime.Value:MM-dd HH:mm:ss}";
            }
            else
            {
                status += "| 停: 未启用";
            }
            
            ScheduleStatusText.Text = status;
        }

        private void TimeInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                // Remove non-digits
                string text = new string(tb.Text.Where(char.IsDigit).ToArray());
                
                if (int.TryParse(text, out int val))
                {
                    // Check if it is Hour or Minute/Second based on Name
                    if (tb.Name.EndsWith("H")) // Hour
                    {
                        if (val > 23) text = "23";
                    }
                    else // Minute or Second
                    {
                        if (val > 59) text = "59";
                    }
                }
                
                if (text != tb.Text)
                {
                    tb.Text = text;
                    tb.SelectionStart = text.Length; // Restore cursor position
                }
            }
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                AppendLog($"无法打开链接: {ex.Message}");
            }
        }
 
        private void RunScriptAll()
        {
            if (_steps.Count == 0)
            {
                AppendLog("没有要执行的步骤");
                return;
            }
            RunSteps(_steps.ToList());
        }

        private void RunScriptAll_Click(object sender, RoutedEventArgs e)
        {
            RunScriptAll();
        }

        private async void RunSteps(System.Collections.Generic.List<ScriptStep> steps)
        {
            int loops = ParseInt(LoopCount?.Text, 1);
            bool breakOnEmpty = BreakOnEmpty?.IsChecked == true;
            _stopAll = false;
            _isRunning = true;
            DoRegisterHotkey(); // 开始运行时注册热键
            InstallHook();
            
            await System.Threading.Tasks.Task.Run(() =>
            {
                string lastOcrA = "", lastOcrB = "";
                try
                {
                    int n = steps.Count;

                    var loopEndByStart = new System.Collections.Generic.Dictionary<int, int>();
                    var labelIndex = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
                    var nextBranch = new System.Collections.Generic.Dictionary<int, int>();
                    var endIfByBranch = new System.Collections.Generic.Dictionary<int, int>();
                    var endIfContaining = new int[n];
                    for (int i = 0; i < n; i++) endIfContaining[i] = -1;

                    {
                        var loopStack = new System.Collections.Generic.Stack<int>();
                        for (int i = 0; i < n; i++)
                        {
                            var t = steps[i].Type;
                            if (t == ActionType.LoopStart) loopStack.Push(i);
                            else if (t == ActionType.LoopEnd)
                            {
                                if (loopStack.Count > 0)
                                {
                                    var start = loopStack.Pop();
                                    loopEndByStart[start] = i;
                                }
                            }
                            else if (t == ActionType.Label)
                            {
                                var name = steps[i].Pattern ?? "";
                                if (!string.IsNullOrWhiteSpace(name) && !labelIndex.ContainsKey(name))
                                {
                                    labelIndex[name] = i;
                                }
                            }
                        }
                    }

                    {
                        var ifStack = new System.Collections.Generic.Stack<(int ifStart, System.Collections.Generic.List<int> branches)>();
                        for (int i = 0; i < n; i++)
                        {
                            var t = steps[i].Type;
                            if (t == ActionType.IfStart)
                            {
                                ifStack.Push((i, new System.Collections.Generic.List<int> { i }));
                            }
                            else if (t == ActionType.ElseIf || t == ActionType.Else)
                            {
                                if (ifStack.Count > 0)
                                {
                                    var top = ifStack.Pop();
                                    top.branches.Add(i);
                                    ifStack.Push(top);
                                }
                            }
                            else if (t == ActionType.EndIf)
                            {
                                if (ifStack.Count > 0)
                                {
                                    var top = ifStack.Pop();
                                    for (int b = 0; b < top.branches.Count; b++)
                                    {
                                        var branchIdx = top.branches[b];
                                        endIfByBranch[branchIdx] = i;
                                        nextBranch[branchIdx] = (b + 1 < top.branches.Count) ? top.branches[b + 1] : i;
                                    }
                                }
                            }
                        }

                        var endStack = new System.Collections.Generic.Stack<int>();
                        for (int i = 0; i < n; i++)
                        {
                            var t = steps[i].Type;
                            if (t == ActionType.IfStart)
                            {
                                if (endIfByBranch.TryGetValue(i, out var endIfIdx))
                                {
                                    endStack.Push(endIfIdx);
                                }
                            }
                            if (endStack.Count > 0) endIfContaining[i] = endStack.Peek();
                            if (t == ActionType.EndIf && endStack.Count > 0)
                            {
                                var topEnd = endStack.Peek();
                                if (topEnd == i) endStack.Pop();
                            }
                        }
                    }

                    for (int i = 0; i < Math.Max(1, loops); i++)
                    {
                        bool breakAll = false;
                        var stack = new System.Collections.Generic.Stack<(int start, int remain)>();
                        int idx = 0;
                        bool forceEval = false;
                        while (idx < n)
                        {
                        if (_stopAll) { breakAll = true; break; }
                        var step = steps[idx];
                        int delay = Math.Max(0, GetRandomVal(step.DelayMs, step.RandomDelay));
                        System.Threading.Thread.Sleep(delay);
                        
                        if (step.Type == ActionType.Click)
                        {
                            var hwnd = step.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
                            if (hwnd == IntPtr.Zero) { AppendLog($"{step.Target}：未绑定窗口"); breakAll = true; break; }
                            var wrect = NativeMethods.GetRect(hwnd);
                            int offX = GetRandomVal(0, step.RandomX);
                            int offY = GetRandomVal(0, step.RandomY);
                            int sx = wrect.Left + step.Point.X + offX;
                            int sy = wrect.Top + step.Point.Y + offY;
                            
                            if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, 9);
                            NativeMethods.SetForegroundWindow(hwnd);
                            
                            int dwell = Math.Max(0, GetRandomVal(step.DwellMs, step.RandomDwell));
                            NativeMethods.ClickAtScreen(sx, sy, dwell);
                            AppendLog($"{step.Target}执行：点击 {sx},{sy} (延{delay} 停{dwell})");
                        }
                        else if (step.Type == ActionType.Ocr)
                        {
                            var hwnd = step.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
                            if (hwnd == IntPtr.Zero) { AppendLog($"{step.Target}：未绑定窗口"); breakAll = true; break; }
                            using var bmp = NativeMethods.CaptureWindow(hwnd);
                            using var mat = BitmapConverter.ToMat(bmp);
                            var r = step.Rect;
                            var x = Math.Max(0, Math.Min(mat.Cols - 1, r.X));
                            var y = Math.Max(0, Math.Min(mat.Rows - 1, r.Y));
                            var w = Math.Max(1, Math.Min(mat.Cols - x, r.Width));
                            var h = Math.Max(1, Math.Min(mat.Rows - y, r.Height));
                            var roi = new OpenCvSharp.Rect(x, y, w, h);
                            using var matRoi = new Mat(mat, roi);
                            var ocr = PerformOcrWithCache(step, matRoi);

                            if (step.Target == TargetType.A) { lastOcrA = ocr.text; Dispatcher.Invoke(() => SetOcrResult(OcrResultTextA, ocr.text)); }
                            else { lastOcrB = ocr.text; Dispatcher.Invoke(() => SetOcrResult(OcrResultTextB, ocr.text)); }
                            var tag = step.ReuseOcrOnRoiUnchanged ? (ocr.reused ? "(复用)" : "(重新识别)") : "";
                            string logText = $"{step.Target}执行：识别{tag} {ocr.text}";
                            if (string.IsNullOrWhiteSpace(ocr.text))
                            {
                                logText += " 未识别到数据 大概原因：识别区域过小或执行速度太快没有识别到对应图片";
                            }
                            AppendLog(logText);
                            if (breakOnEmpty && string.IsNullOrWhiteSpace(ocr.text)) { breakAll = true; break; }
                        }
                        else if (step.Type == ActionType.Save)
                        {
                            var txt = step.Target == TargetType.A ? lastOcrA : lastOcrB;
                            var savedVal = txt ?? "";
                            _vars[step.Key] = savedVal;
                            AppendLog($"保存 {step.Target} 结果到 {step.Key}: {savedVal}");
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
                            AppendLog($"条件检查: {(match ? "匹配" : "不匹配")} 模式 {step.Pattern} 文本 {text}");
                            if ((step.JumpOnTrue && match) || (!step.JumpOnTrue && !match))
                            {
                                if (stack.Count > 0)
                                {
                                    bool skipped = false;
                                    var top = stack.Pop();
                                    if (loopEndByStart.TryGetValue(top.start, out var loopEndIdx))
                                    {
                                        idx = loopEndIdx + 1;
                                        skipped = true;
                                    }
                                    if (skipped) continue;
                                }
                                breakAll = true; break;
                            }
                        }
                        else if (step.Type == ActionType.KeyPress)
                        {
                            var keyStr = step.Key;
                            if (Enum.TryParse<System.Windows.Input.Key>(keyStr, true, out var k))
                            {
                                var vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
                                NativeMethods.keybd_event((byte)vk, 0, 0, 0);
                                if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
                                NativeMethods.keybd_event((byte)vk, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
                                AppendLog($"执行按键：{keyStr}");
                            }
                            else
                            {
                                AppendLog($"未知的按键名称：{keyStr}");
                            }
                        }
                        else if (step.Type == ActionType.Expression)
                        {
                            bool ok = EvaluateExpression(step.Pattern, out string? errorMsg);
                            step.LastResult = ok;
                            if (errorMsg != null)
                            {
                                AppendLog($"表达式错误: {errorMsg}");
                            }
                            AppendLog($"表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}，跳出条件={ (step.JumpOnTrue ? "为真" : "为假") }");
                            AppendExprLog($"表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}");
                            if ((step.JumpOnTrue && ok) || (!step.JumpOnTrue && !ok))
                            {
                                if (stack.Count > 0)
                                {
                                    bool skipped = false;
                                    var top = stack.Pop();
                                    if (loopEndByStart.TryGetValue(top.start, out var loopEndIdx))
                                    {
                                        idx = loopEndIdx + 1;
                                        skipped = true;
                                    }
                                    if (skipped) continue;
                                }
                                breakAll = true; break;
                            }
                        }
                        else if (step.Type == ActionType.BringFront)
                        {
                            var hwnd = step.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
                            if (hwnd == IntPtr.Zero) { AppendLog($"{step.Target}：未绑定窗口"); breakAll = true; break; }
                            if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, 9);
                            NativeMethods.SetForegroundWindow(hwnd);
                            AppendLog($"{step.Target}执行：置顶窗口");
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
                        else if (step.Type == ActionType.IfStart)
                        {
                            bool res = EvaluateExpression(step.Pattern, out string? errorMsg);
                            if (errorMsg != null)
                            {
                                AppendLog($"If 检查: 错误 - {errorMsg} {step.Pattern}");
                            }
                            else
                            {
                                AppendLog($"If 检查: {(res ? "为真" : "为假")} {step.Pattern}");
                            }
                            if (!res)
                            {
                                if (nextBranch.TryGetValue(idx, out var nb)) idx = nb;
                                else idx = n;
                                forceEval = true;
                                continue;
                            }
                        }
                        else if (step.Type == ActionType.ElseIf)
                        {
                            if (forceEval)
                            {
                                forceEval = false;
                                bool res = EvaluateExpression(step.Pattern, out string? errorMsg);
                                if (errorMsg != null)
                                {
                                    AppendLog($"ElseIf 检查: 错误 - {errorMsg} {step.Pattern}");
                                }
                                else
                                {
                                    AppendLog($"ElseIf 检查: {(res ? "为真" : "为假")} {step.Pattern}");
                                }
                                if (!res)
                                {
                                    if (nextBranch.TryGetValue(idx, out var nb)) idx = nb;
                                    else idx = n;
                                    forceEval = true;
                                    continue;
                                }
                            }
                            else
                            {
                                if (endIfByBranch.TryGetValue(idx, out var endIf)) idx = endIf;
                                else if (endIfContaining[idx] >= 0) idx = endIfContaining[idx];
                                else idx = n;
                                continue;
                            }
                        }
                        else if (step.Type == ActionType.Else)
                        {
                            if (forceEval)
                            {
                                forceEval = false;
                            }
                            else
                            {
                                if (endIfContaining[idx] >= 0) idx = endIfContaining[idx];
                                else idx = n;
                                continue;
                            }
                        }
                        else if (step.Type == ActionType.EndIf)
                        {
                            forceEval = false;
                        }
                        else if (step.Type == ActionType.BreakLoop)
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                if (loopEndByStart.TryGetValue(top.start, out var loopEndIdx))
                                {
                                    idx = loopEndIdx + 1;
                                    AppendLog("执行：跳出循环");
                                    continue;
                                }
                            }
                            AppendLog("跳出循环失败：未在循环内");
                        }
                        else if (step.Type == ActionType.ContinueLoop)
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Peek();
                                if (loopEndByStart.TryGetValue(top.start, out var loopEndIdx))
                                {
                                    idx = loopEndIdx; // Jump to LoopEnd, which will handle decr and jump back
                                    AppendLog("执行：跳入下次循环");
                                    continue;
                                }
                            }
                             AppendLog("跳入下次循环失败：未在循环内");
                        }
                        else if (step.Type == ActionType.BreakBlock)
                        {
                             if (endIfContaining[idx] >= 0)
                             {
                                 idx = endIfContaining[idx];
                                 AppendLog("执行：跳出代码块");
                                 continue;
                             }
                             AppendLog("跳出代码块失败：未在 If/Else 块内");
                        }
                        else if (step.Type == ActionType.Goto)
                        {
                            var targetLabel = step.Pattern;
                            if (!string.IsNullOrWhiteSpace(targetLabel) && labelIndex.TryGetValue(targetLabel, out var targetIdx))
                            {
                                idx = targetIdx;
                                AppendLog($"执行：跳转到 {targetLabel}");
                                continue;
                            }
                            else
                            {
                                AppendLog($"跳转失败：找不到标签 {targetLabel}");
                                breakAll = true; break;
                            }
                        }
                        else if (step.Type == ActionType.Label)
                        {
                            // Do nothing
                        }
                        else if (step.Type == ActionType.Network)
                        {
                            if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
                        }
                        else if (step.Type == ActionType.Comment)
                        {
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
                AppendLog("全部步骤执行完毕/停止");
            }
            });
            _stopAll = false;
        }

        private bool EvaluateExpression(string expr, out string? errorMsg)
        {
            errorMsg = null;
            // 预处理：替换中文引号和双引号为单引号
            expr = expr.Replace("“", "'").Replace("”", "'").Replace("\"", "'");
            
            // 定义替换逻辑函数
            string BuildExpression(bool forceString, out Dictionary<string, string> currentMaps)
            {
                currentMaps = new Dictionary<string, string>();
                var maps = currentMaps; // capture for lambda
                
                // 匹配 字符串字面量(支持转义'') 或 变量名
                var tokenRegex = new System.Text.RegularExpressions.Regex(@"'(''|[^'])*'|(?<var>\b[A-Za-z_]\w*\b)");
                
                // 忽略的关键字
                var keywords = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { 
                    "AND", "OR", "NOT", "TRUE", "FALSE", "NULL", "LIKE", "IN", "IS" 
                };

                string replaced = tokenRegex.Replace(expr, m =>
                {
                    // 如果是字符串字面量，原样返回
                    if (m.Value.StartsWith("'")) return m.Value;

                    var name = m.Value;
                    if (keywords.Contains(name)) return name;

                    if (_vars.TryGetValue(name, out var val))
                    {
                        if (!forceString)
                        {
                            // 尝试解析为数字
                            if (double.TryParse(val, out double d))
                            {
                                 string s = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                 maps[name] = s;
                                 return s;
                            }
                        }
                        // 否则作为字符串，转义单引号
                        string strVal = "'" + (val ?? "").Replace("'", "''") + "'";
                        maps[name] = val ?? ""; 
                        return strVal;
                    }
                    
                    // 未找到变量，返回0
                    maps[name] = "0";
                    return forceString ? "'0'" : "0";
                });

                // 支持 C# 风格操作符
                return replaced.Replace("||", " OR ")
                               .Replace("&&", " AND ")
                               .Replace("!=", "<>")
                               .Replace("==", "=");
            }

            // 执行计算的辅助函数
            object RunCompute(string finalExpr)
            {
                var dt = new System.Data.DataTable();
                dt.Columns.Add("x", typeof(double));
                return dt.Compute(finalExpr, "");
            }

            Dictionary<string, string> maps;
            string finalExpr = BuildExpression(false, out maps);
            object? result = null;
            string usedExpr = finalExpr;

            // 检查是否包含字符串大小比较 (禁止 >, <, >=, <= 用于字符串)
            // 字符串字面量正则: '(''|[^'])*'
            // 比较运算符正则: (>=|<=|>(?![=])|<(?![=>]))  注意排除 = 和 <>
            // 匹配模式: 字符串+运算符 或 运算符+字符串
            var strCmpRegex = new System.Text.RegularExpressions.Regex(
                @"('(''|[^'])*'\s*(>=|<=|>(?![=])|<(?![=>])))|((>=|<=|>(?![=])|<(?![=>]))\s*'(''|[^'])*')");

            if (strCmpRegex.IsMatch(finalExpr))
            {
                errorMsg = "原因没有开启仅识别数字或未识别到数字，所以不支持大小比较运算 (>, <, >=, <=)";
                string msg = errorMsg;
                Dispatcher.Invoke(() => AppendExprLog($"表达式错误：{msg}"));
                return false;
            }

            try
            {
                result = RunCompute(finalExpr);
            }
            catch (Exception ex)
            {
                // 如果是类型不匹配错误，尝试强制字符串模式
                if (ex.Message.Contains("System.Int32") && ex.Message.Contains("System.String") || ex is System.Data.EvaluateException)
                {
                    try 
                    {
                        finalExpr = BuildExpression(true, out maps);
                        usedExpr = finalExpr;
                        result = RunCompute(finalExpr);
                    }
                    catch (Exception ex2)
                    {
                        errorMsg = ex2.Message;
                        Dispatcher.Invoke(() => AppendExprLog($"表达式错误(重试失败)：{ex2.Message}"));
                        return false;
                    }
                }
                else
                {
                    errorMsg = ex.Message;
                    Dispatcher.Invoke(() => AppendExprLog($"表达式错误：{ex.Message}"));
                    return false;
                }
            }

            // 记录日志 (使用最终成功的表达式)
            try
            {
                // 尝试计算每个子表达式的算术结果用于显示
                string displayReplaced = usedExpr;
                var dt = new System.Data.DataTable(); // New instance for display eval
                dt.Columns.Add("x", typeof(double));
                
                try
                {
                    // 1. 按逻辑运算符分割: AND, OR
                    var logicParts = System.Text.RegularExpressions.Regex.Split(usedExpr, @"( AND | OR )", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    for (int i = 0; i < logicParts.Length; i++)
                    {
                        var part = logicParts[i];
                        if (part.Trim().ToUpper() == "AND" || part.Trim().ToUpper() == "OR") continue;

                        // 2. 按关系运算符分割: <=, >=, <>, =, <, >
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
                            logicParts[i] = EvalSimple(part, dt);
                        }
                    }
                    displayReplaced = string.Join("", logicParts);
                }
                catch { }

                string mapping = string.Join(", ", maps.Where(kv => kv.Value != "").Select(kv => kv.Key + "=" + kv.Value));
                Dispatcher.Invoke(() => AppendExprLog($" 原='{expr}' | [{mapping}]\n 结果='{displayReplaced}' | {result}"));
            }
            catch { }

            if (result is bool b) return b;
            if (result is IConvertible c)
            {
                double v = c.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                return v != 0;
            }
            
            errorMsg = "计算结果无效";
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
            public bool OcrNumbersOnly { get; set; }
            public bool ReuseOcrOnRoiUnchanged { get; set; }
            public string NetworkAdapterName { get; set; } = "";
            public bool NetworkEnable { get; set; }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureSavedQueuesDir();
            LoadSavedScripts();
        }

        private void EnsureSavedQueuesDir()
        {
            if (!System.IO.Directory.Exists(_savedQueuesDir))
            {
                try { System.IO.Directory.CreateDirectory(_savedQueuesDir); } catch { }
            }
        }

        private void LoadSavedScripts()
        {
            if (QuickLoadCombo == null) return;
            try
            {
                EnsureSavedQueuesDir();
                var files = System.IO.Directory.GetFiles(_savedQueuesDir, "*.json");
                var items = files.Select(f => System.IO.Path.GetFileName(f)).ToList();
                QuickLoadCombo.ItemsSource = items;
            }
            catch (Exception ex)
            {
                AppendLog($"刷新队列列表失败: {ex.Message}");
            }
        }

        private void QuickLoadCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (QuickLoadCombo.SelectedItem is string filename)
            {
                var fullPath = System.IO.Path.Combine(_savedQueuesDir, filename);
                LoadStepsFromFile(fullPath);
            }
        }

        private void RefreshScripts_Click(object sender, RoutedEventArgs e)
        {
            LoadSavedScripts();
            AppendLog("已刷新队列列表");
        }

        private void OpenScriptDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureSavedQueuesDir();
                Process.Start("explorer.exe", _savedQueuesDir);
            }
            catch (Exception ex)
            {
                AppendLog($"无法打开目录: {ex.Message}");
            }
        }

        private void SaveSteps_Click(object sender, RoutedEventArgs e)
        {
            EnsureSavedQueuesDir();
            var dlg = new SaveFileDialog();
            dlg.Filter = "JSON 文件|*.json";
            dlg.FileName = "steps.json";
            dlg.InitialDirectory = _savedQueuesDir;
            if (dlg.ShowDialog() == true)
            {
                var stepList = _steps
                    .Where(s => s.Type != ActionType.Network)
                    .Select(s => new StepDto
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
                    JumpOnTrue = s.JumpOnTrue,
                    OcrNumbersOnly = s.OcrNumbersOnly,
                    ReuseOcrOnRoiUnchanged = s.ReuseOcrOnRoiUnchanged,
                    NetworkAdapterName = s.NetworkAdapterName,
                    NetworkEnable = s.NetworkEnable
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
                    LoadSavedScripts(); // Refresh list after save
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
            dlg.InitialDirectory = _savedQueuesDir;
            if (dlg.ShowDialog() == true)
            {
                LoadStepsFromFile(dlg.FileName);
            }
        }

        private void LoadStepsFromFile(string filePath)
        {
                try
                {
                    var json = System.IO.File.ReadAllText(filePath);
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
                    _ocrRoiCache.Clear();
                    foreach (var d in list)
                    {
                        if (string.Equals(d.Type, "Network", StringComparison.OrdinalIgnoreCase)) continue;
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
                            JumpOnTrue = d.JumpOnTrue,
                            OcrNumbersOnly = d.OcrNumbersOnly,
                            ReuseOcrOnRoiUnchanged = d.ReuseOcrOnRoiUnchanged,
                            NetworkAdapterName = d.NetworkAdapterName ?? "",
                            NetworkEnable = d.NetworkEnable
                        };
                        _steps.Add(s);
                    }
                    RefreshSteps();
                    
                    // 显示加载的说明
                    if (UsageNoteText != null)
                    {
                        UsageNoteText.Text = $"步骤队列使用事项：{(string.IsNullOrWhiteSpace(note) ? "(无)" : note)}";
                        UsageNoteText.Visibility = Visibility.Visible;
                    }

                    AppendLog($"已加载队列：{System.IO.Path.GetFileName(filePath)}");
                }
                catch (Exception ex)
                {
                    AppendLog($"加载失败：{ex.Message}");
                }
        }

        private void StopScript()
        {
            _stopAll = true;
            AppendLog("已请求停止全部步骤");
        }

        private void StopAll_Click(object sender, RoutedEventArgs e)
        {
            StopScript();
        }

        private bool _identifyingKey = false;

        private void IdentifyKey_Click(object sender, RoutedEventArgs e)
        {
            _identifyingKey = true;
            IdentifyKeyButton.Content = "请按键...";
            // 确保窗口获得焦点以接收键盘事件
            this.Focus();
        }

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_identifyingKey)
            {
                var key = (e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key);
                // 忽略单独的 Ctrl/Shift/Alt 键本身，除非用户就是想录制修饰键
                // 这里我们直接记录所有键
                KeyPressBox.Text = key.ToString();
                
                _identifyingKey = false;
                IdentifyKeyButton.Content = "识别按键";
                e.Handled = true;
                return;
            }

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
            _ocrRoiCache.Clear();
            RefreshSteps();
            if (UsageNoteText != null) UsageNoteText.Visibility = Visibility.Collapsed;
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

        private void InstallDirectMlEnv_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("即将执行以下操作：\n1. 卸载当前的 onnxruntime 相关库\n2. 安装 onnxruntime-directml (支持 AMD/Intel/NVIDIA)\n\n确定要继续吗？", "确认安装", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
            lock (_exprLogLock)
            {
                _pendingExprLogs.Enqueue((DateTime.Now, text));
                if (_exprLogFlushScheduled) return;
                _exprLogFlushScheduled = true;
            }

            Dispatcher.BeginInvoke(new Action(FlushExprLogs));
        }
        private void AppendLog(string text)
        {
            lock (_logLock)
            {
                _pendingLogs.Enqueue((DateTime.Now, text));
                if (_logFlushScheduled) return;
                _logFlushScheduled = true;
            }

            Dispatcher.BeginInvoke(new Action(FlushLogs));
        }

        private void FlushLogs()
        {
            System.Collections.Generic.List<(DateTime ts, string text)> batch = new();
            lock (_logLock)
            {
                _logFlushScheduled = false;
                while (_pendingLogs.Count > 0 && batch.Count < 200)
                {
                    batch.Add(_pendingLogs.Dequeue());
                }
                if (_pendingLogs.Count > 0) _logFlushScheduled = true;
            }

            if (batch.Count > 0 && OutputBox != null)
            {
                var paragraph = OutputBox.Document.Blocks.FirstBlock as System.Windows.Documents.Paragraph;
                if (paragraph == null)
                {
                    paragraph = new System.Windows.Documents.Paragraph();
                    OutputBox.Document.Blocks.Add(paragraph);
                }

                foreach (var item in batch)
                {
                    var timestampRun = new System.Windows.Documents.Run(item.ts.ToString("HH:mm:ss") + " ") { Foreground = System.Windows.Media.Brushes.Black };
                    paragraph.Inlines.Add(timestampRun);

                    string[] logParts = System.Text.RegularExpressions.Regex.Split(item.text, @"( 未识别到数据 大概原因：识别区域过小或执行速度太快没有识别到对应图片| 未识别到数据 大概原因：识别区域过小或执行速度太快没有识别到对应应图片| 未识别到数据可能原因：识别区域过小或执行速度太快没有识别到对应应图片| 未识别到数据| 未识别到)");
                    foreach (var part in logParts)
                    {
                        if (string.IsNullOrEmpty(part)) continue;
                        if (part.StartsWith(" 未识别到"))
                        {
                            paragraph.Inlines.Add(new System.Windows.Documents.Run(part) { Foreground = System.Windows.Media.Brushes.Red });
                        }
                        else
                        {
                            paragraph.Inlines.Add(new System.Windows.Documents.Run(part) { Foreground = System.Windows.Media.Brushes.Black });
                        }
                    }
                    paragraph.Inlines.Add(new System.Windows.Documents.LineBreak());
                }

                if (paragraph.Inlines.Count > 2000)
                {
                    OutputBox.Document.Blocks.Clear();
                    paragraph = new System.Windows.Documents.Paragraph();
                    paragraph.Inlines.Add(new System.Windows.Documents.Run($"[系统] 日志过长已自动清理 ({DateTime.Now:HH:mm:ss})\n") { Foreground = System.Windows.Media.Brushes.Gray });
                    OutputBox.Document.Blocks.Add(paragraph);
                }

                OutputBox.ScrollToEnd();
            }

            if (batch.Count > 0)
            {
                try
                {
                    var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jietu");
                    System.IO.Directory.CreateDirectory(dir);
                    var log = System.IO.Path.Combine(dir, "app.log");
                    var sbFile = new System.Text.StringBuilder();
                    foreach (var item in batch)
                    {
                        sbFile.Append(item.ts.ToString("yyyy-MM-dd HH:mm:ss")).Append(' ').Append(item.text).Append('\n');
                    }
                    System.IO.File.AppendAllText(log, sbFile.ToString());
                }
                catch { }
            }

            lock (_logLock)
            {
                if (_logFlushScheduled)
                {
                    Dispatcher.BeginInvoke(new Action(FlushLogs));
                }
            }
        }

        private void FlushExprLogs()
        {
            System.Collections.Generic.List<(DateTime ts, string text)> batch = new();
            lock (_exprLogLock)
            {
                _exprLogFlushScheduled = false;
                while (_pendingExprLogs.Count > 0 && batch.Count < 200)
                {
                    batch.Add(_pendingExprLogs.Dequeue());
                }
                if (_pendingExprLogs.Count > 0) _exprLogFlushScheduled = true;
            }

            if (batch.Count > 0 && ExprLogBox != null)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in batch)
                {
                    sb.Append(item.ts.ToString("HH:mm:ss")).Append(' ').Append(item.text).Append('\n');
                }
                ExprLogBox.AppendText(sb.ToString());
                if (ExprLogBox.Text.Length > 20000)
                {
                    ExprLogBox.Text = "[系统] 日志过长已清理\n" + ExprLogBox.Text.Substring(ExprLogBox.Text.Length - 10000);
                }
                ExprLogBox.ScrollToEnd();
            }

            lock (_exprLogLock)
            {
                if (_exprLogFlushScheduled)
                {
                    Dispatcher.BeginInvoke(new Action(FlushExprLogs));
                }
            }
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            if (OutputBox != null)
            {
                var textRange = new System.Windows.Documents.TextRange(OutputBox.Document.ContentStart, OutputBox.Document.ContentEnd);
                if (!string.IsNullOrWhiteSpace(textRange.Text))
                {
                    try
                    {
                        Clipboard.SetText(textRange.Text);
                        MessageBox.Show("日志已复制到剪贴板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"复制失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }
        private void AddIfStep_Click(object sender, RoutedEventArgs e)
        {
            var expr = ExprBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(expr)) { AppendLog("If 表达式不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.IfStart, Pattern = expr });
            RefreshSteps();
            AppendLog($"已添加 If: {expr}");
        }
        private void AddElseIfStep_Click(object sender, RoutedEventArgs e)
        {
            var expr = ExprBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(expr)) { AppendLog("ElseIf 表达式不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.ElseIf, Pattern = expr });
            RefreshSteps();
            AppendLog($"已添加 ElseIf: {expr}");
        }
        private void AddElseStep_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.Else });
            RefreshSteps();
            AppendLog("已添加 Else");
        }
        private void AddEndIfStep_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.EndIf });
            RefreshSteps();
            AppendLog("已添加 EndIf");
        }

        private int FindNextBranch(int startIdx)
        {
            int depth = 0;
            for (int i = startIdx + 1; i < _steps.Count; i++)
            {
                var t = _steps[i].Type;
                if (t == ActionType.IfStart) depth++;
                else if (t == ActionType.EndIf)
                {
                    if (depth == 0) return i;
                    depth--;
                }
                else if ((t == ActionType.ElseIf || t == ActionType.Else) && depth == 0)
                {
                    return i;
                }
            }
            return _steps.Count;
        }

        private int FindEndIf(int startIdx)
        {
            int depth = 0;
            for (int i = startIdx + 1; i < _steps.Count; i++)
            {
                var t = _steps[i].Type;
                if (t == ActionType.IfStart) depth++;
                else if (t == ActionType.EndIf)
                {
                    if (depth == 0) return i;
                    depth--;
                }
            }
            return _steps.Count;
        }
    }
}
