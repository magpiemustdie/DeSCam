using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeSCam;

/// <summary>ViewModel for a single parameter row in the DataGrid.</summary>
public class ParamRow : INotifyPropertyChanged
{
    public ParamDef Def { get; }

    string _live  = "—";
    string _input = "";
    bool   _locked;
    uint   _ps3Addr;

    public string Name        => Def.Name;
    public string Description => Def.Description;
    public string Unit        => Def.UnitLabel;

    /// <summary>PS3 virtual address of this parameter (set after node is found).</summary>
    public uint Ps3Addr
    {
        get => _ps3Addr;
        set { _ps3Addr = value; OnPropertyChanged(); OnPropertyChanged(nameof(AddrHex)); OnPropertyChanged(nameof(AddrCE)); }
    }

    public string AddrHex => _ps3Addr == 0 ? "—" : $"0x{_ps3Addr:X8}";
    public string AddrCE  => _ps3Addr == 0 ? "—" : $"{Ps3Memory.PS3_BASE + _ps3Addr:X9}";

    public string Live
    {
        get => _live;
        set { _live = value; OnPropertyChanged(); }
    }

    public string Input
    {
        get => _input;
        set { _input = value; OnPropertyChanged(); }
    }

    /// <summary>When true the value is continuously written to memory (freeze).</summary>
    public bool IsLocked
    {
        get => _locked;
        set { _locked = value; OnPropertyChanged(); OnPropertyChanged(nameof(LockLabel)); }
    }

    public string LockLabel => _locked ? "🔒" : "🔓";

    public ParamRow(ParamDef def) => Def = def;

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
