using Koduma.Analyzers.CSharp;
using Koduma.Core.Config;
using Koduma.Core.Model;
using Xunit;

public class ExpandedCatalogRulesTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "koduma-expanded-" + Guid.NewGuid() + ".cs");

    public void Dispose() => File.Delete(_file);

    private async Task<List<IIssue>> RunAsync(string source, string ruleId)
    {
        File.WriteAllText(_file, source);
        var result = await new CSharpAnalyzer().AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);
        return result.Issues.Where(i => i.RuleId == ruleId).ToList();
    }

    [Fact]
    public async Task DivisionByZero_FlagsIntAndDecimalLiteralZero_ButNotDoubleZeroOrNonZero()
    {
        var issues = await RunAsync("""
        public class C
        {
            public void M(int a, decimal b, double c)
            {
                var x = a / 0;
                var y = a % 0;
                var z = b / 0m;
                var w = c / 0.0;
                var v = a / 2;
            }
        }
        """, "CS-BUG-007");

        Assert.Equal(3, issues.Count);
    }

    [Fact]
    public async Task SelfAssignment_FlagsVariableAndMemberAccess_ButNotInitializerOrDifferentTarget()
    {
        var issues = await RunAsync("""
        public class C
        {
            public int X;
            public void M(C other)
            {
                int a = 1;
                a = a;
                this.X = other.X;
                other.X = other.X;
                var p = new C { X = X };
            }
        }
        """, "CS-BUG-008");

        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public async Task EqualsHashCode_FlagsEitherOverrideAlone_ButNotBothOrNeither()
    {
        var issues = await RunAsync("""
        public class OnlyEquals
        {
            public override bool Equals(object obj) => true;
        }
        public class OnlyHashCode
        {
            public override int GetHashCode() => 1;
        }
        public class Both
        {
            public override bool Equals(object obj) => true;
            public override int GetHashCode() => 1;
        }
        public class Neither
        {
        }
        """, "CS-BUG-009");

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("OnlyEquals"));
        Assert.Contains(issues, i => i.Message.Contains("OnlyHashCode"));
    }

    [Fact]
    public async Task DisposableNotUsing_FlagsPlainDeclaration_ButNotUsingDeclarationOrUnrelatedType()
    {
        var issues = await RunAsync("""
        public class C
        {
            public void M()
            {
                var a = new System.IO.StreamReader("f.txt");
                using var b = new System.IO.StreamReader("f.txt");
                using (var c = new System.IO.StreamReader("f.txt")) { }
                var d = new System.Collections.Generic.List<int>();
            }
        }
        """, "CS-BUG-010");

        var issue = Assert.Single(issues);
        Assert.Contains("'a'", issue.Message);
    }

    [Fact]
    public async Task SwitchWithoutDefault_FlagsMissingDefault_ButNotWhenPresent()
    {
        var issues = await RunAsync("""
        public class C
        {
            public void NoDefault(int x)
            {
                switch (x)
                {
                    case 1: break;
                    case 2: break;
                }
            }
            public void WithDefault(int x)
            {
                switch (x)
                {
                    case 1: break;
                    default: break;
                }
            }
        }
        """, "CS-DESIGN-007");

        Assert.Single(issues);
    }

    [Fact]
    public async Task CommentedOutCode_FlagsCodeLikeComments_ButNotProseOrTodo()
    {
        var issues = await RunAsync("""
        public class C
        {
            // var total = ComputeTotal();
            // total = total + 1;
            // This method processes the order queue.
            // TODO: revisit this later
            public void M() { }
        }
        """, "CS-SMELL-003");

        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public async Task InsecureHttpUrl_FlagsPlainHttp_ButNotHttpsLoopbackOrXmlNamespace()
    {
        var issues = await RunAsync("""
        public class C
        {
            public string A = "http://api.example-service.com/data";
            public string B = "https://api.example-service.com/data";
            public string D = "http://localhost:5000";
            public string E = "http://www.w3.org/2001/XMLSchema";
        }
        """, "CS-SEC-004");

        Assert.Single(issues);
    }

    [Fact]
    public async Task CommandInjection_FlagsConcatenatedOrInterpolatedArgument_ButNotLiteral()
    {
        var issues = await RunAsync("""
        public class C
        {
            public void M(string userInput)
            {
                System.Diagnostics.Process.Start("cmd.exe", "/c " + userInput);
                System.Diagnostics.Process.Start("notepad.exe");
                var info = new System.Diagnostics.ProcessStartInfo($"app.exe {userInput}");
            }
        }
        """, "CS-SEC-005");

        Assert.Equal(2, issues.Count);
    }
}
