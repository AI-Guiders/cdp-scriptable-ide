namespace Cdp.ScriptableIde;

/// <summary>Globals injected into every CSX — allowlisted surface only.</summary>
public sealed class ScriptGlobals
{
    public ScriptGlobals(ScriptToolBus bus, PlanContext plan)
    {
        Bus = bus;
        Plan = plan;
        ExecutionRegistry = new ExecutionRegistry();
        Debug = new DebugFacade(bus);
        Roslyn = new RoslynFacade(bus);
        Git = new GitFacade(bus);
        Verify = new VerifyFacade(bus);
        Fs = new FsFacade(bus, plan);
        Mutate = new MutateFacade(bus, this);
        Anui = new AnuiFacade(bus);
        ExecEnvironment = new ExecEnvironment(this);
        Execution = new ExecutionFacade(this);
        Symbol = new SymbolFacade(bus, plan);
        SemanticMap = new SemanticMapFacade(bus, plan);
        Correspondence = new CorrespondenceFacade();
        Work = new WorkFacade(bus);
        Refactor = new RefactorFacade(bus, plan);
        Annotate = new AnnotateFacade(bus, plan);
        Insert = new InsertFacade(bus, plan);
        Fix = new FixFacade(bus, plan);
        Generate = new GenerateFacade(bus, plan);
        Body = new BodyFacade(bus, plan);
        Code = new CodeFacade(bus, plan);
        Modules = new ModulesFacade(bus, plan);
        TypeDecl = new TypeDeclFacade(bus, plan);
        Method = new MethodFacade(bus, plan);
        Create = new CreateFacade(bus, plan);
        Convert = new ConvertFacade(bus, plan);
        Settings = new SettingsFacade(bus, plan);
        Packages = new PackagesFacade(bus, plan);
        Projects = new ProjectsFacade(bus, plan);
        Solutions = new SolutionsFacade(bus, plan);
        Scratch = new ScratchFacade(bus, plan);
        Open = new OpenFacade(bus, plan);
        Help = new HelpFacade();
    }

    internal ScriptToolBus Bus { get; }
    internal ExecutionRegistry ExecutionRegistry { get; }
    public PlanContext Plan { get; }

    public DebugFacade Debug { get; }
    public RoslynFacade Roslyn { get; }
    public GitFacade Git { get; }
    public VerifyFacade Verify { get; }
    public FsFacade Fs { get; }
    public MutateFacade Mutate { get; }
    public AnuiFacade Anui { get; }
    public ExecEnvironment ExecEnvironment { get; }
    public ExecutionFacade Execution { get; }
    /// <summary>Explore by name: <c>Symbol.Named("T").In("F.cs")</c> — not SearchAsync.</summary>
    public SymbolFacade Symbol { get; }
    /// <summary>Related/scene map: <c>SemanticMap.Explore(anchor).Mode("related").GetSceneAsync()</c>.</summary>
    public SemanticMapFacade SemanticMap { get; }
    public CorrespondenceFacade Correspondence { get; }
    public WorkFacade Work { get; }
    /// <summary>Refactor: <c>Extract.Method.From(a).Till(b).Name(…).ApplyAsync()</c>, Rename.At, Move.MembersToPartial.</summary>
    public RefactorFacade Refactor { get; }
    public AnnotateFacade Annotate { get; }
    public InsertFacade Insert { get; }
    public FixFacade Fix { get; }
    public GenerateFacade Generate { get; }
    /// <summary>Method-body authoring: AddCondition / AddLoop / AddDeclare.</summary>
    public BodyFacade Body { get; }
    /// <summary>Relocate span: <c>Code.Move().From(a).To(b).Before().ApplyAsync()</c>.</summary>
    public CodeFacade Code { get; }
    /// <summary>Imports: <c>Modules.Import("System").Into(file)</c>.</summary>
    public ModulesFacade Modules { get; }
    /// <summary>Create type shell: <c>TypeDecl.Create("Quadratic").Static().Namespace(…).Into(file)</c>.</summary>
    public TypeDeclFacade TypeDecl { get; }
    /// <summary>Alias: prefer <see cref="Create"/>.Method.</summary>
    public MethodFacade Method { get; }
    /// <summary>Declare: <c>Create.Record|Class|Method|Property|Field</c>.</summary>
    public CreateFacade Create { get; }
    /// <summary>Form change: <c>Convert.ToProperty</c> / <c>Convert.AnonymousReturn.At(method).To(T)</c>.</summary>
    public ConvertFacade Convert { get; }
    public SettingsFacade Settings { get; }
    public PackagesFacade Packages { get; }
    public ProjectsFacade Projects { get; }
    public SolutionsFacade Solutions { get; }
    /// <summary>TEMP probes — auto-cleaned after CSX; never under WorkRoot.</summary>
    public ScratchFacade Scratch { get; }
    /// <summary>Open Recent + Anchor→solution: <c>Open.At(anchor)</c>, <c>Open.Recent.ListAsync()</c>, <c>Open.Recent.AtAsync(0)</c>.</summary>
    public OpenFacade Open { get; }
    /// <summary>Live CSX API help from XML docs: <c>Help.Toc()</c>, <c>Help.Of("Symbol")</c>.</summary>
    public HelpFacade Help { get; }
}

public sealed class MutateFacade(IScriptToolBus bus, ScriptGlobals root)
{
    public RoslynFacade Roslyn => root.Roslyn;
    public GitFacade Git => root.Git;
    public VerifyFacade Verify => root.Verify;
    public FsFacade Fs => root.Fs;
    public ExecutionFacade Execution => root.Execution;

    public BodyFacade Body => root.Body;
    public CodeFacade Code => root.Code;
    public string Submit()
    {
        var steps = bus.Steps.Select(s => new
        {
            domain = s.Domain,
            tool = s.Underlying,
            dry_run_skipped = s.SkippedDryRun,
            at = s.AtUtc,
            result_preview = s.Result is { Length: > 200 } r ? r[..200] + "…" : s.Result
        });
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            submitted = true,
            plan_id = root.Plan.PlanId,
            work_root = root.Plan.WorkRoot,
            primary_root = root.Plan.PrimaryRoot,
            execution_config = root.ExecutionRegistry.Source,
            step_count = bus.Steps.Count,
            steps
        });
    }
}
