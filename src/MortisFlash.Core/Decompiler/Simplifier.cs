namespace MortisFlash.Decompiler;

/// <summary>
/// Cleanup passes over the tree produced by the structurer: while(true) → while / do-while / for,
/// redundant else after a jump, nested if → &&, x = x + 1 → x++, superfluous continue/return.
/// </summary>
public static class Simplifier
{
    public static List<Stmt> Run(List<Stmt> body, bool isFunctionBody = true)
    {
        for (int pass = 0; pass < 4; pass++) body = Block(body);
        if (isFunctionBody && body.Count > 0 && body[^1] is ReturnStmt { Value: null }) body.RemoveAt(body.Count - 1);
        return body;
    }

    static List<Stmt> Block(List<Stmt> list)
    {
        var res = new List<Stmt>(list.Count);
        foreach (var s0 in list)
        {
            var s = Stmt(s0);
            // if (c) { …; return; } else { B }  →  if (c) { …; return; } B
            if (s is IfStmt { Else: { Count: > 0 } } ifs && EndsWithJump(ifs.Then))
            {
                var els = ifs.Else;
                ifs.Else = null;
                res.Add(ifs);
                res.AddRange(els);
                continue;
            }
            // if (c) { A } else { …; break; }  →  if (!c) { …; break; } A
            if (s is IfStmt { Else: { Count: > 0 } } ifs2 && EndsWithJump(ifs2.Else) && !EndsWithJump(ifs2.Then))
            {
                var then = ifs2.Then;
                res.Add(new IfStmt(Structurer.Negate(ifs2.Cond), ifs2.Else, null));
                res.AddRange(then);
                continue;
            }
            if (s is BlockStmt bs)
            {
                res.AddRange(bs.Body);
                continue;
            }
            // if (true) { X } else { Y } → X
            if (s is IfStmt { Cond: LitExpr { Value: bool constant } } ci)
            {
                res.AddRange(constant ? ci.Then : ci.Else ?? []);
                if (res.Count > 0 && res[^1] is ReturnStmt or ThrowStmt or BreakStmt or ContinueStmt) break;
                continue;
            }
            res.Add(s);
            // Nothing executes after an unconditional jump
            if (s is ReturnStmt or ThrowStmt or BreakStmt or ContinueStmt) break;
        }
        // for (init; …): the statement preceding the loop initializes the counter variable
        for (int i = 1; i < res.Count; i++)
        {
            if (res[i] is ForStmt { Init: null } f && f.Update.Count == 1 && IsCounterInit(res[i - 1], f))
            {
                f.Init = res[i - 1];
                res.RemoveAt(i - 1);
                i--;
            }
        }
        return res;
    }

    static bool EndsWithJump(List<Stmt> b) => b.Count > 0 && b[^1] is ReturnStmt or ThrowStmt or BreakStmt or ContinueStmt
                                                                      || b.Count > 0 && b[^1] is IfStmt { Else: { Count: > 0 } } i && EndsWithJump(i.Then) && EndsWithJump(i.Else);

