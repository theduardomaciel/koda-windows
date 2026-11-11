using Koda.Core.Parsing;
using Koda.Core.Profiles;

namespace Koda.Core.Tests;

/// <summary>The account/channel list and the folder each channel owns.</summary>
public class ChannelTests
{
    [Fact]
    public void ReadsTheMultiPageAccountList()
    {
        var root = Json.Parse("""
            {"actions":[{"getMultiPageMenuAction":{"menu":{"multiPageMenuRenderer":{"sections":[
              {"accountSectionListRenderer":{
                "header":{"googleAccountHeaderRenderer":{"email":{"runs":[{"text":"me@example.com"}]},"name":{"runs":[{"text":"Eduardo"}]}}},
                "contents":[{"accountItemSectionRenderer":{"contents":[
                  {"accountItem":{
                    "accountName":{"runs":[{"text":"Eduardo"}]},
                    "channelHandle":{"runs":[{"text":"@eduardo"}]},
                    "accountPhoto":{"thumbnails":[{"url":"https://yt3.ggpht.com/a=s48","width":48,"height":48}]},
                    "isSelected":true,
                    "serviceEndpoint":{"selectActiveIdentityEndpoint":{"supportedTokens":[
                      {"accountSigninToken":{"signinUrl":"/signin?action_handle_signin=true&pageid=brandA"}},
                      {"pageIdToken":{"pageId":"brandA"}},
                      {"datasyncIdToken":{"datasyncIdToken":"DS1"}}
                    ]}}
                  }},
                  {"accountItem":{
                    "accountName":{"runs":[{"text":"Brand B"}]},
                    "isSelected":false,
                    "serviceEndpoint":{"selectActiveIdentityEndpoint":{"supportedTokens":[
                      {"accountSigninToken":{"signinUrl":"https://www.youtube.com/signin?pageid=brandB"}},
                      {"pageIdToken":{"pageId":"brandB"}}
                    ]}}
                  }}
                ]}}]
              }}
            ]}}}}]}
            """);

        var list = AccountPages.AccountsList(root);

        Assert.Equal("me@example.com", list.Email);
        Assert.Equal("Eduardo", list.Name);
        Assert.Equal(2, list.Channels.Length);
        Assert.Equal("brandA", list.Channels[0].PageId);
        Assert.True(list.Channels[0].IsSelected);
        Assert.Equal("DS1", list.Channels[0].DatasyncId);
        Assert.Equal("https://www.youtube.com/signin?action_handle_signin=true&pageid=brandA", list.Channels[0].SigninUrl);
        Assert.Equal("Brand B", list.Channels[1].Name);
        Assert.Null(list.Channels[1].Handle);
    }

    [Fact]
    public void ReadsTheChannelSwitcherPage()
    {
        var root = Json.Parse("""
            {"actions":[{"updateChannelSwitcherPageAction":{"page":{"channelSwitcherPageRenderer":{"contents":[
              {"accountItemRenderer":{
                "accountName":{"runs":[{"text":"Primary"}]},
                "isSelected":false,
                "serviceEndpoint":{"selectActiveIdentityEndpoint":{"supportedTokens":[
                  {"accountSigninToken":{"signinUrl":"https://www.youtube.com/signin?primary=1"}}
                ]}}
              }}
            ]}}}}]}
            """);

        var list = AccountPages.AccountsList(root);

        Assert.Single(list.Channels);
        Assert.Null(list.Channels[0].PageId);
        Assert.Equal("Primary", list.Channels[0].Name);
    }

    [Fact]
    public void TheSelectedChannelOwnsTheExistingRoot()
    {
        var root = Path.Combine(Fixtures.Scratch, $"roster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var roster = new ProfileRoster(root);
        var profile = roster.Active;
        profile.UsesRoot = true;
        profile.Kind = ProfileKind.YouTube;

        roster.SetChannels(profile.Id, new AccountsList("me@example.com", "Me",
        [
            new AccountChannel("brandA", null, "Brand A", "@a", null, true,
                "https://www.youtube.com/signin?pageid=brandA", "DS1"),
            new AccountChannel(null, null, "Primary", null, null, false,
                "https://www.youtube.com/signin?primary=1", "DS1"),
        ]));

        var loaded = roster.Profile(profile.Id)!;
        Assert.Equal("brandA", loaded.ActiveChannelPageId);
        Assert.True(loaded.RootChannelAssigned);
        Assert.Equal(root, roster.DataFolder(loaded));
        Assert.Contains(Path.Combine("channels", "primary"), roster.ChannelFolder(loaded, null));
        Assert.Equal("Brand A", loaded.ListeningName);
    }

    [Fact]
    public void AChannelSwitchChangesTheFolder()
    {
        var root = Path.Combine(Fixtures.Scratch, $"roster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var roster = new ProfileRoster(root);
        var profile = roster.Active;
        profile.UsesRoot = true;
        profile.Kind = ProfileKind.YouTube;
        roster.SetChannels(profile.Id, new AccountsList(null, null,
        [
            new AccountChannel("brandA", null, "Brand A", null, null, true,
                "https://www.youtube.com/signin?pageid=brandA", null),
            new AccountChannel("brandB", null, "Brand B", null, null, false,
                "https://www.youtube.com/signin?pageid=brandB", null),
        ]));

        roster.SetActiveChannel(profile.Id, "brandB");

        var loaded = roster.Profile(profile.Id)!;
        Assert.Equal("brandB", loaded.ActiveChannelPageId);
        Assert.Contains(Path.Combine("channels", "brandB"), roster.DataFolder(loaded));
        Assert.Equal("Brand B", loaded.ListeningName);
    }

    [Fact]
    public void ARosterRefreshKeepsTheChosenChannel()
    {
        var root = Path.Combine(Fixtures.Scratch, $"roster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var roster = new ProfileRoster(root);
        var profile = roster.Active;
        profile.UsesRoot = true;
        profile.Kind = ProfileKind.YouTube;

        AccountsList List(bool brandSelected) => new(null, null,
        [
            new AccountChannel("brandA", null, "Brand A", null, null, brandSelected,
                "https://www.youtube.com/signin?pageid=brandA", null),
            new AccountChannel("brandB", null, "Brand B", null, null, !brandSelected,
                "https://www.youtube.com/signin?pageid=brandB", null),
        ]);

        roster.SetChannels(profile.Id, List(brandSelected: true));
        roster.SetActiveChannel(profile.Id, "brandB");

        // A server hint that still says A must not pull the listener back.
        roster.SetChannels(profile.Id, List(brandSelected: true));

        Assert.Equal("brandB", roster.Profile(profile.Id)!.ActiveChannelPageId);
    }
}
