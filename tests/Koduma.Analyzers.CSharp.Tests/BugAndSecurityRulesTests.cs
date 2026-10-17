using Koduma.Analyzers.CSharp;
using Koduma.Core.Config;
using Koduma.Core.Model;
using Xunit;

public class BugAndSecurityRulesTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "koduma-rules-" + Guid.NewGuid() + ".cs");

    public void Dispose() => File.Delete(_file);

    private async Task<List<IIssue>> RunAsync(string source, string ruleId)
    {
        File.WriteAllText(_file, source);
        var result = await new CSharpAnalyzer().AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);
        return result.Issues.Where(i => i.RuleId == ruleId).ToList();
    }

    [Fact]
    public async Task AsyncVoid_FlagsPlainMethod_ButNotEventHandlerOrTaskReturning()
    {
        var issues = await RunAsync("""
        public class C
        {
            public async void Bad() { }
            public async void OnClick(object sender, EventArgs e) { }
            public async Task Good() { }
        }
        """, "CS-BUG-001");

        var issue = Assert.Single(issues);
        Assert.Contains("Bad", issue.Message);
    }

    [Fact]
    public async Task SyncOverAsync_FlagsBlockingCallsInAsyncMethod_ButNotInSyncMethod()
    {
        var issues = await RunAsync("""
        public class C
        {
            public async Task A()
            {
                Fetch().Wait();
                var x = Fetch().Result;
                Fetch().GetAwaiter().GetResult();
            }
            public void Sync() { Fetch().Wait(); }
            private Task<int> Fetch() => null;
        }
        """, "CS-BUG-002");

        Assert.Equal(3, issues.Count);
        Assert.All(issues, i => Assert.Contains("'A'", i.Message));
    }

    [Fact]
    public async Task RethrowException_FlagsThrowEx_ButNotBareThrowOrNewException()
    {
        var issues = await RunAsync("""
        public class C
        {
            public void M()
            {
                try { }
                catch (Exception ex) { throw ex; }
                try { }
                catch (Exception ex) { throw; }
                try { }
                catch (Exception ex) { throw new InvalidOperationException("x", ex); }
            }
        }
        """, "CS-BUG-003");

        var issue = Assert.Single(issues);
        Assert.Equal(6, issue.Line);
    }

    [Fact]
    public async Task LockOnSharedObject_FlagsThisTypeofAndStringLiteral_ButNotPrivateObject()
    {
        var issues = await RunAsync("""
        public class C
        {
            private readonly object _gate = new object();
            public void M()
            {
                lock (this) { }
                lock (typeof(C)) { }
                lock ("key") { }
                lock (_gate) { }
            }
        }
        """, "CS-BUG-004");

        Assert.Equal(3, issues.Count);
    }

    [Fact]
    public async Task IdenticalOperands_FlagsSameExpressionBothSides_ButNotDifferentOrSideEffecting()
    {
        var issues = await RunAsync("""
        public class C
        {
            public bool M(int a, int b)
            {
                var x = a == a;
                var y = a == b;
                var z = Next() == Next();
                var w = a + a;
                return x && y && z && w > 0;
            }
            private int Next() => 1;
        }
        """, "CS-BUG-005");

        var issue = Assert.Single(issues);
        Assert.Equal(5, issue.Line);
    }

    [Fact]
    public async Task EmptyStatementBody_FlagsIfSemicolon_ButNotBlock()
    {
        var issues = await RunAsync("""
        public class C
        {
            public void M(int a)
            {
                if (a > 0);
                if (a > 1) { }
            }
        }
        """, "CS-BUG-006");

        var issue = Assert.Single(issues);
        Assert.Equal(5, issue.Line);
    }

    [Fact]
    public async Task GenericCatch_FlagsExceptionWithoutRethrow_ButNotSpecificFilteredRethrowingOrEmpty()
    {
        var issues = await RunAsync("""
        public class C
        {
            public void M()
            {
                try { } catch (Exception) { Log(); }
                try { } catch (IOException) { Log(); }
                try { } catch (Exception) { Log(); throw; }
                try { } catch (Exception) when (true) { Log(); }
                try { } catch (Exception) { }
                try { } catch { Log(); }
            }
            private void Log() { }
        }
        """, "CS-DESIGN-004");

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Line == 5);
        Assert.Contains(issues, i => i.Line == 10);
    }

    [Fact]
    public async Task NestingDepth_FlagsFiveLevels_ButNotFourOrElseIfChain()
    {
        var issues = await RunAsync("""
        public class C
        {
            public void Deep(bool a)
            {
                if (a) { if (a) { if (a) { if (a) { if (a) { } } } } }
            }
            public void Four(bool a)
            {
                if (a) { if (a) { if (a) { if (a) { } } } }
            }
            public void Chain(int a)
            {
                if (a == 1) { }
                else if (a == 2) { }
                else if (a == 3) { }
                else if (a == 4) { }
                else if (a == 5) { }
                else if (a == 6) { }
            }
        }
        """, "CS-DESIGN-006");

        var issue = Assert.Single(issues);
        Assert.Equal(5, issue.Line);
    }

    [Fact]
    public async Task ClassSize_FlagsTypeOver500Lines()
    {
        var body = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"    public int F{i};"));
        var issues = await RunAsync($"public class Big\n{{\n{body}\n}}\npublic class Small {{ }}\n", "CS-DESIGN-005");

        var issue = Assert.Single(issues);
        Assert.Contains("Big", issue.Message);
    }

    [Fact]
    public async Task PublicMutableStatic_FlagsMutableField_ButNotReadonlyConstOrPrivate()
    {
        var issues = await RunAsync("""
        public class C
        {
            public static int Counter;
            public static readonly int Fixed = 1;
            public const int Limit = 2;
            private static int _hidden;
            public int Instance;
        }
        """, "CS-SMELL-002");

        var issue = Assert.Single(issues);
        Assert.Contains("Counter", issue.Message);
    }

    [Fact]
    public async Task WeakCryptography_FlagsMd5Sha1AndDes_ButNotSha256()
    {
        var issues = await RunAsync("""
        public class C
        {
            public void M()
            {
                var a = MD5.Create();
                var b = new SHA1CryptoServiceProvider();
                var c = System.Security.Cryptography.DES.Create();
                var d = SHA256.Create();
            }
        }
        """, "CS-SEC-001");

        Assert.Equal(3, issues.Count);
        Assert.All(issues, i => Assert.Equal(Category.Security, i.Category));
    }

    [Fact]
    public async Task InsecureDeserialization_FlagsBinaryFormatterAndTypeNameHandling_ButNotNone()
    {
        var issues = await RunAsync("""
        public class C
        {
            public void M()
            {
                var f = new BinaryFormatter();
                var s = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
                var t = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None };
            }
        }
        """, "CS-SEC-002");

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(Severity.Critical, i.Severity));
    }

    [Fact]
    public async Task CertificateValidation_FlagsAlwaysTrueCallbacks_ButNotRealValidation()
    {
        var issues = await RunAsync("""
        public class C
        {
            public void M(HttpClientHandler handler)
            {
                ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => e == SslPolicyErrors.None;
            }
        }
        """, "CS-SEC-003");

        // The always-true lambda (line 5) and the Dangerous... validator (line 6).
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Line == 5);
        Assert.Contains(issues, i => i.Line == 6);
    }
}