    static Stmt Stmt(Stmt s)
    {
        switch (s)
        {
            case ExprStmt e:
                e.Expr = SimplifyAssign(e.Expr);
                return e;
            case IfStmt i:
            {
                i.Then = Block(i.Then);
                if (i.Else != null) i.Else = Block(i.Else);
                if (i.Else is { Count: 0 }) i.Else = null;
                if (i.Then.Count == 0 && i.Else != null)
                {
                    i.Cond = Structurer.Negate(i.Cond);
                    i.Then = i.Else;
                    i.Else = null;
                }
                // if (a) { if (b) { X } }  →  if (a && b) { X }
                if (i.Else == null && i.Then is [IfStmt { Else: null } inner])
                {
                    i.Cond = new BinaryExpr("&&", i.Cond, inner.Cond);
                    i.Then = inner.Then;
                }
                return i;
            }
            case WhileStmt w:
                w.Body = Block(w.Body);
                return Loop(w);
            case DoWhileStmt d:
                d.Body = TrimContinue(Block(d.Body));
                return d;
            case ForStmt f:
            {
                f.Body = TrimContinue(Block(f.Body));
                // for (;;) { if (!c) break; … }
                if (f.Cond == null && f.Body is [IfStmt { Else: null, Then: [BreakStmt { Label: null }] } first, ..])
                {
                    f.Cond = Structurer.Negate(first.Cond);
                    f.Body.RemoveAt(0);
                }
                for (int k = 0; k < f.Update.Count; k++)
                    if (f.Update[k] is ExprStmt ue) ue.Expr = SimplifyAssign(ue.Expr);
                // for (;;) { if (c) { X; } else break; } (already flattened form: [if(c){X; continue}, break])
                if (f.Cond == null && f.Body is [IfStmt { Else: null } body0, BreakStmt { Label: null }])
                {
                    f.Cond = body0.Cond;
                    f.Body = TrimContinue(body0.Then);
                }
                return f;
            }
            case ForInStmt fi:
                fi.Body = TrimContinue(Block(fi.Body));
                return fi;
            case SwitchStmt sw:
                foreach (var c in sw.Cases) c.Body = Block(c.Body);
                if (sw.Cases.Count > 0 && sw.Cases[^1].Body is [.., BreakStmt { Label: null }] last && last.Count > 1)
                    last.RemoveAt(last.Count - 1);
                return sw;
            case TryStmt t:
                t.Body = Block(t.Body);
                foreach (var c in t.Catches) c.Body = Block(c.Body);
                if (t.Finally != null) t.Finally = Block(t.Finally);
                return t;
            case WithStmt w:
                return new WithStmt(w.Object, Block(w.Body));
            case FunctionStmt fs:
                fs.Function.Body = Run(fs.Function.Body);
                return fs;
        }
        return s;
    }

    static Stmt Loop(WhileStmt w)
    {
        if (!(w.Cond is LitExpr { Value: true })) return w;
        var b = w.Body;
        // while(true) { if (c) { X; continue; } break; }  →  while (c) { X }
        if (b is [IfStmt { Else: null } ifs, BreakStmt { Label: null }] && EndsWithContinue(ifs.Then))
        {
            w.Cond = ifs.Cond;
            w.Body = TrimContinue(ifs.Then);
            return Loop2(w);
        }
        // while(true) { …; if (c) { X; continue; } break; }  →  while(true) { …; if (!c) break; X }
        if (b.Count >= 2 && b[^1] is BreakStmt { Label: null } && b[^2] is IfStmt { Else: null } tail && EndsWithContinue(tail.Then))
        {
            var nb = b.Take(b.Count - 2).ToList();
            nb.Add(new IfStmt(Structurer.Negate(tail.Cond), [new BreakStmt()], null));
            nb.AddRange(TrimContinue(tail.Then));
            w.Body = nb;
            b = nb;
        }
        // while(true) { if (c) break; X }  →  while (!c) { X }
        if (b is [IfStmt { Else: null, Then: [BreakStmt { Label: null }] } first, ..])
        {
            w.Cond = Structurer.Negate(first.Cond);
            w.Body = TrimContinue(b.Skip(1).ToList());
            return Loop2(w);
        }
        // while(true) { X; if (c) break; }  →  do { X } while (!c)
        if (b.Count >= 2 && b[^1] is IfStmt { Else: null, Then: [BreakStmt { Label: null }] } last && !ContainsContinue(b.Take(b.Count - 1)))
        {
            return new DoWhileStmt { Label = w.Label, Body = b.Take(b.Count - 1).ToList(), Cond = Structurer.Negate(last.Cond) };
        }
        w.Body = TrimContinue(w.Body);
        return w;
    }

    /// <summary>while (c) { …; i++; } preceded by "i = …" becomes a for loop (detected by the block pass).</summary>
    static Stmt Loop2(WhileStmt w)
    {
        w.Body = TrimContinue(w.Body);
        if (w.Body.Count >= 1 && w.Body[^1] is ExprStmt { Expr: PostfixExpr or UnaryExpr { Op: "++" or "--" } or AssignExpr { Op: not "=" } } inc
            && !ContainsContinue(w.Body.Take(w.Body.Count - 1)) && TargetOf(inc.Expr) is { } target && Mentions(w.Cond, target))
        {
            return new ForStmt { Label = w.Label, Cond = w.Cond, Update = [inc], Body = w.Body.Take(w.Body.Count - 1).ToList() };
        }
        return w;
    }

