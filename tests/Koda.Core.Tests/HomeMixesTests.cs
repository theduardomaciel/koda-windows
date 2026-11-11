using Koda.Core.Parsing;

namespace Koda.Core.Tests;

/// <summary>Home pages page their shelves; the mix walk needs the token from each answer.</summary>
public class HomeMixesTests
{
    [Fact]
    public void ReadsTheFirstPagesContinuation()
    {
        var root = Json.Parse("""
            {"contents":{"singleColumnBrowseResultsRenderer":{"tabs":[{"tabRenderer":{"content":{"sectionListRenderer":{
              "contents":[],
              "continuations":[{"nextContinuationData":{"continuation":"TOKEN"}}]}}}}]}}}
            """);

        Assert.Equal("TOKEN", Pages.HomeContinuation(root));
    }

    [Fact]
    public void ReadsAContinuationPagesNextToken()
    {
        var root = Json.Parse("""
            {"continuationContents":{"sectionListContinuation":{
              "continuations":[{"nextContinuationData":{"continuation":"LEGACY"}}]}}}
            """);

        Assert.Equal("LEGACY", Pages.HomeContinuation(root));
    }

    [Fact]
    public void ALastPageHasNoContinuation()
    {
        var root = Json.Parse("""{"continuationContents":{"sectionListContinuation":{"contents":[]}}}""");

        Assert.Null(Pages.HomeContinuation(root));
    }
}
