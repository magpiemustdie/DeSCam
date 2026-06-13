using System.Windows;
using System.Windows.Controls;

namespace DeSCam;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;

        // Auto-scroll log
        _vm.Log.CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    // ── Connection tab ────────────────────────────────────────────────────────
    async void Connect_Click(object s, RoutedEventArgs e)
        => await _vm.ConnectAsync();

    void Disconnect_Click(object s, RoutedEventArgs e)
        => _vm.Disconnect();

    async void Verify_Click(object s, RoutedEventArgs e)
    {
        var rows = await _vm.VerifyStringsAsync();
        StringsGrid.ItemsSource = rows.Select(r => new
        {
            Name = r.name,
            Addr = $"PS3:0x{r.va:X8}",
            Value = r.val
        }).ToList();
    }

    async void Diag_Click(object s, RoutedEventArgs e)
    {
        if (!_vm.IsConnected) { MessageBox.Show("Connect first.", "DeSCam"); return; }
        await _vm.RunDiagnosticAsync();
    }

    // ── ChrFollowCam tab ──────────────────────────────────────────────────────
    async void Find_Click(object s, RoutedEventArgs e)
        => await _vm.FindNodeAsync(DebugEbootCheck.IsChecked == true);

    async void Refresh_Click(object s, RoutedEventArgs e)
        => await _vm.RefreshAsync();
    async void ResetAll_Click(object s, RoutedEventArgs e)
        => await _vm.ResetAllAsync();

    void ResetUI_Click(object s, RoutedEventArgs e)
        => _vm.ResetUI();

    void LoadPreset_Click(object s, RoutedEventArgs e)
        => _vm.LoadPresetFromFile();

    void DebugTree_Click(object s, RoutedEventArgs e)
        => _ = _vm.DumpFollowNodeAsync();

    async void WriteParam_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is ParamRow row)
            await _vm.WriteParamAsync(row);
    }

    void LockParam_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is ParamRow row)
            _vm.ToggleLock(row);
    }

    void SaveParam_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is ParamRow row)
        {
            var val = !string.IsNullOrWhiteSpace(row.Input) ? row.Input : row.Live;
            if (!string.IsNullOrWhiteSpace(val) && val != "—" && val != "err")
                _vm.SaveParamToPreset(row.Name, val);
        }
    }


    void LockInterval_Click(object s, RoutedEventArgs e)
    {
        if (int.TryParse(LockIntervalBox.Text.Trim(), out int ms) && ms >= 1)
            _vm.SetLockInterval(ms);
        else
            LockIntervalBox.Text = _vm.LockIntervalMs.ToString();
    }

    void RefreshInterval_Click(object s, RoutedEventArgs e)
    {
        if (int.TryParse(RefreshIntervalBox.Text.Trim(), out int ms) && ms >= 1)
            _vm.SetRefreshInterval(ms);
        else
            RefreshIntervalBox.Text = _vm.RefreshIntervalMs.ToString();
    }

    // ── Log tab ───────────────────────────────────────────────────────────────
    void ClearLog_Click(object s, RoutedEventArgs e)
        => _vm.Log.Clear();

    // ── FovY Scan tab ─────────────────────────────────────────────────────────
    async void FovyScan_Click(object s, RoutedEventArgs e)
    {
        FovyScanGrid.ItemsSource = _vm.FovyScanResults;
        float minDeg = float.TryParse(FovyScanMin.Text.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float mn) ? mn : 42.9f;
        float maxDeg = float.TryParse(FovyScanMax.Text.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float mx) ? mx : 43.1f;
        int count = await _vm.ScanFovYAsync(msg => FovyScanStatus.Text = msg, minDeg, maxDeg);
        FovyScanCount.Text = count.ToString();
    }

    async void FovyWriteAll_Click(object s, RoutedEventArgs e)
    {
        if (!double.TryParse(FovyWriteValue.Text.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double deg))
            deg = 120.0;
        if (!int.TryParse(FovyFromRow.Text.Trim(), out int from)) from = 0;
        if (!int.TryParse(FovyToRow.Text.Trim(),   out int to))   to   = 10;
        await _vm.WriteFovYRangeAsync(deg, from, to);
    }

    async void FovyWrite_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is FovyScanRow row)
            await _vm.WriteFovYRowAsync(row);
    }

    void FovyLock_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is FovyScanRow row)
            _vm.ToggleFovYLock(row);
    }

    void FovyGoto_Click(object s, RoutedEventArgs e)
    {
        if (!int.TryParse(FovyGotoIndex.Text.Trim(), out int idx)) return;
        if (idx < 0 || idx >= _vm.FovyScanResults.Count)
        {
            FovyScanStatus.Text = $"Index {idx} out of range (0..{_vm.FovyScanResults.Count-1})";
            return;
        }
        var row = _vm.FovyScanResults[idx];
        FovyScanGrid.SelectedItem  = row;
        FovyScanGrid.ScrollIntoView(row);
        FovyScanStatus.Text = $"Row {idx}: PS3={row.AddrHex}  CE={row.AddrCE}  Live={row.LiveDeg}  (In CE: attach to rpcs3.exe, address={row.AddrCE})";
    }

    // ── Binary search ─────────────────────────────────────────────────────────
    void BsStart_Click(object s, RoutedEventArgs e)
    {
        if (!int.TryParse(FovyFromRow.Text.Trim(), out int lo)) lo = 0;
        if (!int.TryParse(FovyToRow.Text.Trim(),   out int hi)) hi = 10;
        _vm.BsStart(lo, hi);
    }

    async void BsLeft_Click(object s, RoutedEventArgs e)  => await _vm.BsLeft();
    async void BsRight_Click(object s, RoutedEventArgs e) => await _vm.BsRight();
    void       BsFixLeft_Click(object s, RoutedEventArgs e)  => _vm.BsFixLeft();
    void       BsFixRight_Click(object s, RoutedEventArgs e) => _vm.BsFixRight();
    void       BsReset_Click(object s, RoutedEventArgs e) => _vm.BsReset();

    // ── Shutdown ──────────────────────────────────────────────────────────────
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        _vm.Disconnect();
        _vm.Dispose();
    }
}
