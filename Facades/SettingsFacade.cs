namespace Cdp.ScriptableIde;

/// <summary>CSX: read/update project conventions (<c>.cdp/project.toml</c>).</summary>
public sealed class SettingsFacade(IScriptToolBus bus, PlanContext plan)
{
    public ProjectSettings Current => plan.Settings;

    public TestFrameworkKind? TestFramework => plan.Settings.TestFramework;
    public string? TestFrameworkSource => plan.Settings.TestFrameworkSource;
    public string? DocstringStyle => plan.Settings.DocstringStyle;
    public string? FormatProfile => plan.Settings.FormatProfile;
    public string Path => plan.Settings.SettingsPath;

    /// <summary>Re-read file + Detect-fill.</summary>
    public Task<StepResponse> RefreshAsync(CancellationToken ct = default)
    {
        _ = ct;
        ProjectSettingsLoader.Hydrate(plan);
        var result = StepResponse.Success("project.settings.refresh", "ok", Snapshot());
        bus.RecordLocal("settings", "project.settings.refresh", ScriptArgs.From(Snapshot()), result.ToJson());
        return Task.FromResult(result);
    }

    /// <summary>Pin test FW into project settings (persist to toml by default).</summary>
    public Task<StepResponse> SetTestFrameworkAsync(
        TestFrameworkKind framework,
        bool persist = true,
        CancellationToken ct = default)
    {
        _ = ct;
        plan.Settings.TestFramework = framework;
        plan.Settings.TestFrameworkPolicy = TestFrameworkPolicy.Specified;
        plan.Settings.TestFrameworkSource = persist ? "set+file" : "set";
        if (persist)
            ProjectSettingsLoader.Save(plan);
        var result = StepResponse.Success("project.settings.set_test_framework", framework.ToString(), Snapshot());
        bus.RecordLocal("settings", "project.settings.set_test_framework", ScriptArgs.From(Snapshot()), result.ToJson());
        return Task.FromResult(result);
    }

    public Task<StepResponse> ClearTestFrameworkAsync(bool persist = true, CancellationToken ct = default)
    {
        _ = ct;
        plan.Settings.TestFramework = null;
        plan.Settings.TestFrameworkPolicy = TestFrameworkPolicy.Detect;
        plan.Settings.TestFrameworkSource = null;
        ProjectSettingsLoader.FillDetect(plan);
        if (persist)
            ProjectSettingsLoader.Save(plan);
        var result = StepResponse.Success("project.settings.clear_test_framework", "detect", Snapshot());
        bus.RecordLocal("settings", "project.settings.clear_test_framework", ScriptArgs.From(Snapshot()), result.ToJson());
        return Task.FromResult(result);
    }

    private object Snapshot() => new
    {
        path = plan.Settings.SettingsPath,
        language = plan.Language,
        test_framework = plan.Settings.TestFramework?.ToString(),
        test_framework_policy = plan.Settings.TestFrameworkPolicy.ToString(),
        test_framework_source = plan.Settings.TestFrameworkSource,
        docstring_style = plan.Settings.DocstringStyle,
        format_profile = plan.Settings.FormatProfile
    };
}
