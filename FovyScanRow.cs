using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeSCam;

/// <summary>One row in the FovY scan results table.</summary>
public class FovyScanRow : INotifyPropertyChanged
{
    public uint   Addr    { get; init; }
    public string AddrHex  => $"0x{Addr:X8}";
    public string AddrCE   => $"{Ps3Memory.PS3_BASE + Addr:X9}";  // RPCS3 host address for CE

    string _liveDeg  = "—";
    string _setValue = "";
    bool   _isLocked;
    string _context  = "";

    public string LiveDeg
    {
        get => _liveDeg;
        set { _liveDeg = value; OnPC(); }
    }

    public string SetValue
    {
        get => _setValue;
        set { _setValue = value; OnPC(); }
    }

    public bool IsLocked
    {
        get => _isLocked;
        set { _isLocked = value; OnPC(); OnPC(nameof(LockLabel)); }
    }

    public string LockLabel => _isLocked ? "🔒" : "🔓";

    public string Context
    {
        get => _context;
        set { _context = value; OnPC(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
