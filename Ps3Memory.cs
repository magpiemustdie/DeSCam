using System.Runtime.InteropServices;
using System.Text;

namespace DeSCam;

/// <summary>
/// Low-level PS3/RPCS3 memory access via NtReadVirtualMemory/NtWriteVirtualMemory.
/// PS3 RAM is PAGE_NOACCESS — ReadProcessMemory fails, Nt* works.
/// All PS3 values are Big-Endian.
/// </summary>
public sealed class Ps3Memory : IDisposable
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────
    [DllImport("ntdll.dll")]
    static extern int NtReadVirtualMemory(
        nint hProcess, nint baseAddr, byte[] buf, nint size, out nint read);

    [DllImport("ntdll.dll")]
    static extern int NtWriteVirtualMemory(
        nint hProcess, nint baseAddr, byte[] buf, nint size, out nint written);

    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] static extern bool Process32First(nint snap, ref PE32 e);
    [DllImport("kernel32.dll")] static extern bool Process32Next(nint snap,  ref PE32 e);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct PE32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int  pcPriClassBase, dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    // ── Constants ─────────────────────────────────────────────────────────────
    const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    const uint TH32CS_SNAPPROCESS = 2;
    const int  PAGE               = 4096;

    public const long   PS3_BASE         = 0x300000000L;
    public const uint   PS3_CHRCAM_STR   = 0x016C54A8;
    public const uint   PS3_HEAP_START   = 0x38000000;
    public const uint   PS3_HEAP_END     = 0x40000000;
    public const uint   PS3_BSS_LO       = 0x01840000;
    public const uint   PS3_BSS_HI       = 0x02000000;
    public const uint   CHRCAM_NEEDLE    = 0x016C5420;
    public const int    PARAM_VALPTR_OFF = 0x30;
    public const double RAD2DEG          = 57.295780181884766;
    public const double DEG2RAD          = 1.0 / RAD2DEG;

    // debug_menu chain (BLUS30443v100): *(r2-0x7F98) chain
    const uint TOC_STEP1 = 0x019AD0A8;

    // ── State ─────────────────────────────────────────────────────────────────
    nint _handle;
    public int  Pid        { get; private set; }
    public bool Verified   { get; private set; }
    public uint FollowNode { get; private set; }
    public bool IsOpen     => _handle != nint.Zero;

    /// <summary>
    /// True  = normal EBOOT: params are direct floats in FollowNode body (DirectOffset).
    /// False = debug EBOOT:  params are accessed via ptr-chain (NodeOffset+PARAM_VALPTR_OFF).
    /// </summary>
    public bool DirectMode { get; private set; }

    // Reusable 4-byte buffer for scalar reads — avoids repeated GC allocs
    readonly byte[] _buf4 = new byte[4];

    // ── Process helpers ───────────────────────────────────────────────────────
    public static int FindPid(string name)
    {
        nint snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == -1) return 0;
        var e = new PE32 { dwSize = (uint)Marshal.SizeOf<PE32>() };
        try
        {
            if (!Process32First(snap, ref e)) return 0;
            do
            {
                if (string.Equals(e.szExeFile, name, StringComparison.OrdinalIgnoreCase))
                    return (int)e.th32ProcessID;
            } while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return 0;
    }

    public bool Open(int pid)
    {
        Close();
        nint h = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
        if (h == nint.Zero) return false;
        _handle = h;
        Pid     = pid;
        var s   = ReadString(PS3_CHRCAM_STR, 7);
        Verified = s?.StartsWith("ChrCam") == true;
        return true;
    }

    public void Close()
    {
        if (_handle != nint.Zero) { CloseHandle(_handle); _handle = nint.Zero; }
        Pid = 0; Verified = false; FollowNode = 0; DirectMode = false;
    }

    // ── Raw read/write ────────────────────────────────────────────────────────
    /// <summary>Reads <paramref name="size"/> bytes from PS3 virtual address.</summary>
    public byte[]? ReadBytes(uint ps3Va, int size)
    {
        if (!IsOpen) return null;
        var buf = new byte[size];
        int off = 0;
        while (off < size)
        {
            int  chunk  = Math.Min(PAGE, size - off);
            nint addr   = (nint)(PS3_BASE + ps3Va + off);
            // Slice of buf as target — avoid extra tmp allocation
            var  slice  = new byte[chunk];
            if (NtReadVirtualMemory(_handle, addr, slice, chunk, out nint rd) == 0 && rd > 0)
                Buffer.BlockCopy(slice, 0, buf, off, (int)rd);
            off += chunk;
        }
        return buf;
    }

    public bool WriteBytes(uint ps3Va, byte[] data)
        => IsOpen &&
           NtWriteVirtualMemory(_handle, (nint)(PS3_BASE + ps3Va), data, data.Length, out _) == 0;

    // ── Typed helpers (Big-Endian) ────────────────────────────────────────────
    // Use _buf4 for scalar reads to avoid per-call allocation
    bool ReadRaw4(uint ps3Va)
    {
        if (!IsOpen) return false;
        nint addr = (nint)(PS3_BASE + ps3Va);
        return NtReadVirtualMemory(_handle, addr, _buf4, 4, out nint rd) == 0 && rd == 4;
    }

    public uint? ReadU32(uint ps3Va)
    {
        if (!ReadRaw4(ps3Va)) return null;
        if (BitConverter.IsLittleEndian)
        {
            byte t = _buf4[0]; _buf4[0] = _buf4[3]; _buf4[3] = t;
            t = _buf4[1]; _buf4[1] = _buf4[2]; _buf4[2] = t;
        }
        return BitConverter.ToUInt32(_buf4, 0);
    }

    public float? ReadF32(uint ps3Va)
    {
        if (!ReadRaw4(ps3Va)) return null;
        if (BitConverter.IsLittleEndian)
        {
            byte t = _buf4[0]; _buf4[0] = _buf4[3]; _buf4[3] = t;
            t = _buf4[1]; _buf4[1] = _buf4[2]; _buf4[2] = t;
        }
        float v = BitConverter.ToSingle(_buf4, 0);
        return float.IsNaN(v) || float.IsInfinity(v) ? null : v;
    }

    public bool WriteF32(uint ps3Va, float value)
    {
        var b = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        return WriteBytes(ps3Va, b);
    }

    public string? ReadString(uint ps3Va, int maxLen = 32)
    {
        var b = ReadBytes(ps3Va, maxLen);
        if (b == null) return null;
        int end = Array.IndexOf(b, (byte)0);
        return Encoding.ASCII.GetString(b, 0, end >= 0 ? end : maxLen);
    }

    // ── ChrFollowCam node — stable debug_menu chain ───────────────────────────
    // my_get_debug_menu (BLUS30443v100):
    //   step1 = u32 @ PS3:0x019AD0A8          (static .data)
    //   step2 = u32 @ (step1 - 0x7FFC)        (static .bss ptr)
    //   dm    = u32 @ step2                    (debug_menu heap addr)
    //   root  = u32 @ (dm + 0x04)
    //   Walk tree to find node with CHRCAM_NEEDLE at +0x10 → ChrCam node
    //   follow = u32 @ (ChrCam + 0x1C)

    uint? FindFollowNodeViaDebugMenu(IProgress<string>? progress)
    {
        FollowNode = 0;

        var step1 = ReadU32(TOC_STEP1);
        if (!step1.HasValue || step1.Value < PS3_BSS_LO || step1.Value >= PS3_BSS_HI)
        {
            progress?.Report($"Step1 invalid: 0x{step1.GetValueOrDefault():X8}");
            return null;
        }

        var step2 = ReadU32(step1.Value - 0x7FFCu);
        if (!step2.HasValue) { progress?.Report("Step2 failed"); return null; }

        var dm = ReadU32(step2.Value);
        if (!dm.HasValue || dm.Value < 0x30000000 || dm.Value >= 0x50000000u)
        {
            progress?.Report($"debug_menu invalid: 0x{dm.GetValueOrDefault():X8}");
            return null;
        }
        progress?.Report($"debug_menu=0x{dm.Value:X8}");

        var root = ReadU32(dm.Value + 0x04);
        if (!root.HasValue || root.Value < 0x30000000)
        {
            progress?.Report($"root_node invalid: 0x{root.GetValueOrDefault():X8}");
            return null;
        }
        progress?.Report($"root_node=0x{root.Value:X8}");

        var chrcam = FindChrCamInTree(root.Value, progress, 0, new HashSet<uint>());
        if (!chrcam.HasValue) { progress?.Report("ChrCam not found in tree"); return null; }

        var follow = ReadU32(chrcam.Value + 0x1C);
        if (!follow.HasValue || follow.Value < 0x30000000 || follow.Value >= 0x50000000u)
        {
            progress?.Report($"ChrFollowCam invalid: 0x{follow.GetValueOrDefault():X8}");
            return null;
        }
        progress?.Report($"ChrCam=0x{chrcam.Value:X8}  ChrFollowCam=0x{follow.Value:X8}");
        FollowNode = follow.Value;
        return follow;
    }

    uint? FindChrCamInTree(uint va, IProgress<string>? prog, int depth, HashSet<uint> visited)
    {
        if (depth > 8 || !visited.Add(va) || va < 0x30000000 || va >= 0x50000000u)
            return null;

        var data = ReadBytes(va, 0x20);
        if (data == null || data.Length < 0x14) return null;

        // Check +0x10 for CHRCAM_NEEDLE (big-endian)
        var tmp = data[0x10..0x14];
        if (BitConverter.IsLittleEndian) Array.Reverse(tmp);
        if (BitConverter.ToUInt32(tmp, 0) == CHRCAM_NEEDLE) return va;

        // Walk children at +0x04, +0x08, +0x0C, +0x1C
        foreach (int co in new[] { 0x1C, 0x04, 0x08, 0x0C })
        {
            var cb = data[co..(co + 4)];
            if (BitConverter.IsLittleEndian) Array.Reverse(cb);
            uint child = BitConverter.ToUInt32(cb, 0);
            if (child < 0x30000000 || child >= 0x50000000u) continue;
            var found = FindChrCamInTree(child, prog, depth + 1, visited);
            if (found.HasValue) return found;
        }
        return null;
    }

    // ── Heap-scan: needle search (original Python algorithm) ─────────────────
    // Scan 0x38000000..0x40000000 for CHRCAM_NEEDLE bytes,
    // then validate vtable and extract follow node.
    uint? FindFollowNodePtrChain(IProgress<string>? progress)
    {
        FollowNode = 0; DirectMode = false;
        const int  CHUNK = 0x80000;
        const uint LO    = PS3_HEAP_START; // 0x38000000
        const uint HI    = PS3_HEAP_END;   // 0x40000000

        // Needle = BE bytes of CHRCAM_NEEDLE (0x016C5420)
        byte n0=0x01, n1=0x6C, n2=0x54, n3=0x20;

        for (uint va = LO; va < HI; va += CHUNK)
        {
            var d = ReadBytes(va, (int)Math.Min(CHUNK, HI - va));
            if (d == null) continue;

            int off = 0;
            while (true)
            {
                // Find needle in chunk
                int idx = -1;
                for (int j = off; j <= d.Length - 4; j++)
                {
                    if (d[j]==n0 && d[j+1]==n1 && d[j+2]==n2 && d[j+3]==n3)
                    { idx = j; break; }
                }
                if (idx < 0) break;

                if (idx % 4 == 0 && idx >= 0x10)
                {
                    // ChrCam node starts at chunk_base + idx - 0x10
                    uint chrcam = (uint)(va + idx - 0x10);
                    var vt = ReadU32(chrcam);
                    if (vt.HasValue && vt.Value >= PS3_BSS_LO && vt.Value < PS3_BSS_HI)
                    {
                        var follow = ReadU32(chrcam + 0x1C);
                        if (follow.HasValue && follow.Value >= LO && follow.Value < HI)
                        {
                            progress?.Report($"ChrCam=0x{chrcam:X8}  ChrFollowCam=0x{follow.Value:X8}");
                            DirectMode = false;
                            FollowNode = follow.Value;
                            return follow;
                        }
                    }
                }
                off = idx + 4;
            }
        }
        return null;
    }

    // ── Normal EBOOT direct-float scan ────────────────────────────────────────
    // CE analysis (confirmed): mov ecx,[rax+rbx+50]
    //   rax = object with vtable 0x01856018, FovY at rax+0x50
    //   rbx = 0x300000000 (PS3_BASE, constant)
    // ChrCam vtable=0x01AD6B68, at ChrCam+0x018 → ptr to sub-object (vt=0x01863EB8)
    // sub-object has sub-sub-objects; one of them has vtable 0x01856018 with FovY@+0x50
    uint? FindFollowNodeNormalEboot(IProgress<string>? progress)
    {
        FollowNode = 0; DirectMode = false;
        const uint LO    = 0x30000000;
        const uint HI    = 0x42000000;
        const int  CHUNK = 0x100000;

        // Strategy 1: direct scan for vtable 0x01856018 → FovY at +0x50
        // This is the exact object accessed by the game's mov ecx,[rax+rbx+50]
        const uint TARGET_VT = 0x01856018;
        byte t0=unchecked((byte)(TARGET_VT>>24)), t1=unchecked((byte)(TARGET_VT>>16)),
             t2=unchecked((byte)(TARGET_VT>>8)),  t3=unchecked((byte)TARGET_VT);

        for (uint va = LO; va < HI; va += CHUNK)
        {
            var d = ReadBytes(va, CHUNK);
            if (d == null) continue;
            for (int i = 0; i + 0x54 < d.Length; i += 4)
            {
                if (d[i]!=t0||d[i+1]!=t1||d[i+2]!=t2||d[i+3]!=t3) continue;
                float f50 = BitConverter.ToSingle(new byte[]{d[i+0x53],d[i+0x52],d[i+0x51],d[i+0x50]});
                if (float.IsNaN(f50) || f50 < 0.1f || f50 > 3.1f) continue;
                uint cand = (uint)(va + i);
                progress?.Report($"Node=0x{cand:X8}  vt=0x{TARGET_VT:X8}  FovY@+0x50={f50*RAD2DEG:F1}°");
                DirectMode = true;
                FollowNode = cand;
                return cand;
            }
        }

        // Strategy 2: ChrCam vtable 0x01AD6B68 → follow at +0x1C → FovY at follow+0x1B0
        progress?.Report("vtable 0x01856018 not found — trying ChrCam chain");
        const uint CHRCAM_VT = 0x01AD6B68;
        byte c0=unchecked((byte)(CHRCAM_VT>>24)), c1=unchecked((byte)(CHRCAM_VT>>16)),
             c2=unchecked((byte)(CHRCAM_VT>>8)),  c3=unchecked((byte)CHRCAM_VT);

        for (uint va = LO; va < HI; va += CHUNK)
        {
            var d = ReadBytes(va, CHUNK);
            if (d == null) continue;
            for (int i = 0; i + 0x20 < d.Length; i += 4)
            {
                if (d[i]!=c0||d[i+1]!=c1||d[i+2]!=c2||d[i+3]!=c3) continue;
                int fo = i + 0x1C;
                if (fo + 4 > d.Length) continue;
                uint follow = (uint)(d[fo]<<24|d[fo+1]<<16|d[fo+2]<<8|d[fo+3]);
                if (follow < LO || follow >= HI) continue;
                var followVtCheck = ReadU32(follow);
                if (!followVtCheck.HasValue || followVtCheck.Value < PS3_BSS_LO || followVtCheck.Value >= PS3_BSS_HI) continue;
                var fovy = ReadF32(follow + 0x1B0);
                if (!fovy.HasValue || fovy.Value < 0.1f || fovy.Value > 3.1f) continue;
                uint cand = (uint)(va + i);
                progress?.Report($"ChrCam=0x{cand:X8}  Follow=0x{follow:X8}  FovY@+0x1B0={fovy.Value*RAD2DEG:F1}°");
                DirectMode = true;
                FollowNode = follow;
                return follow;
            }
        }

        progress?.Report("not found in 0x30M..0x42M");
        return null;
    }

    // ── Old heap scan (kept for reference, not used) ──────────────────────────
    uint? FindFollowNodeHeapScan(IProgress<string>? progress, bool debugEboot = false)
        => debugEboot ? FindFollowNodePtrChain(progress) : FindFollowNodeNormalEboot(progress);

    /// <summary>
    /// Dumps all non-zero floats from FollowNode+0 to FollowNode+0x800.
    /// Used to map parameter offsets for normal EBOOT.
    /// </summary>
    public List<string> DumpFollowNodeFloats()
    {
        var lines = new List<string>();
        if (!IsOpen || FollowNode == 0) { lines.Add("FollowNode not set"); return lines; }

        lines.Add($"FollowNode = 0x{FollowNode:X8}  DirectMode={DirectMode}");
        var data = ReadBytes(FollowNode, 0x800);
        if (data == null) { lines.Add("Read failed"); return lines; }

        for (int i = 0; i + 4 <= data.Length; i += 4)
        {
            float f = BitConverter.ToSingle(new byte[]{data[i+3],data[i+2],data[i+1],data[i]});
            if (float.IsNaN(f) || float.IsInfinity(f) || f == 0f) continue;
            if (Math.Abs(f) < 1e-6f || Math.Abs(f) > 1e6f) continue;
            lines.Add($"  +0x{i:X3} = {f:G6}");
        }
        return lines;
    }

    /// <summary>Finds ChrFollowCam node.</summary>
    public uint? FindFollowNode(IProgress<string>? progress = null, bool debugEboot = false)
    {
        // Debug EBOOT: try debug_menu chain first
        if (debugEboot)
        {
            var fast = FindFollowNodeViaDebugMenu(progress);
            if (fast.HasValue) return fast;
            progress?.Report("Debug menu path failed — heap scan fallback…");
        }
        // Needle scan 0x38M..0x40M (works with debug EBOOT)
        var result = FindFollowNodePtrChain(progress);
        if (result.HasValue) return result;
        // Normal EBOOT: wider needle scan
        if (!debugEboot)
            return FindFollowNodeNormalEboot(progress);
        progress?.Report("Heap scan done — NOT FOUND");
        return null;
    }

    /// <summary>
    /// Targeted diagnostic: walks chain, dumps found node neighbours,
    /// searches ~0.75 rad float only in small known regions.
    /// </summary>
    public List<string> DiagnosticDump()
    {
        var lines = new List<string>();
        if (!IsOpen) { lines.Add("Not open"); return lines; }

        // 1. Known strings
        lines.Add($"ChrCam str @ 0x016C54A8 = \"{ReadString(0x016C54A8, 8)}\"");

        // 2. Full chain walk
        var step1 = ReadU32(TOC_STEP1);
        lines.Add($"step1 @ 0x{TOC_STEP1:X8} = 0x{step1.GetValueOrDefault():X8}");
        if (!step1.HasValue || step1.Value < PS3_BSS_LO || step1.Value >= PS3_BSS_HI)
        { lines.Add("step1 invalid — chain aborted"); goto scanFloat; }

        uint s2addr = step1.Value - 0x7FFCu;
        var step2 = ReadU32(s2addr);
        lines.Add($"step2 @ 0x{s2addr:X8} = 0x{step2.GetValueOrDefault():X8}");
        if (!step2.HasValue || step2.Value < 0x01000000) goto scanFloat;

        var dmVal = ReadU32(step2.Value);
        lines.Add($"dm   @ 0x{step2.Value:X8} = 0x{dmVal.GetValueOrDefault():X8}");
        if (!dmVal.HasValue || dmVal.Value < 0x20000000 || dmVal.Value >= 0x60000000) goto scanFloat;

        // 3. Dump dm[0..0x4F]
        {
            var dmData = ReadBytes(dmVal.Value, 0x50);
            if (dmData != null)
            {
                var sb = new System.Text.StringBuilder($"dm[0..4F] @ 0x{dmVal.Value:X8}: ");
                for (int i = 0; i < dmData.Length; i += 4)
                    sb.Append($"{dmData[i]:X2}{dmData[i+1]:X2}{dmData[i+2]:X2}{dmData[i+3]:X2} ");
                lines.Add(sb.ToString());
            }

            // Walk all heap ptrs in dm block, check FovY chain on each
            lines.Add("--- dm ptr walk ---");
            if (dmData != null)
                for (int off = 0; off + 4 <= dmData.Length; off += 4)
                {
                    uint p = (uint)(dmData[off]<<24|dmData[off+1]<<16|dmData[off+2]<<8|dmData[off+3]);
                    if (p < 0x30000000 || p >= 0x42000000) continue;
                    var fPtr = ReadU32(p + 0x0050 + PARAM_VALPTR_OFF);
                    if (!fPtr.HasValue || fPtr.Value < 0x10000000 || fPtr.Value > 0x50000000u) continue;
                    var fv = ReadF32(fPtr.Value);
                    if (!fv.HasValue || fv.Value < 0.1f || fv.Value > 3.5f) continue;
                    var vt = ReadU32(p);
                    lines.Add($"  dm[+0x{off:X2}]=0x{p:X8} vt=0x{vt.GetValueOrDefault():X8} FovY={fv.Value*RAD2DEG:F1}°");
                }
        }

        scanFloat:
        // 4. Read 0x200 bytes around current FollowNode (if set) — dump all floats
        if (FollowNode != 0)
        {
            lines.Add($"--- FollowNode 0x{FollowNode:X8} dump ---");
            var nd = ReadBytes(FollowNode, 0x100);
            if (nd != null)
            {
                var sb2 = new System.Text.StringBuilder();
                for (int i = 0; i + 4 <= nd.Length; i += 4)
                {
                    float f = BitConverter.ToSingle(new byte[]{nd[i+3],nd[i+2],nd[i+1],nd[i]});
                    if (!float.IsNaN(f) && !float.IsInfinity(f) && f != 0f && Math.Abs(f) < 1e6f)
                        sb2.Append($"+0x{i:X2}={f:G4} ");
                }
                lines.Add(sb2.ToString());
            }
        }

        // 5. Targeted float scan: only check 4K pages that contain any non-zero data
        //    near follow node and around 0x38B36F40 (the heap ptr we saw in dm)
        lines.Add("--- FovY ~0.75 targeted scan ---");
        uint[] checkBases = { 0x30DE7000, 0x38B36000, 0x37BEC000, 0x38741000 };
        foreach (var b in checkBases)
        {
            var pg = ReadBytes(b, 0x1000);
            if (pg == null) continue;
            for (int i = 0; i + 4 <= pg.Length; i += 4)
            {
                float f = BitConverter.ToSingle(new byte[]{pg[i+3],pg[i+2],pg[i+1],pg[i]});
                if (f >= 0.73f && f <= 0.77f)
                    lines.Add($"  0x{b+i:X8} = {f:F4} rad ({f*RAD2DEG:F1}°)");
            }
        }

        // 5. Targeted vtable scan: find ChrCam by vtable 0x01AD6B68
        lines.Add("--- Scan for ChrCam vtable 0x01AD6B68 ---");
        {
            byte n0=0x01,n1=0xAD,n2=0x6B,n3=0x68;
            var candidates = new List<(uint chrCam, uint follow, uint fvt)>();
            for (uint scanVa = 0x30000000; scanVa < 0x42000000; scanVa += 0x100000)
            {
                var blk = ReadBytes(scanVa, 0x100000);
                if (blk == null) continue;
                for (int i = 0; i + 0x20 < blk.Length; i += 4)
                {
                    if (blk[i]!=n0||blk[i+1]!=n1||blk[i+2]!=n2||blk[i+3]!=n3) continue;
                    uint cand = (uint)(scanVa + i);
                    int fi = i + 0x1C;
                    if (fi + 4 > blk.Length) continue;
                    uint follow = (uint)(blk[fi]<<24|blk[fi+1]<<16|blk[fi+2]<<8|blk[fi+3]);
                    if (follow < 0x30000000 || follow >= 0x60000000) continue;
                    var fvt = ReadU32(follow);
                    if (!fvt.HasValue || fvt.Value < PS3_BSS_LO || fvt.Value >= PS3_BSS_HI) continue;
                    if (!candidates.Any(x => x.follow == follow))
                        candidates.Add((cand, follow, fvt.Value));
                }
            }
            lines.Add($"  Found {candidates.Count} unique follow candidates");

            // For best candidate: dump ChrCam body AND follow body fully
            foreach (var (chrCam, follow, fvt) in candidates.Take(1))
            {
                lines.Add($"  ChrCam=0x{chrCam:X8} follow=0x{follow:X8} fvt=0x{fvt:X8}");

                // Dump ChrCam body floats (parameters may live here directly)
                var chrCamData = ReadBytes(chrCam, 0x800);
                if (chrCamData != null)
                {
                    var sb1 = new System.Text.StringBuilder("  ChrCam floats[0.01..200]: ");
                    for (int i = 0; i + 4 <= chrCamData.Length; i += 4)
                    {
                        float f = BitConverter.ToSingle(new byte[]{chrCamData[i+3],chrCamData[i+2],chrCamData[i+1],chrCamData[i]});
                        if (!float.IsNaN(f) && !float.IsInfinity(f) && f >= 0.01f && f <= 200f)
                            sb1.Append($"+{i:X3}={f:G4} ");
                    }
                    lines.Add(sb1.ToString());
                }

                // Also check: ChrCam might have sub-ptrs that point to param blocks
                // Scan each ptr in ChrCam[0..0x100] and check if target has ~0.75
                if (chrCamData != null)
                {
                    for (int i = 0; i + 4 <= Math.Min(0x100, chrCamData.Length); i += 4)
                    {
                        uint p = (uint)(chrCamData[i]<<24|chrCamData[i+1]<<16|chrCamData[i+2]<<8|chrCamData[i+3]);
                        if (p < 0x30000000 || p >= 0x42000000) continue;
                        var fv = ReadF32(p);
                        if (fv.HasValue && fv.Value >= 0.70f && fv.Value <= 0.80f)
                            lines.Add($"  ChrCam[+0x{i:X3}]=0x{p:X8} direct fovy={fv.Value*RAD2DEG:F1}°");
                        // Also check +0x00 of sub-obj
                        var subvt = ReadU32(p);
                        if (!subvt.HasValue || subvt.Value < PS3_BSS_LO || subvt.Value >= PS3_BSS_HI) continue;
                        var nd2 = ReadBytes(p, 0x400);
                        if (nd2 == null) continue;
                        for (int j = 0; j + 4 <= nd2.Length; j += 4)
                        {
                            float f2 = BitConverter.ToSingle(new byte[]{nd2[j+3],nd2[j+2],nd2[j+1],nd2[j]});
                            if (f2 >= 0.70f && f2 <= 0.80f)
                                lines.Add($"  sub[ChrCam+0x{i:X3}]+0x{j:X3} = {f2:F4}rad ({f2*RAD2DEG:F1}°)  subvt=0x{subvt.Value:X8}");
                        }
                    }
                }
            }
        }
        return lines;
    }

    // ── Parameter access ──────────────────────────────────────────────────────
    public uint? GetParamPtr(uint nodeOffset)
    {
        if (FollowNode == 0) return null;
        var v = ReadU32(FollowNode + nodeOffset + PARAM_VALPTR_OFF);
        return v is { } val && val != 0 ? val : null;
    }

    public double? ReadParam(uint nodeOffset, string unit, int fieldOffset = 0, uint directOffset = 0)
    {
        float? raw;
        if (DirectMode && directOffset != 0)
        {
            raw = ReadF32(FollowNode + directOffset + (uint)fieldOffset);
        }
        else
        {
            var ptr = GetParamPtr(nodeOffset);
            if (ptr is null) return null;
            raw = ReadF32((uint)(ptr.Value + fieldOffset));
        }
        if (raw is null) return null;
        return unit == "rad" ? raw.Value * RAD2DEG : raw.Value;
    }

    public bool WriteParam(uint nodeOffset, string unit, double display, int fieldOffset = 0, uint directOffset = 0)
    {
        float store = unit == "rad" ? (float)(display * DEG2RAD) : (float)display;
        if (DirectMode && directOffset != 0)
            return WriteF32(FollowNode + directOffset + (uint)fieldOffset, store);
        var ptr = GetParamPtr(nodeOffset);
        if (ptr is null) return false;
        return WriteF32((uint)(ptr.Value + fieldOffset), store);
    }

    /// <summary>
    /// Bulk read: direct mode reads floats straight from nodeBlock;
    /// ptr-chain mode groups live-ptr reads by 64-byte page.
    /// </summary>
    public Dictionary<string, double?> ReadParamsBulk(ParamDef[] defs)
    {
        var result = new Dictionary<string, double?>(defs.Length);
        if (FollowNode == 0 || !IsOpen)
        {
            foreach (var d in defs) result[d.Name] = null;
            return result;
        }

        const int blockSize = 0x800;
        var nodeBlock = ReadBytes(FollowNode, blockSize);
        if (nodeBlock == null)
        {
            foreach (var d in defs) result[d.Name] = null;
            return result;
        }

        if (DirectMode)
        {
            // Direct mode: float at FollowNode + DirectOffset + FieldOffset
            foreach (var def in defs)
            {
                if (def.DirectOffset == 0) { result[def.Name] = null; continue; }
                int off = (int)def.DirectOffset + def.FieldOffset;
                if (off + 4 > nodeBlock.Length) { result[def.Name] = null; continue; }
                Span<byte> fb = nodeBlock.AsSpan(off, 4);
                float raw = BitConverter.IsLittleEndian
                    ? BitConverter.ToSingle(new byte[] { fb[3], fb[2], fb[1], fb[0] })
                    : BitConverter.ToSingle(fb);
                if (float.IsNaN(raw) || float.IsInfinity(raw)) { result[def.Name] = null; continue; }
                result[def.Name] = def.Unit == "rad" ? raw * RAD2DEG : raw;
            }
            return result;
        }

        // Ptr-chain mode (debug EBOOT)
        var pageCache = new Dictionary<uint, byte[]?>(8);

        foreach (var def in defs)
        {
            int ptrOff = (int)(def.NodeOffset + PARAM_VALPTR_OFF);
            if (ptrOff + 4 > nodeBlock.Length) { result[def.Name] = null; continue; }

            Span<byte> pb = nodeBlock.AsSpan(ptrOff, 4);
            uint ptr = BitConverter.IsLittleEndian
                ? (uint)(pb[0] << 24 | pb[1] << 16 | pb[2] << 8 | pb[3])
                : BitConverter.ToUInt32(pb);

            if (ptr < 0x10000000 || ptr > 0x50000000u) { result[def.Name] = null; continue; }

            uint pageBase = ptr & ~63u;
            if (!pageCache.TryGetValue(pageBase, out var page))
            {
                page = ReadBytes(pageBase, 64);
                pageCache[pageBase] = page;
            }
            if (page == null) { result[def.Name] = null; continue; }

            int lo = (int)(ptr - pageBase) + def.FieldOffset;
            if ((uint)(lo + 4) > (uint)page.Length) { result[def.Name] = null; continue; }

            Span<byte> fb = page.AsSpan(lo, 4);
            float raw = BitConverter.IsLittleEndian
                ? BitConverter.ToSingle(new byte[] { fb[3], fb[2], fb[1], fb[0] })
                : BitConverter.ToSingle(fb);

            if (float.IsNaN(raw) || float.IsInfinity(raw)) { result[def.Name] = null; continue; }
            result[def.Name] = def.Unit == "rad" ? raw * RAD2DEG : raw;
        }
        return result;
    }

    public void Dispose() => Close();
}
