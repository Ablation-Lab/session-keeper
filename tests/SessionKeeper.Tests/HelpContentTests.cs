using SessionKeeper.ViewModels;

namespace SessionKeeper.Tests;

public sealed class HelpContentTests
{
    [Theory]
    [InlineData(HelpContent.Current)]
    [InlineData(HelpContent.Recent)]
    [InlineData(HelpContent.Snapshots)]
    [InlineData(HelpContent.Terminals)]
    public void EveryTabHasItsOwnHelpTopic(string id) => Assert.Equal(id, HelpContent.For(id).Id);

    [Fact]
    public void AnUnknownTopicOpensGettingStarted() => Assert.Equal(HelpContent.Overview, HelpContent.For("nope").Id);

    [Fact]
    public void TopicsHaveUniqueIdsAndNoEmptyText()
    {
        Assert.Equal(HelpContent.Sections.Count, HelpContent.Sections.Select(s => s.Id).Distinct().Count());
        foreach (var s in HelpContent.Sections)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.False(string.IsNullOrWhiteSpace(s.Summary));
            Assert.NotEmpty(s.Blocks);
            foreach (var b in s.Blocks)
            {
                Assert.NotEmpty(b.Points);
                // Bold markers come in pairs, so no text is accidentally left bold or shows stray asterisks.
                Assert.All(b.Points, p => Assert.True(p.Split("**").Length % 2 == 1, $"{s.Id}: unbalanced ** in \"{p}\""));
                Assert.All(b.Points, p => Assert.False(string.IsNullOrWhiteSpace(p)));
            }
        }
    }

    [Fact]
    public void BoldMarkersBecomeBoldRuns()
    {
        Assert.Equal(new[] { ("Click ", false), ("Restore", true), (" now.", false) }, HelpContent.Runs("Click **Restore** now."));
        Assert.Equal(new[] { ("plain", false) }, HelpContent.Runs("plain"));
        Assert.Equal(new[] { ("a ", false), ("b", false) }, HelpContent.Runs("a **b")); // an unclosed marker isn't bold
    }
}
