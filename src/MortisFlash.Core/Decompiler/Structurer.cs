namespace MortisFlash.Decompiler;

/// <summary>Symbolic state propagated along the graph: the expression stack (plus VM-specific state).</summary>
public abstract class SymState
{
    public List<Expr> Stack = [];

    public abstract SymState Clone();

    public Expr Pop() => Stack.Count > 0 ? Stack[^1].Also(_ => Stack.RemoveAt(Stack.Count - 1)) : new RawExpr("§§pop()");
    public Expr Peek() => Stack.Count > 0 ? Stack[^1] : new RawExpr("§§pop()");
    public void Push(Expr e) => Stack.Add(e);

    public List<Expr> PopN(int n)
    {
        var r = new List<Expr>(n);
        for (int i = 0; i < n; i++) r.Add(Pop());
        r.Reverse();
        return r;
    }
}

static class ObjExt
{
    public static T Also<T>(this T v, Action<T> a)
    {
        a(v);
        return v;
    }
}

/// <summary>AVM2 try region: the [From, To) address range and its handlers.</summary>
public sealed class TryRegion
{
    public int From, To;
    public List<HandlerInfo> Handlers = [];
}

public sealed class HandlerInfo
{
    public required CfgBlock Target;
    public string? Name;
    public string? Type;
    public bool IsFinally;
    public int Index;
}

/// <summary>Translates a block's instructions into high-level statements (VM-specific).</summary>
public interface IBlockTranslator
{
    /// <summary>
    /// Translates the block and appends the resulting statements. Returns the branch condition (Cond block,
    /// true → Succs[0]) or the switch index (Switch block).
    /// </summary>
    Expr? Translate(CfgBlock b, SymState st, List<Stmt> output);

    /// <summary>The block has no observable effect (labels, nop, jump).</summary>
    bool IsEmpty(CfgBlock b);

    /// <summary>Initial state of an exception handler.</summary>
    SymState HandlerState(HandlerInfo h, SymState outer);
}

/// <summary>
/// Rebuilds control structures (if/else, loops, switch, try) from the graph: branches join at the
/// immediate post-dominator, loops are natural loops, and the remaining jumps become break / continue.
/// Anything that cannot be structured is emitted with §§goto, as JPEXS does.
/// </summary>
public sealed class Structurer
{
    readonly Cfg _cfg;
    readonly IBlockTranslator _tr;
    readonly List<TryRegion> _tries;
    readonly HashSet<CfgBlock> _emitted = [];
    readonly HashSet<Loop> _activeLoops = [];
    readonly HashSet<TryRegion> _openedTries = [];
    readonly List<Frame> _frames = [];
    int _labelCounter;
    int _budget = 200000;

    enum FrameKind { Loop, Switch, Try }

    sealed class Frame
    {
        public FrameKind Kind;
        public CfgBlock? Break;
        public CfgBlock? Continue;
        public Loop? Loop;
        public TryRegion? Try;
        public string? Label;
        public List<CfgBlock> TryExits = [];
    }

    public Structurer(Cfg cfg, IBlockTranslator tr, List<TryRegion>? tries = null)
    {
        _cfg = cfg;
        _tr = tr;
        _tries = tries ?? [];
    }

    public List<Stmt> Run(SymState st) => Walk(_cfg.Entry, null, st, false, out _);

    List<Stmt> Walk(CfgBlock? start, CfgBlock? exit, SymState st, bool loopBody, out bool reachedExit)
    {
        var output = new List<Stmt>();
        reachedExit = false;
        var cur = start;
        bool first = true;
        while (cur != null)
        {
            if (--_budget < 0) throw new InvalidOperationException("Control-flow structuring took too long");
            if (cur == exit && !(loopBody && first))
            {
                reachedExit = true;
                return output;
            }
            if (!(loopBody && first) && JumpOut(cur, output)) return output;

            var tryFrame = _frames.LastOrDefault(f => f.Kind == FrameKind.Try);
            if (tryFrame != null && !InRange(cur, tryFrame.Try!))
            {
                tryFrame.TryExits.Add(cur);
                return output;
            }
            if (_emitted.Contains(cur))
            {
                output.Add(new RawStmt($"§§goto(addr{cur.Offset:X4});"));
                return output;
            }

            var region = TryStartingAt(cur);
            _cfg.Loops.TryGetValue(cur, out var loop);
            if (loop != null && _activeLoops.Contains(loop)) loop = null;
            if (region != null && (loop == null || loop.Blocks.All(b => InRange(b, region))))
            {
                cur = WalkTry(region, cur, st, output);
                first = false;
                continue;
            }
            if (loop != null && !(loopBody && first))
            {
                cur = WalkLoop(loop, st, output);
                first = false;
                continue;
            }

            _emitted.Add(cur);
            var term = _tr.Translate(cur, st, output);
            first = false;
            switch (cur.Kind)
            {
                case BlockKind.Exit:
                    return output;
                case BlockKind.Fall:
                case BlockKind.Jump:
                    cur = cur.Succs.Count > 0 ? cur.Succs[0] : null;
                    break;
                case BlockKind.Cond:
                    cur = WalkIf(cur, term ?? new RawExpr("§§cond"), st, output, exit, out bool stop);
                    if (stop) return output;
                    break;
                case BlockKind.Switch:
                    cur = WalkSwitch(cur, term ?? new RawExpr("§§index"), st, output);
                    break;
            }
        }
        return output;
    }

