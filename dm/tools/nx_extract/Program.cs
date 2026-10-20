// NSP / XCI extraction: nx_extract <prod.keys> <base NSP|XCI> <update NSP|-> <output dir> [base RomFS dir to compare against] [--manifest]
// An XCI's Secure partition is the base game. Its Update partition normally holds a system update, not the game's update: it is used only when its
// Program title is the base title's update (...800). Otherwise the update must be a separate NSP, exactly as for an NSP base.
// Writes <output dir>/ExeFS and <output dir>/RomFS (the merged RomFS when there is an update); with a base RomFS dir, files whose content matches it become APFS clones (no new space).
// --manifest: the RomFS is not written, only a list <output dir>/romfs_manifest.tsv (path, size, first 32 bytes), for surveying a new game (a large game's RomFS is well over 10 GB).
// --info: print the base title ID, display version and name, and the update partition's title ID and version when the file is an XCI (no extraction).
// A damaged NSP (a file whose data fails the NCA's hash check, e.g. a copy cut short or a broken download) stops the extraction with exit code 3 after listing every damaged file;
//   --allow-damaged writes those files as read instead and only lists them (Super Mario Galaxy's base NSP with 147 MB zeroed: its two prologue WebM movies).
// Add-on content: nx_extract <prod.keys> --aoc <DLC NSP> <output dir> writes the RomFS of the DLC's data NCA to <output dir> (the packing script puts it at aoc/<index>/, the host mounts it through DM_AOC_DIR).
// Title keys come from the common tickets (.tik) inside the NSPs or the XCI, so only prod.keys is needed; key values are never printed.
// A cartridge dump's (.xci, Monster Hunter Rise) secure partition holds the NCAs, which carry their keys in the key area (no ticket).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Ncm;
using LibHac.Tools.Ncm;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Spl;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.FsSystem.RomFs;
using Path = System.IO.Path;

static class P {
    [DllImport("libc", SetLastError = true)] static extern int clonefile(string src, string dst, uint flags);

