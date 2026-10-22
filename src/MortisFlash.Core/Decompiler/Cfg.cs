namespace MortisFlash.Decompiler;

public enum BlockKind
{
    /// <summary>Falls through to the next block (Succs[0]).</summary>
    Fall,
    /// <summary>Unconditional jump (Succs[0]).</summary>
    Jump,
    /// <summary>Conditional jump: Succs[0] if the condition is true, Succs[1] otherwise.</summary>
    Cond,
    /// <summary>Switch: Succs[0] = default, Succs[1..] = cases 0, 1, …</summary>
    Switch,
    /// <summary>return / throw: no successor.</summary>
    Exit,
}

/// <summary>Basic block: instruction range [Start, End) with no incoming jump in the middle.</summary>
public sealed class CfgBlock
{
    public int Index;
    public int Start, End;
    /// <summary>Byte address of the first instruction, used for labels.</summary>
    public int Offset;
    public BlockKind Kind;
    public List<CfgBlock> Succs = [];
    public List<CfgBlock> Preds = [];
    /// <summary>Exception handlers covering this block (virtual edges).</summary>
    public List<CfgBlock> Handlers = [];
    /// <summary>Case labels of a reconstructed switch (otherwise the indices 0, 1, …).</summary>
    public List<Expr>? CaseLabels;
    public override string ToString() => $"B{Index}@{Offset:X}";
}

public sealed class Loop
{
    public required CfgBlock Header;
    public HashSet<CfgBlock> Blocks = [];
    public List<CfgBlock> Latches = [];
    public Loop? Parent;
    public bool Contains(CfgBlock b) => Blocks.Contains(b);
}

/// <summary>Control-flow graph with dominators, post-dominators and natural loops.</summary>
public sealed class Cfg
{
    public List<CfgBlock> Blocks { get; }
    public CfgBlock Entry => Blocks[0];
    public List<CfgBlock> ExtraEntries { get; } = [];
    readonly CfgBlock?[] _idom;
    readonly int[] _ipdom; // -1 = virtual exit
    int[] _rpo = [];
    int[] _pdRpo = []; // rank in the reversed-graph order (the virtual exit has rank 0)
    public Dictionary<CfgBlock, Loop> Loops { get; } = [];

    public Cfg(List<CfgBlock> blocks, IEnumerable<CfgBlock>? extraEntries = null)
    {
        Blocks = blocks;
        for (int i = 0; i < blocks.Count; i++) blocks[i].Index = i;
        if (extraEntries != null) ExtraEntries.AddRange(extraEntries.Distinct());
        foreach (var b in blocks) b.Preds.Clear();
        foreach (var b in blocks)
            foreach (var s in b.Succs)
                if (!s.Preds.Contains(b)) s.Preds.Add(b);
        _idom = ComputeDominators();
        _ipdom = ComputePostDominators();
        FindLoops();
    }

    // ------------------------------------------------------------- Dominators (Cooper, Harvey, Kennedy)

