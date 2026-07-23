using Cdp.ScriptableIde;

static string TempGit()
{
    var root = Path.Combine(Path.GetTempPath(), "cdp-promote-harden-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);
    void Git(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd());
    }
    Git("init");
    Git("config", "user.email", "smoke@test");
    Git("config", "user.name", "smoke");
    File.WriteAllText(Path.Combine(root, "a.txt"), "one\n");
    Git("add", "a.txt");
    Git("commit", "-m", "init");
    return root;
}

static async Task<string> NoopInvoke(string d, string u, IReadOnlyDictionary<string, System.Text.Json.JsonElement> a, CancellationToken ct)
    => """{"ok":true}""";

var code = """
await Mutate.Fs.WriteTextAsync("b.txt", "two");
return Mutate.Submit();
""";

// --- dirty OUTSIDE plan path → overlap_safe promote OK ---
var dirtyElsewhere = TempGit();
File.WriteAllText(Path.Combine(dirtyElsewhere, "dirt.txt"), "x");
var plan = await WorktreePlanRunner.RunInWorktreeAsync(code, dirtyElsewhere, NoopInvoke);
if (!plan.Ok || plan.PlanId is null) throw new Exception("run_plan failed: " + plan.Error);
if (plan.PromotePolicy != WorktreePlanRunner.PromoteOverlapSafe)
    throw new Exception("expected default overlap_safe, got " + plan.PromotePolicy);
var okDirtyElse = WorktreePlanRunner.Promote(plan.PlanId);
if (!okDirtyElse.Ok) throw new Exception("expected promote ok with dirty elsewhere: " + okDirtyElse.Error);
if (!File.Exists(Path.Combine(dirtyElsewhere, "b.txt"))) throw new Exception("b.txt missing after promote");
Console.WriteLine("PASS dirty-elsewhere-promote: " + okDirtyElse.Result);

// --- strict_clean still refuses any dirty ---
var dirtyStrict = TempGit();
File.WriteAllText(Path.Combine(dirtyStrict, "dirt.txt"), "x");
var planStrict = await WorktreePlanRunner.RunInWorktreeAsync(
    code, dirtyStrict, NoopInvoke, promotePolicy: WorktreePlanRunner.PromoteStrictClean);
if (!planStrict.Ok || planStrict.PlanId is null) throw new Exception("run_plan strict failed: " + planStrict.Error);
var refuseStrict = WorktreePlanRunner.Promote(planStrict.PlanId);
if (refuseStrict.Ok) throw new Exception("expected strict_clean refuse on dirty primary");
if (refuseStrict.Error is null || !refuseStrict.Error.Contains("strict_clean", StringComparison.OrdinalIgnoreCase))
    throw new Exception("unexpected error: " + refuseStrict.Error);
_ = WorktreePlanRunner.Discard(planStrict.PlanId);
Console.WriteLine("PASS strict_clean-refuse: " + refuseStrict.Error.Split('\n')[0]);

// --- overlapping dirty diverged after plan start → refuse ---
var overlap = TempGit();
File.WriteAllText(Path.Combine(overlap, "a.txt"), "one\n"); // already committed content
// Make a.txt dirty matching HEAD first so overlay is a no-op-ish; plan will rewrite a.txt
var codeTouchA = """
await Mutate.Fs.WriteTextAsync("a.txt", "from-plan\n");
return Mutate.Submit();
""";
var planOverlap = await WorktreePlanRunner.RunInWorktreeAsync(codeTouchA, overlap, NoopInvoke);
if (!planOverlap.Ok || planOverlap.PlanId is null) throw new Exception("run_plan overlap failed: " + planOverlap.Error);
// Diverge primary after plan started
File.WriteAllText(Path.Combine(overlap, "a.txt"), "diverged-on-primary\n");
var refuseOverlap = WorktreePlanRunner.Promote(planOverlap.PlanId!);
if (refuseOverlap.Ok) throw new Exception("expected overlap conflict refuse");
if (refuseOverlap.Error is null || !refuseOverlap.Error.Contains("Conflicts", StringComparison.OrdinalIgnoreCase))
    throw new Exception("unexpected overlap error: " + refuseOverlap.Error);
_ = WorktreePlanRunner.Discard(planOverlap.PlanId!);
Console.WriteLine("PASS overlap-conflict-refuse: " + refuseOverlap.Error.Split('\n')[0]);

// --- clean primary + apply succeeds ---
var cleanRoot = TempGit();
var plan2 = await WorktreePlanRunner.RunInWorktreeAsync(code, cleanRoot, NoopInvoke);
if (!plan2.Ok || plan2.PlanId is null) throw new Exception("run_plan2 failed: " + plan2.Error);
var ok = WorktreePlanRunner.Promote(plan2.PlanId);
if (!ok.Ok) throw new Exception("expected promote ok: " + ok.Error);
if (!File.Exists(Path.Combine(cleanRoot, "b.txt"))) throw new Exception("b.txt missing after promote");
Console.WriteLine("PASS clean-promote: " + ok.Result);

// --- GitRoot resolve from nested path ---
var nested = TempGit();
var sub = Path.Combine(nested, "pkg");
Directory.CreateDirectory(sub);
File.WriteAllText(Path.Combine(sub, "c.txt"), "c\n");
var gitRoot = GitRootResolver.ResolveGitRoot(sub);
if (!string.Equals(Path.GetFullPath(gitRoot), Path.GetFullPath(nested), StringComparison.OrdinalIgnoreCase))
    throw new Exception($"GitRoot mismatch: {gitRoot} vs {nested}");
var scope = GitRootResolver.ResolvePlanScope(nested, sub);
if (scope != "pkg") throw new Exception("expected scope pkg, got " + scope);
Console.WriteLine("PASS gitroot-scope: " + scope);
