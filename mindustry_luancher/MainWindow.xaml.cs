using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MindustryLauncherGUI
{
    public partial class MainWindow : Window
    {
        private static readonly string ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_config.json");
        private AppConfig _config = new AppConfig();
        private GameInstanceInfo? _currentInstance;
        private HashSet<string> _runningInstancePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private VersionConfig _currentVersionConfig = new VersionConfig();
        private readonly HttpClient _http;

        private string _currentDownloadRepo = "Anuken/Mindustry";
        private List<ModRegistryEntry> _allOnlineMods = new List<ModRegistryEntry>();
        private ModRegistryEntry? _selectedModToInstall;

        private string _currentSchematicRepo = "MinRi2/schematics-archives";
        private string _currentSchematicBranch = "master";
        private List<SchematicEntry> _allOnlineSchematics = new List<SchematicEntry>();
        private SchematicEntry? _selectedSchematicToInstall;

        private CancellationTokenSource? _schematicFetchCts;
        private readonly SemaphoreSlim _schematicFetchLock = new SemaphoreSlim(1, 1);
        private bool _isDownloading = false;
        private string _currentDetailUrl = "";

        public static int CurrentProxyIndex = 1;
        private ICollectionView? _settingsView;

        public MainWindow()
        {
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (m, c, ch, e) => true };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadConfig();
            GlobalJavaComboBox.Text = _config.GlobalJavaPath;
            int maxRam = (HardwareInfo.GetTotalPhysicalMemoryMB() / 512) * 512;
            GlobalRamSlider.Maximum = maxRam;
            VSettingsRamSlider.Maximum = maxRam;
            _config.GlobalRamMB = Math.Min(_config.GlobalRamMB, maxRam);
            GlobalRamSlider.Value = _config.GlobalRamMB;
            GlobalAutoRamCheck.IsChecked = _config.GlobalUseAutoRam;
            CurrentProxyIndex = _config.ProxyNodeIndex;
            ProxyNodeBox.SelectedIndex = _config.ProxyNodeIndex;

            if (!string.IsNullOrEmpty(_config.LastSelectedInstancePath) && File.Exists(Path.Combine(_config.LastSelectedInstancePath, "Mindustry.jar")))
            {
                _currentInstance = new GameInstanceInfo { Name = Path.GetFileName(_config.LastSelectedInstancePath), FullPath = _config.LastSelectedInstancePath };
            }

            UpdateMainUI();
            RbOfficial.IsChecked = true;
            RbSchemMinRi2.IsChecked = true;
            MainTabControl.SelectedIndex = -1;
            SwitchTab(0);

            Task.Run(() =>
            {
                var javas = JavaScanner.Scan();
                Dispatcher.InvokeAsync(() =>
                {
                    GlobalJavaComboBox.ItemsSource = javas;
                    VSettingsJavaComboBox.ItemsSource = javas;
                });
            });

            LoadGameIconAsync();
        }

        private int CalculateSmartRam()
        {
            int tg = (HardwareInfo.GetTotalPhysicalMemoryMB() - 2048) / 2;
            return (Math.Clamp(tg, 1024, 8192) / 512) * 512;
        }

        private void GlobalAutoRamCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (GlobalRamSlider == null || GlobalRamText == null) return;
            bool isAuto = GlobalAutoRamCheck.IsChecked ?? false;
            GlobalRamSlider.IsEnabled = !isAuto;
            _config.GlobalUseAutoRam = isAuto;
            if (isAuto)
            {
                int autoRam = CalculateSmartRam();
                GlobalRamSlider.Value = autoRam;
                GlobalRamText.Text = $"{autoRam} MB (自动)";
            }
            else
            {
                GlobalRamText.Text = $"{(int)GlobalRamSlider.Value} MB";
            }
        }

        private void GlobalRamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GlobalRamText != null && GlobalAutoRamCheck.IsChecked == false)
                GlobalRamText.Text = $"{(int)e.NewValue} MB";
        }

        private void VersionAutoRamCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (VSettingsRamSlider == null || VSettingsRamText == null) return;
            bool isAuto = VersionAutoRamCheck.IsChecked ?? false;
            VSettingsRamSlider.IsEnabled = !isAuto;
            _currentVersionConfig.UseAutoRam = isAuto;
            if (isAuto)
            {
                int targetRam = _config.GlobalUseAutoRam ? CalculateSmartRam() : _config.GlobalRamMB;
                VSettingsRamSlider.Value = targetRam;
                VSettingsRamText.Text = $"{targetRam} MB (跟随全局)";
            }
            else
            {
                VSettingsRamText.Text = $"{(int)VSettingsRamSlider.Value} MB";
            }
        }

        private void VSettingsRamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VSettingsRamText != null && VersionAutoRamCheck.IsChecked == false)
                VSettingsRamText.Text = $"{(int)e.NewValue} MB";
        }

        private async void LoadGameIconAsync()
        {
            string cd = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache");
            Directory.CreateDirectory(cd);
            string p = Path.Combine(cd, "icon_64.png");
            if (!File.Exists(p))
            {
                try
                {
                    var bytes = await _http.GetByteArrayAsync(FormatUrlStatic("https://raw.githubusercontent.com/Anuken/Mindustry/master/core/assets/icons/icon_64.png", false));
                    await File.WriteAllBytesAsync(p, bytes);
                }
                catch { return; }
            }
            if (File.Exists(p))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(p);
                bmp.EndInit();
                bmp.Freeze();
                MainGameIcon.Source = bmp;
                SettingsGameIcon.Source = bmp;
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _config.GlobalJavaPath = GlobalJavaComboBox.Text;
            _config.GlobalRamMB = (int)GlobalRamSlider.Value;
            SaveConfig();
        }

        public static string FormatUrlStatic(string url, bool isApi = false)
        {
            int m = CurrentProxyIndex;
            if (m == 0) return url;
            if (m == 1) return "https://ghfast.top/" + url;
            if (m == 2) return "https://gh-proxy.com/" + url;
            if (m == 3)
            {
                if (isApi) return "https://ghfast.top/" + url;
                if (url.StartsWith("https://github.com")) return url.Replace("https://github.com", "https://kkgithub.com");
                if (url.StartsWith("https://raw.githubusercontent.com")) return url.Replace("https://raw.githubusercontent.com", "https://raw.kkgithub.com");
                return "https://kkgithub.com/" + url;
            }
            if (m == 4 && !isApi && url.StartsWith("https://raw.githubusercontent.com/"))
            {
                return url.Replace("https://raw.githubusercontent.com/", "https://cdn.jsdelivr.net/gh/").Replace("/master/", "@master/").Replace("/main/", "@main/");
            }
            return url;
        }

        private void ProxyNodeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProxyNodeBox.SelectedIndex != -1)
            {
                _config.ProxyNodeIndex = ProxyNodeBox.SelectedIndex;
                CurrentProxyIndex = ProxyNodeBox.SelectedIndex;
                SaveConfig();
            }
        }

        private void ToggleDownloadState(bool isD)
        {
            _isDownloading = isD;
            RemoteVersionListBox.IsEnabled = !isD;
            ModBrowserListBox.IsEnabled = !isD;
            SchematicBrowserListBox.IsEnabled = !isD;
            if (RbSchemMinRi2 != null) RbSchemMinRi2.IsEnabled = !isD;
            if (RbSchemDesignIt != null) RbSchemDesignIt.IsEnabled = !isD;
        }

        private void SwitchTab(int idx)
        {
            MainTabControl.SelectedIndex = idx;
            var d = new SolidColorBrush(Color.FromRgb(85, 85, 85));
            var a = new SolidColorBrush(Color.FromRgb(33, 150, 243));

            // 把所有导航按钮都恢复默认颜色 (加入了 NavMultiplayerBtn)
            NavLaunchBtn.Foreground = NavDownloadBtn.Foreground = NavModBrowserBtn.Foreground = NavSchematicsBtn.Foreground = NavMultiplayerBtn.Foreground = NavSettingsBtn.Foreground = NavMoreBtn.Foreground = d;

            // 根据索引点亮对应的按钮
            if (idx == 0) NavLaunchBtn.Foreground = a;
            else if (idx == 1) NavDownloadBtn.Foreground = a;
            else if (idx == 2) NavModBrowserBtn.Foreground = a;
            else if (idx == 3) NavSchematicsBtn.Foreground = a;
            else if (idx == 4) NavMultiplayerBtn.Foreground = a; // 新增的联机页面
            else if (idx == 5) NavSettingsBtn.Foreground = a;    // 设置顺延到 5
            else if (idx == 6) NavMoreBtn.Foreground = a;        // 更多顺延到 6
        }

        private void SmoothScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is DependencyObject d)
            {
                ScrollViewer? sv = FindVisualChild<ScrollViewer>(d);
                if (sv != null)
                {
                    e.Handled = true;
                    sv.BeginAnimation(SmoothScrollHelper.ScrollOffsetProperty, null);
                    SmoothScrollHelper.SetScrollOffset(sv, sv.VerticalOffset);
                    double t = Math.Max(0, Math.Min(sv.ScrollableHeight, sv.VerticalOffset - (e.Delta * 1.2)));
                    sv.BeginAnimation(SmoothScrollHelper.ScrollOffsetProperty, new DoubleAnimation(t, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }
            }
        }

        private T? FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                DependencyObject c = VisualTreeHelper.GetChild(obj, i);
                if (c is T t) return t;
                T? res = FindVisualChild<T>(c);
                if (res != null) return res;
            }
            return null;
        }

        private void AnimateFade(FrameworkElement ele, bool isShow)
        {
            if (isShow) ele.Visibility = Visibility.Visible;
            DoubleAnimation op = new DoubleAnimation { From = isShow ? 0 : 1, To = isShow ? 1 : 0, Duration = TimeSpan.FromSeconds(0.25) };
            DoubleAnimation tr = new DoubleAnimation { From = isShow ? 20 : 0, To = isShow ? 0 : 20, Duration = TimeSpan.FromSeconds(0.25), EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } };
            if (!isShow) op.Completed += (s, e) => ele.Visibility = Visibility.Collapsed;
            ele.BeginAnimation(OpacityProperty, op);
            ele.RenderTransform.BeginAnimation(TranslateTransform.YProperty, tr);
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is TabControl tc)
            {
                var h = (FrameworkElement)tc.Template.FindName("PART_SelectedContentHost", tc);
                if (h != null)
                {
                    Storyboard? sb = h.Resources["FadeIn"] as Storyboard;
                    sb?.Begin(h);
                }
            }
        }

        private void NavLaunch_Click(object sender, RoutedEventArgs e) => SwitchTab(0);
        private void NavDownload_Click(object sender, RoutedEventArgs e) => SwitchTab(1);
        private async void NavModBrowser_Click(object sender, RoutedEventArgs e) { SwitchTab(2); if (_allOnlineMods.Count == 0) await FetchModRegistryAsync(); }
        private void NavSchematics_Click(object sender, RoutedEventArgs e) { SwitchTab(3); if (_allOnlineSchematics.Count == 0 && !_isDownloading) _ = FetchSchematicsAsync(false); }

        // 新增：联机按钮点击 (传入 false 走缓存)
        private void NavMultiplayer_Click(object sender, RoutedEventArgs e) { SwitchTab(4); _ = CheckAndDownloadEasyTierAsync(false); }

        // 注意：这两个的索引更新了！
        private void NavSettings_Click(object sender, RoutedEventArgs e) => SwitchTab(5);
        private void NavMore_Click(object sender, RoutedEventArgs e) => SwitchTab(6);

        // ==========================================
        // 联机组件 (EasyTier) 自动下载、缓存与事件
        // ==========================================

        // 追踪后台 EasyTier 进程的变量
        private Process? _easyTierProcess = null;

        // 核心增强：智能寻找解压后的 exe (无视里面嵌套了多少层文件夹)
        private string GetEasyTierExePath()
        {
            string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "EasyTier");
            if (System.IO.Directory.Exists(dir))
            {
                // 开启 AllDirectories，穿透所有子文件夹寻找客户端
                var files = System.IO.Directory.GetFiles(dir, "easytier-core.exe", System.IO.SearchOption.AllDirectories);
                if (files.Length > 0) return files[0];
            }
            return "";
        }

        // 手动点击下载按钮 (传入 true 强制无视缓存重新下载)
        private void BtnDownloadEasyTier_Click(object sender, RoutedEventArgs e)
        {
            _ = CheckAndDownloadEasyTierAsync(true);
        }

        private async Task CheckAndDownloadEasyTierAsync(bool forceDownload)
        {
            string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "EasyTier");

            // 缓存拦截：如果不强制下载，且通过智能扫描找到了 exe，直接秒进就绪！绝对不重下！
            if (!forceDownload && !string.IsNullOrEmpty(GetEasyTierExePath()))
            {
                EasyTierStatusText.Text = "EasyTier 组件已缓存就绪，可以开始联机";
                return;
            }

            EasyTierStatusText.Text = "正在连接 GitHub 获取最新 EasyTier 版本...";
            EasyTierProgressBar.Visibility = Visibility.Visible;
            EasyTierProgressBar.Value = 0;

            try
            {
                var rel = await _http.GetFromJsonAsync<GitHubRelease>("https://api.github.com/repos/EasyTier/EasyTier/releases/latest");
                var asset = rel?.Assets?.FirstOrDefault(a => a.Name.Contains("windows-x86_64") && a.Name.EndsWith(".zip"));
                if (asset == null) { EasyTierStatusText.Text = "未找到适用的 Windows 版本。"; return; }

                System.IO.Directory.CreateDirectory(dir);
                string zipPath = System.IO.Path.Combine(dir, asset.Name);

                EasyTierStatusText.Text = "正在使用加速节点下载底层组件...";
                await DownloadFileAsync(FormatUrlStatic(asset.BrowserDownloadUrl), zipPath, new Progress<double>(p => EasyTierProgressBar.Value = p));

                EasyTierStatusText.Text = "正在解压安装...";
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, dir, true);
                System.IO.File.Delete(zipPath); // 下完清理垃圾

                EasyTierStatusText.Text = "EasyTier 安装并缓存成功！准备就绪。";
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                EasyTierStatusText.Text = "下载失败: 节点拒绝访问(403)，请去设置页切换下载节点！";
            }
            catch (Exception ex) { EasyTierStatusText.Text = "下载失败: " + ex.Message; }
            finally { EasyTierProgressBar.Visibility = Visibility.Collapsed; }
        }

        private void GenerateRoom_Click(object sender, RoutedEventArgs e)
        {
            Random rnd = new Random();
            EasyTierRoomBox.Text = rnd.Next(100000, 999999).ToString();
        }

        // ==========================================
        // 核心：基于房号系统的 EasyTier 连接逻辑 (终极完美版)
        // 包含：动态IP分配、双向防火墙静默穿透、进程树强制断开
        // ==========================================
        private void ToggleEasyTier_Click(object sender, RoutedEventArgs e)
        {
            // 1. 强制断开逻辑：如果已经在运行了，执行“斩草除根”操作
            if (_easyTierProcess != null && !_easyTierProcess.HasExited)
            {
                try
                {
                    // 核心修复：由于黑框是管理员权限，且带有子进程，必须用系统的 taskkill 连根拔起！
                    ProcessStartInfo killInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/PID {_easyTierProcess.Id} /T /F", // /T 杀掉整个进程树，/F 强制执行
                        UseShellExecute = true,
                        Verb = "runas", // 必须提权才能杀掉管理员级别的黑框
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    // 启动杀手程序，并等待它把黑框杀完
                    Process.Start(killInfo)?.WaitForExit();
                }
                catch (Win32Exception)
                {
                    // 如果用户在断开时的 UAC 弹窗点了“否”，就不做任何处理
                    MessageBox.Show("取消断开：需要管理员权限才能强制关闭后台网络进程。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                catch { }

                _easyTierProcess = null;
                ToggleEasyTierBtn.Content = "🚀 提权并连接虚拟局域网";
                ToggleEasyTierBtn.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                return;
            }

            // 2. 获取客户端路径
            string exe = GetEasyTierExePath();
            if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe))
            {
                MessageBox.Show("未找到核心组件！请先点击上方按钮下载联机组件！", "缺少组件", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string server = EasyTierServerBox.Text.Trim();
            string roomCode = EasyTierRoomBox.Text.Trim();

            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(roomCode) || roomCode.Length < 4)
            {
                MessageBox.Show("服务器地址和房间号不能为空！\n(请点击“生成房间”或输入朋友给的至少4位数字房号)", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 3. 网络隔离机制：房号转换为密码
            string networkName = $"mdl_room_{roomCode}";
            string networkSecret = $"mdl_pwd_{roomCode}";

            // 4. 强制 IPv4 防冲突分配 (避开现有VPN和虚拟机的干扰)
            string sub1 = roomCode.Substring(0, 2);
            string sub2 = roomCode.Substring(2, 2);
            int lastIpNode = new Random().Next(1, 254);
            string ipv4Subnet = $"10.{sub1}.{sub2}.{lastIpNode}/24";
            string args = $"-e \"{server}\" --network-name \"{networkName}\" --network-secret \"{networkSecret}\" --ipv4 {ipv4Subnet}";

            // ================= 5. 终极静默双向防火墙穿透 =================
            // 入站规则 (dir=in)：允许别人连进你的游戏房间
            string fwIn = "netsh advfirewall firewall add rule name=\"MDL_Mindustry_UDP_In\" dir=in action=allow protocol=UDP localport=6567 >nul 2>&1";
            // 出站规则 (dir=out)：允许你的游戏数据发往别人的房间
            string fwOut = "netsh advfirewall firewall add rule name=\"MDL_Mindustry_UDP_Out\" dir=out action=allow protocol=UDP localport=6567 >nul 2>&1";

            // 6. 将防火墙 入站 + 出站 + 启动组件 三个命令用 & 符号暴力串联！
            string cmdArgs = $"/k \"{fwIn} & {fwOut} & \"{exe}\" {args}\"";

            try
            {
                var pInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmdArgs,
                    UseShellExecute = true,  // 必须为true才能触发UAC提权
                    Verb = "runas",          // 申请管理员权限 (防火墙和虚拟网卡都需要它)
                    WindowStyle = ProcessWindowStyle.Normal // 保持黑框可见，方便查错和关闭
                };

                _easyTierProcess = Process.Start(pInfo);

                if (_easyTierProcess != null)
                {
                    // 按钮状态变为断开
                    ToggleEasyTierBtn.Content = $"⏹ 正在连接房间 {roomCode} (黑框关闭即断开)";
                    ToggleEasyTierBtn.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54));

                    // 监听黑框关闭事件，自动恢复按钮状态
                    _easyTierProcess.EnableRaisingEvents = true;
                    _easyTierProcess.Exited += (s, ev) => Dispatcher.Invoke(() => {
                        _easyTierProcess = null;
                        ToggleEasyTierBtn.Content = "🚀 提权并连接虚拟局域网";
                        ToggleEasyTierBtn.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    });
                }
            }
            catch (Win32Exception)
            {
                MessageBox.Show("您取消了管理员授权！必须同意权限才能设置防火墙和建立虚拟网卡。", "提权失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动联机服务失败: {ex.Message}");
            }
        }
        private void CloseOverlays_Click(object sender, RoutedEventArgs e)
        {
            if (VersionSettingsOverlay.Visibility == Visibility.Visible && _currentInstance != null) { SaveVersionConfig(_currentInstance.FullPath); AnimateFade(VersionSettingsOverlay, false); }
            if (VersionSelectOverlay.Visibility == Visibility.Visible) AnimateFade(VersionSelectOverlay, false);
            if (ReleaseNotesOverlay.Visibility == Visibility.Visible) AnimateFade(ReleaseNotesOverlay, false);
            if (SchematicInstallOverlay.Visibility == Visibility.Visible) AnimateFade(SchematicInstallOverlay, false);
            UpdateMainUI();
        }

        private void CloseReleaseNotes_Click(object sender, RoutedEventArgs e) => AnimateFade(ReleaseNotesOverlay, false);
        private void UpdateMainUI()
        {
            if (_currentInstance == null)
            {
                CurrentLaunchVersionText.Text = "未选择版本，请点击下方选择";
                LaunchBtn.IsEnabled = false;
            }
            else
            {
                // 核心修复：如果当前选中的实例正在运行，按钮变灰；否则允许启动
                if (_runningInstancePaths.Contains(_currentInstance.FullPath))
                {
                    CurrentLaunchVersionText.Text = "该实例正在运行中...";
                    LaunchBtn.IsEnabled = false;
                }
                else
                {
                    CurrentLaunchVersionText.Text = _currentInstance.Name;
                    LaunchBtn.IsEnabled = true;
                }
            }
        }
        private List<GameInstanceInfo> GetAllInstalledInstances()
        {
            var all = new List<GameInstanceInfo>();
            foreach (var root in _config.ManagedFolders)
            {
                if (!Directory.Exists(root)) continue;
                string vDir = Path.Combine(root, "Versions");
                if (Directory.Exists(vDir))
                {
                    foreach (var d in Directory.GetDirectories(vDir))
                    {
                        if (File.Exists(Path.Combine(d, "Mindustry.jar")))
                        {
                            all.Add(new GameInstanceInfo { Name = Path.GetFileName(d), FullPath = d });
                        }
                    }
                }
            }
            return all;
        }
        private void OpenVersionSelect_Click(object sender, RoutedEventArgs e)
        {
            FolderListBox.ItemsSource = null;
            FolderListBox.ItemsSource = _config.ManagedFolders;
            if (_config.ManagedFolders.Count > 0) FolderListBox.SelectedIndex = 0;
            AnimateFade(VersionSelectOverlay, true);
        }

        private void AddNewFolder_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFolderDialog();
            if (d.ShowDialog() == true)
            {
                if (!_config.ManagedFolders.Contains(d.FolderName))
                {
                    _config.ManagedFolders.Add(d.FolderName);
                    SaveConfig();
                    OpenVersionSelect_Click(null!, null!);
                }
            }
        }

        private void RemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string fp)
            {
                if (MessageBox.Show($"仅从启动器列表中移除此文件夹（不影响本地文件）。\n确定移除吗？\n{fp}", "移除列表项", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    _config.ManagedFolders.Remove(fp);
                    SaveConfig();
                    OpenVersionSelect_Click(null!, null!);
                }
            }
        }

        private void FolderListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FolderListBox.SelectedItem is string p && Directory.Exists(p))
            {
                var list = new List<GameInstanceInfo>();
                string vDir = Path.Combine(p, "Versions");
                if (Directory.Exists(vDir))
                {
                    foreach (var d in Directory.GetDirectories(vDir))
                    {
                        if (File.Exists(Path.Combine(d, "Mindustry.jar")))
                        {
                            list.Add(new GameInstanceInfo { Name = Path.GetFileName(d), FullPath = d });
                        }
                    }
                }
                InstanceListBox.ItemsSource = list;
            }
            else
            {
                InstanceListBox.ItemsSource = null;
            }
        }

        private void InstanceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (InstanceListBox.SelectedItem is GameInstanceInfo info)
            {
                _currentInstance = info;
                _config.LastSelectedInstancePath = info.FullPath;
                SaveConfig();
                CloseOverlays_Click(null!, null!);
            }
        }

        private void DeleteInstance_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is GameInstanceInfo info)
            {
                if (MessageBox.Show($"警告：确定要彻底删除该版本吗？此操作不可逆！\n{info.FullPath}", "删除版本", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    try
                    {
                        Directory.Delete(info.FullPath, true);
                        if (_currentInstance != null && _currentInstance.FullPath == info.FullPath)
                        {
                            _currentInstance = null;
                            _config.LastSelectedInstancePath = "";
                            SaveConfig();
                            UpdateMainUI();
                        }
                        FolderListBox_SelectionChanged(null!, null!);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"删除失败: {ex.Message}");
                    }
                }
            }
        }

        private void OpenVersionSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInstance == null) { MessageBox.Show("请先选择一个版本！"); return; }
            LoadVersionConfig(_currentInstance.FullPath);
            VSettingsTitle.Text = $"版本设置 - {_currentInstance.Name}";
            VSettingsIsolationBox.SelectedIndex = _currentVersionConfig.UseIsolation ? 0 : 1;
            VSettingsJavaComboBox.Text = _currentVersionConfig.CustomJavaPath;
            VSettingsJvmArgsBox.Text = _currentVersionConfig.CustomJvmArgs;
            VersionAutoRamCheck.IsChecked = _currentVersionConfig.UseAutoRam;
            VSettingsRamSlider.Value = Math.Min(_currentVersionConfig.CustomRamMB, GlobalRamSlider.Maximum);
            CancelRename_Click(null!, null!);
            VSidebarConfig_Click(null!, null!);
            AnimateFade(VersionSettingsOverlay, true);
        }

        private void ResetSidebarStyles()
        {
            var d = new SolidColorBrush(Color.FromRgb(51, 51, 51));
            VSidebarOverviewBtn.Foreground = VSidebarConfigBtn.Foreground = VSidebarModBtn.Foreground = VSidebarSchematicBtn.Foreground = VSidebarSaveDataBtn.Foreground = d;
            VSidebarOverviewBtn.FontWeight = VSidebarConfigBtn.FontWeight = VSidebarModBtn.FontWeight = VSidebarSchematicBtn.FontWeight = VSidebarSaveDataBtn.FontWeight = FontWeights.Normal;
            VSettingsOverviewPanel.Visibility = VSettingsConfigPanel.Visibility = VSettingsModPanel.Visibility = VSettingsSchematicPanel.Visibility = VSettingsSaveDataPanel.Visibility = Visibility.Collapsed;
        }

        private void VSidebarOverview_Click(object sender, RoutedEventArgs e)
        {
            ResetSidebarStyles();
            VSettingsOverviewPanel.Visibility = Visibility.Visible;
            VSidebarOverviewBtn.Foreground = Brushes.DodgerBlue;
            VSidebarOverviewBtn.FontWeight = FontWeights.Bold;
            if (_currentInstance != null)
            {
                OverviewVersionName.Text = _currentInstance.Name;
                OverviewVersionPath.Text = _currentInstance.FullPath;
            }
        }

        private void VSidebarConfig_Click(object sender, RoutedEventArgs e)
        {
            ResetSidebarStyles();
            VSettingsConfigPanel.Visibility = Visibility.Visible;
            VSidebarConfigBtn.Foreground = Brushes.DodgerBlue;
            VSidebarConfigBtn.FontWeight = FontWeights.Bold;
        }

        private void VSidebarMod_Click(object sender, RoutedEventArgs e)
        {
            ResetSidebarStyles();
            VSettingsModPanel.Visibility = Visibility.Visible;
            VSidebarModBtn.Foreground = Brushes.DodgerBlue;
            VSidebarModBtn.FontWeight = FontWeights.Bold;
            ScanMods();
        }

        private void VSidebarSchematic_Click(object sender, RoutedEventArgs e)
        {
            ResetSidebarStyles();
            VSettingsSchematicPanel.Visibility = Visibility.Visible;
            VSidebarSchematicBtn.Foreground = Brushes.DodgerBlue;
            VSidebarSchematicBtn.FontWeight = FontWeights.Bold;
            ScanSchematics();
        }

        private void VSidebarSaveData_Click(object sender, RoutedEventArgs e)
        {
            ResetSidebarStyles();
            VSettingsSaveDataPanel.Visibility = Visibility.Visible;
            VSidebarSaveDataBtn.Foreground = Brushes.DodgerBlue;
            VSidebarSaveDataBtn.FontWeight = FontWeights.Bold;
            ScanSaveDataStatus();
        }

        private void VSidebarOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInstance != null) Process.Start("explorer.exe", _currentInstance.FullPath);
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInstance == null) return;
            LoadVersionConfig(_currentInstance.FullPath);
            string data = _currentVersionConfig.UseIsolation ? Path.Combine(_currentInstance.FullPath, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindustry");
            Directory.CreateDirectory(data);
            Process.Start("explorer.exe", data);
        }

        private void BrowseVersionJava_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "Java|java.exe;javaw.exe" };
            if (d.ShowDialog() == true) VSettingsJavaComboBox.Text = d.FileName;
        }

        private void StartRename_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInstance == null) return;
            RenameTextBox.Text = _currentInstance.Name;
            StartRenameBtn.Visibility = Visibility.Collapsed;
            RenamePanel.Visibility = Visibility.Visible;
        }

        private void CancelRename_Click(object sender, RoutedEventArgs e)
        {
            StartRenameBtn.Visibility = Visibility.Visible;
            RenamePanel.Visibility = Visibility.Collapsed;
        }

        private void ConfirmRename_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInstance == null) return;
            string nn = RenameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(nn) || nn.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { MessageBox.Show("名称无效！"); return; }
            if (nn == _currentInstance.Name) { CancelRename_Click(null!, null!); return; }
            try
            {
                string op = _currentInstance.FullPath;
                string np = Path.Combine(Directory.GetParent(op)!.FullName, nn);
                if (Directory.Exists(np)) { MessageBox.Show("已存在同名版本！"); return; }
                Directory.Move(op, np);
                _currentInstance.Name = nn;
                _currentInstance.FullPath = np;
                if (_config.LastSelectedInstancePath == op)
                {
                    _config.LastSelectedInstancePath = np;
                    SaveConfig();
                }
                OverviewVersionName.Text = nn;
                OverviewVersionPath.Text = np;
                VSettingsTitle.Text = $"版本设置 - {nn}";
                UpdateMainUI();
                FolderListBox_SelectionChanged(null!, null!);
                CancelRename_Click(null!, null!);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重命名失败: {ex.Message}");
            }
        }
        private void ScanMods()
        {
            if (_currentInstance == null) return;
            string data = _currentVersionConfig.UseIsolation ? Path.Combine(_currentInstance.FullPath, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindustry");
            string mDir = Path.Combine(data, "mods");
            if (!Directory.Exists(mDir)) { ModListBox.ItemsSource = null; NoModText.Visibility = Visibility.Visible; return; }

            var files = new DirectoryInfo(mDir).GetFiles()
                .Where(f => f.Extension.Equals(".jar", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var list = new List<ModInfo>();
            foreach (var f in files)
            {
                var info = new ModInfo { FileName = f.Name, FullPath = f.FullName, FileSize = $"{(f.Length / 1024.0):F2} KB" };
                ParseModArchive(info);
                list.Add(info);
            }
            ModListBox.ItemsSource = list;
            NoModText.Visibility = list.Any() ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ParseModArchive(ModInfo info)
        {
            try
            {
                using var stream = File.OpenRead(info.FullPath);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                var iconEntry = zip.Entries.FirstOrDefault(e => e.Name.Equals("icon.png", StringComparison.OrdinalIgnoreCase));
                if (iconEntry != null)
                {
                    using var iconStream = iconEntry.Open();
                    using var ms = new MemoryStream();
                    iconStream.CopyTo(ms);
                    ms.Position = 0;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    info.IconImage = bitmap;
                }
                var metaEntry = zip.Entries.FirstOrDefault(e => e.Name.Equals("mod.json", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("mod.hjson", StringComparison.OrdinalIgnoreCase));
                if (metaEntry != null)
                {
                    using var metaStream = metaEntry.Open();
                    using var reader = new StreamReader(metaStream);
                    string content = reader.ReadToEnd();
                    try
                    {
                        var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
                        using var doc = JsonDocument.Parse(content, options);
                        var root = doc.RootElement;
                        string GetJsonString(string key) => root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? "" : "";
                        string name = GetJsonString("displayName");
                        if (string.IsNullOrEmpty(name)) name = GetJsonString("name");
                        info.DisplayName = StripColors(name);
                        info.Author = StripColors(GetJsonString("author"));
                        info.Description = StripColors(GetJsonString("description"));
                        info.Version = StripColors(GetJsonString("version"));
                    }
                    catch
                    {
                        info.DisplayName = StripColors(ExtractHjsonValue(content, "displayName") ?? ExtractHjsonValue(content, "name") ?? "");
                        info.Author = StripColors(ExtractHjsonValue(content, "author") ?? "");
                        string desc = ExtractHjsonValue(content, "description") ?? "";
                        info.Description = StripColors(desc).Replace("\\n", "\n");
                        info.Version = StripColors(ExtractHjsonValue(content, "version") ?? "");
                    }
                }
            }
            catch { }
        }

        private string? ExtractHjsonValue(string content, string key)
        {
            var match = Regex.Match(content, $@"""?{key}""?\s*:\s*([^""\r\n]+|""([^""]*)"")", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string val = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[1].Value.Trim();
                return val.TrimEnd(',').Trim();
            }
            return null;
        }

        private string StripColors(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return Regex.Replace(input, @"\[.*?\]", "");
        }

        private void RefreshMods_Click(object sender, RoutedEventArgs e) => ScanMods();

        private void DeleteMod_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string p)
            {
                if (MessageBox.Show("确定删除吗？", "MDL", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    try { File.Delete(p); Dispatcher.InvokeAsync(() => ScanMods()); }
                    catch (Exception ex) { MessageBox.Show("删除失败: " + ex.Message); }
                }
            }
        }

        private void ScanSchematics()
        {
            if (_currentInstance == null) return;
            string data = _currentVersionConfig.UseIsolation ? Path.Combine(_currentInstance.FullPath, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindustry");
            string sDir = Path.Combine(data, "schematics");
            if (!Directory.Exists(sDir)) { LocalSchematicListBox.ItemsSource = null; NoSchematicText.Visibility = Visibility.Visible; return; }
            var files = new DirectoryInfo(sDir).GetFiles("*.msch", SearchOption.TopDirectoryOnly)
                .Select(f => new ModInfo { FileName = f.Name, FullPath = f.FullName, FileSize = $"{(f.Length / 1024.0):F2} KB" })
                .ToList();
            LocalSchematicListBox.ItemsSource = files;
            NoSchematicText.Visibility = files.Any() ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RefreshSchematics_Click(object sender, RoutedEventArgs e) => ScanSchematics();

        private void DeleteSchematic_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string p)
            {
                if (MessageBox.Show("确定删除该蓝图吗？", "MDL", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    try { File.Delete(p); Dispatcher.InvokeAsync(() => ScanSchematics()); }
                    catch (Exception ex) { MessageBox.Show("删除失败: " + ex.Message); }
                }
            }
        }

        // ===============================================
        // 核心增强：带 DataGrid 虚拟化的动态解析与编辑
        // ===============================================
        private string GetSettingsBinPath()
        {
            if (_currentInstance == null) return "";
            bool isIso = VSettingsIsolationBox.SelectedIndex == 0;
            string d = isIso ? Path.Combine(_currentInstance.FullPath, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindustry");
            return Path.Combine(d, "settings.bin");
        }

        private void ScanSaveDataStatus()
        {
            string binPath = GetSettingsBinPath();
            if (string.IsNullOrEmpty(binPath)) return;
            if (!File.Exists(binPath))
            {
                SaveDataStatusText.Text = "文件未找到 (从未启动过游戏，或被移动)";
                SaveDataStatusText.Foreground = Brushes.Gray;
                return;
            }
            var editor = new MindustrySettingsEditor();
            bool isHealthy = editor.LoadList(binPath, out var lst);
            if (isHealthy)
            {
                SaveDataStatusText.Text = $"解析完美！包含 {lst.Count} 个游戏配置项\n(注：地图存档位于 saves 文件夹)";
                SaveDataStatusText.Foreground = Brushes.Green;
            }
            else
            {
                SaveDataStatusText.Text = $"部分损坏！({editor.ErrorMessage})";
                SaveDataStatusText.Foreground = Brushes.Crimson;
            }
        }

        private void ParseSaveData_Click(object sender, RoutedEventArgs e)
        {
            string binPath = GetSettingsBinPath();
            if (!File.Exists(binPath))
            {
                MessageBox.Show("找不到 settings.bin！", "提示");
                return;
            }
            var editor = new MindustrySettingsEditor();
            bool isHealthy = editor.LoadList(binPath, out var lst);
            if (lst.Count == 0 && !isHealthy)
            {
                MessageBox.Show($"解析严重失败:\n{editor.ErrorMessage}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            _settingsView = CollectionViewSource.GetDefaultView(lst);
            UpdateSettingsFilter();
            SettingsDataGrid.ItemsSource = _settingsView;
            if (!isHealthy)
            {
                MessageBox.Show($"解析中途遇到异常，尾部数据可能已丢失。\n原因: {editor.ErrorMessage}", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e) { UpdateSettingsFilter(); }
        private void SettingsCategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateSettingsFilter(); }

        private void UpdateSettingsFilter()
        {
            if (_settingsView == null) return;
            string q = SettingsSearchBox?.Text.ToLower() ?? "";
            int category = SettingsCategoryBox?.SelectedIndex ?? 0;
            _settingsView.Filter = o =>
            {
                if (o is SettingItem si)
                {
                    bool matchSearch = string.IsNullOrEmpty(q) || si.Key.ToLower().Contains(q) || (!si.IsBinary && si.DisplayValue.ToLower().Contains(q));
                    if (!matchSearch) return false;
                    bool isTechTree = si.Key.Contains("req-") || si.Key.Contains("-unlocked") || si.Key.Contains("sector-") || si.Key.StartsWith("save-");
                    if (category == 1) return !isTechTree;
                    if (category == 2) return isTechTree;
                    return true;
                }
                return false;
            };
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsView == null) return;
            var list = _settingsView.SourceCollection.Cast<SettingItem>().ToList();
            var editor = new MindustrySettingsEditor();
            try
            {
                string p = GetSettingsBinPath();
                string bak = p + ".bak";
                File.Copy(p, bak, true);
                editor.SaveList(p, list);
                MessageBox.Show("保存成功！已自动备份原文件为 .bak", "MDL");
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RescueSaveData_Click(object sender, RoutedEventArgs e)
        {
            string binPath = GetSettingsBinPath();
            if (!File.Exists(binPath)) return;
            if (MessageBox.Show("此操作会将 settings.bin 隔离，让游戏下次启动时重建全新索引！\n\n是否继续？", "重建索引", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try
                {
                    File.Move(binPath, binPath + $".bak_{DateTime.Now:MMddHHmm}");
                    string bak = Path.Combine(Path.GetDirectoryName(binPath)!, "settings.backup");
                    if (File.Exists(bak)) File.Delete(bak);
                    MessageBox.Show("重建指令已下达！请启动游戏恢复存档。");
                    ScanSaveDataStatus();
                    SettingsDataGrid.ItemsSource = null;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"操作失败: {ex.Message}");
                }
            }
        }

        private void LaunchGame_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInstance == null) { MessageBox.Show("请先选择版本！"); return; }

            string instancePath = _currentInstance.FullPath; // 捕获当前要启动的路径

            if (_runningInstancePaths.Contains(instancePath))
            {
                MessageBox.Show("该实例已经正在运行中！\n请不要重复启动同一个版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string jar = Path.Combine(instancePath, "Mindustry.jar");
            if (!File.Exists(jar)) { MessageBox.Show("核心缺失！"); return; }

            LoadVersionConfig(instancePath);
            string data = _currentVersionConfig.UseIsolation ? Path.Combine(instancePath, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindustry");
            if (_currentVersionConfig.UseIsolation) Directory.CreateDirectory(data);

            string exe = string.IsNullOrWhiteSpace(_currentVersionConfig.CustomJavaPath) ? _config.GlobalJavaPath : _currentVersionConfig.CustomJavaPath;
            if (string.IsNullOrWhiteSpace(exe)) exe = "java";

            int finalRam = _currentVersionConfig.UseAutoRam ? CalculateSmartRam() : _currentVersionConfig.CustomRamMB;
            string memArg = $"-Xmx{finalRam}m ";
            string jvmArgs = string.IsNullOrWhiteSpace(_currentVersionConfig.CustomJvmArgs) ? "" : _currentVersionConfig.CustomJvmArgs + " ";

            try
            {
                var pInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"{memArg}{jvmArgs}-jar \"{jar}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = instancePath
                };
                if (_currentVersionConfig.UseIsolation) { pInfo.EnvironmentVariables["MINDUSTRY_DATA_DIR"] = data; }

                Process? p = Process.Start(pInfo);
                if (p == null) return;

                // 记录该路径正在运行，并刷新 UI
                _runningInstancePaths.Add(instancePath);
                UpdateMainUI();

                string errorLog = "";
                _ = Task.Run(async () => { while (!p.HasExited) { string? line = await p.StandardError.ReadLineAsync(); if (line != null) errorLog += line + "\n"; } });

                p.EnableRaisingEvents = true;
                p.Exited += (s, ev) => Dispatcher.Invoke(() => {
                    // 进程结束，把该路径从运行列表中移除，并刷新 UI
                    _runningInstancePaths.Remove(instancePath);
                    UpdateMainUI();

                    if (p.ExitCode != 0) AnalyzeCrash(errorLog);
                });
            }
            catch (Exception ex) { MessageBox.Show($"启动失败: {ex.Message}"); }
        }

        private void AnalyzeCrash(string log)
        {
            string advice = "【未知错误】建议检查最近安装的 Mod 是否冲突。";
            if (string.IsNullOrWhiteSpace(log)) { log = "未捕获到具体错误信息，可能是系统强制终止了游戏。"; }
            else if (log.Contains("OutOfMemoryError")) advice = "【内存爆炸】检测到分配内存不足。当前模组较多，请在设置中关闭“自动分配”并将内存滑块向右拉大。";
            else if (log.Contains("UnsupportedClassVersionError")) advice = "【Java版本过旧】请安装并切换到 Java 17 或更高版本。";
            else if (log.Contains("MixinTransformationException") || log.Contains("MixinApplyError")) advice = "【模组冲突】检测到多个 Mod 尝试修改同一核心代码。请尝试逐个禁用最近安装的 Mod。";
            else if (log.Contains("NoSuchMethodError") || log.Contains("ClassNotFoundException")) advice = "【版本不兼容】某些模组不支持当前的游戏核心版本。";
            ReleaseNotesTitle.Text = "🚀 游戏不幸坠毁！"; ReleaseNotesTitle.Foreground = Brushes.Crimson;
            ReleaseNotesText.Text = $"{advice}\n\n--- 原始报错诊断 (底部) ---\n{(log.Length > 800 ? log.Substring(log.Length - 800) : log)}";
            OpenRepoBtn.Visibility = Visibility.Collapsed; ExportCrashBtn.Visibility = Visibility.Visible;
            AnimateFade(ReleaseNotesOverlay, true);
        }

        private void ExportCrash_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Title = "导出错误报告", Filter = "文本文件 (*.txt)|*.txt", FileName = $"MDL_CrashReport_{DateTime.Now:yyyyMMdd_HHmmss}.txt" };
            if (dialog.ShowDialog() == true) { try { File.WriteAllText(dialog.FileName, ReleaseNotesText.Text); MessageBox.Show("错误报告导出成功！", "MDL", MessageBoxButton.OK, MessageBoxImage.Information); } catch (Exception ex) { MessageBox.Show($"导出失败: {ex.Message}"); } }
        }
        private async void RefreshModBrowser_Click(object sender, RoutedEventArgs e) => await FetchModRegistryAsync();
        private async Task FetchModRegistryAsync()
        {
            ModBrowserLoadingText.Visibility = Visibility.Visible; ModBrowserListBox.Visibility = Visibility.Collapsed;
            try
            {
                string url = FormatUrlStatic("https://raw.githubusercontent.com/Anuken/MindustryMods/master/mods.json", false);
                var list = await _http.GetFromJsonAsync<List<ModRegistryEntry>>(url);
                if (list != null) { _allOnlineMods = list.OrderByDescending(m => m.Stars).ToList(); ModBrowserListBox.ItemsSource = _allOnlineMods; ModBrowserListBox.Visibility = Visibility.Visible; ModBrowserLoadingText.Visibility = Visibility.Collapsed; }
            }
            catch (Exception ex) { ModBrowserLoadingText.Text = $"拉取列表失败: {ex.InnerException?.Message ?? ex.Message}"; }
        }
        private void ModSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string k = ModSearchBox.Text.ToLower();
            ModBrowserListBox.ItemsSource = string.IsNullOrWhiteSpace(k) ? _allOnlineMods : _allOnlineMods.Where(m => m.Name.ToLower().Contains(k) || m.Author.ToLower().Contains(k) || (m.Description != null && m.Description.ToLower().Contains(k))).ToList();
        }
        private void ModItem_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement f && f.DataContext is ModRegistryEntry mod)
            {
                ReleaseNotesTitle.Text = $"{mod.Name} - 模组详情"; ReleaseNotesTitle.Foreground = Brushes.Black; ReleaseNotesText.Text = $"作者: {mod.Author}\n仓库: {mod.Repo}\n描述: \n{mod.Description}"; _currentDetailUrl = $"https://github.com/{mod.Repo}"; OpenRepoBtn.Visibility = Visibility.Visible; ExportCrashBtn.Visibility = Visibility.Collapsed; AnimateFade(ReleaseNotesOverlay, true);
            }
        }
        private async void InstallModFromBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading) { MessageBox.Show("当前有任务正在下载中，请稍后操作！", "MDL", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (sender is Button b && b.Tag is ModRegistryEntry mod)
            {
                var all = GetAllInstalledInstances(); if (all.Count == 0) { MessageBox.Show("请先导入游戏！"); return; }
                _selectedModToInstall = mod; ModInstallTitle.Text = $"安装模组 - {mod.Name}"; AllInstancesListBox.ItemsSource = all;
                if (_currentInstance != null) { var m = all.FirstOrDefault(i => i.FullPath == _currentInstance.FullPath); if (m != null) AllInstancesListBox.SelectedItem = m; }
                ModVersionComboBox.ItemsSource = null; ModInstallProgressPanel.Visibility = Visibility.Visible; ModInstallStatusText.Text = "获取版本中..."; AllInstancesListBox.IsEnabled = ModVersionComboBox.IsEnabled = ConfirmModInstallBtn.IsEnabled = false;
                AnimateFade(ModInstallOverlay, true);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    string apiUrl = FormatUrlStatic($"https://api.github.com/repos/{mod.Repo}/releases", true);
                    var rels = await _http.GetFromJsonAsync<List<GitHubRelease>>(apiUrl, cts.Token);
                    if (rels != null) { if (rels.Count == 0) { MessageBox.Show("该模组没有任何发布版本，无法下载。", "MDL", MessageBoxButton.OK, MessageBoxImage.Information); AnimateFade(ModInstallOverlay, false); return; } ModVersionComboBox.ItemsSource = rels; ModVersionComboBox.SelectedIndex = 0; }
                }
                catch (Exception ex) { MessageBox.Show($"获取版本网络错误: {ex.InnerException?.Message ?? ex.Message}"); AnimateFade(ModInstallOverlay, false); return; }
                finally { ModInstallProgressPanel.Visibility = Visibility.Collapsed; AllInstancesListBox.IsEnabled = ModVersionComboBox.IsEnabled = true; ValidateModInstallForm(); }
            }
        }
        private void ModInstallForm_SelectionChanged(object sender, SelectionChangedEventArgs e) => ValidateModInstallForm();
        private void ValidateModInstallForm() => ConfirmModInstallBtn.IsEnabled = AllInstancesListBox.SelectedItem != null && ModVersionComboBox.SelectedItem != null;
        private void CancelModInstall_Click(object sender, RoutedEventArgs e) => AnimateFade(ModInstallOverlay, false);
        private async void ConfirmModInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModToInstall == null || AllInstancesListBox.SelectedItem is not GameInstanceInfo target || ModVersionComboBox.SelectedItem is not GitHubRelease rel) return;
            ConfirmModInstallBtn.IsEnabled = false; ModInstallProgressPanel.Visibility = Visibility.Visible; ToggleDownloadState(true);
            try
            {
                var asset = rel.Assets?.FirstOrDefault(a => a.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                string url, file;
                if (asset != null) { url = FormatUrlStatic(asset.BrowserDownloadUrl); file = asset.Name; }
                else
                {
                    if (MessageBox.Show("该版本无编译好的附件，是否下载源码 ZIP？", "MDL", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { url = FormatUrlStatic($"https://github.com/{_selectedModToInstall.Repo}/archive/refs/tags/{rel.TagName}.zip"); file = $"{string.Join("_", _selectedModToInstall.Name.Split(Path.GetInvalidFileNameChars()))}_{rel.TagName}_source.zip"; }
                    else { ModInstallProgressPanel.Visibility = Visibility.Collapsed; return; }
                }
                LoadVersionConfig(target.FullPath);
                string modsDir = Path.Combine(_currentVersionConfig.UseIsolation ? Path.Combine(target.FullPath, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindustry"), "mods");
                Directory.CreateDirectory(modsDir);
                var prog = new Progress<double>(p => { ModInstallProgressBar.Value = p; ModInstallStatusText.Text = $"下载中 {p:F1}%"; });
                await DownloadFileAsync(url, Path.Combine(modsDir, file), prog); MessageBox.Show("模组安装成功！", "MDL", MessageBoxButton.OK, MessageBoxImage.Information); AnimateFade(ModInstallOverlay, false);
            }
            catch (Exception ex) { MessageBox.Show($"安装失败: {ex.InnerException?.Message ?? ex.Message}"); }
            finally { ConfirmModInstallBtn.IsEnabled = true; ToggleDownloadState(false); }
        }

        private async void SchematicSource_Checked(object sender, RoutedEventArgs e) { if (sender is RadioButton rb && rb.Tag is string tag) { var parts = tag.Split('|'); if (parts.Length == 2) { _currentSchematicRepo = parts[0]; _currentSchematicBranch = parts[1]; if (SchematicSourceTitle != null) SchematicSourceTitle.Text = $"社区蓝图库 - {rb.Content}"; if (SchematicSearchBox != null) SchematicSearchBox.Text = ""; await FetchSchematicsAsync(false); } } }
        private async void RefreshSchematicBrowser_Click(object sender, RoutedEventArgs e) => await FetchSchematicsAsync(true);
        private async Task FetchSchematicsAsync(bool forceRefresh)
        {
            _schematicFetchCts?.Cancel(); _schematicFetchCts = new CancellationTokenSource(); var token = _schematicFetchCts.Token; await _schematicFetchLock.WaitAsync();
            try
            {
                if (token.IsCancellationRequested) return;
                if (SchematicBrowserLoadingText != null) { SchematicBrowserLoadingText.Visibility = Visibility.Visible; }
                if (SchematicBrowserListBox != null) SchematicBrowserListBox.Visibility = Visibility.Collapsed; ToggleDownloadState(true);
                string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache"); Directory.CreateDirectory(cacheDir); string zipPath = Path.Combine(cacheDir, $"{_currentSchematicRepo.Replace("/", "_")}.zip");
                if (forceRefresh || !File.Exists(zipPath))
                {
                    SchematicBrowserLoadingText!.Text = "正在解析并下载仓库 ZIP，首次拉取可能需要几十秒，请耐心等待..."; string zipUrl = FormatUrlStatic($"https://github.com/{_currentSchematicRepo}/archive/refs/heads/{_currentSchematicBranch}.zip");
                    using var resp = await _http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, token); using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None); await resp.Content.CopyToAsync(fs);
                }
                if (token.IsCancellationRequested) return;
                SchematicBrowserLoadingText!.Text = "正在本地原生解析二进制蓝图元数据..."; var newList = new List<SchematicEntry>();
                await Task.Run(() => {
                    using var zip = ZipFile.OpenRead(zipPath); foreach (var entry in zip.Entries) { if (token.IsCancellationRequested) return; if (entry.Name.EndsWith(".msch", StringComparison.OrdinalIgnoreCase)) { using var es = entry.Open(); using var ms = new MemoryStream(); es.CopyTo(ms); string desc = ""; string? realName = ParseMschName(ms.ToArray(), out desc); newList.Add(new SchematicEntry(realName ?? "", desc, entry.Name, entry.FullName)); } }
                }, token);
                if (token.IsCancellationRequested) return;
                _allOnlineSchematics = newList; if (SchematicBrowserListBox != null) { SchematicBrowserListBox.ItemsSource = null; SchematicBrowserListBox.ItemsSource = _allOnlineSchematics; SchematicBrowserListBox.Visibility = Visibility.Visible; }
                if (SchematicBrowserLoadingText != null) SchematicBrowserLoadingText.Visibility = Visibility.Collapsed;
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (SchematicBrowserLoadingText != null && !token.IsCancellationRequested) SchematicBrowserLoadingText.Text = $"拉取蓝图列表失败: {ex.InnerException?.Message ?? ex.Message}\n请前往“设置”切换节点后，重试拉取。"; }
            finally { _schematicFetchLock.Release(); if (!token.IsCancellationRequested) { ToggleDownloadState(false); } }
        }
        private string? ParseMschName(byte[] mschBytes, out string description) { description = ""; try { using var ms = new MemoryStream(mschBytes); using var reader = new BinaryReader(ms); if (reader.ReadByte() != 'm' || reader.ReadByte() != 's' || reader.ReadByte() != 'c' || reader.ReadByte() != 'h') return null; reader.ReadByte(); ms.Seek(2, SeekOrigin.Current); using var deflate = new DeflateStream(ms, CompressionMode.Decompress); using var deflatedMs = new MemoryStream(); deflate.CopyTo(deflatedMs); deflatedMs.Position = 0; using var dataReader = new BinaryReader(deflatedMs); short ReadShort() { return (short)((dataReader.ReadByte() << 8) | dataReader.ReadByte()); } string ReadString() { short len = ReadShort(); return System.Text.Encoding.UTF8.GetString(dataReader.ReadBytes(len)); } ReadShort(); ReadShort(); byte tagsCount = dataReader.ReadByte(); string? foundName = null; for (int i = 0; i < tagsCount; i++) { string key = ReadString(); string val = ReadString(); if (key == "name") foundName = StripColors(val); if (key == "description") description = StripColors(val); } return foundName; } catch { return null; } }
        private void SchematicSearchBox_TextChanged(object sender, TextChangedEventArgs e) { string k = SchematicSearchBox.Text.ToLower(); SchematicBrowserListBox.ItemsSource = string.IsNullOrWhiteSpace(k) ? _allOnlineSchematics : _allOnlineSchematics.Where(s => s.UI_Name.ToLower().Contains(k) || s.UI_Description.ToLower().Contains(k)).ToList(); }
        private void InstallSchematicFromBrowser_Click(object sender, RoutedEventArgs e) { if (_isDownloading) { MessageBox.Show("当前有任务正在下载中，请稍后操作！", "MDL", MessageBoxButton.OK, MessageBoxImage.Warning); return; } if (sender is Button b && b.Tag is SchematicEntry schematic) { var all = GetAllInstalledInstances(); if (all.Count == 0) { MessageBox.Show("请先导入游戏实例！"); return; } _selectedSchematicToInstall = schematic; SchematicInstallTitle.Text = $"安装蓝图 - {schematic.UI_Name}"; SchematicInstancesListBox.ItemsSource = all; if (_currentInstance != null) { var m = all.FirstOrDefault(i => i.FullPath == _currentInstance.FullPath); if (m != null) SchematicInstancesListBox.SelectedItem = m; } SchematicInstancesListBox.IsEnabled = true; ConfirmSchematicInstallBtn.IsEnabled = SchematicInstancesListBox.SelectedItem != null; AnimateFade(SchematicInstallOverlay, true); } }
        private void SchematicInstallForm_SelectionChanged(object sender, SelectionChangedEventArgs e) => ConfirmSchematicInstallBtn.IsEnabled = SchematicInstancesListBox.SelectedItem != null;
        private void CancelSchematicInstall_Click(object sender, RoutedEventArgs e) => AnimateFade(SchematicInstallOverlay, false);
        private void ConfirmSchematicInstall_Click(object sender, RoutedEventArgs e) { if (_selectedSchematicToInstall == null || SchematicInstancesListBox.SelectedItem is not GameInstanceInfo target) return; try { LoadVersionConfig(target.FullPath); string schematicDir = Path.Combine(_currentVersionConfig.UseIsolation ? Path.Combine(target.FullPath, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindustry"), "schematics"); Directory.CreateDirectory(schematicDir); string targetFile = Path.Combine(schematicDir, _selectedSchematicToInstall.FileName); string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache"); string zipPath = Path.Combine(cacheDir, $"{_currentSchematicRepo.Replace("/", "_")}.zip"); using var fs = File.OpenRead(zipPath); using var zip = new ZipArchive(fs, ZipArchiveMode.Read); var entry = zip.GetEntry(_selectedSchematicToInstall.ZipEntryFullName); if (entry != null) { entry.ExtractToFile(targetFile, true); MessageBox.Show("蓝图从缓存解压并安装成功！", "MDL", MessageBoxButton.OK, MessageBoxImage.Information); } AnimateFade(SchematicInstallOverlay, false); } catch (Exception ex) { MessageBox.Show("安装失败: " + ex.Message); } }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => this.DragMove();
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => this.Close();
        private void OpenWiki_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo("https://mdtwiki.top/") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("无法打开链接: " + ex.Message); } }
        private void OpenItch_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo("https://anuke.itch.io/mindustry") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("无法打开链接: " + ex.Message); } }
        private void OpenSteam_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo("https://store.steampowered.com/app/1127400/Mindustry") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("无法打开链接: " + ex.Message); } }

        private async void DownloadSource_Checked(object sender, RoutedEventArgs e) { if (sender is RadioButton rb && rb.Tag is string repo) { _currentDownloadRepo = repo; if (DownloadSourceTitle != null) DownloadSourceTitle.Text = $"获取新版本 - {rb.Content}"; await FetchRemoteVersionsAsync(); } }
        private async Task FetchRemoteVersionsAsync() { if (RemoteVersionLoadingText != null) { RemoteVersionLoadingText.Text = "正在拉取列表..."; RemoteVersionLoadingText.Visibility = Visibility.Visible; } if (RemoteVersionListBox != null) RemoteVersionListBox.Visibility = Visibility.Collapsed; try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); string apiUrl = FormatUrlStatic($"https://api.github.com/repos/{_currentDownloadRepo}/releases", true); var rels = await _http.GetFromJsonAsync<List<GitHubRelease>>(apiUrl, cts.Token); if (rels != null) { var list = rels.Where(r => r.Assets != null && r.Assets.Any(a => a.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("server", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("android", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("dependencies", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("javadoc", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("sources", StringComparison.OrdinalIgnoreCase))).ToList(); if (RemoteVersionListBox != null) { RemoteVersionListBox.ItemsSource = list; RemoteVersionListBox.Visibility = Visibility.Visible; } if (RemoteVersionLoadingText != null) RemoteVersionLoadingText.Visibility = Visibility.Collapsed; } } catch (Exception ex) { if (RemoteVersionLoadingText != null) RemoteVersionLoadingText.Text = $"拉取超时，请稍后刷新: {ex.InnerException?.Message ?? ex.Message}"; } }
        private void RemoteVersion_RightClick(object sender, MouseButtonEventArgs e) { if (sender is FrameworkElement f && f.DataContext is GitHubRelease rel) { ReleaseNotesTitle.Text = $"{rel.TagName} - 更新日志"; ReleaseNotesTitle.Foreground = Brushes.Black; ReleaseNotesText.Text = string.IsNullOrWhiteSpace(rel.Body) ? "作者很懒，没有留下任何说明..." : rel.Body; _currentDetailUrl = $"https://github.com/{_currentDownloadRepo}/releases/tag/{rel.TagName}"; OpenRepoBtn.Visibility = Visibility.Visible; ExportCrashBtn.Visibility = Visibility.Collapsed; AnimateFade(ReleaseNotesOverlay, true); } }
        private void OpenRepo_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(_currentDetailUrl)) { try { Process.Start(new ProcessStartInfo(_currentDetailUrl) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("无法打开链接: " + ex.Message); } } }
        private async void DownloadVersion_Click(object sender, RoutedEventArgs e)
        {
            if (_config.ManagedFolders.Count == 0) { MessageBox.Show("请先导入文件夹！"); return; }
            var rel = (sender as Button)?.Tag as GitHubRelease; if (rel == null) return;
            if (_isDownloading) { MessageBox.Show("当前有下载任务，请稍后完成！"); return; }
            var candidates = rel.Assets?.Where(a => a.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("server", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("android", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("dependencies", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("javadoc", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("sources", StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates == null || candidates.Count == 0) { MessageBox.Show("无适用的客户端文件。"); return; }
            GitHubAsset? asset = null;
            if (_currentDownloadRepo.Contains("antigrief", StringComparison.OrdinalIgnoreCase))
            {
                var audio = candidates.FirstOrDefault(a => a.Name.Contains("audio", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("voice", StringComparison.OrdinalIgnoreCase));
                var standard = candidates.FirstOrDefault(a => (a.Name.Contains("desktop", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("client", StringComparison.OrdinalIgnoreCase)) && !a.Name.Contains("audio", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("voice", StringComparison.OrdinalIgnoreCase));
                if (standard == null) standard = candidates.FirstOrDefault(a => !a.Name.Contains("audio", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("voice", StringComparison.OrdinalIgnoreCase));
                if (audio != null && standard != null) { var r = MessageBox.Show("检测到 Foo 端带语音版。\n▶ 是: 下载语音版\n▶ 否: 下载标准版", "版本选择", MessageBoxButton.YesNoCancel, MessageBoxImage.Question); if (r == MessageBoxResult.Yes) asset = audio; else if (r == MessageBoxResult.No) asset = standard; else return; }
            }
            if (asset == null) { asset = candidates.FirstOrDefault(a => a.Name.Equals("Mindustry.jar", StringComparison.OrdinalIgnoreCase)); if (asset == null) { asset = candidates.FirstOrDefault(a => a.Name.Contains("desktop", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("Desktop") || a.Name.Contains("client", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase)); if (asset == null) { var nonModAssets = candidates.Where(a => !a.Name.Contains("mod", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("addon", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("plugin", StringComparison.OrdinalIgnoreCase)).ToList(); asset = nonModAssets.Count > 0 ? nonModAssets[0] : candidates[0]; } } }
            if (asset == null) { MessageBox.Show("无法确定需要下载的文件，数据可能异常。"); return; }
            string folder = Path.Combine(_config.ManagedFolders[0], "Versions", rel.TagName + (_currentDownloadRepo.Contains("TinyLake") ? "-X端" : (_currentDownloadRepo.Contains("antigrief") ? "-Foo端" : "")));
            int c = 1; string baseF = folder; while (Directory.Exists(folder)) { folder = $"{baseF}-{c++}"; }
            Directory.CreateDirectory(folder); DownloadPanel.Visibility = Visibility.Visible; ToggleDownloadState(true);
            try { var prog = new Progress<double>(p => { DownloadProgressBar.Value = p; StatusText.Text = $"下载中 {p:F1}%"; }); await DownloadFileAsync(FormatUrlStatic(asset.BrowserDownloadUrl), Path.Combine(folder, "Mindustry.jar"), prog); MessageBox.Show("下载成功！"); StatusText.Text = "下载成功！"; } catch (Exception ex) { MessageBox.Show($"下载失败: {ex.InnerException?.Message ?? ex.Message}"); } finally { await Task.Delay(2000); DownloadPanel.Visibility = Visibility.Collapsed; ToggleDownloadState(false); }
        }
        private async Task DownloadFileAsync(string url, string p, IProgress<double> prog) { using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead); var total = resp.Content.Headers.ContentLength ?? -1L; using var rs = await resp.Content.ReadAsStreamAsync(); using var ws = File.Open(p, FileMode.Create); var buf = new byte[8192]; long read = 0; int r; while ((r = await rs.ReadAsync(buf, 0, buf.Length)) != 0) { await ws.WriteAsync(buf, 0, r); read += r; if (total != -1) prog.Report((double)read / total * 100); } }

        private async void AutoScanGlobalJava_Click(object sender, RoutedEventArgs e) { try { ScanGlobalJavaBtn.Content = "扫描中..."; ScanGlobalJavaBtn.IsEnabled = false; string currentPath = GlobalJavaComboBox.Text; var javas = await Task.Run(() => JavaScanner.Scan(currentPath, true)); GlobalJavaComboBox.ItemsSource = javas; if (javas.Count > 0) GlobalJavaComboBox.Text = javas[0].Path; else MessageBox.Show("未在电脑中找到 Java。请手动浏览选择 javaw.exe", "MDL", MessageBoxButton.OK, MessageBoxImage.Information); } catch (Exception ex) { MessageBox.Show($"扫描出错: {ex.Message}"); } finally { ScanGlobalJavaBtn.Content = "重新扫描"; ScanGlobalJavaBtn.IsEnabled = true; } }
        private async void AutoScanVersionJava_Click(object sender, RoutedEventArgs e) { try { ScanVersionJavaBtn.Content = "扫描中..."; ScanVersionJavaBtn.IsEnabled = false; string currentPath = VSettingsJavaComboBox.Text; var javas = await Task.Run(() => JavaScanner.Scan(currentPath, true)); VSettingsJavaComboBox.ItemsSource = javas; if (javas.Count > 0) VSettingsJavaComboBox.Text = javas[0].Path; else MessageBox.Show("未在电脑中找到 Java。请手动浏览选择 javaw.exe", "MDL", MessageBoxButton.OK, MessageBoxImage.Information); } catch (Exception ex) { MessageBox.Show($"扫描出错: {ex.Message}"); } finally { ScanVersionJavaBtn.Content = "重新扫描"; ScanVersionJavaBtn.IsEnabled = true; } }
        private void BrowseGlobalJavaBtn_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Java|java.exe;javaw.exe" }; if (d.ShowDialog() == true) GlobalJavaComboBox.Text = d.FileName; }
        private void LoadConfig() { if (File.Exists(ConfigFilePath)) try { _config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFilePath)) ?? new AppConfig(); } catch { } }
        private void SaveConfig() => File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(_config));
        private void LoadVersionConfig(string p) { string cp = Path.Combine(p, "mdl_instance_config.json"); if (File.Exists(cp)) { try { _currentVersionConfig = JsonSerializer.Deserialize<VersionConfig>(File.ReadAllText(cp)) ?? new VersionConfig(); } catch { _currentVersionConfig = new VersionConfig(); } } else { _currentVersionConfig = new VersionConfig(); _currentVersionConfig.CustomRamMB = _config.GlobalRamMB; } }
        private void SaveVersionConfig(string p) { _currentVersionConfig.CustomJavaPath = VSettingsJavaComboBox.Text; _currentVersionConfig.CustomJvmArgs = VSettingsJvmArgsBox.Text; _currentVersionConfig.UseIsolation = VSettingsIsolationBox.SelectedIndex == 0; if (VSettingsRamSlider != null) _currentVersionConfig.CustomRamMB = (int)VSettingsRamSlider.Value; string cp = Path.Combine(p, "mdl_instance_config.json"); try { File.WriteAllText(cp, JsonSerializer.Serialize(_currentVersionConfig)); } catch { } }

    }

    public class SettingItem
    {
        public string Key { get; set; } = "";
        public object OriginalValue { get; set; } = new object();
        public string DisplayValue { get; set; } = "";
        public byte Type { get; set; }
        public bool IsBinary => Type == 5;
    }

    public class MindustrySettingsEditor
    {
        public string ErrorMessage { get; private set; } = "";

        public bool LoadList(string filePath, out List<SettingItem> items)
        {
            items = new List<SettingItem>();
            ErrorMessage = "";
            if (!File.Exists(filePath)) return false;
            try
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                using var ms = new MemoryStream(fileBytes);
                using var reader = new BinaryReader(ms);
                int amount = ReadInt32BE(reader);

                for (int i = 0; i < amount; i++)
                {
                    string key = ReadJavaUTF(reader);
                    byte type = reader.ReadByte();
                    var item = new SettingItem { Key = key, Type = type };

                    switch (type)
                    {
                        case 0:
                            bool bVal = reader.ReadBoolean();
                            item.OriginalValue = bVal;
                            item.DisplayValue = bVal.ToString();
                            break;
                        case 1:
                            int iVal = ReadInt32BE(reader);
                            item.OriginalValue = iVal;
                            item.DisplayValue = iVal.ToString();
                            break;
                        case 2:
                            long lVal = ReadInt64BE(reader);
                            item.OriginalValue = lVal;
                            item.DisplayValue = lVal.ToString();
                            break;
                        case 3:
                            byte[] fb = reader.ReadBytes(4);
                            if (BitConverter.IsLittleEndian) Array.Reverse(fb);
                            float fVal = BitConverter.ToSingle(fb, 0);
                            item.OriginalValue = fVal;
                            item.DisplayValue = fVal.ToString();
                            break;
                        case 4:
                            string sVal = ReadJavaUTF(reader);
                            item.OriginalValue = sVal;
                            item.DisplayValue = sVal;
                            break;
                        case 5:
                            int len = ReadInt32BE(reader);
                            byte[] bin = reader.ReadBytes(len);
                            item.OriginalValue = bin;
                            item.DisplayValue = $"[二进制数据, 长度={len}]";
                            break;
                        default:
                            ErrorMessage = $"未知类型 {type} (Key: {key})";
                            return false;
                    }
                    items.Add(item);
                }
                return true;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                return false;
            }
        }

        public void SaveList(string filePath, List<SettingItem> items)
        {
            using var fs = File.Create(filePath);
            using var bw = new BinaryWriter(fs);
            WriteInt32BE(bw, items.Count);

            foreach (var item in items)
            {
                WriteJavaUTF(bw, item.Key);
                bw.Write(item.Type);

                switch (item.Type)
                {
                    case 0:
                        bw.Write(bool.Parse(item.DisplayValue));
                        break;
                    case 1:
                        WriteInt32BE(bw, int.Parse(item.DisplayValue));
                        break;
                    case 2:
                        WriteInt64BE(bw, long.Parse(item.DisplayValue));
                        break;
                    case 3:
                        byte[] fb = BitConverter.GetBytes(float.Parse(item.DisplayValue));
                        if (BitConverter.IsLittleEndian) Array.Reverse(fb);
                        bw.Write(fb);
                        break;
                    case 4:
                        WriteJavaUTF(bw, item.DisplayValue);
                        break;
                    case 5:
                        byte[] bin = (byte[])item.OriginalValue;
                        WriteInt32BE(bw, bin.Length);
                        bw.Write(bin);
                        break;
                }
            }
        }

        private int ReadInt32BE(BinaryReader r)
        {
            byte[] b = r.ReadBytes(4);
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        private void WriteInt32BE(BinaryWriter w, int v)
        {
            w.Write((byte)(v >> 24));
            w.Write((byte)(v >> 16));
            w.Write((byte)(v >> 8));
            w.Write((byte)v);
        }

        private long ReadInt64BE(BinaryReader r)
        {
            byte[] b = r.ReadBytes(8);
            return ((long)b[0] << 56) | ((long)b[1] << 48) | ((long)b[2] << 40) | ((long)b[3] << 32) | ((long)b[4] << 24) | ((long)b[5] << 16) | ((long)b[6] << 8) | b[7];
        }

        private void WriteInt64BE(BinaryWriter w, long v)
        {
            w.Write((byte)(v >> 56));
            w.Write((byte)(v >> 48));
            w.Write((byte)(v >> 40));
            w.Write((byte)(v >> 32));
            w.Write((byte)(v >> 24));
            w.Write((byte)(v >> 16));
            w.Write((byte)(v >> 8));
            w.Write((byte)v);
        }

        private string ReadJavaUTF(BinaryReader r)
        {
            byte[] b = r.ReadBytes(2);
            int len = (b[0] << 8) | b[1];
            return Encoding.UTF8.GetString(r.ReadBytes(len));
        }

        private void WriteJavaUTF(BinaryWriter w, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            w.Write((byte)(b.Length >> 8));
            w.Write((byte)b.Length);
            w.Write(b);
        }
    }

    public static class SmoothScrollHelper
    {
        public static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.RegisterAttached("ScrollOffset", typeof(double), typeof(SmoothScrollHelper), new PropertyMetadata(0.0, OnScrollOffsetChanged));
        public static void SetScrollOffset(DependencyObject d, double v) => d.SetValue(ScrollOffsetProperty, v);
        private static void OnScrollOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    public static class HardwareInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys; public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static int GetTotalPhysicalMemoryMB()
        {
            try
            {
                MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref memStatus)) return (int)(memStatus.ullTotalPhys / (1024 * 1024));
            }
            catch { }
            return 16384;
        }
    }

    public static class JavaScanner
    {
        private static List<JavaInfo>? _cachedJavas = null;

        public static List<JavaInfo> Scan(string currentConfigPath = "", bool forceRefresh = false)
        {
            if (_cachedJavas != null && !forceRefresh) return _cachedJavas;
            var javaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void CheckAndAdd(string? path)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path)) return;
                    if (path.IndexOf("\\javapath", StringComparison.OrdinalIgnoreCase) >= 0) return;
                    if (File.Exists(path)) javaPaths.Add(Path.GetFullPath(path));
                }
                catch { }
            }

            void SearchBin(string dir, int maxDepth = 2, int currentDepth = 0)
            {
                try { if (currentDepth > maxDepth || !Directory.Exists(dir)) return; } catch { return; }
                try
                {
                    string javawPath = Path.Combine(dir, "bin", "javaw.exe");
                    string javaPath = Path.Combine(dir, "bin", "java.exe");
                    if (File.Exists(javawPath)) CheckAndAdd(javawPath);
                    else if (File.Exists(javaPath)) CheckAndAdd(javaPath);

                    string directJavaw = Path.Combine(dir, "javaw.exe");
                    string directJava = Path.Combine(dir, "java.exe");
                    if (File.Exists(directJavaw)) CheckAndAdd(directJavaw);
                    else if (File.Exists(directJava)) CheckAndAdd(directJava);

                    foreach (var subDir in Directory.GetDirectories(dir))
                    {
                        string name = Path.GetFileName(subDir).ToLower();
                        if (currentDepth == 0 || name.Contains("jre") || name.Contains("jdk") || name.Contains("java") || name.Contains("bin") || name.Contains("runtime") || name.Contains("x64") || name.Contains("x86") || name.Contains("hotspot") || name.Contains("corretto") || name.Contains("zulu") || name.Contains("adopt") || name.StartsWith("jre-") || name.StartsWith("jdk-") || name.Contains("versions"))
                        {
                            SearchBin(subDir, maxDepth, currentDepth + 1);
                        }
                    }
                }
                catch { }
            }

            CheckAndAdd(currentConfigPath);

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft");
                if (key != null)
                {
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey != null)
                        {
                            foreach (var verKeyName in subKey.GetSubKeyNames())
                            {
                                using var verKey = subKey.OpenSubKey(verKeyName);
                                string? jHome = verKey?.GetValue("JavaHome") as string;
                                if (!string.IsNullOrEmpty(jHome))
                                {
                                    CheckAndAdd(Path.Combine(jHome, "bin", "javaw.exe"));
                                    CheckAndAdd(Path.Combine(jHome, "bin", "java.exe"));
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome)) SearchBin(javaHome, 1);

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var p in pathEnv.Split(Path.PathSeparator))
                {
                    try { CheckAndAdd(Path.Combine(p.Trim('"', ' '), "javaw.exe")); } catch { }
                }
            }

            var appData = Environment.GetEnvironmentVariable("APPDATA");
            var localAppData = Environment.GetEnvironmentVariable("LocalAppData");
            var baseDirs = new List<string?>
            {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                localAppData + "\\Programs",
                appData + "\\.minecraft\\runtime",
                appData + "\\.hmcl\\java",
                appData + "\\.minecraft\\versions",
                localAppData + "\\Packages\\Microsoft.4297127D64EC6_8wekyb3d8bbwe\\LocalCache\\Local\\runtime"
            };

            try
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                {
                    string root = drive.Name;
                    baseDirs.Add(Path.Combine(root, "Java"));
                    baseDirs.Add(Path.Combine(root, "java"));
                    baseDirs.Add(Path.Combine(root, "Program Files", "Java"));
                    baseDirs.Add(Path.Combine(root, "Program Files (x86)", "Java"));
                    baseDirs.Add(Path.Combine(root, "MinecraftLauncher", "runtime"));
                    baseDirs.Add(Path.Combine(root, "MCLauncher", "runtime"));
                    baseDirs.Add(Path.Combine(root, ".minecraft", "runtime"));
                    baseDirs.Add(Path.Combine(root, "MC", "runtime"));
                }
            }
            catch { }

            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            baseDirs.Add(Path.Combine(currentDir, "runtime"));
            baseDirs.Add(Path.Combine(currentDir, "java"));
            baseDirs.Add(Path.Combine(currentDir, "jre"));
            baseDirs.Add(Path.Combine(currentDir, ".minecraft", "runtime"));
            baseDirs.Add(Path.Combine(currentDir, "hmcl", ".minecraft", "versions"));

            foreach (var dir in baseDirs)
            {
                if (!string.IsNullOrWhiteSpace(dir)) SearchBin(dir, 3);
            }

            var results = new List<JavaInfo>();
            foreach (var path in javaPaths)
            {
                var info = GetJavaVersionFast(path);
                results.Add(info);
            }

            _cachedJavas = results.OrderByDescending(j => j.VersionNumber).ToList();
            return _cachedJavas;
        }

        private static JavaInfo GetJavaVersionFast(string javaPath)
        {
            var info = new JavaInfo { Path = javaPath, Version = "未知版本", VersionNumber = 0 };
            string type = javaPath.IndexOf("jre", StringComparison.OrdinalIgnoreCase) >= 0 ? "JRE" : "JDK";
            string rawVersion = "";

            try
            {
                string javaHome = Directory.GetParent(Path.GetDirectoryName(javaPath)!)!.FullName;
                string releaseFile = Path.Combine(javaHome, "release");
                if (File.Exists(releaseFile))
                {
                    foreach (var line in File.ReadLines(releaseFile))
                    {
                        if (line.StartsWith("JAVA_VERSION="))
                        {
                            rawVersion = line.Substring(13).Trim('"', ' ', '\r', '\n');
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(rawVersion))
                {
                    var fvi = FileVersionInfo.GetVersionInfo(javaPath);
                    string? v = fvi.ProductVersion;
                    if (string.IsNullOrEmpty(v)) v = fvi.FileVersion;
                    if (!string.IsNullOrEmpty(v)) rawVersion = v.Split(' ')[0];
                }

                if (!string.IsNullOrEmpty(rawVersion))
                {
                    string majorStr = rawVersion.StartsWith("1.") ? rawVersion.Split('.')[1] : rawVersion.Split('.')[0];
                    if (int.TryParse(majorStr, out int v))
                    {
                        info.VersionNumber = v;
                        info.Version = $"{type} {v} ({rawVersion})";
                    }
                    else
                    {
                        info.Version = $"{type} {rawVersion}";
                    }
                    return info;
                }

                var match = Regex.Match(javaHome, @"(jre|jdk|java)-?(\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    if (int.TryParse(match.Groups[2].Value, out int v2))
                    {
                        info.VersionNumber = v2;
                        info.Version = $"{type} {v2} ({v2}.0)";
                    }
                }
            }
            catch { }
            return info;
        }
    }

    public class JavaInfo { public string Path { get; set; } = ""; public string Version { get; set; } = ""; public int VersionNumber { get; set; } }
    public class AppConfig { public List<string> ManagedFolders { get; set; } = new List<string>(); public string GlobalJavaPath { get; set; } = ""; public string LastSelectedInstancePath { get; set; } = ""; public int ProxyNodeIndex { get; set; } = 1; public int GlobalRamMB { get; set; } = 4096; public bool GlobalUseAutoRam { get; set; } = true; }
    public class VersionConfig { public bool UseIsolation { get; set; } = true; public string CustomJavaPath { get; set; } = ""; public string CustomJvmArgs { get; set; } = ""; public int CustomRamMB { get; set; } = 4096; public bool UseAutoRam { get; set; } = true; }
    public class GameInstanceInfo { public string Name { get; set; } = ""; public string FullPath { get; set; } = ""; }
    public class ModInfo { public string FileName { get; set; } = ""; public string FullPath { get; set; } = ""; public string FileSize { get; set; } = ""; public string DisplayName { get; set; } = ""; public string Author { get; set; } = ""; public string Description { get; set; } = ""; public string Version { get; set; } = ""; public ImageSource? IconImage { get; set; } public string UI_Name => string.IsNullOrEmpty(DisplayName) ? FileName : DisplayName; public string UI_Author => string.IsNullOrEmpty(Author) ? "未知作者" : $"作者: {Author}"; }
    public class ModRegistryEntry { [JsonPropertyName("repo")] public string Repo { get; set; } = ""; [JsonPropertyName("name")] public string Name { get; set; } = ""; [JsonPropertyName("author")] public string Author { get; set; } = ""; [JsonPropertyName("description")] public string Description { get; set; } = ""; [JsonPropertyName("stars")] public int Stars { get; set; } [JsonIgnore] public string AuthorFormatted => $"作者: {Author}"; [JsonIgnore] public string StarsFormatted => $"★ {Stars}"; [JsonIgnore] public string IconUrl => MainWindow.FormatUrlStatic($"https://raw.githubusercontent.com/{Repo}/master/icon.png"); }
    public record GitHubRelease([property: JsonPropertyName("tag_name")] string TagName, [property: JsonPropertyName("body")] string? Body, [property: JsonPropertyName("assets")] List<GitHubAsset>? Assets);
    public record GitHubAsset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
    public record SchematicEntry(string RealName, string Description, string FileName, string ZipEntryFullName) { public string UI_Name => string.IsNullOrEmpty(RealName) ? FileName : RealName; public string UI_Description => string.IsNullOrEmpty(Description) ? "暂无描述" : Description; }
    public class GitHubTreeResponse { [JsonPropertyName("tree")] public List<GitHubTreeItem>? Tree { get; set; } }
    public class GitHubTreeItem { [JsonPropertyName("path")] public string Path { get; set; } = ""; [JsonPropertyName("type")] public string Type { get; set; } = ""; [JsonPropertyName("size")] public long Size { get; set; } }


    }