    CfgBlock?[] ComputeDominators()
    {
        int n = Blocks.Count;
        var order = new List<CfgBlock>();
        var visited = new bool[n];
        // Iterative post-order traversal from the entry and the exception handlers
        void Dfs(CfgBlock root)
        {
            if (visited[root.Index]) return;
            var stack = new Stack<(CfgBlock B, int I)>();
            stack.Push((root, 0));
            visited[root.Index] = true;
            while (stack.Count > 0)
            {
                var (b, i) = stack.Pop();
                var succ = AllSuccs(b);
                if (i < succ.Count)
                {
                    stack.Push((b, i + 1));
                    var s = succ[i];
                    if (!visited[s.Index])
                    {
                        visited[s.Index] = true;
                        stack.Push((s, 0));
                    }
                }
                else order.Add(b);
            }
        }
        // Secondary entries first: after reversal, the main entry (root) has rank 0
        foreach (var e in ExtraEntries) Dfs(e);
        Dfs(Entry);
        order.Reverse();
        var rpo = new int[n];
        Array.Fill(rpo, int.MaxValue);
        for (int i = 0; i < order.Count; i++) rpo[order[i].Index] = i;
        _rpo = rpo;

        var idom = new CfgBlock?[n];
        idom[Entry.Index] = Entry;
        // Extra entry points are dominated by the entry (shared virtual root)
        foreach (var e in ExtraEntries) if (e != Entry) idom[e.Index] = Entry;
        var allPreds = new List<CfgBlock>[n];
        for (int i = 0; i < n; i++) allPreds[i] = [];
        foreach (var b in Blocks)
            foreach (var s in AllSuccs(b))
                allPreds[s.Index].Add(b);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var b in order)
            {
                if (b == Entry || ExtraEntries.Contains(b)) continue;
                CfgBlock? newIdom = null;
                foreach (var p in allPreds[b.Index])
                {
                    if (idom[p.Index] == null) continue;
                    newIdom = newIdom == null ? p : Intersect(p, newIdom, idom, rpo);
                }
                if (newIdom != null && idom[b.Index] != newIdom)
                {
                    idom[b.Index] = newIdom;
                    changed = true;
                }
            }
        }
        return idom;
    }

    CfgBlock Intersect(CfgBlock a, CfgBlock b, CfgBlock?[] idom, int[] rpo)
    {
        int guard = 0;
        while (a != b && guard++ < 100000)
        {
            while (rpo[a.Index] > rpo[b.Index] && idom[a.Index] != null && idom[a.Index] != a) a = idom[a.Index]!;
            while (rpo[b.Index] > rpo[a.Index] && idom[b.Index] != null && idom[b.Index] != b) b = idom[b.Index]!;
            if (a != b && (idom[a.Index] == a || idom[b.Index] == b)) break;
        }
        // With no meeting point, the shared virtual root is represented by the entry
        return a == b ? a : Entry;
    }

    static List<CfgBlock> AllSuccs(CfgBlock b) => b.Handlers.Count == 0 ? b.Succs : [.. b.Succs, .. b.Handlers];

    /// <summary>Reverse post-order rank (unreachable blocks come last).</summary>
    public int Rpo(CfgBlock b) => b.Index < _rpo.Length ? _rpo[b.Index] : int.MaxValue;

    public bool Dominates(CfgBlock a, CfgBlock b)
    {
        int guard = 0;
        var cur = b;
        while (cur != null && guard++ < 100000)
        {
            if (cur == a) return true;
            var d = _idom[cur.Index];
            if (d == null || d == cur) return false;
            cur = d;
        }
        return false;
    }

    // ------------------------------------------------------------- Post-dominators (normal edges only)

    int[] ComputePostDominators()
    {
        int n = Blocks.Count;
        int exit = n; // virtual node
        var succ = new List<int>[n + 1];
        var pred = new List<int>[n + 1];
        for (int i = 0; i <= n; i++) { succ[i] = []; pred[i] = []; }
        foreach (var b in Blocks)
        {
            foreach (var s in b.Succs) { succ[b.Index].Add(s.Index); pred[s.Index].Add(b.Index); }
            if (b.Succs.Count == 0) { succ[b.Index].Add(exit); pred[exit].Add(b.Index); }
        }
        // Infinite loops never reach the exit: connect their latest blocks to the exit
        var reach = new bool[n + 1];
        void Mark()
        {
            Array.Clear(reach);
            var st = new Stack<int>();
            st.Push(exit);
            reach[exit] = true;
            while (st.Count > 0)
            {
                int x = st.Pop();
                foreach (var p in pred[x]) if (!reach[p]) { reach[p] = true; st.Push(p); }
            }
        }
        Mark();
        for (int i = n - 1; i >= 0; i--)
        {
            if (reach[i]) continue;
            succ[i].Add(exit);
            pred[exit].Add(i);
            Mark();
        }

        // Reverse post-order on the reversed graph
        var order = new List<int>();
        var vis = new bool[n + 1];
        var stack = new Stack<(int, int)>();
        stack.Push((exit, 0));
        vis[exit] = true;
        while (stack.Count > 0)
        {
            var (x, i) = stack.Pop();
            if (i < pred[x].Count)
            {
                stack.Push((x, i + 1));
                int p = pred[x][i];
                if (!vis[p]) { vis[p] = true; stack.Push((p, 0)); }
            }
            else order.Add(x);
        }
        order.Reverse();
        var rpo = new int[n + 1];
        Array.Fill(rpo, int.MaxValue);
        for (int i = 0; i < order.Count; i++) rpo[order[i]] = i;

        var ipdom = new int[n + 1];
        Array.Fill(ipdom, -2);
        ipdom[exit] = exit;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (int b in order)
            {
                if (b == exit) continue;
                int newI = -2;
                foreach (int s in succ[b])
                {
                    if (ipdom[s] == -2) continue;
                    newI = newI == -2 ? s : IntersectInt(s, newI, ipdom, rpo);
                }
                if (newI != -2 && ipdom[b] != newI)
                {
                    ipdom[b] = newI;
                    changed = true;
                }
            }
        }
        var res = new int[n];
        for (int i = 0; i < n; i++) res[i] = ipdom[i] < 0 || ipdom[i] == exit ? -1 : ipdom[i];
        _pdRpo = rpo;
        return res;
    }

    static int IntersectInt(int a, int b, int[] idom, int[] rpo)
    {
        int guard = 0;
        while (a != b && guard++ < 1000000)
        {
            while (rpo[a] > rpo[b]) a = idom[a];
            while (rpo[b] > rpo[a]) b = idom[b];
        }
        return a;
    }

    /// <summary>Immediate post-dominator, or null if it is the function exit.</summary>
    public CfgBlock? PostDominator(CfgBlock b) => _ipdom[b.Index] < 0 ? null : Blocks[_ipdom[b.Index]];

    /// <summary>Nearest common post-dominator (null = function exit), found by walking up the tree.</summary>
    public CfgBlock? CommonPostDominator(CfgBlock a, CfgBlock b)
    {
        int n = Blocks.Count;
        int x = a.Index, y = b.Index, guard = 0;
        int Up(int i) => i == n ? n : _ipdom[i] < 0 ? n : _ipdom[i];
        int R(int i) => i == n ? 0 : _pdRpo[i];
        while (x != y && guard++ < 1_000_000)
        {
            while (R(x) > R(y) && x != n) x = Up(x);
            while (R(y) > R(x) && y != n) y = Up(y);
            if (x != y && R(x) == R(y)) { x = Up(x); y = Up(y); }
        }
        return x == y && x != n ? Blocks[x] : null;
    }

    public bool PostDominates(CfgBlock a, CfgBlock b)
    {
        int cur = b.Index, guard = 0;
        while (cur >= 0 && guard++ < 100000)
        {
            if (cur == a.Index) return true;
            int next = _ipdom[cur];
            if (next == cur) return false;
            cur = next;
        }
        return false;
    }

    // ------------------------------------------------------------- Natural loops

    void FindLoops()
    {
        foreach (var b in Blocks)
        {
            foreach (var s in b.Succs)
            {
                if (!Dominates(s, b)) continue;
                if (!Loops.TryGetValue(s, out var loop))
                {
                    loop = new Loop { Header = s };
                    loop.Blocks.Add(s);
                    Loops[s] = loop;
                }
                loop.Latches.Add(b);
                var stack = new Stack<CfgBlock>();
                if (loop.Blocks.Add(b)) stack.Push(b);
                while (stack.Count > 0)
                {
                    var x = stack.Pop();
                    foreach (var p in x.Preds)
                        if (loop.Blocks.Add(p)) stack.Push(p);
                }
            }
        }
        // Nesting: the parent is the smallest loop that contains the header
        foreach (var l in Loops.Values)
        {
            Loop? best = null;
            foreach (var o in Loops.Values)
            {
                if (o == l || !o.Blocks.Contains(l.Header) || o.Blocks.Count <= l.Blocks.Count) continue;
                if (best == null || o.Blocks.Count < best.Blocks.Count) best = o;
            }
            l.Parent = best;
        }
    }
}
