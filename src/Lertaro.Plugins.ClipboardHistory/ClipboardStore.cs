using System.IO;
using System.Text.Json;
using Lertaro.PluginSdk;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 剪贴板历史存储。三层结构，为"长期使用、条目持续增长"设计：
///   1. 索引层（常驻内存）：Cf. ClipboardIndexEntry —— 只有预览+元数据，约 2 KB/条；
///   2. 负载层：文本在 P1 的内存字典里（P2 换磁盘/SQLite，接口不变）；
///      图片是缓存目录里的 PNG 文件，字典只存路径；
///   3. 检索层：新→旧的顺序扫描 + 廉价子串预筛，命中够了就停，不做全量模糊匹配。
///
/// 三重上限，任何一维超标都会从最旧的开始淘汰（已固定的条目豁免）：
///   条数上限、单条时效、负载总预算。
///   注意：预算单位是"字符"，图片按字节计入（1 字节 = 1 字符），文本实际占用的
///   UTF-16 内存约是字符数的 2 倍 —— 混算偏保守，只会提前淘汰，不会超用。
/// </summary>
internal sealed class ClipboardStore
{
    /// <summary>实例编号：诊断"同进程出现多份 store"用（每 new 一个自增）。</summary>
    private static int _instanceCounter;
    internal int InstanceId { get; }

    private readonly object _gate = new();

    private readonly List<ClipboardIndexEntry> _index = [];          // 新 → 旧
    private readonly Dictionary<ulong, ClipboardIndexEntry> _byHash = [];
    private readonly Dictionary<long, string> _payloads = [];        // 文本全文（P1：内存）
    private readonly Dictionary<long, string> _imagePayloads = [];   // 图片 PNG 路径
    private readonly List<string> _pendingImageDeletes = [];         // 淘汰的图片文件，锁外删

    private readonly int _capacity;
    private readonly TimeSpan _maxAge;
    private readonly long _payloadCharBudget;

    private long _nextId = 1;
    private long _payloadChars;
    private int _pinnedCount;
    private int _version;
    private bool _dirty;
    private ClipboardIndexEntry[] _cachedSnapshot = [];
    private int _cachedVersion = -1;

    /// <summary>内容有变化且尚未落盘。由持久化定时器轮询消费。</summary>
    internal bool Dirty
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    internal ClipboardStore(
        int capacity,
        TimeSpan maxAge,
        long payloadCharBudget = 64L * 1024 * 1024)
    {
        InstanceId = Interlocked.Increment(ref _instanceCounter);
        _capacity = Math.Max(1, capacity);
        // 0 或负数 = 不限时（TimeSpan.MaxValue）。必须归一化：
        // 否则 TimeSpan.Zero 会被 Trim 解释为"全部立即过期"，把非收藏记录清空。
        _maxAge = maxAge <= TimeSpan.Zero ? TimeSpan.MaxValue : maxAge;
        _payloadCharBudget = Math.Max(1024, payloadCharBudget);
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    internal int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>全文总字符数，用于观察内存占用。</summary>
    internal long PayloadChars
    {
        get
        {
            lock (_gate)
            {
                return _payloadChars;
            }
        }
    }

    /// <summary>
    /// 写入一条记录。重复内容提到最前，不新增条目。
    /// 返回是否为新内容。
    /// </summary>
    internal bool Add(string text, string? sourceProcess)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var hash = ClipboardIndexEntry.HashOf(text);
        bool added;

        lock (_gate)
        {
            if (_byHash.TryGetValue(hash, out var existing))
            {
                var index = _index.IndexOf(existing);
                if (index > 0)
                {
                    _index.RemoveAt(index);
                    _index.Insert(0, existing);
                    _version++;
                    _dirty = true;
                }

                DeletePendingImages();
                return false;
            }

            var entry = new ClipboardIndexEntry(_nextId++, text, sourceProcess, DateTime.Now);
            _index.Insert(0, entry);
            _byHash[hash] = entry;
            _payloads[entry.Id] = text;
            _payloadChars += text.Length;

            Trim();
            _version++;
            _dirty = true;
            added = true;
        }

        DeletePendingImages();
        return added;
    }

