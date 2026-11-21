// ============================================================================
//  Ledger.cs — 写前日志账本：先读原值 → 落盘 → 再写新值 → 校验
//  设计约束：
//   1) 任何写操作在落盘失败时必须中止，绝不出现「改了但还原不回来」；
//   2) 记录永远保留最早的原值（重复应用不会覆盖用户真实原始值）；
//   3) 原子写入（临时文件 + Replace），断电也不会留下半个 JSON；
//   4) 文件带格式版本号（v1 = 裸数组，v2 = { Ver, Entries }），读取兼容两代；
//   5) 截断 / 半行 / 手工改坏的账本先抢救再隔离，抢救到的记录立刻写回磁盘。
//
//  Rev86 锁语义：
//   旧实现用同一把锁既护内存列表、又护文件 IO，还夹着 Diag/托盘 trace 这种外部调用
//   ——持锁做 IO、持锁回调外部，正是死锁的两个经典来源。现在拆成：
//     _writer   唯一的状态锁：只改内存快照，绝不持锁做 IO、绝不持锁回调；
//     _fileLock 文件句柄锁：只包住一次「写临时文件 + 替换」，锁内没有托管回调。
//   两者从不嵌套获取（Save 先在 _writer 里取快照，释放后再进 _fileLock），
//   因此不可能出现锁序反转。读路径返回不可变快照，完全不加锁。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Go
{
    public class LedgerEntry
    {
        public string Id { get; set; }
        public string Time { get; set; }
        public string ActionId { get; set; }
        public string ActionName { get; set; }
        public string Kind { get; set; }        // Reg / Service / Bcd / PowerAc / Hibernate / Command
        public string Hive { get; set; }        // HKLM .. 仅 Reg
        public string Path { get; set; }        // 仅 Reg
        public string Name { get; set; }
        public string ValueType { get; set; }   // DWord / String / QWord / Multi
        public bool Existed { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public string Extra { get; set; }       // PowerAc: sub|set|index ; Command: 全命令
        public string Note { get; set; }
    }

    /// <summary>
    /// 账本文件的 v2 形状。版本号存在的意义是「以后能迁移」：读到比自己新的
    /// 版本时仍按 Entries 读取（条目形状没变），但会把这件事记进 Diag。
    /// </summary>
    public sealed class LedgerFile
    {
        public int Ver { get; set; }
        public LedgerEntry[] Entries { get; set; }
    }

    public class Ledger
    {
        /// <summary>当前写出的格式版本。v1 是 Rev85 及更早的裸数组。</summary>
        public const int FormatVersion = 2;

        /// <summary>
        /// 账本目录覆盖。null / 空 = 用真实用户目录。
        /// 这是显式开关（不是环境变量魔法）：自检与截图渲染必须先把账本
        /// （含 audit-*.log）挪到沙箱，否则测试数据会写进用户真实的审计账本。
        /// </summary>
        public static string DirOverride;

        /// <summary>
        /// 只读模式：--render 这类"看一眼"的进程必须打开它。
        /// 为什么：Ledger 构造函数会 Load，损坏时会 Quarantine（移走真实 ledger.json）
        /// 并立刻 Save 覆盖原件；而 --render 不占单实例锁、可以和真实实例并存 ——
        /// 一次截图就可能把用户真实的写前记录换掉。只读模式把这条路径整条关掉。
        /// </summary>
        public static bool ReadOnlyMode;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly object _auditLock = new object();
        /// <summary>跨进程写锁：命名互斥体不可用时退化为进程内串行（单实例本来也只允许一个写者）。</summary>
        private static readonly object _crossProcessFallback = new object();

        private readonly string _dir;
        private readonly string _file;
        private readonly bool _readOnly;
        /// <summary>唯一的状态锁：只护内存快照，锁内不做 IO、不回调外部。</summary>
        private readonly object _writer = new object();
        /// <summary>文件锁：串行化整个「临时文件 + 替换」，防止并发落盘互相截断。</summary>
        private readonly object _fileLock = new object();
        /// <summary>不可变快照：写者整体替换引用，读者直接取（读到的一半状态是不可能存在的）。</summary>
        private LedgerEntry[] _entries = new LedgerEntry[0];
        /// <summary>只读包装缓存：Entries 在页面每次重绘时都会被取，不该每次 new 一个包装。</summary>
        private ReadOnlyCollection<LedgerEntry> _view;
        private LedgerEntry[] _viewOf;
        /// <summary>读取阶段失败过：此时内存账本不代表磁盘内容，禁止覆盖写。</summary>
        private volatile bool _unreadable;
        /// <summary>
        /// 粘性污染：一旦读到过坏/读不出来的账本，本会话拒绝任何覆盖写。
        /// 旧实现只看「文件是否还在」——外部把文件删掉后守卫就失效，
        /// 一次"读失败 + 文件消失"会把空账本写成权威账本，直接丢掉全部写前记录。
        /// </summary>
        private volatile bool _poisoned;
        /// <summary>磁盘内容指纹（FNV-1a over chars）。用来发现"别的进程改过账本"。</summary>
        private string _diskFingerprint;
        /// <summary>内存快照的版本号（只在 _writer 内自增）。</summary>
        private int _saveSeq;
        /// <summary>已经落盘的版本号（只在 _fileLock 内推进）。</summary>
        private int _savedSeq;

        public string DirectoryPath { get { return _dir; } }
        public string FilePath { get { return _file; } }
        public bool ReadOnly { get { return _readOnly; } }

        /// <summary>本次会话读账本失败过：内存账本不代表磁盘内容（此时禁止覆盖写）。</summary>
        public bool Unreadable { get { return _unreadable; } }
        /// <summary>本会话是否因为一次读失败而彻底拒绝写盘（粘性，不会自己恢复）。</summary>
        public bool Poisoned { get { return _poisoned; } }

        /// <summary>当前生效的账本目录：沙箱覆盖优先，否则 LocalAppData 下的 GameOptimizer/ledger。</summary>
        public static string CurrentDir
        {
            get
            {
                string o = DirOverride;
                if (!string.IsNullOrEmpty(o)) return o;
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GameOptimizer", "ledger");
            }
        }

        /// <summary>
        /// 把账本与审计日志切到 TEMP 下的 GameOptimizer/ledger-sandbox/[pid]。
        /// 必须在第一个 Ledger 实例构造之前调用（Engine 的字段初始化就会构造）。
        /// 每个进程独立目录，并发跑两份自检不会互删记录。
        /// </summary>
        public static void UseSandboxDir()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GameOptimizer", "ledger-sandbox");
            int pid = 0;
            try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch (Exception ex)
            {
                // 取不到 pid 就用 0 兜底：目录不再隔离到进程，但仍不会碰真实账本。
                Log("ledger.sandbox.pid", ex);
            }
            PruneSandbox(root);
            DirOverride = System.IO.Path.Combine(root, pid.ToString());
        }

        /// <summary>
        /// 清掉旧的沙箱目录：四套自检每次运行都会在 TEMP 下留一份账本 + 审计日志，
        /// 从没人清理（实测一台机器上已经堆了 94 个目录）。只删超过 2 天的，
        /// 正在并行跑的其它实例（今天的目录）不会被误删。
        /// </summary>
        private static void PruneSandbox(string root)
        {
            try
            {
                if (!System.IO.Directory.Exists(root)) return;
                string[] dirs = System.IO.Directory.GetDirectories(root);
                DateTime cut = DateTime.UtcNow.AddDays(-2);
                for (int i = 0; i < dirs.Length; i++)
                {
                    try
                    {
                        if (System.IO.Directory.GetLastWriteTimeUtc(dirs[i]) > cut) continue;
                        System.IO.Directory.Delete(dirs[i], true);
                    }
                    catch (Exception ex) { Diag.Fail("ledger.sandbox.prune", ex); }
                }
            }
            catch (Exception ex) { Diag.Fail("ledger.sandbox.prune.root", ex); }
        }

        public Ledger()
        {
            _dir = CurrentDir;
            _file = System.IO.Path.Combine(_dir, "ledger.json");
            _readOnly = ReadOnlyMode;
            Load();
        }

        public IList<LedgerEntry> Entries
        {
            // 只读包装按快照缓存：快照一换，包装跟着换（页面每帧取 Entries 时不再分配）。
            get
            {
                LedgerEntry[] snap = _entries;
                ReadOnlyCollection<LedgerEntry> v = _view;
                if (v != null && object.ReferenceEquals(_viewOf, snap)) return v;
                v = Array.AsReadOnly(snap);
                _viewOf = snap;
                _view = v;
                return v;
            }
        }

        public int Count { get { return _entries.Length; } }

        // ---- 读取与抢救 ------------------------------------------------------
        private void Load()
        {
            bool missing;
            string txt = ReadAllWithRetry(_file, out missing);
            if (missing)
            {
                // 主文件不存在，但崩溃可能留下一个**完整**的 .tmp（写完临时文件、
                // 还没来得及 Replace 就断电/被杀）。它比"空账本"更接近真相。
                AdoptTempIfComplete();
                return;
            }
            if (txt == null) return;                 // 读失败：ReadAllWithRetry 已置 poisoned
            _diskFingerprint = Fingerprint(txt);
            if (string.IsNullOrWhiteSpace(txt)) return;

            LedgerEntry[] read = ReadEntries(txt, true);
            if (read != null) { _entries = read; return; }

            // 截断 / 半行 / 手工改坏：先按「最后一条完整记录」抢救，再隔离原件。
            LedgerEntry[] salvage = SalvageEntries(txt);
            LedgerEntry[] fromTmp = TryReadTempEntries();
            if (fromTmp != null && fromTmp.Length > (salvage == null ? 0 : salvage.Length)) salvage = fromTmp;
            _entries = salvage == null ? new LedgerEntry[0] : salvage;

            if (_readOnly)
            {
                // 只读进程只抢救到内存，绝不迁移/覆盖磁盘原件。
                Diag.Hit("ledger.readonly.corrupt");
                return;
            }

            Quarantine(salvage != null && salvage.Length > 0);
            // 抢救出来的记录只留在内存里是不够的：Quarantine 会把原文件挪走/留证，
            // 而这次运行如果只是「打开看了一眼」就不会触发 Save —— 下次启动 ledger.json
            // 已经不存在，抢救到的写前记录全部消失，系统停在改过的状态且无记录可还原。
            if (salvage != null && salvage.Length > 0)
            {
                try { Save(); }
                catch (Exception ex) { Log("ledger.salvage.save", ex); }
            }
        }

        /// <summary>
        /// 读文件：最多重试 3 次。杀软扫描/另一个实例正在 Replace 时会短暂 sharing violation，
        /// 一次瞬时冲突不该被判成"账本读不出来，本会话禁止写"。
        /// 打开共享包含 Delete：File.ReadAllText 默认不带它，而我们自己的 Replace 需要。
        /// </summary>
        private string ReadAllWithRetry(string path, out bool missing)
        {
            missing = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                          FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                    {
                        return sr.ReadToEnd();
                    }
                }
                catch (FileNotFoundException) { missing = true; return null; }
                catch (DirectoryNotFoundException) { missing = true; return null; }
                catch (Exception ex)
                {
                    if (attempt == 2)
                    {
                        _unreadable = true;
                        _poisoned = true;
                        Log("ledger.read", ex);
                        _entries = new LedgerEntry[0];
                        return null;
                    }
                    Thread.Sleep(40);
                }
            }
            return null;
        }

        /// <summary>主账本缺失时的兜底：完整的 .tmp 就是上一轮已经写好的内容。</summary>
        private void AdoptTempIfComplete()
        {
            LedgerEntry[] tmp = TryReadTempEntries();
            if (tmp == null || tmp.Length == 0) return;
            _entries = tmp;
            Diag.Hit("ledger.tmp.adopted");
            if (_readOnly) return;
            try { Save(); }
            catch (Exception ex) { Log("ledger.tmp.adopt", ex); }
        }

        private LedgerEntry[] TryReadTempEntries()
        {
            bool missing;
            // 用"不置污染位"的读：临时文件读不出来（被杀软占用）不该让整个会话拒绝写账本。
            string txt = ReadTextQuiet(_file + ".tmp", out missing);
            if (txt == null || string.IsNullOrWhiteSpace(txt)) return null;
            return ReadEntries(txt, false);
        }

        /// <summary>
        /// 只读一次、不改变任何会话状态。用于指纹比对与 .tmp 探测：
        /// 一次瞬时占用不能把账本判成"读失败"，从而永久禁止本会话写盘。
        /// </summary>
        private static string ReadTextQuiet(string path, out bool missing)
        {
            missing = false;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                {
                    return sr.ReadToEnd();
                }
            }
            catch (FileNotFoundException) { missing = true; return null; }
            catch (DirectoryNotFoundException) { missing = true; return null; }
            catch (Exception ex)
            {
                Log("ledger.read.quiet", ex);
                return null;
            }
        }

        private static string Fingerprint(string s)
        {
            // FNV-1a（字符级）：只用来回答"磁盘上的内容和我知道的那份是不是同一份"。
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }
            return h.ToString("X8") + ":" + s.Length.ToString();
        }

        /// <summary>整份读取：按首字符分派 v1（裸数组）/ v2（信封）。失败返回 null。</summary>
        private static LedgerEntry[] ReadEntries(string txt, bool log)
        {
            int i = 0;
            while (i < txt.Length && char.IsWhiteSpace(txt[i])) i++;
            if (i >= txt.Length) return null;
            try
            {
                if (txt[i] == '[') return ParseArray(txt);
                if (txt[i] == '{')
                {
                    JavaScriptSerializer js = NewSerializer();
                    LedgerFile f = js.Deserialize<LedgerFile>(txt);
                    if (f == null) return null;
                    // 自己写不出的版本：条目形状可能已经变了。能读多少读多少，但要留痕，
                    // 免得把「读到了但语义不同」伪装成正常。
                    if (f.Ver > FormatVersion) Diag.Hit("ledger.version.newer");
                    return f.Entries == null ? new LedgerEntry[0] : f.Entries;
                }
            }
            catch (Exception ex)
            {
                if (log)
                {
                    Log("ledger.parse", ex);
                }
            }
            return null;
        }

        private static LedgerEntry[] ParseArray(string json)
        {
            JavaScriptSerializer js = NewSerializer();
            return js.Deserialize<LedgerEntry[]>(json);
        }

        private static JavaScriptSerializer NewSerializer()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 8 * 1024 * 1024;
            return js;
        }

        /// <summary>
        /// 把被截断的账本补成合法数组：保留最后一个完整记录 '}' 之前的全部条目。
        /// 只跳过残行是不够的——整个数组解析失败会丢掉全部历史。截断点可能落在字符串
        /// 值内部（路径/命令里带 '}'），所以从后往前逐条回退，最多试 32 个边界。
        /// </summary>
        private static LedgerEntry[] SalvageEntries(string txt)
        {
            int start = txt.IndexOf('[');
            if (start < 0) return null;
            int end = txt.LastIndexOf('}');
            for (int attempt = 0; attempt < 32 && end > start; attempt++)
            {
                LedgerEntry[] arr = TryParseSlice(txt, start, end);
                if (arr != null) return arr;
                end = txt.LastIndexOf('}', end - 1);
            }
            return null;
        }

        private static LedgerEntry[] TryParseSlice(string txt, int start, int end)
        {
            string body = txt.Substring(start, end - start + 1).TrimEnd();
            body = body.TrimEnd(',');
            if (body.Length < 2) return null;
            try
            {
                return ParseArray(body + "]");
            }
            catch (Exception)
            {
                // 抢救路径本来就是赌格式：这一段拼不成数组只说明截断点更靠前，交给上一层回退。
                return null;
            }
        }

        /// <summary>损坏的账本原样存证，绝不静默丢弃。</summary>
        private void Quarantine(bool keepOriginal)
        {
            try
            {
                if (!File.Exists(_file)) return;
                string dst = NextQuarantinePath();
                // 抢救成功时留一份证据（原文件稍后被 Save 覆盖）；没抢救出来就挪走。
                if (keepOriginal) File.Copy(_file, dst, false);
                else File.Move(_file, dst);
            }
            catch (Exception ex)
            {
                // 迁移失败时原文件保持原样；下一次 Save 才会覆盖它。
                Log("ledger.quarantine", ex);
            }
        }

        /// <summary>同一秒内两次损坏也要各留一份证据：旧实现撞名就直接放弃，第二次的现场没了。</summary>
        private string NextQuarantinePath()
        {
            string stem = _file + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dst = stem;
            for (int i = 2; i < 100 && File.Exists(dst); i++) dst = stem + "-" + i;
            return dst;
        }

        // ---- 写入 ------------------------------------------------------------
        /// <summary>原子落盘。失败抛异常 —— 调用方必须中止后续写入。</summary>
        public void Save()
        {
            if (_readOnly)
                throw new InvalidOperationException("账本以只读模式打开，拒绝写入：" + _file);

            string json;
            int seq;
            // 序列化在状态锁里只做内存拷贝与格式化，不碰磁盘。
            lock (_writer)
            {
                json = Serialize(_entries);
                seq = _saveSeq;
            }

            Exception cleanupFail = null;
            Mutex gate = null;
            bool owned = false;
            try
            {
                lock (_fileLock)
                {
                    // 读阶段失败过：内存账本不代表磁盘内容，覆盖就是数据丢失。
                    // 这条守卫是**粘性**的：即使外部随后把文件删掉也不能解除，
                    // 否则"一次读失败 + 文件消失"会把空账本写成权威账本。
                    if (_poisoned)
                        throw new InvalidOperationException("账本读取失败过，为保护现有数据本次会话拒绝覆盖写入：" + _file);

                    // 更新的快照已经落盘了（另一条线程先写完）：本次跳过。
                    // 快照是整份账本，所以后写的快照必然包含先写的改动，跳过不会丢记录。
                    if (seq < _savedSeq) return;

                    // 跨进程串行：单实例锁挡住了常规路径，但 GO_INSTANCE_KEY 的并行自检/
                    // 两个 --render 仍可能同时写同一目录。命名互斥体拿不到就中止（拒绝而非覆盖）。
                    gate = TryOpenWriteGate();
                    if (gate != null)
                    {
                        try { owned = gate.WaitOne(3000); }
                        catch (AbandonedMutexException) { owned = true; }   // 上一个写者崩了：接管
                        catch (Exception ex) { Diag.Fail("ledger.gate.wait", ex); }
                        if (!owned)
                            throw new InvalidOperationException("另一个进程正在写账本，本次写入已放弃（系统未被修改）");
                    }

                    lock (_crossProcessFallback)
                    {
                        // 磁盘上的内容被别的写者换过：整份替换会把对方刚写的记录抹掉。
                        // 拒绝覆盖是安全方向——用户会看到明确原因，而不是悄悄丢一条写前记录。
                        if (ExternalChanged())
                            throw new InvalidOperationException("账本已被其它进程修改，为避免覆盖已放弃本次写入；请重启本程序后再操作");

                        string tmp = _file + ".tmp";
                        try
                        {
                            if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                            using (StreamWriter sw = new StreamWriter(fs, Utf8NoBom))
                            {
                                sw.Write(json);
                                sw.Flush();        // 先把手写缓冲推进 FileStream
                                fs.Flush(true);    // 再 FlushFileBuffers：真正落盘，不是只到 OS 缓存
                            }
                            ReplaceWithRetry(tmp, _file);
                            _savedSeq = seq;
                            _diskFingerprint = Fingerprint(json);
                        }
                        catch
                        {
                            // 失败不留半截 .tmp（残留会让人误判「已写入」）；记账放到锁外。
                            try { if (File.Exists(tmp)) File.Delete(tmp); }
                            catch (Exception ex) { cleanupFail = ex; }
                            throw;   // 调用方必须知道这次没落盘
                        }
                    }
                }
            }
            finally
            {
                if (owned)
                {
                    try { gate.ReleaseMutex(); }
                    catch (Exception ex) { Diag.Fail("ledger.gate.release", ex); }
                }
                if (gate != null)
                {
                    try { gate.Dispose(); }
                    catch (Exception ex) { Diag.Fail("ledger.gate.dispose", ex); }
                }
                if (cleanupFail != null) Log("ledger.tmpclean", cleanupFail);
            }
        }

        private static Mutex TryOpenWriteGate()
        {
            try { return new Mutex(false, @"Global\GameOptimizerLedgerWrite"); }
            catch (Exception ex)
            {
                // 标准用户在 Global\ 下可能没有建对象权限：退回进程内串行（单实例已足够）。
                Diag.Fail("ledger.gate.open", ex);
                return null;
            }
        }

        /// <summary>Replace 在杀软扫描新文件时会短暂失败：重试几次，别把一次占用报成写失败。</summary>
        private static void ReplaceWithRetry(string tmp, string dst)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(dst)) File.Replace(tmp, dst, null);
                    else File.Move(tmp, dst);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt >= 4)
                    {
                        Diag.Fail("ledger.replace", ex);
                        throw;
                    }
                    Thread.Sleep(40);
                }
            }
        }

        /// <summary>磁盘上的账本是否已经被我之外的写者换过（内容指纹不同）。</summary>
        private bool ExternalChanged()
        {
            string known = _diskFingerprint;
            if (known == null) return false;          // 没读到过（文件本来就不存在）：不拦
            bool missing;
            // 这里必须用不置污染位的读：杀软扫一下不该把本会话永久判成"账本不可写"。
            string now = ReadTextQuiet(_file, out missing);
            if (missing) return false;                // 文件被删：写回我们这份是恢复，不是覆盖
            if (now == null) return false;            // 读不了：交给 Replace 自己失败，不误报
            return Fingerprint(now) != known;
        }

        private static string Serialize(LedgerEntry[] entries)
        {
            LedgerFile f = new LedgerFile();
            f.Ver = FormatVersion;
            f.Entries = entries;
            return NewSerializer().Serialize(f);
        }

        private static bool SameKey(LedgerEntry e, string actionId, string kind, string hive, string path, string name, string extra)
        {
            if (e.ActionId != actionId || e.Kind != kind) return false;
            if (kind == "Reg")
            {
                return e.Hive == hive
                    && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase);
            }
            if (kind == "RegKv")
            {
                return e.Hive == hive
                    && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(KvSubKey(e.Extra), KvSubKey(extra), StringComparison.OrdinalIgnoreCase);
            }
            return e.Name == name;
        }

        private static string KvSubKey(string extra)
        {
            if (string.IsNullOrEmpty(extra)) return "";
            int bar = extra.IndexOf('|');
            return bar > 0 ? extra.Substring(0, bar) : extra;
        }

        public bool Has(string actionId, string kind, string hive, string path, string name)
        {
            LedgerEntry[] snap = _entries;
            for (int i = 0; i < snap.Length; i++)
            {
                if (SameKey(snap[i], actionId, kind, hive, path, name, null)) return true;
            }
            return false;
        }

        /// <summary>
        /// 追加；同键已存在则保留最早记录。返回账本里**真正生效**的那一条：
        /// 去重命中时是既有记录，否则是 e 本身。调用方必须用它来保存 Id ——
        /// 旧实现去重后静默丢弃新条目，但调用方仍拿着新 Id，回滚 Remove(新 Id) 会打空，
        /// 账本里留下一条永远清不掉的"幽灵记录"。
        /// </summary>
        public LedgerEntry Add(LedgerEntry e)
        {
            if (e == null) return null;
            lock (_writer)
            {
                LedgerEntry[] snap = _entries;
                for (int i = 0; i < snap.Length; i++)
                    if (SameKey(snap[i], e.ActionId, e.Kind, e.Hive, e.Path, e.Name, e.Extra)) return snap[i];

                LedgerEntry[] next = new LedgerEntry[snap.Length + 1];
                Array.Copy(snap, next, snap.Length);
                next[snap.Length] = e;
                _entries = next;
                _saveSeq++;
                return e;
            }
        }

        public void Remove(string id)
        {
            lock (_writer)
            {
                LedgerEntry[] snap = _entries;
                int at = -1;
                for (int i = 0; i < snap.Length; i++)
                    if (snap[i].Id == id) { at = i; break; }
                if (at < 0) return;

                LedgerEntry[] next = new LedgerEntry[snap.Length - 1];
                if (at > 0) Array.Copy(snap, 0, next, 0, at);
                if (at < snap.Length - 1) Array.Copy(snap, at + 1, next, at, snap.Length - at - 1);
                _entries = next;
                _saveSeq++;
            }
        }

        public void Clear()
        {
            lock (_writer)
            {
                if (_entries.Length == 0) return;
                _entries = new LedgerEntry[0];
                _saveSeq++;
            }
        }

        public void AppendAudit(string line)
        {
            Audit(line);
        }

        /// <summary>
        /// 审计日志追加（静态入口：不需要 Ledger 实例，电源恢复等退出路径也能留证）。
        /// 调用点全是用户动作/游戏生命周期事件，没有周期性高频调用，因此不做 StreamWriter 缓存
        /// ——逐行打开只在这十几次/一天的量级上是划算的（缓存反而会在崩溃时丢行）。
        /// _auditLock 只护一次 AppendAllText，锁内没有任何托管回调，不会和状态锁交叉。
        /// </summary>
        public static void Audit(string line)
        {
            try
            {
                string dir = CurrentDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string p = System.IO.Path.Combine(dir, "audit-" + DateTime.Now.ToString("yyyyMM") + ".log");
                lock (_auditLock)
                {
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                // 审计写不进去不能反过来让业务失败（返回值才是真相）。
                // 但静默失败会让「谁改的」无从查起：Diag 记账 + 托盘 trace 留证。
                Log("ledger.audit", ex);
            }
        }

        /// <summary>诊断落点：先 Diag 再托盘 trace。两者都失败也不能把业务带崩。</summary>
        private static void Log(string tag, Exception ex)
        {
            Diag.Fail(tag, ex);
            try { Go.Tray.TrayTrace.Write(tag + ": " + ex.Message); }
            catch (Exception tx)
            {
                // Diag 已记账；trace 是最后一道记录手段，不能再抛（只再计一个数，不再调 trace）。
                Diag.Fail("ledger.trace", tx);
            }
        }
    }
}
