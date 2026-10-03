using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// <c>GameViewModel.NotifyWindowSize</c> raises a change notification, by hand, for every public
/// property derived from the effective column count. A derived property left out of that list never
/// updates on resize, and nothing else would say so: XAML bindings are checked by neither the
/// compiler nor any suite, and no test project can compile against the MAUI assembly.
///
/// <para>This reads the view model's SOURCE instead: every public property whose body reaches
/// <c>_effCols</c> - directly, or through another property or a private helper that does - must be
/// in that list, and nothing that does not must be. The column count's own exposure,
/// <c>EffCols</c>, counts as derived.</para>
/// </summary>
public class WindowSizeNotificationsTests
{
    private const string Field = "_effCols";

    private static string ViewModelPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "ViewModels", "GameViewModel.cs");
    }

    /// <summary>The public properties of <paramref name="cls"/> whose body depends on
    /// <see cref="Field"/>, transitively through members of the same class.</summary>
    public static SortedSet<string> ColumnDependentProperties(ClassDeclarationSyntax cls)
    {
        // Every property GETTER and method body, by member name - a setter that notifies a derived
        // property is not a dependency on it. Overloads merge, which only widens.
        var bodies = new Dictionary<string, List<SyntaxNode>>(StringComparer.Ordinal);
        foreach (var member in cls.Members)
        {
            var (name, body) = member switch
            {
                PropertyDeclarationSyntax p => (p.Identifier.Text, (SyntaxNode?)p.ExpressionBody
                    ?? p.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))),
                MethodDeclarationSyntax m => (m.Identifier.Text, (SyntaxNode?)m.ExpressionBody ?? m.Body),
                _ => (null, null),
            };
            if (name is null || body is null)
                continue;
            if (!bodies.TryGetValue(name, out var list))
                bodies[name] = list = [];
            list.Add(body);
        }

        var references = bodies.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.SelectMany(b => b.DescendantNodes().OfType<IdentifierNameSyntax>())
                          .Select(id => id.Identifier.Text).ToHashSet(StringComparer.Ordinal));

        var dependent = new HashSet<string>(StringComparer.Ordinal);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (name, refs) in references)
            {
                if (dependent.Contains(name))
                    continue;
                if (refs.Contains(Field) || refs.Overlaps(dependent))
                    changed |= dependent.Add(name);
            }
        }

        var publicProperties = cls.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(SyntaxKind.PublicKeyword))
            .Select(p => p.Identifier.Text);
        return new SortedSet<string>(publicProperties.Where(dependent.Contains), StringComparer.Ordinal);
    }

    /// <summary>The <c>nameof</c> arguments of every <c>OnPropertyChanged</c> call in
    /// <paramref name="method"/>.</summary>
    public static SortedSet<string> NotifiedProperties(MethodDeclarationSyntax method)
        => new(method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                   .Where(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "OnPropertyChanged" })
                   .Select(i => i.ArgumentList.Arguments.SingleOrDefault()?.Expression)
                   .OfType<InvocationExpressionSyntax>()
                   .Where(n => n.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" })
                   .Select(n => n.ArgumentList.Arguments.Single().Expression.ToString()),
               StringComparer.Ordinal);

    private static ClassDeclarationSyntax Parse(string source)
        => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();

    [Fact]
    public void NotifyWindowSize_NotifiesExactlyTheColumnDependentProperties()
    {
        var cls = CSharpSyntaxTree.ParseText(File.ReadAllText(ViewModelPath())).GetRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.Text == "GameViewModel");
        var notify = cls.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "NotifyWindowSize");

        var dependent = ColumnDependentProperties(cls);
        var notified = NotifiedProperties(notify);
        Assert.True(dependent.Count > 10, "found almost no column-dependent properties - the walk is wrong");

        var missing = dependent.Except(notified).ToList();
        var extra = notified.Except(dependent).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            "GameViewModel.NotifyWindowSize must notify every property derived from the column count, and only those.\n"
            + "  Derived but not notified (never updates on resize): " + string.Join(", ", missing) + "\n"
            + "  Notified but not derived: " + string.Join(", ", extra));
    }

    /// <summary>The walk has to be able to see a dependency through a property and through a private
    /// helper, and to leave an unrelated property alone - or the gate above passes on anything.</summary>
    [Fact]
    public void TheWalk_FollowsPropertiesAndHelpers_AndIgnoresTheRest()
    {
        var cls = Parse("""
            class C
            {
                private int _effCols;
                public int Direct => _effCols;
                public int ViaProperty => Direct + 1;
                private int Helper() => _effCols * 2;
                public int ViaHelper { get { return Helper(); } }
                public int Unrelated => 4;
                private int Hidden => _effCols;
                public int Notifier { get => 0; set => Notify(nameof(Direct)); }
            }
            """);

        Assert.Equal(["Direct", "ViaHelper", "ViaProperty"], ColumnDependentProperties(cls));
    }

    [Fact]
    public void TheNotifiedList_IsReadFromNameofArguments()
    {
        var cls = Parse("""
            class C
            {
                void NotifyWindowSize()
                {
                    OnPropertyChanged(nameof(A));
                    OnPropertyChanged(nameof(B));
                    OnPropertyChanged("C");
                    Other(nameof(D));
                }
            }
            """);

        Assert.Equal(["A", "B"], NotifiedProperties(cls.Members.OfType<MethodDeclarationSyntax>().Single()));
    }
}
