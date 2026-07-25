namespace Cdp.ScriptableIde;

/// <summary>
/// Post-<c>dotnet new</c> hygiene: promote template <c>Class1.cs</c> to the project type name
/// so agents are not stuck with a throwaway filename after rewrite.
/// </summary>
public static class ProjectScaffoldHygiene
{
    /// <summary>
    /// If <c>Class1.cs</c> exists and <paramref name="projectName"/> yields a safe type name,
    /// rename file + type to that name. No-op when destination already exists.
    /// </summary>
    public static string? TryPromoteClass1(string outputDir, string projectName)
    {
        if (string.IsNullOrWhiteSpace(outputDir) || string.IsNullOrWhiteSpace(projectName))
            return null;

        var class1 = Path.Combine(outputDir, "Class1.cs");
        if (!File.Exists(class1))
            return null;

        var typeName = SanitizeTypeName(projectName);
        if (string.IsNullOrEmpty(typeName) || typeName is "Class1")
            return null;

        var dest = Path.Combine(outputDir, typeName + ".cs");
        if (File.Exists(dest))
            return null;

        var text = File.ReadAllText(class1);
        text = text.Replace("class Class1", "class " + typeName, StringComparison.Ordinal);
        // record Class1 { } / Class1;
        text = text.Replace("record Class1", "record " + typeName, StringComparison.Ordinal);
        text = text.Replace("struct Class1", "struct " + typeName, StringComparison.Ordinal);

        File.WriteAllText(dest, text);
        File.Delete(class1);
        return dest;
    }

    public static string SanitizeTypeName(string projectName)
    {
        var chars = projectName.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        if (s.Length == 0)
            return "App";
        if (char.IsDigit(s[0]))
            s = "_" + s;
        return s;
    }
}