    static bool IsCounterInit(Stmt s, ForStmt f)
    {
        var target = f.Update[0] is ExprStmt e ? TargetOf(e.Expr) : null;
        if (target == null) return false;
        return s switch
        {
            VarStmt v => v.Var != null && target is LocalExpr l && l.Var == v.Var || target is NameExpr n && n.Name == v.Name,
            ExprStmt { Expr: AssignExpr { Op: "=" } a } => Same(a.Target, target),
            _ => false,
        };
    }

    static Expr? TargetOf(Expr e) => e switch
    {
        PostfixExpr p => p.Operand,
        UnaryExpr { Op: "++" or "--" } u => u.Operand,
        AssignExpr a => a.Target,
        _ => null,
    };

    static bool Same(Expr a, Expr b) => a switch
    {
        LocalExpr la => b is LocalExpr lb && la.Var == lb.Var,
        NameExpr na => b is NameExpr nb && na.Name == nb.Name,
        _ => a.ToString() == b.ToString(),
    };

    static bool Mentions(Expr? e, Expr target)
    {
        if (e == null) return false;
        string t = target.ToString();
        return e.ToString().Contains(t, StringComparison.Ordinal);
    }

    static List<Stmt> TrimContinue(List<Stmt> b)
    {
        if (b.Count > 0 && b[^1] is ContinueStmt { Label: null }) b = b.Take(b.Count - 1).ToList();
        if (b.Count > 0 && b[^1] is IfStmt i)
        {
            i.Then = TrimContinue(i.Then);
            if (i.Else != null) i.Else = TrimContinue(i.Else);
            if (i.Then.Count == 0 && i.Else is { Count: > 0 })
            {
                i.Cond = Structurer.Negate(i.Cond);
                i.Then = i.Else;
                i.Else = null;
            }
            if (i.Else is { Count: 0 }) i.Else = null;
            if (i.Then.Count == 0 && i.Else == null) b = [.. b.Take(b.Count - 1), .. i.Cond.HasSideEffects ? [new ExprStmt(i.Cond)] : Array.Empty<Stmt>()];
        }
        return b;
    }

    static bool EndsWithContinue(List<Stmt> b) => b.Count > 0 && b[^1] is ContinueStmt { Label: null };

    static bool ContainsContinue(IEnumerable<Stmt> stmts) => stmts.Any(s => Walk(s).Any(x => x is ContinueStmt { Label: null }));

    /// <summary>Contained statements, without descending into nested loops / switches (break/continue scope).</summary>
    static IEnumerable<Stmt> Walk(Stmt s)
    {
        yield return s;
        IEnumerable<Stmt> children = s switch
        {
            IfStmt i => i.Then.Concat(i.Else ?? []),
            TryStmt t => t.Body.Concat(t.Catches.SelectMany(c => c.Body)).Concat(t.Finally ?? []),
            WithStmt w => w.Body,
            BlockStmt b => b.Body,
            _ => [],
        };
        foreach (var c in children)
            foreach (var x in Walk(c))
                yield return x;
    }

    /// <summary>x = x + 1 → x++; x = x op y → x op= y.</summary>
    public static Expr SimplifyAssign(Expr e)
    {
        if (e is not AssignExpr { Op: "=" } a) return e;
        if (a.Value is BinaryExpr b && Same(b.Left, a.Target) && IsLValueSimple(a.Target))
        {
            if (b.Right is LitExpr { IsNumber: true } lit && lit.AsNumber == 1 && b.Op is "+" or "-")
                return new PostfixExpr(b.Op == "+" ? "++" : "--", a.Target);
            if (b.Op is "+" or "-" or "*" or "/" or "%" or "<<" or ">>" or ">>>" or "&" or "|" or "^")
                return new AssignExpr(a.Target, b.Right, b.Op + "=");
        }
        return e;
    }

    /// <summary>Side-effect-free target (x, this.a.b, o[i]) that can be written only once in "x += …".</summary>
    static bool IsLValueSimple(Expr t) => t switch
    {
        LocalExpr or NameExpr or ThisExpr => true,
        MemberExpr m => IsLValueSimple(m.Object),
        IndexExpr i => IsLValueSimple(i.Object) && !i.Index.HasSideEffects,
        _ => false,
    };
}
