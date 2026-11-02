using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Pass the renderer project directory."); return 1; }
        string root = Path.GetFullPath(args[0]);
        string[] directories = { "Assets/Renderer", "Assets/Editor", "Assets/Tests/Editor" };
        string[][] configurations =
        {
            new[] { "UNITY_EDITOR", "UNITY_WEBGL", "UNITY_6000_3_OR_NEWER" },
            new[] { "UNITY_WEBGL", "UNITY_6000_3_OR_NEWER" }
        };
        int count = 0;
        int failures = 0;
        foreach (string directory in directories)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked source files are not allowed.");
                count++;
                string source = File.ReadAllText(file);
                foreach (string[] configuration in configurations)
                {
                    var options = new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: configuration);
                    SyntaxTree tree = CSharpSyntaxTree.ParseText(source, options, file);
                    foreach (Diagnostic diagnostic in tree.GetDiagnostics().Where(value => value.Severity == DiagnosticSeverity.Error))
                    {
                        Console.Error.WriteLine(diagnostic);
                        failures++;
                    }
                }
            }
        }
        Console.WriteLine($"Source-only C#9 syntax: {count} actual source files, editor/player branches, {failures} errors. This is NOT Unity compilation or EditMode execution.");
        return failures == 0 ? 0 : 1;
    }
}