    static int Main(string[] a) {
        bool info = Array.IndexOf(a, "--info") >= 0; a = Array.FindAll(a, s => s != "--info");
        bool manifest = Array.IndexOf(a, "--manifest") >= 0; a = Array.FindAll(a, s => s != "--manifest");
        g_allow = Array.IndexOf(a, "--allow-damaged") >= 0; a = Array.FindAll(a, s => s != "--allow-damaged");
        if (info) {
            if (a.Length < 2) { Console.Error.WriteLine("usage: nx_extract <prod.keys> --info <game.nsp|game.xci>"); return 2; }
            return Info(ExternalKeyReader.ReadKeyFile(a[0]), a[1]);
        }
        if (a.Length < 4) { Console.Error.WriteLine("usage: nx_extract <prod.keys> <base.nsp|base.xci> <update.nsp|-> <out dir> [base RomFS dir] [--manifest] [--allow-damaged]\n       nx_extract <prod.keys> --aoc <dlc.nsp> <out dir>\n       nx_extract <prod.keys> --info <game.nsp|game.xci>"); return 2; }
        KeySet ks = ExternalKeyReader.ReadKeyFile(a[0]);
        if (a[1] == "--aoc") return Aoc(ks, a[2], a[3]);
        var basePkg = OpenPackage(ks, a[1], false, out var xciUpdate);
        var updPkg = a[2] == "-" ? xciUpdate : OpenPackage(ks, a[2], true, out _);
        Nca baseProg = Pick(basePkg, NcaContentType.Program) ?? throw new Exception("no Program NCA in the base game");
        Nca updProg = updPkg == null ? null : Pick(updPkg, NcaContentType.Program) ?? throw new Exception("no Program NCA in the update");
        Nca ctrl = Pick(updPkg ?? basePkg, NcaContentType.Control);
        Console.WriteLine($"title {baseProg.Header.TitleId:x16}" + (updProg != null ? $", update {updProg.Header.TitleId:x16}" : ""));
        if (updPkg != null && a[2] == "-" && IsXci(a[1])) Console.WriteLine("update: the XCI's update partition");
        string outDir = a[3], cmpDir = a.Length > 4 ? a[4] : null;
        if (ctrl != null) {   // control data (NACP): display version and game name; a raw copy goes to <output dir>/control.nacp for naming the app
            byte[] nacp = Nacp(ctrl);
            Console.WriteLine($"display version {VersionOf(nacp)}");
            Console.WriteLine($"name {TitleName(nacp)}");
            Directory.CreateDirectory(outDir); File.WriteAllBytes(Path.Combine(outDir, "control.nacp"), nacp);
            var (icon, lang) = Icon(ctrl);   // the game icon for the app icon, saved as <output dir>/icon.jpg (build_ipa.sh picks it up)
            if (icon != null) { File.WriteAllBytes(Path.Combine(outDir, "icon.jpg"), icon); Console.WriteLine($"icon {lang} ({icon.Length} bytes)"); }
            else Console.WriteLine("no icon in the control data");
        }
        // Code section: the update's ExeFS is complete; opening it layered over the base gives the same result
        IFileSystem exefs = updProg != null ? baseProg.OpenFileSystemWithPatch(updProg, NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid) : baseProg.OpenFileSystem(NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid);
        Copy(exefs, Path.Combine(outDir, "ExeFS"), null);
        string[] mods = Directory.GetFiles(Path.Combine(outDir, "ExeFS")); Array.Sort(mods, StringComparer.Ordinal);
        foreach (string p in mods) {   // build ID of every NSO module (main, sdk, subsdk*, rtld...)
            if (new FileInfo(p).Length < 0x50) continue;
            byte[] h = new byte[0x50]; using (var f = File.OpenRead(p)) f.ReadExactly(h);
            if (h[0] == 'N' && h[1] == 'S' && h[2] == 'O' && h[3] == '0') Console.WriteLine($"{Path.GetFileName(p)} build id {Convert.ToHexString(h, 0x40, 16).ToLowerInvariant()}");
        }
        IStorage romfs = updProg != null ? baseProg.OpenStorageWithPatch(updProg, NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid) : baseProg.OpenStorage(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
        // the same RomFS without hash checks, for reading damaged files as they are (--allow-damaged)
        g_lenient = () => new RomFsFileSystem(updProg != null ? baseProg.OpenStorageWithPatch(updProg, NcaSectionType.Data, IntegrityCheckLevel.IgnoreOnInvalid) : baseProg.OpenStorage(NcaSectionType.Data, IntegrityCheckLevel.IgnoreOnInvalid));
        if (manifest) Manifest(new RomFsFileSystem(romfs), Path.Combine(outDir, "romfs_manifest.tsv"));
        else Copy(new RomFsFileSystem(romfs), Path.Combine(outDir, "RomFS"), cmpDir);
        return Damaged();
    }

    // Files whose data failed the hash check: listed at the end; exit code 3 unless --allow-damaged
    static readonly List<string> g_damaged = new List<string>(); static bool g_allow; static Func<IFileSystem> g_lenient; static IFileSystem g_lenientFs;
    static bool HashError(HorizonResultException e) => e.ResultValue.Module == 2 && e.ResultValue.Description >= 4600 && e.ResultValue.Description < 4700;   // fs: integrity verification failures (2002-46xx)
    static int Damaged() {
        if (g_damaged.Count == 0) return 0;
        foreach (string p in g_damaged) Console.WriteLine($"[RED] hash check failed: {p}");
        if (g_allow) { Console.WriteLine($"[RED] the NSP is damaged: {g_damaged.Count} files written as read (--allow-damaged)"); return 0; }
        Console.WriteLine($"[RED] the NSP is damaged: " +
                          $"{g_damaged.Count} files fail the NCA's hash check (an incomplete copy or a broken download); copy or download it again, or pass --allow-damaged to write them as read");
        return 3;
    }

    // DLC NSP: one data NCA (content type PublicData / Data) + a meta NCA + the ticket; only the data NCA's RomFS is written
    static int Aoc(KeySet ks, string nsp, string outDir) {
        var pkg = OpenNsp(ks, nsp);
        Nca data = Pick(pkg, NcaContentType.PublicData) ?? Pick(pkg, NcaContentType.Data) ?? throw new Exception("no data NCA in the DLC NSP");
        Console.WriteLine($"add-on content {data.Header.TitleId:x16}");
        Directory.CreateDirectory(outDir);
        Copy(new RomFsFileSystem(data.OpenStorage(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid)), outDir, null);
        return 0;
    }

    // Open an NSP (PFS0) or the secure partition of an XCI, import the common tickets, list all NCAs
    static List<Nca> OpenNsp(KeySet ks, string path) {
        IFileSystem pfs; var st0 = new LocalStorage(path, FileAccess.Read);
        byte[] magic = new byte[4]; st0.Read(0x100, magic).ThrowIfFailure();
        if (System.Text.Encoding.ASCII.GetString(magic) == "HEAD") {   // XCI: the card header's magic at 0x100
            var xci = new Xci(ks, st0);
            if (!xci.HasPartition(XciPartitionType.Secure)) throw new Exception($"no secure partition in {path}");
            pfs = xci.OpenPartition(XciPartitionType.Secure);
        } else {
            var p = new PartitionFileSystem(); p.Initialize(st0).ThrowIfFailure(); pfs = p;
        }
        var ncas = new List<Nca>();
        foreach (DirectoryEntryEx e in pfs.EnumerateEntries("*", SearchOptions.Default)) {
            using var f = new UniqueRef<IFile>();
            pfs.OpenFile(ref f.Ref, e.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
            IStorage st = f.Release().AsStorage();
            if (e.Name.EndsWith(".tik", StringComparison.OrdinalIgnoreCase)) {
                byte[] t = new byte[0x2c0]; st.Read(0, t).ThrowIfFailure();
                if (t[0x281] != 0) { Console.Error.WriteLine($"[RED] {e.Name} is not a common ticket; supply the title key separately"); continue; }
                ks.ExternalKeySet.Add(new RightsId(t.AsSpan(0x2a0, 0x10)), new AccessKey(t.AsSpan(0x180, 0x10))).ThrowIfFailure();
            } else if (e.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase)) ncas.Add(new Nca(ks, st));
        }
        return ncas;
    }
    static Nca Pick(List<Nca> l, NcaContentType t) { foreach (Nca n in l) if (n.Header.ContentType == t) return n; return null; }

    // ---- XCI: the Secure partition holds the base NCAs, and in a merged cartridge image the patch and DLC NCAs too. The Update partition normally holds a system update.
    sealed class XciContent { public List<Nca> Base; public List<Nca> Update; public List<Nca> SystemUpdate; public uint BaseVersion; public uint UpdateVersion; }
    static bool IsXci(string path) => path.EndsWith(".xci", StringComparison.OrdinalIgnoreCase);

    static List<Nca> OpenPackage(KeySet ks, string path, bool update, out List<Nca> xciUpdate) {
        xciUpdate = null;
        if (!IsXci(path)) return OpenNsp(ks, path);
        XciContent c = OpenXci(ks, path);
        xciUpdate = c.Update;
        return update ? (c.Update ?? new List<Nca>()) : c.Base;
    }

    static XciContent OpenXci(KeySet ks, string path) {
        // The Xci and its partitions keep the LocalStorage alive through their storages; do not dispose it while NCAs are in use.
        var xci = new Xci(ks, new LocalStorage(path, FileAccess.Read));
        if (xci.HasPartition(XciPartitionType.Normal)) ImportTickets(ks, xci.OpenPartition(XciPartitionType.Normal));
        Dictionary<string, Nca> all = ReadNcas(ks, xci, XciPartitionType.Secure);
        var (b, bv, u, uv) = SplitByCnmt(all);
        if (b == null) b = all.Values.Where(n => n.Header.ContentType == NcaContentType.Program).ToList();   // no CNMT: fall back to every Program
        List<Nca> sys = null;
        if (u == null && xci.HasPartition(XciPartitionType.Update)) {
            Dictionary<string, Nca> upd = ReadNcas(ks, xci, XciPartitionType.Update);
            var (ub, ubv, uu, uuv) = SplitByCnmt(upd);
            if (uu != null) { u = uu; uv = uuv; }
            else if (upd.Count > 0) sys = upd.Values.ToList();
        }
        return new XciContent { Base = b, Update = u, SystemUpdate = sys, BaseVersion = bv, UpdateVersion = uv };
    }

    static Dictionary<string, Nca> ReadNcas(KeySet ks, Xci xci, XciPartitionType type) {
        var ncas = new Dictionary<string, Nca>(StringComparer.OrdinalIgnoreCase);
        XciPartition p = xci.OpenPartition(type);
        foreach (DirectoryEntryEx e in p.EnumerateEntries("*", SearchOptions.Default)) {
            if (e.Type == DirectoryEntryType.Directory || !e.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase)) continue;
            using var f = new UniqueRef<IFile>();
            p.OpenFile(ref f.Ref, e.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
            ncas[Path.GetFileNameWithoutExtension(e.Name)] = new Nca(ks, f.Release().AsStorage());   // <nca id>.nca or <nca id>.cnmt.nca
        }
        return ncas;
    }

    // A merged XCI's Secure partition holds the Application and Patch NCAs side by side; their CNMTs say which Program/Control belongs to which.
    static (List<Nca> basePkg, uint baseVer, List<Nca> patchPkg, uint patchVer) SplitByCnmt(Dictionary<string, Nca> all) {
        List<Nca> b = null, u = null; uint bv = 0, uv = 0;
        foreach (var kv in all) {
            if (!kv.Key.EndsWith(".cnmt", StringComparison.OrdinalIgnoreCase)) continue;
            Cnmt c; try { c = ReadCnmt(kv.Value); } catch { continue; }
            if (c == null) continue;
            var ids = new HashSet<string>(c.ContentEntries.Select(e => Convert.ToHexString(e.NcaId).ToLowerInvariant()));
            var pkg = ids.Select(id => all.TryGetValue(id, out Nca n) ? n : null).Where(n => n != null).ToList();
            if (pkg.Count == 0) continue;
            if (c.Type == ContentMetaType.Application && c.TitleVersion.Version >= bv) { b = pkg; bv = c.TitleVersion.Version; }
            else if (c.Type == ContentMetaType.Patch && c.TitleVersion.Version >= uv) { u = pkg; uv = c.TitleVersion.Version; }
        }
        return (b, bv, u, uv);
    }

    static Cnmt ReadCnmt(Nca meta) {
        IFileSystem fs = meta.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
        foreach (DirectoryEntryEx x in fs.EnumerateEntries("*", SearchOptions.Default)) {
            if (!x.Name.EndsWith(".cnmt", StringComparison.OrdinalIgnoreCase)) continue;
            using var f = new UniqueRef<IFile>();
            fs.OpenFile(ref f.Ref, x.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
            f.Get.GetSize(out long n).ThrowIfFailure();
            var bytes = new byte[n]; f.Get.Read(out _, 0, bytes, ReadOption.None).ThrowIfFailure();
            return new Cnmt(new MemoryStream(bytes));
        }
        return null;
    }

    static void ImportTickets(KeySet ks, XciPartition p) {
        foreach (DirectoryEntryEx e in p.EnumerateEntries("*", SearchOptions.Default)) {
            if (e.Type == DirectoryEntryType.Directory || !e.Name.EndsWith(".tik", StringComparison.OrdinalIgnoreCase)) continue;
            using var f = new UniqueRef<IFile>();
            p.OpenFile(ref f.Ref, e.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
            byte[] t = new byte[0x2c0]; f.Get.Read(out _, 0, t, ReadOption.None).ThrowIfFailure();
            if (t[0x281] != 0) continue;   // not a common ticket; the XCI header's key area may still provide the key
            ks.ExternalKeySet.Add(new RightsId(t.AsSpan(0x2a0, 0x10)), new AccessKey(t.AsSpan(0x180, 0x10))).ThrowIfFailure();
        }
    }

    // --info: what the packing script needs to identify an XCI before extracting it
    static int Info(KeySet ks, string path) {
        XciContent xci = IsXci(path) ? OpenXci(ks, path) : null;
        List<Nca> basePkg = xci != null ? xci.Base : OpenNsp(ks, path);
        List<Nca> xciUpdate = xci?.Update;
        Nca baseProg = Pick(basePkg, NcaContentType.Program) ?? throw new Exception("no Program NCA in the base game");
        Console.WriteLine($"title {baseProg.Header.TitleId:x16}");
        if (xci != null) {
            Console.WriteLine($"base_v {xci.BaseVersion}");
            if (xci.Update != null) Console.WriteLine($"update_v {xci.UpdateVersion}");
        }
        Nca baseCtrl = Pick(basePkg, NcaContentType.Control);
        if (baseCtrl != null) {
            byte[] nacp = Nacp(baseCtrl);
            Console.WriteLine($"base_version {VersionOf(nacp)}");
            Console.WriteLine($"name {TitleName(nacp)}");
        }
        Nca updProg = xciUpdate != null ? Pick(xciUpdate, NcaContentType.Program) : null;
        Nca uctrl = xciUpdate != null ? Pick(xciUpdate, NcaContentType.Control) : null;
        if (uctrl != null) {
            byte[] nacp = Nacp(uctrl);
            Console.WriteLine($"version {VersionOf(nacp)}");
            Console.WriteLine($"update_version {VersionOf(nacp)}");
        } else if (baseCtrl != null) {
            Console.WriteLine($"version {VersionOf(Nacp(baseCtrl))}");
        }
        if (updProg != null) {
            Console.WriteLine($"update {updProg.Header.TitleId:x16}");
            if (uctrl != null) Console.WriteLine($"update_name {TitleName(Nacp(uctrl))}");
        } else if (xci?.SystemUpdate != null) {
            Nca sysProg = Pick(xci.SystemUpdate, NcaContentType.Program);
            if (sysProg != null) Console.WriteLine($"system_update {sysProg.Header.TitleId:x16}");
        }
        return 0;
    }

    static string VersionOf(byte[] nacp) => System.Text.Encoding.ASCII.GetString(nacp, 0x3060, 0x10).TrimEnd('\0');

    // Icon: the control data holds one 256x256 JPEG per language (/icon_<language>.dat). Use the English one (American, then British); if neither exists, take the first in NACP language order
    static readonly string[] IconLangs = { "AmericanEnglish", "BritishEnglish", "Japanese", "French", "German", "LatinAmericanSpanish", "Spanish", "Italian",
                                           "Dutch", "CanadianFrench", "Portuguese", "Russian", "Korean", "TraditionalChinese", "SimplifiedChinese", "BrazilianPortuguese" };
    static (byte[], string) Icon(Nca ctrl) {
        IFileSystem fs = ctrl.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
        foreach (string lang in IconLangs) {
            using var f = new UniqueRef<IFile>();
            if (fs.OpenFile(ref f.Ref, $"/icon_{lang}.dat".ToU8Span(), OpenMode.Read).IsFailure()) continue;
            f.Get.GetSize(out long n).ThrowIfFailure();
            byte[] v = new byte[n]; f.Get.Read(out _, 0, v, ReadOption.None).ThrowIfFailure();
            return (v, lang);
        }
        return (null, null);
    }
    static byte[] Nacp(Nca ctrl) {   // the whole control.nacp (0x4000 bytes)
        IFileSystem fs = ctrl.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
        using var f = new UniqueRef<IFile>();
        fs.OpenFile(ref f.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).ThrowIfFailure();
        f.Get.GetSize(out long n).ThrowIfFailure();
        byte[] v = new byte[n]; f.Get.Read(out _, 0, v, ReadOption.None).ThrowIfFailure();
        return v;
    }
    // Game name: the NACP starts with 16 language entries of 0x300 bytes each (name 0x200 + publisher 0x100); take the first non-empty one (0 = American English); DisplayVersion is at 0x3060
    //   NACPs from newer SDKs (such as Mario 1.2.1) compress these 0x3000 bytes: a u16 size + raw deflate, inflating to 32 languages × 0x300. LibHac 0.19 does not know this, so it is decoded here
    static string TitleName(byte[] nacp) {
        byte[] t = nacp; int n = nacp[0] | nacp[1] << 8;
        if (n > 0 && n <= 0x2ffe) try {
            using var z = new System.IO.Compression.DeflateStream(new MemoryStream(nacp, 2, n), System.IO.Compression.CompressionMode.Decompress);
            var m = new MemoryStream(); z.CopyTo(m);
            if (m.Length == 0x6000) t = m.ToArray();   // a wrong length means an uncompressed name that happened to inflate
        } catch (InvalidDataException) { }   // not compressed
        for (int i = 0; i < (t == nacp ? 16 : 32); i++) {
            int z = Array.IndexOf(t, (byte)0, i * 0x300, 0x200), len = (z < 0 ? i * 0x300 + 0x200 : z) - i * 0x300;
            if (len > 0) return System.Text.Encoding.UTF8.GetString(t, i * 0x300, len);
        }
        return "";
    }

    // Copy out file by file; files identical to the same path in the compare dir become APFS clones instead
    static void Copy(IFileSystem fs, string dst, string cmpDir) {
        long n = 0, cloned = 0, bytes = 0, written = 0; byte[] buf = new byte[4 << 20], cb = new byte[4 << 20];
        foreach (DirectoryEntryEx e in fs.EnumerateEntries("*", SearchOptions.RecurseSubdirectories)) {
            string rel = e.FullPath.TrimStart('/'), op = Path.Combine(dst, rel);
            if (e.Type == DirectoryEntryType.Directory) { Directory.CreateDirectory(op); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(op)); n++; bytes += e.Size;
            using var f = new UniqueRef<IFile>();
            fs.OpenFile(ref f.Ref, e.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
            string cp = cmpDir == null ? null : Path.Combine(cmpDir, rel);
            if (cp != null && File.Exists(cp) && new FileInfo(cp).Length == e.Size && Same(f.Get, cp, e.Size, buf, cb)) {
                if (File.Exists(op)) File.Delete(op);
                if (clonefile(cp, op, 0) != 0) throw new IOException($"clonefile {cp} → {op} failed: errno {Marshal.GetLastWin32Error()}");
                cloned++; continue;
            }
            try { Write(f.Get, op, e.Size, buf); }
            catch (HorizonResultException x) when (HashError(x)) {
                g_damaged.Add(e.FullPath);
                if (!g_allow || g_lenient == null) { File.Delete(op); continue; }
                g_lenientFs ??= g_lenient();
                using var lf = new UniqueRef<IFile>();
                g_lenientFs.OpenFile(ref lf.Ref, e.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                Write(lf.Get, op, e.Size, buf);
            }
            written += e.Size;
        }
        Console.WriteLine($"{dst}: {n} files {bytes / 1048576.0:F1} MB, {cloned} identical to the compare dir (cloned), {written / 1048576.0:F1} MB newly written");
    }
    static void Write(IFile f, string op, long size, byte[] buf) {
        using var o = File.Create(op);
        for (long off = 0; off < size;) {
            f.Read(out long got, off, buf.AsSpan(0, (int)Math.Min(buf.Length, size - off)), ReadOption.None).ThrowIfFailure();
            if (got <= 0) throw new IOException($"cannot read @{off}");
            o.Write(buf, 0, (int)got); off += got;
        }
    }
    // List the RomFS only (path, size, first 32 bytes) without writing game files
    static void Manifest(IFileSystem fs, string path) {
        long n = 0, bytes = 0; byte[] h = new byte[32];
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var o = new StreamWriter(path);
        o.WriteLine("# path\tbytes\tfirst 32 bytes (hex)");
        foreach (DirectoryEntryEx e in fs.EnumerateEntries("*", SearchOptions.RecurseSubdirectories)) {
            if (e.Type == DirectoryEntryType.Directory) continue;
            using var f = new UniqueRef<IFile>();
            fs.OpenFile(ref f.Ref, e.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
            long got = 0; int want = (int)Math.Min(h.Length, e.Size);
            try { if (want > 0) f.Get.Read(out got, 0, h.AsSpan(0, want), ReadOption.None).ThrowIfFailure(); }
            catch (HorizonResultException x) when (HashError(x)) { g_damaged.Add(e.FullPath); got = 0; }   // the survey goes on; the file is listed as damaged
            o.WriteLine($"{e.FullPath}\t{e.Size}\t{Convert.ToHexString(h, 0, (int)got).ToLowerInvariant()}");
            n++; bytes += e.Size;
        }
        Console.WriteLine($"{path}: RomFS {n} files {bytes / 1048576.0:F1} MB (manifest only)");
    }
    static bool Same(IFile f, string path, long size, byte[] buf, byte[] cb) {
        using var c = File.OpenRead(path);
        for (long off = 0; off < size;) {
            int want = (int)Math.Min(buf.Length, size - off);
            f.Read(out long got, off, buf.AsSpan(0, want), ReadOption.None).ThrowIfFailure();
            c.ReadExactly(cb, 0, (int)got);
            if (!buf.AsSpan(0, (int)got).SequenceEqual(cb.AsSpan(0, (int)got))) return false;
            off += got;
        }
        return true;
    }
}