    static bool InRange(CfgBlock b, TryRegion r) => b.Offset >= r.From && b.Offset < r.To;

    TryRegion? TryStartingAt(CfgBlock b)
    {
        TryRegion? best = null;
        foreach (var r in _tries)
            if (r.From == b.Offset && !_openedTries.Contains(r) && (best == null || r.To > best.To)) best = r;
        return best;
    }

    /// <summary>Jump to the exit or start of an enclosing structure: break / continue.</summary>
    bool JumpOut(CfgBlock cur, List<Stmt> output)
    {
        bool innermostBreakable = true, innermostLoop = true;
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            var f = _frames[i];
            if (f.Kind == FrameKind.Try) continue;
            if (cur == f.Break)
            {
                output.Add(new BreakStmt(innermostBreakable ? null : LabelOf(f)));
                return true;
            }
            if (f.Kind == FrameKind.Loop && cur == f.Continue)
            {
                output.Add(new ContinueStmt(innermostLoop ? null : LabelOf(f)));
                return true;
            }
            innermostBreakable = false;
            if (f.Kind == FrameKind.Loop) innermostLoop = false;
        }
        return false;
    }

    string LabelOf(Frame f) => f.Label ??= (f.Kind == FrameKind.Switch ? "switch" : "loop") + _labelCounter++;

    // ------------------------------------------------------------------ if / ternaries

    CfgBlock? WalkIf(CfgBlock b, Expr cond, SymState st, List<Stmt> output, CfgBlock? regionExit, out bool stop)
    {
        stop = false;
        var t = b.Succs[0];
        var f = b.Succs[1];
        var group = new HashSet<CfgBlock> { b };
        MergeShortCircuit(group, ref cond, ref t, ref f, st);

        if (t == f)
        {
            if (cond.HasSideEffects) output.Add(new ExprStmt(cond));
            return t;
        }

        // Join point: the common post-dominator if usable; otherwise (a return, break or continue breaks
        // post-dominance) the first block reached by both branches; otherwise the enclosing region's exit.
        var pd = CommonPostDominator(t, f);
        var merge = pd != null && pd != regionExit && !IsFrameTarget(pd)
            ? pd
            : ReachMerge(Reach(t, regionExit), Reach(f, regionExit), regionExit) ?? regionExit;
        var stT = st.Clone();
        var thenBody = Walk(t, merge, stT, false, out bool tReached);
        var stF = st.Clone();
        var elseBody = Walk(f, merge, stF, false, out bool fReached);

        // Values left on the stack by both branches: ternary, a && b, a || b
        bool sameShape = tReached && fReached && stT.Stack.Count == stF.Stack.Count;
        List<Expr>? merged = null;
        if (sameShape)
        {
            for (int i = 0; i < stT.Stack.Count; i++)
            {
                if (ReferenceEquals(stT.Stack[i].Core, stF.Stack[i].Core)) continue;
                merged ??= [.. stT.Stack];
                merged[i] = CondValue(cond, stT.Stack[i], stF.Stack[i]);
            }
        }
        if (merged != null && thenBody.Count == 0 && elseBody.Count == 0)
        {
            CopyState(stF, st);
            st.Stack = merged;
            return merge;
        }

        if (thenBody.Count > 0 || elseBody.Count > 0) output.Add(MakeIf(cond, thenBody, elseBody));
        else if (cond.HasSideEffects) output.Add(new ExprStmt(cond));

        if (merged != null)
        {
            CopyState(stF, st);
            st.Stack = merged;
        }
        else if (tReached && !fReached) CopyState(stT, st);
        else if (fReached) CopyState(stF, st);
        else
        {
            stop = true;
            return null;
        }
        return merge;
    }

    static void CopyState(SymState from, SymState to)
    {
        to.Stack = from.Stack;
        if (to is ICopyable c) c.CopyFrom(from);
    }

    /// <summary>If (cond) → Succs[0]: the fall-through branch is written first (source order).</summary>
    static IfStmt MakeIf(Expr cond, List<Stmt> taken, List<Stmt> fall)
    {
        if (fall.Count == 0) return new IfStmt(Clean(cond), taken, null);
        return new IfStmt(Negate(cond), fall, taken.Count > 0 ? taken : null);
    }

    /// <summary>!(a &lt; b) → a &gt;= b, !!x → x.</summary>
    public static Expr Clean(Expr e) => e is UnaryExpr { Op: "!" } u && (u.Operand is UnaryExpr { Op: "!" } || u.Operand is BinaryExpr
        { Op: "==" or "!=" or "===" or "!==" or "<" or ">" or "<=" or ">=" }) ? Negate(u.Operand) : e;

    /// <summary>
    /// First block (in reverse post-order) reachable from both branches without leaving the current
    /// structure: this is where the branches join when one of them contains a return.
    /// </summary>
    CfgBlock? ReachMerge(HashSet<CfgBlock> ra, HashSet<CfgBlock> rb, CfgBlock? regionExit)
    {
        // In reverse post-order, a block is never reached (without a back edge) from a higher-ranked block,
        // so the lowest common rank is a first meeting point.
        CfgBlock? best = null;
        foreach (var x in ra)
            if (rb.Contains(x) && (x == regionExit || !IsFrameTarget(x)) && (best == null || _cfg.Rpo(x) < _cfg.Rpo(best)))
                best = x;
        return Restrict(best);
    }

    HashSet<CfgBlock> Reach(CfgBlock start, CfgBlock? barrier)
    {
        var seen = new HashSet<CfgBlock>();
        var queue = new Queue<CfgBlock>();
        queue.Enqueue(start);
        while (queue.Count > 0 && seen.Count < 4096)
        {
            var x = queue.Dequeue();
            if (!seen.Add(x)) continue;
            if (x == barrier || IsFrameTarget(x) || _emitted.Contains(x)) continue;
            foreach (var s in x.Succs)
            {
                // Do not follow back edges (jumps back to a loop header)
                if (_cfg.Rpo(s) <= _cfg.Rpo(x)) continue;
                queue.Enqueue(s);
            }
        }
        return seen;
    }

    static Expr CondValue(Expr cond, Expr taken, Expr fall)
    {
        // dup; iffalse → a && b    dup; iftrue → a || b
        // (the negation may also have been folded away: cond = x while the kept value is !x)
        if (cond is UnaryExpr { Op: "!" } u && ReferenceEquals(u.Operand.Core, taken.Core)
            || taken.Core is UnaryExpr { Op: "!" } t && ReferenceEquals(t.Operand.Core, cond.Core))
            return new BinaryExpr("&&", taken.Core, fall.Core);
        if (ReferenceEquals(cond.Core, taken.Core)) return new BinaryExpr("||", taken.Core, fall.Core);
        if (cond is LitExpr { Value: bool v }) return v ? taken : fall;
        return new TernaryExpr(Negate(cond), fall, taken);
    }

    void MergeShortCircuit(HashSet<CfgBlock> group, ref Expr cond, ref CfgBlock t, ref CfgBlock f, SymState st)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            bool merged = false;
            foreach (var cand in new[] { t, f })
            {
                if (cand.Kind != BlockKind.Cond || _emitted.Contains(cand) || _cfg.Loops.ContainsKey(cand)) continue;
                if (cand.Preds.Any(p => !group.Contains(p))) continue;
                if (TryStartingAt(cand) != null || IsFrameTarget(cand)) continue;
                var probe = st.Clone();
                probe.Stack = [];
                var tmp = new List<Stmt>();
                Expr? c2;
                try { c2 = _tr.Translate(cand, probe, tmp); }
                catch { continue; }
                if (c2 == null || tmp.Count > 0 || probe.Stack.Count > 0) continue;
                var t2 = cand.Succs[0];
                var f2 = cand.Succs[1];
                if (cand == t)
                {
                    if (f2 == f) { cond = And(cond, c2); t = t2; }
                    else if (t2 == f) { cond = And(cond, Negate(c2)); t = f2; }
                    else continue;
                }
                else
                {
                    if (t2 == t) { cond = Or(cond, c2); f = f2; }
                    else if (f2 == t) { cond = Or(cond, Negate(c2)); f = t2; }
                    else continue;
                }
                group.Add(cand);
                _emitted.Add(cand);
                merged = true;
                break;
            }
            if (!merged) return;
        }
    }

    bool IsFrameTarget(CfgBlock b) => _frames.Any(f => f.Break == b || f.Continue == b);

    /// <summary>Join point of the two branches, restricted to the current structure.</summary>
    CfgBlock? CommonPostDominator(CfgBlock a, CfgBlock b) => Restrict(_cfg.CommonPostDominator(a, b));

    CfgBlock? CommonPostDominator(IEnumerable<CfgBlock> blocks)
    {
        CfgBlock? m = null;
        bool firstBlock = true;
        foreach (var b in blocks.Distinct())
        {
            if (firstBlock) { m = b; firstBlock = false; continue; }
            if (m == null) return null;
            m = CommonPostDominator(m, b);
        }
        return Restrict(m);
    }

    CfgBlock? Restrict(CfgBlock? m)
    {
        if (m == null) return null;
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            var f = _frames[i];
            if (f.Kind == FrameKind.Loop)
            {
                if (!f.Loop!.Contains(m) && m != f.Break) return null;
                return m;
            }
            if (f.Kind == FrameKind.Try && !InRange(m, f.Try!)) return null;
        }
        return m;
    }

    // ------------------------------------------------------------------ loops

    CfgBlock? WalkLoop(Loop loop, SymState st, List<Stmt> output)
    {
        var header = loop.Header;
        var latch = loop.Latches.Distinct().Count() == 1 ? loop.Latches[0] : null;
        // do … while: the only back-edge block tests the condition, and the header is not itself the exit
        bool doWhile = latch != null && latch.Kind == BlockKind.Cond && !_emitted.Contains(latch)
                       && latch.Succs.Count(s => s == header) == 1 && latch.Succs.Any(s => !loop.Contains(s))
                       && !(header != latch && header.Kind == BlockKind.Cond && header.Succs.Any(s => !loop.Contains(s)));
        bool forLoop = !doWhile && latch != null && latch != header && latch.Kind is BlockKind.Fall or BlockKind.Jump
                       && latch.Preds.Count >= 2 && !_emitted.Contains(latch) && IsSimple(latch, st);
        var cont = forLoop || doWhile ? latch! : header;
        var brk = LoopExit(loop);
        var frame = new Frame { Kind = FrameKind.Loop, Break = brk, Continue = cont, Loop = loop };
        _frames.Add(frame);
        _activeLoops.Add(loop);
        LoopStmt stmt;
        try
        {
            if (doWhile)
            {
                List<Stmt> body;
                if (latch == header) body = [];
                else body = Walk(header, latch, st, true, out _);
                _emitted.Add(latch!);
                var c = _tr.Translate(latch!, st, body) ?? new RawExpr("§§cond");
                stmt = new DoWhileStmt { Body = body, Cond = latch!.Succs[0] == header ? c : Negate(c) };
            }
            else
            {
                var body = Walk(header, cont, st, true, out _);
                var update = new List<Stmt>();
                if (forLoop)
                {
                    _emitted.Add(latch!);
                    _tr.Translate(latch!, st, update);
                    stmt = new ForStmt { Cond = null, Update = update, Body = body };
                }
                else stmt = new WhileStmt { Body = body };
            }
        }
        finally
        {
            _frames.Remove(frame);
            _activeLoops.Remove(loop);
        }
        stmt.Label = frame.Label;
        output.Add(stmt);
        return brk;
    }

    bool IsSimple(CfgBlock b, SymState st)
    {
        var probe = st.Clone();
        probe.Stack = [];
        var tmp = new List<Stmt>();
        try { _tr.Translate(b, probe, tmp); }
        catch { return false; }
        return tmp.Count > 0 && probe.Stack.Count == 0 && tmp.All(s => s is ExprStmt);
    }

    CfgBlock? LoopExit(Loop loop)
    {
        var header = loop.Header;
        if (header.Kind == BlockKind.Cond)
        {
            var outs = header.Succs.Where(s => !loop.Contains(s)).ToList();
            if (outs.Count == 1) return outs[0];
        }
        foreach (var l in loop.Latches)
        {
            if (l.Kind != BlockKind.Cond) continue;
            var outs = l.Succs.Where(s => !loop.Contains(s)).ToList();
            if (outs.Count == 1) return outs[0];
        }
        var counts = new Dictionary<CfgBlock, int>();
        foreach (var b in loop.Blocks)
            foreach (var s in b.Succs)
                if (!loop.Contains(s))
                    counts[s] = counts.GetValueOrDefault(s) + 1;
        if (counts.Count == 0) return null;
        // Prefer an exit that does more than just "return" (the code following the loop)
        return counts.OrderByDescending(k => k.Key.Kind != BlockKind.Exit || k.Key.Succs.Count > 0)
            .ThenByDescending(k => k.Value).ThenBy(k => k.Key.Offset).First().Key;
    }

    // ------------------------------------------------------------------ switch

    CfgBlock? WalkSwitch(CfgBlock b, Expr index, SymState st, List<Stmt> output)
    {
        var merge = CommonPostDominator(b.Succs);
        var labels = new Dictionary<CfgBlock, List<Expr?>>();
        for (int i = 1; i < b.Succs.Count; i++)
        {
            if (!labels.TryGetValue(b.Succs[i], out var l)) labels[b.Succs[i]] = l = [];
            l.Add(b.CaseLabels != null && i - 1 < b.CaseLabels.Count ? b.CaseLabels[i - 1] : LitExpr.Int(i - 1));
        }
        var dflt = b.Succs[0];
        if (dflt != merge || labels.ContainsKey(dflt))
        {
            if (!labels.TryGetValue(dflt, out var l)) labels[dflt] = l = [];
            l.Add(null);
        }
        var order = labels.Keys.OrderBy(k => k == merge ? int.MaxValue : k.Offset).ToList();
        var sw = new SwitchStmt { Discriminant = index };
        var frame = new Frame { Kind = FrameKind.Switch, Break = merge };
        _frames.Add(frame);
        try
        {
            for (int i = 0; i < order.Count; i++)
            {
                var blk = order[i];
                var c = new SwitchCase { Labels = labels[blk] };
                if (blk == merge) c.Body.Add(new BreakStmt());
                else
                {
                    var next = i + 1 < order.Count && order[i + 1] != merge ? order[i + 1] : merge;
                    c.Body = Walk(blk, next, st.Clone(), false, out bool reached);
                    if (reached && next == merge && i + 1 < order.Count) c.Body.Add(new BreakStmt());
                }
                sw.Cases.Add(c);
            }
        }
        finally
        {
            _frames.Remove(frame);
        }
        sw.Label = frame.Label;
        output.Add(sw);
        return merge;
    }

    // ------------------------------------------------------------------ try / catch

    CfgBlock? WalkTry(TryRegion region, CfgBlock start, SymState st, List<Stmt> output)
    {
        _openedTries.Add(region);
        var frame = new Frame { Kind = FrameKind.Try, Try = region };
        _frames.Add(frame);
        List<Stmt> body;
        try
        {
            body = Walk(start, null, st, false, out _);
        }
        finally
        {
            _frames.Remove(frame);
        }
        CfgBlock? after = frame.TryExits.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        for (int guard = 0; after != null && guard < 8 && after.Kind is BlockKind.Jump or BlockKind.Fall
                            && after.Succs.Count == 1 && _tr.IsEmpty(after) && !IsFrameTarget(after); guard++)
            after = after.Succs[0];
        if (after == null)
        {
            var handlerTargets = region.Handlers.Select(h => h.Target).ToList();
            after = handlerTargets.Count == 1 ? null : CommonPostDominator(handlerTargets);
        }
        var stmt = new TryStmt { Body = body };
        foreach (var h in region.Handlers)
        {
            var hs = _tr.HandlerState(h, st);
            var cbody = _emitted.Contains(h.Target) ? [] : Walk(h.Target, after, hs, false, out _);
            if (h.IsFinally)
            {
                if (cbody.Count > 0 && cbody[^1] is ThrowStmt) cbody.RemoveAt(cbody.Count - 1);
                stmt.Finally = cbody;
            }
            else stmt.Catches.Add(new CatchClause { Name = h.Name, Type = h.Type, Body = cbody });
        }
        output.Add(stmt);
        return after;
    }

    // ------------------------------------------------------------------ conditions

    public static Expr Negate(Expr e)
    {
        switch (e)
        {
            case UnaryExpr { Op: "!" } u:
                return u.Operand;
            case BinaryExpr b:
                string? op = b.Op switch
                {
                    "==" => "!=", "!=" => "==", "===" => "!==", "!==" => "===",
                    "<" => ">=", ">=" => "<", ">" => "<=", "<=" => ">", _ => null,
                };
                if (op != null) return new BinaryExpr(op, b.Left, b.Right);
                break;
            case LitExpr { Value: bool v }:
                return LitExpr.Bool(!v);
        }
        return new UnaryExpr("!", e);
    }

    static Expr And(Expr a, Expr b) => new BinaryExpr("&&", a, b);
    static Expr Or(Expr a, Expr b) => new BinaryExpr("||", a, b);
}

/// <summary>VM state that can copy its own data (in addition to the stack).</summary>
public interface ICopyable
{
    void CopyFrom(SymState other);
}
