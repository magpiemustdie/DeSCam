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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindowW(string? lpClassName, string lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

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

    // Valid PS3 heap range used for pointer sanity checks across both EBOOT modes.
    // Wider than PS3_HEAP_START..PS3_HEAP_END because normal EBOOT objects can
    // reside anywhere in 0x30000000..0x42000000.
    const uint HEAP_LO = 0x30000000;
    const uint HEAP_HI = 0x42000000;

    // debug_menu chain (BLUS30443v100): *(r2-0x7F98) chain
    const uint TOC_STEP1 = 0x019AD0A8;

    public const float FOVY_MIN = 0.1f;  // ~5.7°
    public const float FOVY_MAX = 3.2f;  // ~183°
    public static bool FovRangeCheckEnabled { get; set; } = true;

    // ── State (volatile for cross-thread visibility) ──────────────────────────
    nint _handle;
    public int  Pid        { get; private set; }
    public bool Verified   { get; private set; }

    volatile uint _followNode;
    /// <summary>PS3 virtual address of the ChrFollowCam object.</summary>
    public uint FollowNode { get => _followNode; private set => _followNode = value; }

    public bool IsOpen => _handle != nint.Zero;

    /// <summary>
    /// True  = normal EBOOT: params are direct floats in FollowNode body (DirectOffset).
    /// False = debug EBOOT:  params are accessed via ptr-chain (NodeOffset+PARAM_VALPTR_OFF).
    /// </summary>
    volatile bool _directMode;
    public bool DirectMode { get => _directMode; private set => _directMode = value; }

    // Per-thread 4-byte buffer for scalar reads — avoids GC allocs AND data races
    // between concurrent Task.Run calls that both call ReadRaw4/ReadU32/ReadF32.
    [ThreadStatic]
    static byte[]? _buf4Thread;
    static byte[] Buf4 => _buf4Thread ??= new byte[4];

    // ── Process helpers ───────────────────────────────────────────────────────
    /// <summary>Checks if the Demon's Souls game window (RPCS3 child window) exists.
    /// Uses EnumWindows to find any top-level window whose title contains "Demon's"
    /// (covers "Demon's Souls™", "Demon's Souls [BLUS30443]", etc.).</summary>
    public static bool IsGameWindowOpen()
    {
        bool found = false;
        EnumWindows((hWnd, _) =>
        {
            var sb = new System.Text.StringBuilder(256);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            if (sb.ToString().Contains("Demon's"))
            { found = true; return false; } // stop enumeration
            return true;
        }, nint.Zero);
        return found;
    }

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
    /// <summary>
    /// Reads <paramref name="size"/> bytes from a PS3 virtual address.
    /// Pages that cannot be read are left as zero in the output buffer.
    /// </summary>
    public byte[]? ReadBytes(uint ps3Va, int size)
    {
        if (!IsOpen || size <= 0) return null;
        var buf = new byte[size];
        int off = 0;
        while (off < size)
        {
            int  chunk = Math.Min(PAGE, size - off);
            nint addr  = (nint)(PS3_BASE + ps3Va + (uint)off);
            var  slice = new byte[chunk];
            int  status = NtReadVirtualMemory(_handle, addr, slice, chunk, out nint rd);
            if (status == 0 && rd > 0)
            {
                // Clamp rd to chunk to guard against any NtApi surprise
                int copied = (int)Math.Min(rd, chunk);
                Buffer.BlockCopy(slice, 0, buf, off, copied);
            }
            // Always advance by a full chunk so we don't loop forever on bad pages
            off += chunk;
        }
        return buf;
    }

    public bool WriteBytes(uint ps3Va, byte[] data)
        => IsOpen &&
           NtWriteVirtualMemory(_handle, (nint)(PS3_BASE + ps3Va), data, data.Length, out _) == 0;

    // ── Typed helpers (Big-Endian) ────────────────────────────────────────────
    // Use thread-local Buf4 for scalar reads — avoids allocs and data races
    bool ReadRaw4(uint ps3Va)
    {
        if (!IsOpen) return false;
        nint addr = (nint)(PS3_BASE + ps3Va);
        var buf = Buf4;
        return NtReadVirtualMemory(_handle, addr, buf, 4, out nint rd) == 0 && rd == 4;
    }

    public uint? ReadU32(uint ps3Va)
    {
        if (!ReadRaw4(ps3Va)) return null;
        var buf = Buf4;
        if (BitConverter.IsLittleEndian)
        {
            byte t = buf[0]; buf[0] = buf[3]; buf[3] = t;
            t = buf[1]; buf[1] = buf[2]; buf[2] = t;
        }
        return BitConverter.ToUInt32(buf, 0);
    }

    public float? ReadF32(uint ps3Va)
    {
        if (!ReadRaw4(ps3Va)) return null;
        var buf = Buf4;
        if (BitConverter.IsLittleEndian)
        {
            byte t = buf[0]; buf[0] = buf[3]; buf[3] = t;
            t = buf[1]; buf[1] = buf[2]; buf[2] = t;
        }
        float v = BitConverter.ToSingle(buf, 0);
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

    // ── Pointer sanity helper ─────────────────────────────────────────────────
    /// <summary>Returns true when <paramref name="ptr"/> is a plausible PS3 heap pointer.</summary>
    static bool IsHeapPtr(uint ptr) => ptr >= HEAP_LO && ptr < HEAP_HI;

    /// <summary>Returns true when <paramref name="ptr"/> is a plausible PS3 BSS/vtable pointer.</summary>
    static bool IsBssPtr(uint ptr)  => ptr >= PS3_BSS_LO && ptr < PS3_BSS_HI;

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
        if (!step1.HasValue || !IsBssPtr(step1.Value))
        {
            progress?.Report($"Step1 invalid: 0x{step1.GetValueOrDefault():X8}");
            return null;
        }

        var step2 = ReadU32(step1.Value - 0x7FFCu);
        if (!step2.HasValue) { progress?.Report("Step2 failed"); return null; }

        var dm = ReadU32(step2.Value);
        if (!dm.HasValue || !IsHeapPtr(dm.Value))
        {
            progress?.Report($"debug_menu invalid: 0x{dm.GetValueOrDefault():X8}");
            return null;
        }
        progress?.Report($"debug_menu=0x{dm.Value:X8}");

        var root = ReadU32(dm.Value + 0x04);
        if (!root.HasValue || !IsHeapPtr(root.Value))
        {
            progress?.Report($"root_node invalid: 0x{root.GetValueOrDefault():X8}");
            return null;
        }
        progress?.Report($"root_node=0x{root.Value:X8}");

        var chrcam = FindChrCamInTree(root.Value, progress);
        if (!chrcam.HasValue) { progress?.Report("ChrCam not found in tree"); return null; }

        var follow = ReadU32(chrcam.Value + 0x1C);
        if (!follow.HasValue || !IsHeapPtr(follow.Value))
        {
            progress?.Report($"ChrFollowCam invalid: 0x{follow.GetValueOrDefault():X8}");
            return null;
        }
        progress?.Report($"ChrCam=0x{chrcam.Value:X8}  ChrFollowCam=0x{follow.Value:X8}");
        FollowNode = follow.Value;
        DirectMode = false;
        return follow;
    }

    /// <summary>
    /// HgItem tree walk using correct PS3 SDK node layout:
    ///   +0x00 vtable  +0x04 flags  +0x08 parent
    ///   +0x1C first child  +0x20 next sibling
    ///   +0x2C label (UTF-16BE)  +0x30 value ptr
    /// Children can be a flat-array container (if container's own vtable is NOT BSS).
    /// </summary>
    static uint BE32(byte[] d, int off) => (uint)(d[off]<<24 | d[off+1]<<16 | d[off+2]<<8 | d[off+3]);

    uint? FindChrCamInTree(uint rootVa, IProgress<string>? prog, int maxDepth = 12)
    {
        if (!IsHeapPtr(rootVa)) return null;
        int totalVisited = 0;
        var visited = new HashSet<uint>();
        var queue   = new Queue<(uint addr, int depth)>();
        queue.Enqueue((rootVa, 0));

        while (queue.Count > 0)
        {
            var (va, depth) = queue.Dequeue();
            if (depth > maxDepth || !visited.Add(va)) continue;
            if (!IsHeapPtr(va)) continue;
            totalVisited++;

            var data = ReadBytes(va, 0x40);
            if (data == null || data.Length < 0x24) continue;

            // Check for CHRCAM_NEEDLE at +0x10 (big-endian)
            if (BE32(data, 0x10) == CHRCAM_NEEDLE)
            {
                prog?.Report($"found ChrCam @ 0x{va:X8} (visited {totalVisited} nodes, depth {depth})");
                return va;
            }

            // First child / container pointer at +0x1C
            uint childOrContainer = BE32(data, 0x1C);
            if (childOrContainer == 0 || !IsHeapPtr(childOrContainer)) continue;

            // Detect flat-array container: if its "vtable" (first uint32) is NOT in BSS
            var containerVt = ReadU32(childOrContainer);
            bool isFlatArray = containerVt.HasValue && !IsBssPtr(containerVt.Value);

            if (isFlatArray)
            {
                // Flat array of consecutive uint32 child pointers starting at container+4
                var arr = ReadBytes(childOrContainer, 0x800);
                if (arr == null) continue;
                for (int off = 4; off + 4 <= arr.Length; off += 4)
                {
                    uint c = BE32(arr, off);
                    if (c == 0 || !IsHeapPtr(c)) break;
                    if (!visited.Contains(c))
                        queue.Enqueue((c, depth + 1));
                }
            }
            else
            {
                // HgItem linked list: first child at +0x1C, siblings at +0x20
                // Enqueue in reverse order so siblings appear in natural order via BFS
                var sibAddrs = new List<uint>();
                uint sib = childOrContainer;
                while (sib != 0 && IsHeapPtr(sib) && sibAddrs.Count < 512)
                {
                    sibAddrs.Add(sib);
                    var sibData = ReadBytes(sib, 0x24);
                    if (sibData == null || sibData.Length < 0x24) break;
                    sib = BE32(sibData, 0x20);
                }
                // Push in forward order so BFS processes them naturally
                for (int i = 0; i < sibAddrs.Count; i++)
                    if (!visited.Contains(sibAddrs[i]))
                        queue.Enqueue((sibAddrs[i], depth + 1));
            }
        }

        prog?.Report($"tree walk done — visited {totalVisited} nodes, ChrCam not found");
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
                    if (vt.HasValue && IsBssPtr(vt.Value))
                    {
                        var follow = ReadU32(chrcam + 0x1C);
                        if (!follow.HasValue || !IsHeapPtr(follow.Value)) { off = idx + 4; continue; }

                        // Validate follow node: its vtable must be in BSS range.
                        var fvt = ReadU32(follow.Value);
                        if (!fvt.HasValue || !IsBssPtr(fvt.Value)) { off = idx + 4; continue; }

                        // Validate FovY is plausible to reject stale nodes during
                        // level transitions.  The FovY location depends on EBOOT type:
                        //   normal EBOOT: direct float at follow+0x50
                        //   debug  EBOOT: val ptr at follow+0x0050+0x30 → heap float
                        float? fovy = null;
                        var slotVt = ReadU32(follow.Value + 0x50);
                        if (slotVt.HasValue && IsBssPtr(slotVt.Value))
                        {
                            // Looks like a debug_menu tree entry — read via ptr-chain
                            var vp = ReadU32(follow.Value + 0x0050 + PARAM_VALPTR_OFF);
                            if (vp.HasValue && IsHeapPtr(vp.Value)) fovy = ReadF32(vp.Value);
                        }
                        else
                        {
                            // Direct float at follow+0x50 (normal EBOOT)
                            fovy = ReadF32(follow.Value + 0x50);
                        }
                        if (!fovy.HasValue || (FovRangeCheckEnabled && (fovy.Value < FOVY_MIN || fovy.Value > FOVY_MAX)))
                        { off = idx + 4; continue; }

                        progress?.Report($"ChrCam=0x{chrcam:X8}  ChrFollowCam=0x{follow.Value:X8}  FovY={fovy.Value*RAD2DEG:F1}°");
                        DirectMode = false;
                        FollowNode = follow.Value;
                        return follow;
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
        const uint LO    = HEAP_LO;
        const uint HI    = HEAP_HI;
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
            // FIX: use <= so the last possible aligned position is not skipped
            for (int i = 0; i + 0x54 <= d.Length; i += 4)
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
            // Need at least i + 0x20 bytes to safely read follow at i+0x1C
            for (int i = 0; i + 0x20 <= d.Length; i += 4)
            {
                if (d[i]!=c0||d[i+1]!=c1||d[i+2]!=c2||d[i+3]!=c3) continue;
                int fo = i + 0x1C;
                uint follow = (uint)(d[fo]<<24|d[fo+1]<<16|d[fo+2]<<8|d[fo+3]);
                if (!IsHeapPtr(follow)) continue;
                uint cand = (uint)(va + i);

                // TOCTOU guard: re-read candidate from live memory to detect
                // free+realloc between chunk scan and the validation reads below.
                var reRead = ReadBytes(cand, 0x20);
                if (reRead is null || reRead.Length < 0x20) continue;
                uint vt2 = (uint)(reRead[0]<<24|reRead[1]<<16|reRead[2]<<8|reRead[3]);
                if (vt2 != CHRCAM_VT) continue;
                uint follow2 = (uint)(reRead[0x1C]<<24|reRead[0x1D]<<16|reRead[0x1E]<<8|reRead[0x1F]);
                if (follow2 != follow) continue;

                var followVtCheck = ReadU32(follow);
                if (!followVtCheck.HasValue || !IsBssPtr(followVtCheck.Value)) continue;
                var fovy = ReadF32(follow + 0x1B0);
                if (!fovy.HasValue || (FovRangeCheckEnabled && (fovy.Value < FOVY_MIN || fovy.Value > FOVY_MAX))) continue;
                progress?.Report($"ChrCam=0x{cand:X8}  Follow=0x{follow:X8}  FovY@+0x1B0={fovy.Value*RAD2DEG:F1}°");
                DirectMode = true;
                FollowNode = follow;
                return follow;
            }
        }

        progress?.Report("not found in 0x30M..0x42M");
        return null;
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

    /// <summary>Checks whether the Demon's Souls game is still running.
    /// Fast path: look for the game window by title.  Fallback: verify the
    /// "ChrCam" marker string in PS3 memory (covers headless/no-window modes).</summary>
    public bool IsProcessAlive()
    {
        if (!IsOpen) return false;
        if (IsGameWindowOpen()) return true;
        var s = ReadString(PS3_CHRCAM_STR, 7);
        return s?.StartsWith("ChrCam") == true;
    }

    /// <summary>
    /// Fast check: returns true only when FollowNode is set and its vtable still
    /// falls in the known BSS range.  Call this before any write burst to avoid
    /// writing into a stale / freed heap object after a level transition.
    /// </summary>
    public bool IsFollowNodeValid()
    {
        if (!IsOpen || FollowNode == 0) return false;
        var vt = ReadU32(FollowNode);
        return vt.HasValue && IsBssPtr(vt.Value);
    }

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
            if (Math.Abs(f) > 1e6f) continue;
            lines.Add($"  +0x{i:X3} = {f:G6}");
        }
        return lines;
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
        if (!step1.HasValue || !IsBssPtr(step1.Value))
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

            lines.Add("--- dm ptr walk ---");
            if (dmData != null)
                for (int off = 0; off + 4 <= dmData.Length; off += 4)
                {
                    uint p = (uint)(dmData[off]<<24|dmData[off+1]<<16|dmData[off+2]<<8|dmData[off+3]);
                    if (!IsHeapPtr(p)) continue;
                    var fPtr = ReadU32(p + 0x0050 + PARAM_VALPTR_OFF);
                    if (!fPtr.HasValue || fPtr.Value < 0x10000000 || fPtr.Value > 0x50000000u) continue;
                    var fv = ReadF32(fPtr.Value);
                    if (!fv.HasValue || fv.Value < 0.1f || fv.Value > 3.5f) continue;
                    var vt = ReadU32(p);
                    lines.Add($"  dm[+0x{off:X2}]=0x{p:X8} vt=0x{vt.GetValueOrDefault():X8} FovY={fv.Value*RAD2DEG:F1}°");
                }
        }

        scanFloat:
        // 4. Read bytes around current FollowNode (if set) — dump all floats
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

        // 5. Targeted float scan
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

        // 6. Targeted vtable scan: find ChrCam by vtable 0x01AD6B68
        lines.Add("--- Scan for ChrCam vtable 0x01AD6B68 ---");
        {
            byte n0=0x01,n1=0xAD,n2=0x6B,n3=0x68;
            var candidates = new List<(uint chrCam, uint follow, uint fvt)>();
            for (uint scanVa = HEAP_LO; scanVa < HEAP_HI; scanVa += 0x100000)
            {
                var blk = ReadBytes(scanVa, 0x100000);
                if (blk == null) continue;
                for (int i = 0; i + 0x20 <= blk.Length; i += 4)
                {
                    if (blk[i]!=n0||blk[i+1]!=n1||blk[i+2]!=n2||blk[i+3]!=n3) continue;
                    uint cand = (uint)(scanVa + i);
                    int fi = i + 0x1C;
                    uint follow = (uint)(blk[fi]<<24|blk[fi+1]<<16|blk[fi+2]<<8|blk[fi+3]);
                    if (!IsHeapPtr(follow)) continue;
                    var fvt = ReadU32(follow);
                    if (!fvt.HasValue || !IsBssPtr(fvt.Value)) continue;
                    if (!candidates.Any(x => x.follow == follow))
                        candidates.Add((cand, follow, fvt.Value));
                }
            }
            lines.Add($"  Found {candidates.Count} unique follow candidates");

            foreach (var (chrCam, follow, fvt) in candidates.Take(1))
            {
                lines.Add($"  ChrCam=0x{chrCam:X8} follow=0x{follow:X8} fvt=0x{fvt:X8}");

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

                if (chrCamData != null)
                {
                    for (int i = 0; i + 4 <= Math.Min(0x100, chrCamData.Length); i += 4)
                    {
                        uint p = (uint)(chrCamData[i]<<24|chrCamData[i+1]<<16|chrCamData[i+2]<<8|chrCamData[i+3]);
                        if (!IsHeapPtr(p)) continue;
                        var fv = ReadF32(p);
                        if (fv.HasValue && fv.Value >= 0.70f && fv.Value <= 0.80f)
                            lines.Add($"  ChrCam[+0x{i:X3}]=0x{p:X8} direct fovy={fv.Value*RAD2DEG:F1}°");
                        var subvt = ReadU32(p);
                        if (!subvt.HasValue || !IsBssPtr(subvt.Value)) continue;
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
        if (unit == "bool")
        {
            uint? raw;
            if (DirectMode)
            {
                if (directOffset == 0) return null;
                raw = ReadU32(FollowNode + directOffset + (uint)fieldOffset);
            }
            else
            {
                var ptr = GetParamPtr(nodeOffset);
                if (ptr is null) return null;
                raw = ReadU32((uint)(ptr.Value + fieldOffset));
            }
            return raw;
        }
        else
        {
            float? raw;
            if (DirectMode)
            {
                if (directOffset == 0) return null;
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
    }

    public bool WriteParam(uint nodeOffset, string unit, double display, int fieldOffset = 0, uint directOffset = 0)
    {
        // Guard: NO=0 params (runtime / tree-only) are not writable via ptr-chain
        if (!DirectMode && nodeOffset == 0) return false;
        if (unit == "bool")
        {
            uint val = (uint)(int)display;
            byte[] b = BitConverter.GetBytes(val);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            if (DirectMode)
            {
                if (directOffset == 0) return false;
                return WriteBytes(FollowNode + directOffset + (uint)fieldOffset, b);
            }
            var p = GetParamPtr(nodeOffset);
            if (p is null) return false;
            return WriteBytes((uint)(p.Value + fieldOffset), b);
        }
        float store = unit == "rad" ? (float)(display * DEG2RAD) : (float)display;
        if (DirectMode)
        {
            if (directOffset == 0) return false;
            return WriteF32(FollowNode + directOffset + (uint)fieldOffset, store);
        }
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

        
        // Block must cover the furthest NodeOffset + PARAM_VALPTR_OFF + 4.
        uint maxNo = 0;
        foreach (var d in defs)
        {
            uint need = d.NodeOffset > d.DirectOffset ? d.NodeOffset : d.DirectOffset;
            if (need > maxNo) maxNo = need;
        }
        int blockSize = (int)(maxNo + PARAM_VALPTR_OFF + 4);
        var nodeBlock = ReadBytes(FollowNode, blockSize);
        if (nodeBlock == null)
        {
            foreach (var d in defs) result[d.Name] = null;
            return result;
        }

        // Verify the last 4 bytes of the block are readable.  If the block spans
        // an unmapped page, ReadBytes zero-fills the gap and we'd silently return
        // false-positive 0 for params on that page instead of "err".
        if (ReadU32(FollowNode + (uint)blockSize - 4) is null)
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
                // Guard: offset must be non-negative and fit in the block
                if (off < 0 || off + 4 > nodeBlock.Length) { result[def.Name] = null; continue; }
                if (def.Unit == "bool")
                {
                    int raw = nodeBlock[off + 3] | (nodeBlock[off + 2] << 8) | (nodeBlock[off + 1] << 16) | (nodeBlock[off] << 24);
                    result[def.Name] = raw;
                }
                else
                {
                    Span<byte> fb = nodeBlock.AsSpan(off, 4);
                    float raw = BitConverter.IsLittleEndian
                        ? BitConverter.ToSingle(new byte[] { fb[3], fb[2], fb[1], fb[0] })
                        : BitConverter.ToSingle(fb);
                    if (float.IsNaN(raw) || float.IsInfinity(raw)) { result[def.Name] = null; continue; }
                    result[def.Name] = def.Unit == "rad" ? raw * RAD2DEG : raw;
                }
            }
            return result;
        }
        

        // Ptr-chain mode (debug EBOOT) — NodeOffset is authoritative, no fallback
        var pageCache = new Dictionary<uint, byte[]?>(8);

        foreach (var def in defs)
        {
            if (def.NodeOffset == 0) { result[def.Name] = null; continue; }

            int ptrOff = (int)(def.NodeOffset + PARAM_VALPTR_OFF);
            if (ptrOff < 0 || ptrOff + 4 > nodeBlock.Length) { result[def.Name] = null; continue; }

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

            if (def.Unit == "bool")
            {
                int lo = (int)(ptr - pageBase) + def.FieldOffset;
                if (lo < 0 || lo + 4 > page.Length) { result[def.Name] = null; continue; }
                int raw = page[lo + 3] | (page[lo + 2] << 8) | (page[lo + 1] << 16) | (page[lo] << 24);
                result[def.Name] = raw;
            }
            else
            {
                int lo = (int)(ptr - pageBase) + def.FieldOffset;
                if (lo < 0 || lo + 4 > page.Length) { result[def.Name] = null; continue; }

                Span<byte> fb = page.AsSpan(lo, 4);
                float raw = BitConverter.IsLittleEndian
                    ? BitConverter.ToSingle(new byte[] { fb[3], fb[2], fb[1], fb[0] })
                    : BitConverter.ToSingle(fb);

                if (float.IsNaN(raw) || float.IsInfinity(raw)) { result[def.Name] = null; continue; }
                result[def.Name] = def.Unit == "rad" ? raw * RAD2DEG : raw;
            }
        }
        return result;
    }

    // ── Deep tree analysis ───────────────────────────────────────────────────
    /// <summary>
    /// Exhaustive BFS of the debug_menu tree from root, trying ALL 4-byte aligned
    /// offsets as child pointers.  Dumps every reachable node so the user can
    /// figure out the correct tree offsets for their EBOOT version.
    /// </summary>
    public List<string> DeepTreeAnalysis()
    {
        var lines = new List<string>();
        if (!IsOpen) { lines.Add("Not open"); return lines; }

        lines.Add("=== DEEP TREE ANALYSIS ===");

        // 1. Chain walk
        var step1 = ReadU32(TOC_STEP1);
        lines.Add($"step1 @ 0x{TOC_STEP1:X8} = 0x{step1.GetValueOrDefault():X8}");
        if (!step1.HasValue || !IsBssPtr(step1.Value)) { lines.Add("→ chain broken at step1"); goto scanMode; }

        uint s2addr = step1.Value - 0x7FFCu;
        var step2 = ReadU32(s2addr);
        lines.Add($"step2 @ 0x{s2addr:X8} = 0x{step2.GetValueOrDefault():X8}");
        if (!step2.HasValue || step2.Value < 0x01000000) { lines.Add("→ chain broken at step2"); goto scanMode; }

        var dmAddr = ReadU32(step2.Value);
        lines.Add($"dm   @ 0x{step2.Value:X8} = 0x{dmAddr.GetValueOrDefault():X8}");
        if (!dmAddr.HasValue || !IsHeapPtr(dmAddr.Value)) { lines.Add("→ chain broken at dm"); goto scanMode; }

        // 2. Dump full debug_menu node (0x200 bytes)
        var dmData = ReadBytes(dmAddr.Value, 0x200);
        if (dmData != null)
        {
            lines.Add($"--- dm[0..1FF] @ 0x{dmAddr.Value:X8} ---");
            for (int i = 0; i < dmData.Length; i += 16)
            {
                var sb = new System.Text.StringBuilder($"  {dmAddr.Value:X8}+{i:X3}: ");
                for (int j = 0; j < 16; j++) sb.Append($"{dmData[i+j]:X2}");
                sb.Append("  ");
                for (int j = 0; j < 16; j++)
                {
                    byte b = dmData[i+j];
                    sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                lines.Add(sb.ToString());
            }
        }

        // 3. BFS from root, trying ALL 4-byte offsets as potential children
        var root = ReadU32(dmAddr.Value + 0x04);
        lines.Add($"root @ dm+0x04 = 0x{root.GetValueOrDefault():X8}");
        if (!root.HasValue || !IsHeapPtr(root.Value)) { lines.Add("→ root invalid"); goto scanMode; }

        lines.Add("--- BFS tree walk (trying EVERY 4-byte offset) ---");
        var visited = new HashSet<uint>();
        var queue   = new Queue<(uint addr, int depth, string path)>();
        queue.Enqueue((root.Value, 0, "<root>"));
        uint? foundNeedleAt = null;
        string foundNeedlePath = "";

        while (queue.Count > 0)
        {
            var (addr, depth, path) = queue.Dequeue();
            if (!visited.Add(addr) || depth > 20) continue;

            var data = ReadBytes(addr, 0x80);
            if (data == null || data.Length < 0x20) continue;

            var vt = ReadU32(addr);
            string vtStr = vt.HasValue ? $"0x{vt.Value:X8}" : "?";

            // Search for CHRCAM_NEEDLE at every 4-byte aligned offset
            string needle = "";
            for (int off = 0; off + 4 <= data.Length; off += 4)
            {
                var val = (uint)(data[off]<<24 | data[off+1]<<16 | data[off+2]<<8 | data[off+3]);
                if (val == CHRCAM_NEEDLE)
                {
                    needle = $" ← CHRCAM_NEEDLE @ +{off:X3}";
                    if (foundNeedleAt == null) { foundNeedleAt = addr; foundNeedlePath = path; }
                }
            }

            // Dump node
            string indent = new string(' ', depth * 2);
            var sbNode = new System.Text.StringBuilder();
            sbNode.Append($"  {indent}{path} depth={depth} addr=0x{addr:X8} vt={vtStr}");
            if (needle.Length > 0) sbNode.Append(needle);
            lines.Add(sbNode.ToString());

            // Dump first 32 bytes of node as hex
            var sbHex = new System.Text.StringBuilder($"  {indent}  hex: ");
            for (int i = 0; i < 0x20 && i < data.Length; i++)
                sbHex.Append($"{data[i]:X2}");
            lines.Add(sbHex.ToString());

            // Check for "ChrCam" or "ChrFollowCam" string pointers
            for (int off = 0; off + 4 <= data.Length; off += 4)
            {
                uint val = (uint)(data[off]<<24 | data[off+1]<<16 | data[off+2]<<8 | data[off+3]);
                if (val == 0) continue;
                string? s = IsBssPtr(val) ? ReadString(val, 16) : null;
                if (s != null && (s.Contains("ChrCam") || s.Contains("ChrFollow")))
                    lines.Add($"  {indent}  str_ptr @ +{off:X3} → 0x{val:X8} = \"{s}\"");
            }

            // Try ALL 4-byte aligned offsets as children (skip 0 = vtable slot)
            for (int off = 4; off + 4 <= data.Length; off += 4)
            {
                uint child = (uint)(data[off]<<24 | data[off+1]<<16 | data[off+2]<<8 | data[off+3]);
                if (child == 0 || !IsHeapPtr(child) || visited.Contains(child)) continue;
                queue.Enqueue((child, depth + 1, $"+{off:X3}"));
            }

            // Validate vtable field for self
            for (int off = 0; off + 4 <= 0x20; off += 4)
            {
                uint val = (uint)(data[off]<<24 | data[off+1]<<16 | data[off+2]<<8 | data[off+3]);
                if (val == 0 || !IsBssPtr(val)) continue;
                string? s = ReadString(val, 4);
                if (s != null && s.Length >= 3)
                {
                    var vtd = ReadBytes(val, 0x20);
                    if (vtd != null)
                    {
                        bool hasHeapPtrs = false;
                        for (int vi = 0; vi + 4 <= vtd.Length; vi += 4)
                        {
                            uint vp = (uint)(vtd[vi]<<24 | vtd[vi+1]<<16 | vtd[vi+2]<<8 | vtd[vi+3]);
                            if (IsHeapPtr(vp)) { hasHeapPtrs = true; break; }
                        }
                            if (hasHeapPtrs)
                                lines.Add($"  {indent}  vt+0x{off:X3}=0x{val:X8} (vtable has heap refs)");
                    }
                }
            }
        }

        if (foundNeedleAt.HasValue)
            lines.Add($"\n*** CHRCAM_NEEDLE FOUND at 0x{foundNeedleAt:X8} (path: {foundNeedlePath}) ***");
        else
            lines.Add("\n*** CHRCAM_NEEDLE NOT FOUND anywhere in tree ***");

        lines.Add("--- END BFS ---");

        scanMode:
        // 4. Compare: dump the FollowNode found via ptr-chain scan for reference
        lines.Add("\n--- Reference: ptr-chain scan FollowNode ---");
        DoPtrChainScan(lines);

        lines.Add("=== END DEEP TREE ANALYSIS ===");
        return lines;
    }

    /// <summary>
    /// Scans every possible NO (0x050 step 0x50) and reports whether the
    /// debug_menu ptr-chain entry at NO+0x30 holds a valid value pointer.
    /// This reveals which param slots actually exist in the ChrFollowCam struct.
    /// </summary>
    public List<string> ScanSlots()
    {
        var lines = new List<string>();
        if (!IsOpen || FollowNode == 0) { lines.Add("No node"); return lines; }

        lines.Add($"FollowNode = 0x{FollowNode:X8}  DirectMode = {DirectMode}");
        lines.Add($"NO range: 0x050 to 0x2000 (+0x30 = value ptr)\n");

        const int maxSlots = 0x2000 / 0x50; // 102 slots
        var block = ReadBytes(FollowNode, 0x2100);
        if (block == null) { lines.Add("Failed to read FollowNode block"); return lines; }

        int valid = 0, nulls = 0, invalid = 0;
        for (int i = 0; i < maxSlots; i++)
        {
            uint no  = 0x050 + (uint)i * 0x50;
            int  off = (int)(no + PARAM_VALPTR_OFF);
            if (off + 4 > block.Length) { lines.Add($"  NO=0x{no:X4}  [overflow]"); continue; }

            uint ptr = (uint)(block[off] << 24 | block[off + 1] << 16 |
                              block[off + 2] << 8 | block[off + 3]);

            if (ptr == 0) { nulls++; continue; }
            if (ptr < HEAP_LO || ptr >= HEAP_HI) { invalid++; continue; }

            // Read float at ptr
            var fb = ReadBytes(ptr, 4);
            if (fb == null || fb.Length < 4) { lines.Add($"  NO=0x{no:X4}  ptr=0x{ptr:X8}  [read err]"); continue; }
            float val = BitConverter.IsLittleEndian
                ? BitConverter.ToSingle(new byte[] { fb[3], fb[2], fb[1], fb[0] })
                : BitConverter.ToSingle(fb);

            lines.Add($"  NO=0x{no:X4}  ptr=0x{ptr:X8}  val={val:G6}");
            valid++;
        }

        lines.Add($"\nSummary: {valid} valid, {nulls} null, {invalid} invalid pointers");
        return lines;
    }

    /// <summary>
    /// Scans EVERY 4-byte aligned offset in the FollowNode body for valid
    /// value ptr-chain entries (+0x30 offset).  Finds entries at non-standard NOs.
    /// </summary>
    public List<string> ScanAllOffsets()
    {
        var lines = new List<string>();
        if (!IsOpen || FollowNode == 0) { lines.Add("No node"); return lines; }

        lines.Add($"=== SCAN ALL 4-byte OFFSETS ===");
        lines.Add($"FollowNode = 0x{FollowNode:X8}");
        const int scanLen = 0x2000;
        var block = ReadBytes(FollowNode, scanLen + 0x40);
        if (block == null) { lines.Add("Read failed"); return lines; }

        int found = 0;
        // Build a dictionary of known NO → param names for identification
        var noToParams = new Dictionary<uint, List<string>>();
        foreach (var p in DebugParamDef.All)
        {
            if (!noToParams.ContainsKey(p.NodeOffset))
                noToParams[p.NodeOffset] = new List<string>();
            noToParams[p.NodeOffset].Add(p.Name);
        }

        for (uint off = 0; off + 0x34 <= scanLen; off += 4)
        {
            int ptrOff = (int)(off + PARAM_VALPTR_OFF);
            uint ptr = (uint)(block[ptrOff] << 24 | block[ptrOff + 1] << 16 |
                              block[ptrOff + 2] << 8 | block[ptrOff + 3]);

            if (ptr == 0) continue;
            if (ptr < HEAP_LO || ptr >= HEAP_HI) continue;

            // Read float at ptr
            var fb = ReadBytes(ptr, 4);
            if (fb == null || fb.Length < 4) continue;
            float val = BitConverter.IsLittleEndian
                ? BitConverter.ToSingle(new byte[] { fb[3], fb[2], fb[1], fb[0] })
                : BitConverter.ToSingle(fb);
            if (float.IsNaN(val) || float.IsInfinity(val)) continue;

            string identified = "";
            if (noToParams.TryGetValue(off, out var names))
                identified = $"  ← {string.Join(", ", names)}";

            lines.Add($"  NO=0x{off:X4}  ptr=0x{ptr:X8}  val={val:G6}{identified}");
            found++;
        }

        lines.Add($"\nTotal entries found: {found}");
        return lines;
    }

    /// <summary>
    /// Dumps a large region of PS3 heap memory starting from the FOV value ptr,
    /// showing every 4-byte value as float and uint32.
    /// Use this to find the actual struct layout around known params.
    /// </summary>
    public List<string> DumpParamMemory()
    {
        var lines = new List<string>();
        if (!IsOpen || FollowNode == 0) { lines.Add("No node"); return lines; }

        // Find first valid value ptr (FOV)
        uint fovPtr = 0;
        var block = ReadBytes(FollowNode, 0x2100);
        if (block == null) { lines.Add("Failed to read FollowNode"); return lines; }
        for (int i = 0; i < 0x2000 / 0x50; i++)
        {
            uint no = 0x050 + (uint)i * 0x50;
            int off = (int)(no + PARAM_VALPTR_OFF);
            if (off + 4 > block.Length) break;
            uint ptr = (uint)(block[off] << 24 | block[off + 1] << 16 | block[off + 2] << 8 | block[off + 3]);
            if (ptr >= HEAP_LO && ptr < HEAP_HI) { fovPtr = ptr; break; }
        }
        if (fovPtr == 0) { lines.Add("No valid value ptr found"); return lines; }

        uint baseAddr = fovPtr & ~0xFFFu;
        const int size = 0x4000;
        lines.Add($"FOV ptr = 0x{fovPtr:X8}, dumping {size} bytes from 0x{baseAddr:X8}\n");

        var data = ReadBytes(baseAddr, size);
        if (data == null) { lines.Add("Failed to read memory"); return lines; }

        for (int off = 0; off + 4 <= data.Length; off += 4)
        {
            uint u = (uint)(data[off] << 24 | data[off + 1] << 16 | data[off + 2] << 8 | data[off + 3]);
            float f = BitConverter.IsLittleEndian
                ? BitConverter.ToSingle(new byte[] { data[off + 3], data[off + 2], data[off + 1], data[off] })
                : BitConverter.ToSingle(data, off);

            if (float.IsNaN(f) || float.IsInfinity(f)) continue;
            // Only show reasonable float values
            if (f < -1000f || f > 10000f) continue;

            string marker = (baseAddr + (uint)off) == fovPtr ? " ← FOV" : "";
            lines.Add($"  +0x{off:X4}  0x{u:X8}  {f,12:G6}{marker}");
        }

        lines.Add($"\nTotal floats in {size} bytes at 0x{baseAddr:X8}");
        return lines;
    }

    void DoPtrChainScan(List<string> lines)
    {
        const uint LO = 0x38000000, HI = 0x40000000;
        const int CHUNK = 0x80000;
        byte n0=0x01, n1=0x6C, n2=0x54, n3=0x20;

        for (uint va = LO; va < HI; va += CHUNK)
        {
            var d = ReadBytes(va, (int)Math.Min(CHUNK, HI - va));
            if (d == null) continue;
            for (int idx = 0; idx <= d.Length - 4; idx += 4)
            {
                if (d[idx]!=n0||d[idx+1]!=n1||d[idx+2]!=n2||d[idx+3]!=n3) continue;
                if (idx < 0x10) continue;
                uint chrcam = (uint)(va + idx - 0x10);
                var vt = ReadU32(chrcam);
                if (!vt.HasValue || !IsBssPtr(vt.Value)) continue;
                var follow = ReadU32(chrcam + 0x1C);
                if (!follow.HasValue || !IsHeapPtr(follow.Value)) continue;

                lines.Add($"  ChrCam @ 0x{chrcam:X8} vt=0x{vt.Value:X8}");
                lines.Add($"  FollowNode @ 0x{follow.Value:X8}");

                // Dump FollowNode first 0x100 bytes
                var nd = ReadBytes(follow.Value, 0x100);
                if (nd != null)
                {
                    var sb = new System.Text.StringBuilder("  FollowNode[0..FF]: ");
                    for (int i = 0; i < nd.Length; i += 4)
                        sb.Append($"{nd[i]:X2}{nd[i+1]:X2}{nd[i+2]:X2}{nd[i+3]:X2} ");
                    lines.Add(sb.ToString());
                }
                return; // just first hit
            }
        }
        lines.Add("  No FollowNode found via ptr-chain scan");
    }

    /// <summary>
    /// Clears the FollowNode and DirectMode without closing the handle.
    /// Used when the user switches between Normal/Debug EBOOT modes
    /// so stale addresses from the other mode don't leak into the UI.
    /// </summary>
    public void ResetFollowNode()
    {
        FollowNode  = 0;
        DirectMode  = false;
    }

    public void Dispose() => Close();
}
