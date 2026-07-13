using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace DeSCam;

public class MainViewModel : INotifyPropertyChanged
{
    readonly Ps3Memory  _mem  = new();
    readonly Dispatcher _ui;
    DispatcherTimer?    _refreshTimer;
    DispatcherTimer?    _lockTimer;
    volatile bool       _searching;
    DateTime            _lastSearchTime = DateTime.MinValue;
    bool                _debugEboot;   // mirrors DebugEbootCheck; set by SetDebugEboot()
    int                 _lockIntervalMs    = 250;
    int                 _refreshIntervalMs = 500;

    static readonly string SettingsPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "settings.txt");

    // ── Observable properties ─────────────────────────────────────────────────
    string _connStatus   = "● Not connected";
    string _statusText   = "Disconnected";
    string _nodeText     = "ChrFollowCam node: —";
    bool   _autoRefresh;
    bool   _isConnected;
    bool   _nodeFound;
    bool   _fovRangeCheckEnabled = true;

    public string ConnStatus   { get => _connStatus;   private set { _connStatus   = value; OnPC(); } }
    public string StatusText   { get => _statusText;   private set { _statusText   = value; OnPC(); } }
    public string NodeText     { get => _nodeText;     private set { _nodeText     = value; OnPC(); } }
    public bool   IsConnected  { get => _isConnected;  private set { _isConnected  = value; OnPC(); } }
    public bool   NodeFound    { get => _nodeFound;    private set { _nodeFound    = value; OnPC(); } }

    /// <summary>
    /// Mirrors the Debug EBOOT checkbox.  When true, FindFollowNode uses the
    /// debug_menu ptr-chain first; when false it uses the vtable scan.
    /// Stored here so AutoRefresh re-searches in the same mode as the last
    /// manual Find — regardless of when AutoRefresh was toggled.
    /// </summary>
    public bool DebugEboot
    {
        get => _debugEboot;
        set
        {
            if (_debugEboot == value) return;
            _debugEboot = value;
            OnPC();

            // Mode switch invalidates the old node — it was found in the other mode
            // and its layout does not match the newly selected param table.
            if (_mem.IsOpen)
                _mem.ResetFollowNode();
            StopLockTimer();
            NodeFound = false;
            NodeText  = "ChrFollowCam node: —";

            // Rebuild param list for the NEW mode without resolving addresses.
            // The user must press Find to re-discover the node.
            RebuildParams(false);
        }
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            _autoRefresh = value; OnPC();
            if (value) StartTimer(); else StopTimer();
        }
    }

    public bool FovRangeCheckEnabled
    {
        get => _fovRangeCheckEnabled;
        set
        {
            _fovRangeCheckEnabled = value;
            Ps3Memory.FovRangeCheckEnabled = value;
            OnPC();
            SaveSettings();
        }
    }

    // Exposed for TextBox two-way binding
    public int LockIntervalMs
    {
        get => _lockIntervalMs;
        set
        {
            if (value < 1) return;
            _lockIntervalMs = value;
            if (_lockTimer != null)
                _lockTimer.Interval = TimeSpan.FromMilliseconds(value);
            OnPC();
            SaveSettings();
        }
    }

    public int RefreshIntervalMs
    {
        get => _refreshIntervalMs;
        set
        {
            if (value < 1) return;
            _refreshIntervalMs = value;
            if (_refreshTimer != null)
                _refreshTimer.Interval = TimeSpan.FromMilliseconds(value);
            OnPC();
            SaveSettings();
        }
    }

    public ObservableCollection<ParamRow> Params { get; } = [];
    public ObservableCollection<string>   Log    { get; } = [];
    public ObservableCollection<FovyScanRow> FovyScanResults { get; } = [];

    // ── Ctor ──────────────────────────────────────────────────────────────────
    public MainViewModel(Dispatcher ui)
    {
        _ui = ui;
        LoadSettings();  // loads _debugEboot, _fovRangeCheckEnabled before building params
        Ps3Memory.FovRangeCheckEnabled = _fovRangeCheckEnabled;
        foreach (var def in ActiveDefs())
            Params.Add(new ParamRow(def));
    }

    // ── Logging ───────────────────────────────────────────────────────────────
    bool _logMuted = true;
    public bool LogMuted { get => _logMuted; set { _logMuted = value; OnPC(); } }
    void AddLog(string msg, bool uiOnly = false)
    {
        if (_logMuted) return;
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _ui.BeginInvoke(() =>
        {
            Log.Add(line);
            if (Log.Count > 300) Log.RemoveAt(0);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── Connect ───────────────────────────────────────────────────────────────
    public async Task ConnectAsync()
    {
        // Phase 1: find RPCS3 process
        AddLog("Phase 1: looking for rpcs3.exe…");
        int pid = await Task.Run(() => Ps3Memory.FindPid("rpcs3.exe"));
        if (pid == 0) { AddLog("rpcs3.exe not found — is the emulator running?"); return; }
        AddLog($"rpcs3.exe found (PID={pid})");
        ConnStatus = "● RPCS3 found";
        StatusText = $"PID={pid}, waiting for game…";

        // Phase 2: wait for game window (poll every 500ms, up to 60s)
        AddLog("Phase 2: waiting for Demon's Souls window…");
        bool ready = false;
        for (int i = 0; i < 120; i++) // 120 × 500ms = 60s timeout
        {
            if (await Task.Run(() => Ps3Memory.IsGameWindowOpen())) { ready = true; break; }
            await Task.Delay(500);
        }
        if (!ready) { AddLog("Timed out waiting for game window — try again later"); return; }
        AddLog("Game window detected");

        // Phase 3: ensure handle is open
        if (!_mem.IsOpen)
        {
            bool ok = await Task.Run(() => _mem.Open(pid));
            if (!ok) { AddLog($"OpenProcess failed PID={pid}"); return; }
        }
        AddLog($"Connected  verified={_mem.Verified}");
        IsConnected = true;
        ConnStatus  = "● Connected";
        StatusText  = $"PS3 base 0x{Ps3Memory.PS3_BASE:X}  verified={_mem.Verified}";
    }

    // ── Disconnect ────────────────────────────────────────────────────────────
    public void Disconnect()
    {
        _searching  = false;   // unblock FindNodeAsync if mid-search
        AutoRefresh = false;
        StopLockTimer();
        StopFovyScanTimer();   // stop dangling FovY refresh timer
        _mem.Close();
        IsConnected = false;
        NodeFound   = false;
        ConnStatus  = "● Not connected";
        StatusText  = "Disconnected";
        NodeText    = "ChrFollowCam node: —";
        RebuildParams(false);
        AddLog("Disconnected");
    }

    // ── Params rebuild ────────────────────────────────────────────────────────
    // Returns the correct param table for the current mode.
    ParamDef[] ActiveDefs() => _debugEboot ? DebugParamDef.All : ParamDef.All;

    void RebuildParams(bool directMode)
    {
        var defs = ActiveDefs();
        _ui.Invoke(() =>
        {
            Params.Clear();
            foreach (var def in defs)
            {
                // Normal EBOOT: skip params without a known DirectOffset
                if (directMode && def.DirectOffset == 0) continue;
                var row = new ParamRow(def);
                if (directMode && _mem.FollowNode != 0)
                    row.Ps3Addr = _mem.FollowNode + def.DirectOffset + (uint)def.FieldOffset;
                Params.Add(row);
            }
        });

        // Debug EBOOT: resolve ptr-chain addresses in background
        if (!directMode && _mem.FollowNode != 0)
        {
            var rowSnapshot = Params.ToList();
            Task.Run(() =>
            {
                foreach (var row in rowSnapshot)
                {
                    if (row.Def.NodeOffset == 0) continue;
                    var ptr = _mem.GetParamPtr(row.Def.NodeOffset);
                    if (ptr.HasValue)
                        _ui.BeginInvoke(() => row.Ps3Addr = (uint)(ptr.Value + row.Def.FieldOffset));
                }
            });
        }
    }

    public void ResetUI()
    {
        StopLockTimer();
        _ui.Invoke(() =>
        {
            foreach (var r in Params) r.IsLocked = false;
        });
        RebuildParams(false);
        NodeFound = false;
        NodeText  = "ChrFollowCam node: —";
        AddLog("UI reset.");
    }

    static readonly string PresetPath = 
        System.IO.Path.Combine(AppContext.BaseDirectory, "preset.txt");

    /// <summary>
    /// Reads Name=Value pairs from preset.txt next to exe,
    /// sets Input and locks matching params.
    /// Format: one param per line, "ParamName = value", # for comments.
    /// </summary>
    public void LoadPresetFromFile()
    {
        if (!File.Exists(PresetPath))
        {
            AddLog($"preset.txt not found at {PresetPath}");
            return;
        }
        int loaded = 0, skipped = 0;
        try
        {
            var lines = File.ReadAllLines(PresetPath, System.Text.Encoding.UTF8);
            _ui.Invoke(() =>
            {
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                    var eq = trimmed.IndexOf('=');
                    if (eq < 0) continue;
                    var name  = trimmed[..eq].Trim();
                    var value = trimmed[(eq + 1)..].Trim();
                    var row   = Params.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (row == null) { skipped++; continue; }
                    row.Input    = value;
                    row.IsLocked = true;
                    loaded++;
                }
                if (Params.Any(p => p.IsLocked) && _lockTimer == null)
                    StartLockTimer();
            });
            AddLog($"Preset loaded: {loaded} locked, {skipped} skipped");
        }
        catch (Exception ex) { AddLog($"Load preset failed: {ex.Message}"); }
    }

    /// <summary>Writes or updates a single Name=Value entry in preset.txt.</summary>
    public void SaveParamToPreset(string name, string value)
    {
        try
        {
            var lines = File.Exists(PresetPath)
                ? File.ReadAllLines(PresetPath, System.Text.Encoding.UTF8).ToList()
                : new List<string>();

            // Update existing entry or append
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var eq = lines[i].IndexOf('=');
                if (eq < 0) continue;
                if (string.Equals(lines[i][..eq].Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{name} = {value}";
                    found = true;
                    break;
                }
            }
            if (!found) lines.Add($"{name} = {value}");
            File.WriteAllLines(PresetPath, lines, System.Text.Encoding.UTF8);
            AddLog($"Saved {name} = {value} → preset.txt");
        }
        catch (Exception ex) { AddLog($"Save preset failed: {ex.Message}"); }
    }

    // ── Find node ─────────────────────────────────────────────────────────────
    void FireFindNodeAsync()
    {
        _ = FindNodeAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                AddLog($"FindNodeAsync error: {t.Exception?.InnerException?.Message}");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
    public async Task FindNodeAsync(bool? debugEboot = null)
    {
        if (!IsConnected || _searching) return;
        // Throttle: at least 1s between automatic re-searches
        if ((DateTime.UtcNow - _lastSearchTime).TotalMilliseconds < 1000) return;
        // If caller specifies mode — update the stored flag; otherwise reuse it.
        if (debugEboot.HasValue)
            DebugEboot = debugEboot.Value;
        bool mode = _debugEboot;
        _searching = true;
        try
        {
            if (!NodeFound)
                NodeText = "ChrFollowCam node: searching…";
            AddLog($"Searching heap… (mode: {(mode ? "Debug EBOOT" : "Normal EBOOT")})");

            var progress = new Progress<string>(msg => AddLog($"  {msg}"));
            uint? node = await Task.Run(() => _mem.FindFollowNode(progress, mode));

            if (node.HasValue)
            {
                NodeFound = true;
                NodeText  = $"ChrFollowCam node: 0x{node.Value:X8}";
                AddLog($"Found: 0x{node.Value:X8}");

                // Save lock state before rebuild
                var lockSnapshot = Params
                    .Where(p => p.IsLocked && !string.IsNullOrWhiteSpace(p.Input))
                    .Select(p => (p.Def.Name, p.Input))
                    .ToList();

                // Rebuild param table for current mode (resets all rows)
                RebuildParams(_mem.DirectMode);

                // Restore lock state onto new rows
                if (lockSnapshot.Count > 0)
                {
                    _ui.Invoke(() =>
                    {
                        foreach (var (name, input) in lockSnapshot)
                        {
                            var row = Params.FirstOrDefault(p => p.Name == name);
                            if (row == null)
                            {
                                AddLog($"  ⚠ Lock lost: {name} not available in current mode");
                                continue;
                            }
                            row.Input    = input;
                            row.IsLocked = true;
                        }
                    });
                }

                // Re-apply locked values to new node
                var locked = Params
                    .Where(p => p.IsLocked && !string.IsNullOrWhiteSpace(p.Input))
                    .Select(p => (p.Def, Input: p.Input))   // snapshot on UI thread
                    .ToList();
                if (locked.Count > 0)
                {
                    if (_lockTimer == null) StartLockTimer();
                    AddLog($"Re-applying {locked.Count} locked value(s)…");
                    await Task.Run(() =>
                    {
                        foreach (var (def, input) in locked)
                            if (double.TryParse(input,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double val))
                                _mem.WriteParam(def.NodeOffset, def.Unit, val, def.FieldOffset, def.DirectOffset);
                    });
                }
                await RefreshAsync();
            }
            else
            {
                NodeText  = "ChrFollowCam node: NOT FOUND — will retry";
                AddLog("Node not found — will retry on next refresh", uiOnly: true);
                NodeFound = false;
                StopLockTimer();
            }
        }
        finally { _searching = false; _lastSearchTime = DateTime.UtcNow; }
    }

    // ── Refresh ───────────────────────────────────────────────────────────────
    public async Task RefreshAsync()
    {
        if (!IsConnected) return;
        // Check game window is still open (only matters when auto-refresh is on)
        if (_autoRefresh && !await Task.Run(() => Ps3Memory.IsGameWindowOpen()))
        {
            AddLog("Game window lost — stopping auto-refresh and clearing locks");
            AutoRefresh = false;
            StopLockTimer();
            foreach (var r in Params) r.IsLocked = false;
            return;
        }
        if (!NodeFound)
        {
            // Only search if the game window is actually open
            if (await Task.Run(() => Ps3Memory.IsGameWindowOpen()))
                if (!_searching) FireFindNodeAsync();
            return;
        }

        // Bulk read: node block + all param pages in one shot
        var (valid, vals, fovyOutOfRange) = await Task.Run(() =>
        {
            if (_mem.FollowNode == 0 || !_mem.IsOpen)
                return (false, (Dictionary<string,double?>?)null, false);

            // 1. Validate that FollowNode still points to a ChrFollowCam object
            //    by checking that its vtable pointer falls in the BSS/static range.
            //    After a level transition the old heap object is freed and the vtable
            //    will no longer be valid — this is the reliable signal to re-search.
            var vt = _mem.ReadU32(_mem.FollowNode);
            if (!vt.HasValue || vt.Value < Ps3Memory.PS3_BSS_LO || vt.Value >= Ps3Memory.PS3_BSS_HI)
                return (false, (Dictionary<string,double?>?)null, false);

            // 2. Bulk read all params using the active table
            var d = _mem.ReadParamsBulk(ActiveDefs());

            // 3. If range check is enabled, validate FovY to detect stale nodes
            bool badFovy = false;
            if (Ps3Memory.FovRangeCheckEnabled && d.TryGetValue("FovY", out var fv) && fv.HasValue)
            {
                float rad = (float)(fv.Value * Ps3Memory.DEG2RAD);
                if (rad < Ps3Memory.FOVY_MIN || rad > Ps3Memory.FOVY_MAX)
                    badFovy = true;
            }
            return (true, d, badFovy);
        });

        if (!valid || fovyOutOfRange)
        {
            if (fovyOutOfRange)
                AddLog("FovY out of range — re-searching…");
            else
                AddLog("Node invalidated — re-searching…");
            // Check if the RPCS3 process is still alive before re-searching.
            if (!await Task.Run(() => _mem.IsProcessAlive()))
            {
                AddLog("rpcs3.exe process lost — disconnecting");
                Disconnect();
                return;
            }
            NodeFound = false;
            NodeText  = "ChrFollowCam node: re-searching…";
            if (!_searching) FireFindNodeAsync();
            return;
        }

        if (vals != null)
            _ui.Invoke(() =>
            {
                foreach (var row in Params)
                    if (vals.TryGetValue(row.Name, out var v))
                        row.Live = v.HasValue ? $"{v.Value:G6}" : "err";
            });
    }

    // ── Write single ─────────────────────────────────────────────────────────
    public async Task WriteParamAsync(ParamRow row)
    {
        if (!IsConnected || !NodeFound) return;
        if (!double.TryParse(row.Input, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double val))
        {
            AddLog($"Invalid value for {row.Name}: '{row.Input}'");
            return;
        }
        bool ok = await Task.Run(() =>
            _mem.WriteParam(row.Def.NodeOffset, row.Def.Unit, val, row.Def.FieldOffset, row.Def.DirectOffset));
        AddLog($"{(ok ? "OK" : "FAIL")}  {row.Name} = {val:G6}");

        if (ok)
        {
            var readBack = await Task.Run(() =>
                _mem.ReadParam(row.Def.NodeOffset, row.Def.Unit, row.Def.FieldOffset, row.Def.DirectOffset));
            if (readBack.HasValue)
                _ui.Invoke(() => row.Live = $"{readBack.Value:G6}");
        }
    }

    // ── Reset all ─────────────────────────────────────────────────────────────
    public async Task ResetAllAsync()
    {
        if (!IsConnected || !NodeFound) return;
        AddLog("Resetting all…");
        var defs = Params.Select(p => p.Def).ToList();
        var results = await Task.Run(() =>
        {
            var lines = new List<string>();
            foreach (var def in defs)
            {
                bool ok = _mem.WriteParam(def.NodeOffset, def.Unit, def.Default, def.FieldOffset, def.DirectOffset);
                lines.Add($"{(ok?"OK":"FAIL")}  {def.Name} = {def.Default:G6}");
            }
            return lines;
        });
        foreach (var l in results) AddLog(l);
        await RefreshAsync();
        AddLog("Reset done.");
    }

    // ── Verify strings ───────────────────────────────────────────────────────
    public async Task<List<(string name, uint va, string val)>> VerifyStringsAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<(string, uint, string)>();
            var strings = new Dictionary<string, uint>
            {
                ["ChrCam"]       = 0x016C54A8,
                ["ChrFollowCam"] = 0x016C6CC0,
            };
            foreach (var kv in strings)
            {
                string s = _mem.ReadString(kv.Value) ?? "<err>";
                list.Add((kv.Key, kv.Value, s));
            }
            return list;
        });
    }

    // ── Binary search state ───────────────────────────────────────────────────
    int _bsLo = 0, _bsHi = 0;
    int _bsStep = 0;
    int _bsMid  = 0;   // current midpoint (set by last Left/Right press)
    static readonly double[] BS_FOV_SEQ = { 120, 60, 180, 90, 150, 75, 135, 45, 165 };

    public string BsStatusText { get; private set; } = "Set range in From..To, then Start";

    public void BsStart(int lo, int hi)
    {
        _bsLo = lo; _bsHi = hi; _bsStep = 0; _bsMid = (lo + hi) / 2;
        BsStatusText = $"Ready  [{_bsLo}..{_bsHi}]  size={_bsHi-_bsLo}  mid={_bsMid}";
        OnPC(nameof(BsStatusText));
        AddLog($"BinarySearch started: rows {_bsLo}..{_bsHi}");
    }

    double NextFov() { var d = BS_FOV_SEQ[_bsStep % BS_FOV_SEQ.Length]; _bsStep++; return d; }

    async Task BsWriteRange(int lo, int hi, string label)
    {
        double deg = NextFov();
        float rad = (float)(deg * Ps3Memory.DEG2RAD);
        var targets = FovyScanResults.Skip(lo).Take(hi - lo).ToList();
        var succeeded = await Task.Run(() =>
        {
            var ok = new List<FovyScanRow>();
            foreach (var r in targets) if (_mem.WriteF32(r.Addr, rad)) ok.Add(r);
            return ok;
        });
        _ui.Invoke(() => { foreach (var r in succeeded) r.LiveDeg = $"{deg:F0}°"; });
        AddLog($"BS {label}: wrote {deg:F0}° to rows {lo}..{hi} ({succeeded.Count}/{targets.Count} ok)");
    }

    public async Task BsLeft()
    {
        if (_bsHi - _bsLo <= 0) { AddLog("BS: range empty"); return; }
        _bsMid = (_bsLo + _bsHi) / 2;
        await BsWriteRange(_bsLo, _bsMid, "LEFT");
        BsStatusText = $"Wrote LEFT [{_bsLo}..{_bsMid}]. Changed? → Fix Left. No? → Try Right.  range=[{_bsLo}..{_bsHi}] size={_bsHi-_bsLo}";
        OnPC(nameof(BsStatusText));
    }

    public async Task BsRight()
    {
        if (_bsHi - _bsLo <= 0) { AddLog("BS: range empty"); return; }
        _bsMid = (_bsLo + _bsHi) / 2;
        await BsWriteRange(_bsMid, _bsHi, "RIGHT");
        BsStatusText = $"Wrote RIGHT [{_bsMid}..{_bsHi}]. Changed? → Fix Right. No? → Try Left.  range=[{_bsLo}..{_bsHi}] size={_bsHi-_bsLo}";
        OnPC(nameof(BsStatusText));
    }

    public void BsFixLeft()
    {
        // Left half had the effect → new range = [lo..mid]
        int oldHi = _bsHi;
        _bsHi = _bsMid;
        AddLog($"Fixed LEFT: range [{_bsLo}..{_bsHi}] (dropped [{_bsMid}..{oldHi}])");
        if (_bsHi - _bsLo <= 1)
            BsStatusText = $"🎯 FOUND! Row {_bsLo}  addr={(_bsLo < FovyScanResults.Count ? FovyScanResults[_bsLo].AddrHex : "?")}";
        else
            BsStatusText = $"Fixed LEFT → [{_bsLo}..{_bsHi}] size={_bsHi-_bsLo}. Press Left or Right to continue.";
        OnPC(nameof(BsStatusText));
    }

    public void BsFixRight()
    {
        // Right half had the effect → new range = [mid..hi]
        int oldLo = _bsLo;
        _bsLo = _bsMid;
        AddLog($"Fixed RIGHT: range [{_bsLo}..{_bsHi}] (dropped [{oldLo}..{_bsMid}])");
        if (_bsHi - _bsLo <= 1)
            BsStatusText = $"🎯 FOUND! Row {_bsLo}  addr={(_bsLo < FovyScanResults.Count ? FovyScanResults[_bsLo].AddrHex : "?")}";
        else
            BsStatusText = $"Fixed RIGHT → [{_bsLo}..{_bsHi}] size={_bsHi-_bsLo}. Press Left or Right to continue.";
        OnPC(nameof(BsStatusText));
    }

    public void BsReset()
    {
        _bsLo = _bsHi = _bsStep = _bsMid = 0;
        BsStatusText = "Reset. Set range and press Start.";
        OnPC(nameof(BsStatusText));
        AddLog("BinarySearch reset");
    }

    // ── FovY Scan ────────────────────────────────────────────────────────────
    DispatcherTimer? _fovyRefreshTimer;

    public async Task<int> ScanFovYAsync(Action<string> statusCallback, float minDeg = 42.9f, float maxDeg = 43.1f)
    {
        if (!IsConnected) { AddLog("Not connected"); return 0; }
        float minRad = (float)(minDeg / Ps3Memory.RAD2DEG);
        float maxRad = (float)(maxDeg / Ps3Memory.RAD2DEG);
        statusCallback($"Scanning 0x30000000..0x42000000 for FovY {minDeg:G4}°..{maxDeg:G4}°...");
        AddLog($"FovY scan started ({minDeg:G4}°..{maxDeg:G4}°)...");

        var hits = await Task.Run(() =>
        {
            var results = new List<(uint addr, float val, string ctx)>();
            const uint LO    = 0x30000000;
            const uint HI    = 0x42000000;
            const int  CHUNK = 0x200000;

            for (uint va = LO; va < HI; va += CHUNK)
            {
                var d = _mem.ReadBytes(va, (int)Math.Min(CHUNK, HI - va));
                if (d == null) continue;
                for (int i = 0; i + 4 <= d.Length; i += 4)
                {
                    float f = BitConverter.ToSingle(new byte[]{d[i+3],d[i+2],d[i+1],d[i]});
                    if (float.IsNaN(f) || f < minRad || f > maxRad) continue;
                    uint addr = (uint)(va + i);
                    // Context: nearby floats (big-endian: most-significant byte first)
                    string ctx = "";
                    if (i >= 4 && i + 8 <= d.Length)
                    {
                        float prev = BitConverter.ToSingle(new byte[]{d[i-4],d[i-3],d[i-2],d[i-1]});
                        float next = BitConverter.ToSingle(new byte[]{d[i+4],d[i+5],d[i+6],d[i+7]});
                        ctx = $"prev={prev:G4} next={next:G4}";
                    }
                    results.Add((addr, f, ctx));
                }
            }
            return results;
        });

        _ui.Invoke(() =>
        {
            FovyScanResults.Clear();
            foreach (var (addr, val, ctx) in hits)
                FovyScanResults.Add(new FovyScanRow
                {
                    Addr    = addr,
                    LiveDeg = $"{val * Ps3Memory.RAD2DEG:F1}°",
                    Context = ctx
                });
        });

        AddLog($"FovY scan done: {hits.Count} hits");
        statusCallback($"Found {hits.Count} addresses. CE address = PS3_addr + 0x300000000 (attach CE to rpcs3.exe).");

        // Start live refresh for scan results
        StopFovyScanTimer();
        _fovyRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _fovyRefreshTimer.Tick += async (_, _) => await RefreshFovyScanAsync();
        _fovyRefreshTimer.Start();

        return hits.Count;
    }

    public void StopFovyScanTimer()
    {
        _fovyRefreshTimer?.Stop();
        _fovyRefreshTimer = null;
    }

    async Task RefreshFovyScanAsync()
    {
        if (!IsConnected || FovyScanResults.Count == 0) return;
        var rows = FovyScanResults.ToList();
        var vals = await Task.Run(() =>
            rows.Select(r => (r.Addr, _mem.ReadF32(r.Addr))).ToList());
        _ui.Invoke(() =>
        {
            foreach (var (addr, val) in vals)
            {
                var row = FovyScanResults.FirstOrDefault(r => r.Addr == addr);
                if (row != null)
                    row.LiveDeg = val.HasValue ? $"{val.Value * Ps3Memory.RAD2DEG:F1}°" : "err";
            }
        });

        // Apply locks — only write if connection is alive.
        // Snapshot (addr, setValue) on the UI thread before Task.Run so we never
        // read INotifyPropertyChanged properties from a background thread.
        // Note: FovyScan addresses are raw heap pointers from a previous scan; after
        // a level transition they may point to freed memory. We don't have a per-row
        // vtable to validate, so we rely on NtWriteVirtualMemory returning an error
        // for unmapped pages (WriteF32 returns false) rather than crashing RPCS3.
        var lockedSnap = rows
            .Where(r => r.IsLocked && !string.IsNullOrWhiteSpace(r.SetValue))
            .Select(r => (r.Addr, r.SetValue))
            .ToList();
        if (lockedSnap.Count > 0 && _mem.IsOpen)
        {
            await Task.Run(() =>
            {
                foreach (var (addr, setValue) in lockedSnap)
                    if (double.TryParse(setValue,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double deg))
                        try { _mem.WriteF32(addr, (float)(deg * Ps3Memory.DEG2RAD)); }
                        catch { /* ignore write errors — stale address after teleport */ }
            });
        }
    }

    public async Task WriteFovYRangeAsync(double deg, int fromRow, int toRow)
    {
        if (!IsConnected || FovyScanResults.Count == 0) { AddLog("No results"); return; }
        float rad = (float)(deg * Ps3Memory.DEG2RAD);
        var targets = FovyScanResults.Skip(fromRow).Take(toRow - fromRow).ToList();
        if (targets.Count == 0) { AddLog($"No rows in range {fromRow}..{toRow}"); return; }
        string setStr = $"{deg:F1}";
        var succeeded = await Task.Run(() =>
        {
            var ok = new List<FovyScanRow>();
            foreach (var row in targets)
                if (_mem.WriteF32(row.Addr, rad)) ok.Add(row);
            return ok;
        });
        _ui.Invoke(() =>
        {
            foreach (var row in succeeded)
            {
                row.SetValue = setStr;
                row.LiveDeg  = $"{deg:F1}°";
            }
        });
        AddLog($"Write {deg:F1}° to rows {fromRow}..{toRow} ({succeeded.Count}/{targets.Count} ok)");
    }

    public async Task WriteFovYAllAsync(double deg, int count = int.MaxValue)
    {
        if (!IsConnected || FovyScanResults.Count == 0) { AddLog("No results to write"); return; }
        float rad = (float)(deg * Ps3Memory.DEG2RAD);
        var targets = FovyScanResults.Take(count).ToList();
        string setStr = $"{deg:F1}";
        var succeeded = await Task.Run(() =>
        {
            var ok = new List<FovyScanRow>();
            foreach (var row in targets)
                if (_mem.WriteF32(row.Addr, rad)) ok.Add(row);
            return ok;
        });
        _ui.Invoke(() =>
        {
            foreach (var row in succeeded)
            {
                row.SetValue = setStr;
                row.LiveDeg  = $"{deg:F1}°";
            }
        });
        AddLog($"Write {deg:F0}° to first {count} ({succeeded.Count}/{targets.Count} ok)");
    }

    public async Task WriteFovYRowAsync(FovyScanRow row)
    {
        if (!IsConnected) return;
        if (!double.TryParse(row.SetValue,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double deg))
        {
            AddLog($"Invalid value: '{row.SetValue}'"); return;
        }
        float rad = (float)(deg * Ps3Memory.DEG2RAD);
        bool ok = await Task.Run(() => _mem.WriteF32(row.Addr, rad));
        AddLog($"{(ok?"OK":"FAIL")} FovY @ 0x{row.Addr:X8} = {deg:F1}°");
        if (ok) row.LiveDeg = $"{deg:F1}°";
    }

    public void ToggleFovYLock(FovyScanRow row)
    {
        row.IsLocked = !row.IsLocked;
        if (row.IsLocked && string.IsNullOrWhiteSpace(row.SetValue) && row.LiveDeg != "—")
            row.SetValue = row.LiveDeg.TrimEnd('°');
        AddLog(row.IsLocked ? $"🔒 Locked FovY @ 0x{row.Addr:X8}" : $"🔓 Unlocked FovY @ 0x{row.Addr:X8}");
    }

    public async Task RunDiagnosticAsync()
    {
        if (!IsConnected) { AddLog("Not connected"); return; }
        AddLog("=== DIAGNOSTIC DUMP START ===");
        try
        {
            var lines = await Task.Run(() => _mem.DiagnosticDump());
            foreach (var l in lines) AddLog("  " + l);
        }
        catch (Exception ex) { AddLog($"  EXCEPTION: {ex.Message}"); }
        AddLog("=== DIAGNOSTIC DUMP END ===");
    }

    public async Task DumpFollowNodeAsync()
    {
        if (!IsConnected) { AddLog("Not connected"); return; }
        AddLog("=== FOLLOWNODE FLOAT DUMP ===");
        try
        {
            var lines = await Task.Run(() => _mem.DumpFollowNodeFloats());
            foreach (var l in lines) AddLog(l);
        }
        catch (Exception ex) { AddLog($"  EXCEPTION: {ex.Message}"); }
        AddLog("=== END ===");
    }

    /// <summary>
    /// Bulk-reads all params for the active mode and dumps every param with its
    /// current value (or ERR for failed reads) plus offset info.  Helps the user
    /// quickly identify which offsets are wrong in the current EBOOT.
    /// </summary>
    public async Task DumpErroredParamsAsync()
    {
        if (!IsConnected) { AddLog("Not connected"); return; }
        if (!NodeFound)
        {
            AddLog("Node not found — find it first.");
            return;
        }
        AddLog("=== PARAM DUMP START ===");
        try
        {
            var vals = await Task.Run(() => _mem.ReadParamsBulk(ActiveDefs()));
            if (vals == null) { AddLog("  ReadParamsBulk returned null"); return; }

            int total   = 0;
            int errored = 0;
            foreach (var kv in vals)
            {
                total++;
                var def = ActiveDefs().FirstOrDefault(d => d.Name == kv.Key);
                string modeInfo = "";
                if (def != null)
                    modeInfo = _mem.DirectMode
                        ? $"DO=0x{def.DirectOffset:X3}+{def.FieldOffset}"
                        : $"NO=0x{def.NodeOffset:X3}+{def.FieldOffset}";

                if (kv.Value.HasValue)
                {
                    string val = kv.Value.Value switch
                    {
                        < 1.0 => $"{kv.Value.Value:G6}",
                        < 100.0 => $"{kv.Value.Value:F4}",
                        _ => $"{kv.Value.Value:G6}"
                    };
                    AddLog($"  OK  {kv.Key,-36}  {modeInfo,-18}  {val,10}");
                }
                else
                {
                    errored++;
                    AddLog($"  ERR {kv.Key,-36}  {modeInfo,-18}  {"<null>",10}");
                }
            }

            AddLog($"  OK: {total - errored}/{total}  ERR: {errored}/{total}");
        }
        catch (Exception ex) { AddLog($"  EXCEPTION: {ex.Message}"); }
        AddLog("=== PARAM DUMP END ===");
    }

    /// <summary>
    /// Exhaustive BFS of the debug_menu tree from root, trying ALL 4-byte
    /// offsets as child pointers.  Logs every reachable node so the user
    /// can identify the correct tree offsets for their EBOOT version.
    /// </summary>
    public async Task DeepTreeAnalysisAsync()
    {
        if (!IsConnected) { AddLog("Not connected"); return; }
        AddLog("=== DEEP TREE ANALYSIS START ===");
        try
        {
            var lines = await Task.Run(() => _mem.DeepTreeAnalysis());
            foreach (var l in lines) AddLog(l);
        }
        catch (Exception ex) { AddLog($"  EXCEPTION: {ex.Message}"); }
        AddLog("=== DEEP TREE ANALYSIS END ===");
    }

    /// <summary>
    /// Reads every possible NO (0x050–0x2000, step 0x50) from the ptr-chain
    /// and logs which slots have valid value pointers and their current values.
    /// This discovers the exact parameter map of the debug_menu tree.
    /// </summary>
    public async Task ScanSlotsAsync()
    {
        if (!IsConnected) { AddLog("Not connected"); return; }
        AddLog("=== SLOT SCAN START ===");
        try
        {
            var lines = await Task.Run(() => _mem.ScanSlots());
            foreach (var l in lines) AddLog(l);
        }
        catch (Exception ex) { AddLog($"  EXCEPTION: {ex.Message}"); }
        AddLog("=== SLOT SCAN END ===");
    }

    /// <summary>
    /// Dumps a large memory region around the FOV value ptr to find the struct layout.
    /// </summary>
    public async Task ScanAllOffsetsAsync()
    {
        AddLog("=== SCAN ALL OFFSETS ===");
        try
        {
            var lines = await Task.Run(() => _mem.ScanAllOffsets());
            foreach (var l in lines) AddLog(l);
            AddLog("=== END SCAN ALL OFFSETS ===");
        }
        catch (Exception ex)
        {
            AddLog($"ERROR: {ex.Message}");
        }
    }

    public async Task DumpParamMemoryAsync()
    {
        if (!IsConnected) { AddLog("Not connected"); return; }
        AddLog("=== PARAM MEMORY DUMP START ===");
        try
        {
            var lines = await Task.Run(() => _mem.DumpParamMemory());
            foreach (var l in lines) AddLog(l);
        }
        catch (Exception ex) { AddLog($"  EXCEPTION: {ex.Message}"); }
        AddLog("=== PARAM MEMORY DUMP END ===");
    }

    // ── Auto-refresh timer ────────────────────────────────────────────────────
    void StartTimer()
    {
        StopTimer(); // guard against double-start
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
        {
            Interval = TimeSpan.FromMilliseconds(_refreshIntervalMs)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
    }

    void StopTimer()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    // ── Lock (freeze) ─────────────────────────────────────────────────────────
    public void ToggleLock(ParamRow row)
    {
        row.IsLocked = !row.IsLocked;

        if (row.IsLocked)
        {
            if (string.IsNullOrWhiteSpace(row.Input) && row.Live != "—" && row.Live != "err")
                row.Input = row.Live;
            AddLog($"🔒 Locked {row.Name} = {row.Input}");
        }
        else
        {
            AddLog($"🔓 Unlocked {row.Name}");
        }

        bool anyLocked = Params.Any(p => p.IsLocked);
        if (anyLocked && _lockTimer == null) StartLockTimer();
        else if (!anyLocked)               StopLockTimer();
    }

    void StartLockTimer()
    {
        _lockTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
        {
            Interval = TimeSpan.FromMilliseconds(_lockIntervalMs)
        };
        _lockTimer.Tick += async (_, _) => await WriteLocked();
        _lockTimer.Start();
    }

    void StopLockTimer()
    {
        _lockTimer?.Stop();
        _lockTimer = null;
    }

    public void SetLockInterval(int ms)
    {
        LockIntervalMs = ms;
        AddLog($"Lock interval set to {_lockIntervalMs} ms");
    }

    public void SetRefreshInterval(int ms)
    {
        RefreshIntervalMs = ms;
        AddLog($"Refresh interval set to {_refreshIntervalMs} ms");
    }

    // ── Settings persistence ──────────────────────────────────────────────────
    public void LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return;
        try
        {
            foreach (var line in File.ReadAllLines(SettingsPath, System.Text.Encoding.UTF8))
            {
                var t = line.Trim();
                if (t.StartsWith('#') || !t.Contains('=')) continue;
                var eq  = t.IndexOf('=');
                var key = t[..eq].Trim();
                var val = t[(eq + 1)..].Trim();
                switch (key)
                {
                    case "LockIntervalMs"    when int.TryParse(val, out int l) && l >= 1: _lockIntervalMs    = l; break;
                    case "RefreshIntervalMs" when int.TryParse(val, out int r) && r >= 1: _refreshIntervalMs = r; break;
                    case "FovRangeCheckEnabled" when bool.TryParse(val, out bool f): _fovRangeCheckEnabled = f; break;
                }
            }
            OnPC(nameof(LockIntervalMs));
            OnPC(nameof(RefreshIntervalMs));
        }
        catch (Exception ex) { AddLog($"LoadSettings error: {ex.Message}"); }
    }

    void SaveSettings()
    {
        try
        {
            File.WriteAllLines(SettingsPath,
            [
                "# DeSCam settings — auto-generated, do not edit manually",
                $"LockIntervalMs    = {_lockIntervalMs}",
                $"RefreshIntervalMs = {_refreshIntervalMs}",
                $"FovRangeCheckEnabled = {_fovRangeCheckEnabled}",
            ], System.Text.Encoding.UTF8);
        }
        catch (Exception ex) { AddLog($"SaveSettings error: {ex.Message}"); }
    }

    async Task WriteLocked()
    {
        if (!IsConnected || !NodeFound) return;

        // Snapshot input values on the UI thread before entering Task.Run
        var locked = Params
            .Where(p => p.IsLocked && !string.IsNullOrWhiteSpace(p.Input))
            .Select(p => (p.Def, Input: p.Input))
            .ToList();
        if (locked.Count == 0) return;

        bool nodeStillValid = await Task.Run(() => _mem.IsFollowNodeValid());
        if (!nodeStillValid)
        {
            // Check if RPCS3 is still alive before re-searching.
            if (!await Task.Run(() => _mem.IsProcessAlive()))
            {
                AddLog("Lock: rpcs3.exe process lost — disconnecting");
                Disconnect();
                return;
            }
            // Node was invalidated (teleport / level transition) — don't write stale
            // memory. Mark node as lost so RefreshAsync will trigger a re-search.
            AddLog("WriteLocked: node invalidated — aborting write, re-searching…");
            NodeFound = false;
            NodeText  = "ChrFollowCam node: re-searching…";
            if (!_searching) FireFindNodeAsync();
            return;
        }

        await Task.Run(() =>
        {
            foreach (var (def, input) in locked)
            {
                if (!double.TryParse(input,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double val)) continue;
                try
                {
                    _mem.WriteParam(def.NodeOffset, def.Unit, val, def.FieldOffset, def.DirectOffset);
                }
                catch { /* NtWriteVirtualMemory failure — node was freed mid-write */ }
            }
        });
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public void Dispose()
    {
        StopTimer();
        StopLockTimer();
        StopFovyScanTimer();
        _mem.Dispose();
    }
}