    /// <summary>取全文。P1 直接从内存字典取；P2 换成磁盘读取时签名不变。图片条目返回 null。</summary>
    internal string? TryGetText(long id)
    {
        lock (_gate)
        {
            return _payloads.TryGetValue(id, out var text) ? text : null;
        }
    }

    /// <summary>
    /// 写入一条文件记录。负载是换行分隔的路径清单（体积小，与文本共用负载层）。
    /// 哈希由调用方对清单字符串计算；返回 false 表示重复，旧条目已置顶。
    /// </summary>
    internal bool AddFiles(string pathList, ulong hash, int fileCount, string? sourceProcess)
    {
        bool added;

        lock (_gate)
        {
            if (_byHash.TryGetValue(hash, out var existing))
            {
                var index = _index.IndexOf(existing);
                if (index > 0)
                {
                    _index.RemoveAt(index);
                    _index.Insert(0, existing);
                    _version++;
                    _dirty = true;
                }

                return false;
            }

            var entry = new ClipboardIndexEntry(_nextId++, hash, fileCount, pathList, sourceProcess, DateTime.Now);
            _index.Insert(0, entry);
            _byHash[hash] = entry;
            _payloads[entry.Id] = pathList;
            _payloadChars += pathList.Length;

            Trim();
            _version++;
            _dirty = true;
            added = true;
        }

        DeletePendingImages();
        return added;
    }

    /// <summary>取图片的 PNG 文件路径。文本条目返回 null。</summary>
    internal string? TryGetImagePath(long id)
    {
        lock (_gate)
        {
            return _imagePayloads.TryGetValue(id, out var path) ? path : null;
        }
    }

    /// <summary>
    /// 历史里出现过的全部来源进程名（去重、按名称排序）。
    /// 供面板的来源下拉筛选器填充选项。
    /// </summary>
    internal List<string> SourceProcesses()
    {
        lock (_gate)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _index)
            {
                if (!string.IsNullOrEmpty(entry.SourceProcess))
                {
                    set.Add(entry.SourceProcess!);
                }
            }

