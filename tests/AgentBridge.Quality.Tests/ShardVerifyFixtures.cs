using Xunit;

// Fixtures for the shard verifier (S4, #4104): the shapes of test class that make a containment filter ("FullyQualifiedName~Term.") match more than its own class. They live in this
// assembly so ShardVerifyTests can compare the verifier's matcher with a real `dotnet test --list-tests --filter` on them. The names are global-namespace on purpose: that is the
// repo's common case, and the one where a term is inside another class's name.
public sealed class AnswerFlowTests
{
    [Fact]
    public void Plain() { }

    [Theory]
    [InlineData(1)]
    public void Themed(int value) => Assert.Equal(1, value);
}

public sealed class AgentAnswerFlowTests
{
    [Fact]
    public void Plain() { }
}

public sealed class OuterLookTests
{
    [Fact]
    public void Outer() { }

    public sealed class InnerLookTests
    {
        [Fact]
        public void Nested() { }
    }
}

public abstract class BaseLookTests<T>
{
    [Fact]
    public void FromBase() { }

    [Theory]
    [InlineData(1)]
    public void Generic<TValue>(TValue value) => Assert.NotNull(value);
}

public sealed class DerivedLookTests : BaseLookTests<int>
{
    [Fact]
    public void Own() { }
}

// Abstract and never derived from: nothing runs it, so the verifier must not count it.
public abstract class AbstractNeverRunLookTests
{
    [Fact]
    public void Never() { }
}

// A custom Fact attribute: the one thing a scan for [Fact] and [Theory] by name misses (73 of Host.Tests' 4860 tests, in the S4 spike).
public sealed class CustomFactAttribute : FactAttribute
{
}

public sealed class CustomFactLookTests
{
    [CustomFact]
    public void Custom() { }
}