            var list = set.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }

    /// <summary>
    /// 写入一条图片记录。哈希由调用方对原始剪贴板字节计算 —— 同一张图重复复制去重并置顶。
    /// 返回 false 表示与已有条目重复，调用方应删除刚落盘的 PNG 文件。
    /// </summary>
    internal bool AddImage(
        string imagePath,
        ulong hash,
        int pixelWidth,
        int pixelHeight,
        long payloadBytes,
        string? sourceProcess)
    {
        bool added;

        lock (_gate)
        {
            if (_byHash.TryGetValue(hash, out var existing))
            {
                var index = _index.IndexOf(existing);
                if (index > 0)
                {
                    _index.RemoveAt(index);
                    _index.Insert(0, existing);
                    _version++;
                    _dirty = true;
                }

                return false;
            }

            var entry = new ClipboardIndexEntry(
                _nextId++, hash, pixelWidth, pixelHeight, payloadBytes, imagePath, sourceProcess, DateTime.Now);

            _index.Insert(0, entry);
            _byHash[hash] = entry;
            _imagePayloads[entry.Id] = imagePath;
            _payloadChars += payloadBytes;

            Trim();
            _version++;
            _dirty = true;
            added = true;
        }

        DeletePendingImages();
        return added;
    }

    /// <summary>按时间倒序索引快照。内容未变时复用同一个数组，调用方不得修改。</summary>
    internal IReadOnlyList<ClipboardIndexEntry> Snapshot()
    {
        lock (_gate)
        {
            if (_cachedVersion == _version)
            {
                return _cachedSnapshot;
            }

            _cachedSnapshot = _index.ToArray();
            _cachedVersion = _version;
            return _cachedSnapshot;
        }
    }

    /// <summary>
    /// 两级检索：
    ///   一级 = 在索引的 SearchText 上做子串过滤（零分配扫描，命中 limit 条立即返回）；
    ///   二级 = 只有一级没凑够才做模糊匹配，且只扫最近 fuzzyScanLimit 条，成本有上界。
    /// 由于索引是"新→旧"，一级取到的就是最新的一批命中项。
    ///
    /// 来源筛选：过滤词里带 <c>from:应用名</c> 记号的 token（可多个、大小写不敏感）
    /// 会按 SourceProcess 匹配，如 "from:weixin" = 只看在微信里复制的；
    /// "from:weixin token" = 微信里复制的且内容含 token。其余 token 继续走内容检索。
    /// </summary>
    internal IReadOnlyList<ClipboardIndexEntry> Search(
        string? filter,
        int limit,
        Func<string, string, bool>? fuzzyMatcher = null,
        int fuzzyScanLimit = 1500)
    {
        if (limit <= 0)
        {
            return [];
        }

        var snapshot = Snapshot();
        if (snapshot.Count == 0)
        {
            return [];
        }

        // 解析 from: 记号（大小写不敏感、全角冒号兼容、可多个 = AND、可带空格）
        List<string>? sourceTerms = null;
        List<string>? contentParts = null;
        if (!string.IsNullOrEmpty(filter))
        {
            foreach (var rawToken in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var token = rawToken.Trim().Replace('：', ':');
                if (token.Length > 5 && token.StartsWith("from:", StringComparison.OrdinalIgnoreCase))
                {
                    var term = token[5..].Trim();
                    if (term.Length > 0)
                    {
                        (sourceTerms ??= []).Add(term);
                    }

                    continue;
                }

                (contentParts ??= []).Add(token);
            }
        }

        var contentFilter = contentParts is null ? string.Empty : string.Join(' ', contentParts);
        var hasSourceTerms = sourceTerms is not null;

        if (contentFilter.Length == 0 && !hasSourceTerms)
        {
            var count = Math.Min(limit, snapshot.Count);
            var head = new List<ClipboardIndexEntry>(count);
            for (var i = 0; i < count; i++)
            {
                head.Add(snapshot[i]);
            }

            return head;
        }

        bool MatchesSource(ClipboardIndexEntry entry)
        {
            if (!hasSourceTerms)
            {
                return true;
            }

            if (string.IsNullOrEmpty(entry.SourceProcess))
            {
                return false;
            }

            foreach (var term in sourceTerms!)
            {
                if (!entry.SourceProcess.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        var results = new List<ClipboardIndexEntry>(Math.Min(limit, 16));

        if (contentFilter.Length == 0)
        {
            // 纯来源筛选：from:weixin
            foreach (var entry in snapshot)
            {
                if (!MatchesSource(entry))
                {
                    continue;
                }

                results.Add(entry);
                if (results.Count >= limit)
                {
                    break;
                }
            }

            return results;
        }

        var needle = contentFilter.ToLowerInvariant();

        foreach (var entry in snapshot)
        {
            if (!MatchesSource(entry))
            {
                continue;
            }

            if (entry.SearchText.Contains(needle, StringComparison.Ordinal))
            {
                results.Add(entry);
                if (results.Count >= limit)
                {
                    return results;
                }
            }
        }

        if (fuzzyMatcher is null || results.Count >= limit)
        {
            return results;
        }

        // 模糊只在最近的一批里找，避免条目上万时每次击键都全量打分。
        // 去重用 HashSet：之前用 results.Contains，最坏 O(limit²) 次比较。
        var seen = new HashSet<ClipboardIndexEntry>(results);
        var scanned = 0;
        foreach (var entry in snapshot)
        {
            if (scanned++ >= fuzzyScanLimit)
            {
                break;
            }

            if (!MatchesSource(entry) || !seen.Add(entry))
            {
                continue;
            }

            if (fuzzyMatcher(contentFilter, entry.SearchText))
            {
                results.Add(entry);
                if (results.Count >= limit)
                {
                    break;
                }
            }
        }

        return results;
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _index.Clear();
            _byHash.Clear();
            _payloads.Clear();
            _payloadChars = 0;
            _pinnedCount = 0;
            _pendingImageDeletes.AddRange(_imagePayloads.Values);
            _imagePayloads.Clear();
            _version++;
            _dirty = true;
        }

        DeletePendingImages();
    }

    /// <summary>删除单条（面板里的 Ctrl+Delete）。</summary>
    internal bool Remove(long id)
    {
        bool removed;
        lock (_gate)
        {
            var position = _index.FindIndex(e => e.Id == id);
            if (position < 0)
            {
                return false;
            }

            RemoveAt(position);
            _version++;
            _dirty = true;
            removed = true;
        }

        DeletePendingImages();
        return removed;
    }

    /// <summary>切换固定状态（面板里的 Ctrl+P）。固定的条目在时效淘汰时豁免。</summary>
    internal bool TogglePin(long id)
    {
        lock (_gate)
        {
            var entry = _index.Find(e => e.Id == id);
            if (entry is null)
            {
                return false;
            }

            entry.IsPinned = !entry.IsPinned;
            _pinnedCount += entry.IsPinned ? 1 : -1;
            _version++;
            _dirty = true;
            return entry.IsPinned;
        }
    }

    /// <summary>
    /// 切换收藏。收藏条目永不淘汰（Trim 跳过）、随快照永久持久化，
    /// 面板里单独成组置顶展示；与置顶（仅排序）语义分离。
    /// </summary>
    internal bool ToggleFavorite(long id)
    {
        lock (_gate)
        {
            var entry = _index.Find(e => e.Id == id);
            if (entry is null)
            {
                return false;
            }

            entry.IsFavorite = !entry.IsFavorite;
            var released = DropIfOrphaned(entry);
            _version++;
            _dirty = true;
            return !released && entry.IsFavorite;
        }
    }

    /// <summary>
    /// 切换待办。**标记不改变位置**：默认不自动置顶（设置 TodoAutoPin 打开时才置顶）；
    /// 取消待办时若该条已无任何标记且只存在于标记存储里，则从列表移除。
    /// </summary>
    internal bool ToggleTodo(long id)
    {
        lock (_gate)
        {
            var entry = _index.Find(e => e.Id == id);
            if (entry is null)
            {
                return false;
            }

            entry.IsTodo = !entry.IsTodo;
            if (entry.IsTodo)
            {
                entry.IsTodoArchived = false;
                entry.ArchivedAt = null;

                if (ClipboardSettings.Current.TodoAutoPin)
                {
                    SetPinnedCore(entry, true, null);
                }
            }
            else
            {
                entry.RemindAt = null;
                entry.IsTodoArchived = false;
                entry.ArchivedAt = null;
            }

            var released = DropIfOrphaned(entry);
            _version++;
            _dirty = true;
            return !released && entry.IsTodo;
        }
    }

    /// <summary>归档 / 取消归档待办。归档 ≠ 删除：内容与记录都保留，只是从进行中收起。</summary>
    internal bool ToggleTodoArchive(long id)
    {
        lock (_gate)
        {
            var entry = _index.Find(e => e.Id == id);
            if (entry is null || !entry.IsTodo)
            {
                return false;
            }

            entry.IsTodoArchived = !entry.IsTodoArchived;
            entry.ArchivedAt = entry.IsTodoArchived ? DateTime.Now : null;
            if (entry.IsTodoArchived)
            {
                // 归档即完成：提醒与置顶一并撤销，避免归档项继续提醒
                entry.RemindAt = null;
                SetPinnedCore(entry, false, null);
            }

            _version++;
            _dirty = true;
            return entry.IsTodoArchived;
        }
    }

    /// <summary>
    /// 取消标记后，若该条已无任何标记、且内容只存在于标记存储（不在历史里），
    /// 就从列表移除 —— 它的内容本就只因为"被标记"才被保留。返回是否已移除。
    /// </summary>
    private bool DropIfOrphaned(ClipboardIndexEntry entry)
    {
        if (entry.IsFavorite || entry.IsTodo || !entry.MarksOnly)
        {
            return false;
        }

        var position = _index.IndexOf(entry);
        if (position >= 0)
        {
            RemoveAt(position);
            _version++;
            _dirty = true;
        }

        return true;
    }

    /// <summary>设置置顶及其到期时间（null = 无期限）。</summary>
    internal void SetPin(long id, DateTime? until)
    {
        lock (_gate)
        {
            var entry = _index.Find(e => e.Id == id);
            if (entry is null)
            {
                return;
            }

            SetPinnedCore(entry, true, until);
            _version++;
            _dirty = true;
        }
    }

    /// <summary>取消置顶（不动收藏/待办标记 —— 标记与位置解耦）。</summary>
    internal void ClearPin(long id)
    {
        lock (_gate)
        {
            var entry = _index.Find(e => e.Id == id);
            if (entry is null)
            {
                return;
            }

            SetPinnedCore(entry, false, null);
            _version++;
            _dirty = true;
        }
    }

    /// <summary>设置提醒时间（单次）。设提醒即视为待办并置顶。</summary>
    internal void SetReminder(long id, DateTime? at)
    {
        lock (_gate)
        {
            var entry = _index.Find(e => e.Id == id);
            if (entry is null)
            {
                return;
            }

            entry.RemindAt = at;
            if (at is not null)
            {
                // 设提醒即视为待办（标记），但不改变位置 —— 除非用户开了"待办自动置顶"
                entry.IsTodo = true;
                entry.IsTodoArchived = false;
                entry.ArchivedAt = null;
                if (ClipboardSettings.Current.TodoAutoPin)
                {
                    SetPinnedCore(entry, true, null);
                }
            }

            _version++;
            _dirty = true;
        }
    }

    private void SetPinnedCore(ClipboardIndexEntry entry, bool pinned, DateTime? until)
    {
        if (entry.IsPinned != pinned)
        {
            entry.IsPinned = pinned;
            _pinnedCount += pinned ? 1 : -1;
        }

        entry.PinnedUntil = pinned ? until : null;
    }

    /// <summary>最近的待触发时刻（提醒 / 置顶到期），供"精准单次定时器"对准；无待办项返回 null。</summary>
    internal DateTime? NextDeadline()
    {
        lock (_gate)
        {
            DateTime? next = null;
            foreach (var entry in _index)
            {
                if (entry.RemindAt is { } remind && (next is null || remind < next))
                {
                    next = remind;
                }

                if (entry.PinnedUntil is { } until && (next is null || until < next))
                {
                    next = until;
                }
            }

            return next;
        }
    }

    /// <summary>
    /// 收集并消费所有到期项（提醒触发一次后清空，置顶到期后取消置顶）。
    /// 返回 true 表示有状态变化（调用方需要刷新界面/重新对准定时器）。
    /// </summary>
    internal bool CollectDue(DateTime now, List<ClipboardDueEvent> sink)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var entry in _index)
            {
                if (entry.RemindAt is { } remind && remind <= now)
                {
                    entry.RemindAt = null;
                    sink.Add(new ClipboardDueEvent(ClipboardDueKind.Remind, entry.Id, entry.Preview, entry.Kind));
                    changed = true;
                }

                if (entry.PinnedUntil is { } until && until <= now)
                {
                    SetPinnedCore(entry, false, null);
                    sink.Add(new ClipboardDueEvent(ClipboardDueKind.PinExpired, entry.Id, entry.Preview, entry.Kind));
                    changed = true;
                }
            }

            if (changed)
            {
                _version++;
                _dirty = true;
            }

            return changed;
        }
    }

    /// <summary>
    /// 当前固定条数，用于底部状态显示与刷新签名。
    /// 计数器增量维护：面板每次刷新都会读它，之前用 <c>_index.Count(e =&gt; e.IsPinned)</c>
    /// 等于每次击键都在锁内全表扫一遍。
    /// </summary>
    internal int PinnedCount
    {
        get
        {
            lock (_gate)
            {
                return _pinnedCount;
            }
        }
    }

    /// <summary>三重上限淘汰：条数、时效、全文预算。固定的条目豁免时效淘汰。
    /// maxAge 传 <see cref="TimeSpan.MaxValue"/> 表示关闭时效淘汰（配置里"保留天数 = 0"）。</summary>
    private void Trim()
    {
        var expiryEnabled = _maxAge != TimeSpan.MaxValue;
        var cutoff = expiryEnabled ? DateTime.Now - _maxAge : default;

        while (_index.Count > 0)
        {
            var overCapacity = _index.Count > _capacity;
            var overBudget = _payloadChars > _payloadCharBudget;

            // 收藏与待办永不淘汰：从尾部往前找第一个可淘汰项。
            // 置顶只影响排序，不再豁免过期/容量 —— 那是收藏/待办的语义。
            var victimIndex = -1;
            for (var i = _index.Count - 1; i >= 0; i--)
            {
                var candidate = _index[i];
                if (!candidate.IsFavorite && !candidate.IsTodo)
                {
                    victimIndex = i;
                    break;
                }
            }

            if (victimIndex < 0)
            {
                break; // 全是收藏/待办，无可淘汰
            }

            var victim = _index[victimIndex];
            var expired = expiryEnabled && victim.CreatedAt < cutoff;

            if (!overCapacity && !overBudget && !expired)
            {
                break;
            }

            RemoveAt(victimIndex);
        }
    }

    private void RemoveAt(int index)
    {
        var entry = _index[index];
        _index.RemoveAt(index);
        _byHash.Remove(entry.Hash);
        if (entry.IsPinned)
        {
            _pinnedCount--;
        }

        if (entry.Kind == ClipboardEntryKind.Image)
        {
            _payloadChars -= entry.Length;
            if (_imagePayloads.Remove(entry.Id, out var path))
            {
                // 文件删除放锁外：磁盘 IO 不该发生在锁内
                _pendingImageDeletes.Add(path);
            }
        }
        else if (_payloads.Remove(entry.Id, out var text))
        {
            _payloadChars -= text.Length;
        }
    }

    /// <summary>把淘汰条目的 PNG 文件删掉。调用时机：任何会触发 Trim/Remove 的公开方法返回之后。</summary>
    private void DeletePendingImages()
    {
        string[] paths;
        lock (_gate)
        {
            if (_pendingImageDeletes.Count == 0)
            {
                return;
            }

            paths = [.. _pendingImageDeletes];
            _pendingImageDeletes.Clear();
        }

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 删不掉就留着，孤儿文件由启动清扫兜底
            }
        }
    }

    /// <summary>当前索引引用的全部图片路径（启动清扫用：不在名单里的缓存文件就是孤儿）。</summary>
    internal List<string> ReferencedImagePaths()
    {
        lock (_gate)
        {
            return [.. _imagePayloads.Values];
        }
    }

    // ---- 持久化（P2）：JSON 快照，文本全文 + 图片引用一起存 ----
    // 为什么不全量每次变更都写：几千条的 JSON 有几 MB，5 秒合并一次足够
    // —— 最坏丢最后 5 秒的记录，对剪贴板历史无所谓。

    private sealed class PersistedEntry
    {
        public int K { get; set; }          // 0 = 文本, 1 = 图片
        public string H { get; set; } = "0"; // 去重指纹（ulong 字符串，避免 JSON 数字精度问题）
        public string? T { get; set; }      // 文本全文
        public string? P { get; set; }      // 图片路径
        public int W { get; set; }          // 像素宽
        public int G { get; set; }          // 像素高
        public long L { get; set; }         // PNG 字节数
        public string? S { get; set; }      // 来源进程
        public long C { get; set; }         // CreatedAt.Ticks
        public bool X { get; set; }         // 已固定
        public bool F { get; set; }         // 已收藏（永久保留）
        public bool TD { get; set; }        // 待办（T 已被文本全文占用）
        public bool AR { get; set; }        // 待办已归档
        public long? AA { get; set; }       // 归档时刻 ticks
        public long? PU { get; set; }       // 置顶到期 ticks
        public long? RA { get; set; }       // 提醒时刻 ticks
    }

    private sealed class PersistedState
    {
        public int V { get; set; } = 1;     // 格式版本
        public long N { get; set; }         // nextId
        public List<PersistedEntry> E { get; set; } = [];
    }

    private static readonly JsonSerializerOptions PersistOptions = new() { WriteIndented = false };

    /// <summary>全量快照写盘（临时文件 + File.Replace 原子替换）。锁内取数，锁外写盘。</summary>
    /// <summary>
    /// 落盘。**收藏/待办与普通历史分开存储**：
    ///   - marksPath：收藏或待办的条目（含内容），永不淘汰，不受"重启保留历史"开关影响；
    ///   - historyPath：其余条目。传 null 表示不写历史（持久化关闭时只保标记数据）。
    /// </summary>
    internal void Persist(string? historyPath, string marksPath)
    {
        var state = new PersistedState();
        var textCount = 0;
        var imageCount = 0;

        lock (_gate)
        {
            _dirty = false;
            state.N = _nextId;

            foreach (var entry in _index)
            {
                if (entry.Kind == ClipboardEntryKind.Text)
                {
                    if (!_payloads.TryGetValue(entry.Id, out var text))
                    {
                        continue; // 全文已淘汰的条目没有恢复价值
                    }

                    textCount++;
                    state.E.Add(new PersistedEntry
                    {
                        K = 0,
                        H = entry.Hash.ToString(),
                        T = text,
                        S = entry.SourceProcess,
                        C = entry.CreatedAt.Ticks,
                        X = entry.IsPinned,
                        F = entry.IsFavorite,
                        TD = entry.IsTodo,
                        PU = entry.PinnedUntil?.Ticks,
                        RA = entry.RemindAt?.Ticks,
                        AR = entry.IsTodoArchived,
                        AA = entry.ArchivedAt?.Ticks
                    });
                }
                else if (entry.Kind == ClipboardEntryKind.File)
                {
                    // 文件条目：负载是路径清单，存进 T，L 存文件数
                    if (!_payloads.TryGetValue(entry.Id, out var pathList))
                    {
                        continue;
                    }

                    textCount++;
                    state.E.Add(new PersistedEntry
                    {
                        K = 2,
                        H = entry.Hash.ToString(),
                        T = pathList,
                        L = entry.Length,
                        S = entry.SourceProcess,
                        C = entry.CreatedAt.Ticks,
                        X = entry.IsPinned,
                        F = entry.IsFavorite,
                        TD = entry.IsTodo,
                        PU = entry.PinnedUntil?.Ticks,
                        RA = entry.RemindAt?.Ticks,
                        AR = entry.IsTodoArchived,
                        AA = entry.ArchivedAt?.Ticks
                    });
                }
                else
                {
                    if (!_imagePayloads.TryGetValue(entry.Id, out var imagePath))
                    {
                        continue;
                    }

                    imageCount++;
                    state.E.Add(new PersistedEntry
                    {
                        K = 1,
                        H = entry.Hash.ToString(),
                        P = imagePath,
                        W = entry.PixelWidth,
                        G = entry.PixelHeight,
                        L = entry.Length,
                        S = entry.SourceProcess,
                        C = entry.CreatedAt.Ticks,
                        X = entry.IsPinned,
                        F = entry.IsFavorite,
                        TD = entry.IsTodo,
                        PU = entry.PinnedUntil?.Ticks,
                        RA = entry.RemindAt?.Ticks,
                        AR = entry.IsTodoArchived,
                        AA = entry.ArchivedAt?.Ticks
                    });
                }
            }
        }

        // 按"是否带标记"拆成两份：marks 独立落盘（永不受淘汰与保留设置影响）
        var marksState = new PersistedState { N = state.N };
        var historyOnly = new List<PersistedEntry>(state.E.Count);
        foreach (var persisted in state.E)
        {
            if (persisted.F || persisted.TD)
            {
                marksState.E.Add(persisted);
            }
            else
            {
                historyOnly.Add(persisted);
            }
        }

        state.E = historyOnly;

        try
        {
            WriteState(marksPath, marksState);
            ClipboardListener.Log(
                "marks persisted: " + marksState.E.Count + " entries, store#" + InstanceId,
                LogLevel.Debug);

            if (historyPath is not null)
            {
                WriteState(historyPath, state);
                ClipboardListener.Log(
                    "history persisted: " + state.E.Count + " entries (text=" + textCount + ", image=" + imageCount + "), store#" + InstanceId,
                    LogLevel.Debug);
            }
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("persist failed: " + ex.Message, LogLevel.Warn);
        }
    }

    private static void WriteState(string path, PersistedState state)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, PersistOptions));

        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>
    /// 从快照恢复；文件不存在或损坏返回全新实例（损坏时不带病运行）。
    /// **先读标记存储（收藏/待办，独立文件），再合并历史**：同一指纹以"在历史里"为准
    /// （MarksOnly=false），因此取消全部标记后它仍作为普通历史存在。
    /// 文本条目的指纹用全文重算，与原始哈希必然一致；图片条目的指纹是对
    /// 剪贴板原始字节算的，重算不出来，所以直接存进快照。
    /// </summary>
    internal static ClipboardStore LoadOrCreate(
        int capacity,
        TimeSpan maxAge,
        string historyPath,
        string marksPath,
        bool loadHistory,
        long payloadCharBudget = 64L * 1024 * 1024)
    {
        var store = new ClipboardStore(capacity, maxAge, payloadCharBudget);
        var restored = 0;
        var images = 0;

        void Restore(string? path, bool marksOrigin)
        {
            if (path is null || !File.Exists(path))
            {
                return;
            }

            PersistedState? state;
            try
            {
                state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(path), PersistOptions);
            }
            catch (Exception ex)
            {
                ClipboardListener.Log("snapshot read failed, skipping " + path + ": " + ex.Message, LogLevel.Warn);
                return;
            }

            if (state is null || state.E.Count == 0)
            {
                return;
            }

            lock (store._gate)
            {
                foreach (var item in state.E)
                {
                    if (!ulong.TryParse(item.H, out var hash))
                    {
                        continue;
                    }

                    // 同指纹已存在（marks 先加载）：历史里的这份只负责把 MarksOnly 清掉
                    if (store._byHash.TryGetValue(hash, out var existing))
                    {
                        if (!marksOrigin)
                        {
                            existing.MarksOnly = false;
                        }

                        continue;
                    }

                    ClipboardIndexEntry entry;
                    if (item.K == 0)
                    {
                        if (string.IsNullOrEmpty(item.T))
                        {
                            continue;
                        }

                        entry = new ClipboardIndexEntry(store._nextId++, item.T, item.S, new DateTime(item.C, DateTimeKind.Local));
                    }
                    else if (item.K == 2)
                    {
                        // 文件条目：T 是路径清单
                        if (string.IsNullOrEmpty(item.T))
                        {
                            continue;
                        }

                        var fileCount = item.L > 0 ? (int)item.L : item.T.Split('\n').Length;
                        entry = new ClipboardIndexEntry(store._nextId++, hash, fileCount, item.T, item.S, new DateTime(item.C, DateTimeKind.Local));
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(item.P))
                        {
                            continue;
                        }

                        if (!File.Exists(item.P))
                        {
                            continue; // 图片文件已被外部删除，条目作废
                        }

                        entry = new ClipboardIndexEntry(
                            store._nextId++, hash, item.W, item.G,
                            item.L > 0 ? item.L : new FileInfo(item.P).Length,
                            item.P, item.S, new DateTime(item.C, DateTimeKind.Local));
                        images++;
                    }

                    entry.IsPinned = item.X;
                    entry.IsFavorite = item.F;
                    entry.IsTodo = item.TD;
                    entry.IsTodoArchived = item.AR;
                    entry.ArchivedAt = item.AA is { } aa ? new DateTime(aa) : null;
                    entry.PinnedUntil = item.PU is { } pu ? new DateTime(pu) : null;
                    entry.RemindAt = item.RA is { } ra ? new DateTime(ra) : null;
                    entry.MarksOnly = marksOrigin;
                    if (entry.IsPinned)
                    {
                        store._pinnedCount++;
                    }

                    store._index.Add(entry);   // 快照按新→旧顺序存，保持原序
                    store._byHash[hash] = entry;

                    if (entry.Kind == ClipboardEntryKind.Image)
                    {
                        store._imagePayloads[entry.Id] = entry.ImagePath;
                    }
                    else
                    {
                        store._payloads[entry.Id] = item.T!;
                    }

                    store._payloadChars += entry.Length;
                    restored++;
                }

                // 与旧快照的 nextId 取最大值，避免后续 Add 撞上历史 id
                store._nextId = Math.Max(store._nextId, state.N);
            }
        }

        Restore(marksPath, marksOrigin: true);
        Restore(loadHistory ? historyPath : null, marksOrigin: false);

        lock (store._gate)
        {
            store.Trim();
        }

        store.DeletePendingImages();
        ClipboardListener.Log(
            "store loaded: " + restored + " entries (" + images + " images), store#" + store.InstanceId
                + " (marks=" + marksPath + ", history=" + (loadHistory ? historyPath : "off") + ")",
            LogLevel.Info);
        return store;
    }
}
